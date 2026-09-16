namespace BossMod;

// Custom queue for manual actions.
// When running autorotation, we typically still want the ability to execute actions manually (e.g. if there is no plan available, or if some emergency happens).
// There are some problematic interactions with autorotation that this tweak solves:
// - typically we want to manually execute oGCDs, and typically we prefer slightly delaying them if it means not losing GCD uptime
// - however, we also want to give an emergency 'use asap' option (not worse than if you were to spam button without autorotation active)
// Tweak uses the following implementation:
// - maintain our own queue of manually requested actions, and route user-requested actions here instead of passing directly to the game
// - unlike native action queue, ours supports multiple pending entries
// - our queue distinguishes GCD and oGCD actions; since oGCDs can be delayed, effective 'expiration' time for oGCDs is much larger than native 0.5s
// - trying to queue an oGCD action while it is already queued (double tapping) activates 'emergency mode': all preceeding queued actions are removed and this action is returned even if it would delay GCD
// - entries from the manual queue are added to the autoqueue every frame with appropriate priorities, and usual logic selects best action to execute
// ActionManagerEx 在 detour 內（遊戲主執行緒）當幀取樣後交過來的快照：全是值型別，
// 這一側永遠不持有原生指標。Status 就是 LogMessage 的 row id，0＝遊戲說這一發可以用。
public readonly record struct ActionBlockInfo(uint Status, bool RecastActive, float GCDRemaining, float AnimationLock, float CastRemaining, bool Moving, bool MovementBlocked, float Range, float Distance, bool HasTarget)
{
    // 節流用的穩定鍵：只取離散成分 —— 動畫鎖與距離每幀都在變，放進鍵等於完全沒有節流
    public uint BlockKey => Status
        | (RecastActive ? 0x01000000u : 0u)
        | (AnimationLock > 0 ? 0x02000000u : 0u)
        | (CastRemaining > 0 ? 0x04000000u : 0u)
        | (Moving ? 0x08000000u : 0u)
        | (MovementBlocked ? 0x10000000u : 0u)
        | (HasTarget ? 0u : 0x20000000u)
        | (Range > 0 && Distance > Range ? 0x40000000u : 0u);

    public string Describe()
    {
        var status = Status != 0 ? $"status {Status} '{Service.LuminaRow<Lumina.Excel.Sheets.LogMessage>(Status)?.Text}'" : "status 0（遊戲說這一發可以用）";
        var tgt = !HasTarget ? "無目標" : Range > 0 ? $"dist={Distance:f2}/range={Range:f0}{(Distance > Range ? " 超出範圍" : "")}" : $"dist={Distance:f2}";
        return $"{status}, recast={(RecastActive ? "on" : "off")}, GCD={GCDRemaining:f2}, aLock={AnimationLock:f2}, cast={CastRemaining:f2}, moving={Moving}, moveBlocked={MovementBlocked}, {tgt}";
    }
}

public sealed class ManualActionQueueTweak(WorldState ws, AIHints hints, Func<ActionID, Actor?, ActionBlockInfo>? describeBlockers = null)
{
    private readonly record struct Entry(ActionID Action, Actor? Target, Vector3 TargetPos, Angle? FacingAngle, ActionDefinition Definition, DateTime ExpireAt, float CastTime)
    {
        public readonly bool Expired(DateTime now) => ExpireAt < now || (Target?.IsDestroyed ?? false);
    }

    private readonly ActionTweaksConfig _config = Service.Config.Get<ActionTweaksConfig>();
    private readonly List<Entry> _queue = [];
    private bool _emergencyMode;

    // 節流：同一支技能、同一組拒絕原因，EmergencyLogInterval 秒內只印一次。原因變了、換技能、
    // 或送出去／過期時立刻重置，所以「原因變了」與「恢復正常」都不會被吃掉；
    // 「卡了多久」則由 FlushEmergencyLog 的收尾行保住。
    private const double EmergencyLogInterval = 5;
    private ActionID _emergencyLogAction;
    private uint _emergencyLogKey;
    private DateTime _emergencyLogTime;
    private int _emergencySuppressed;

    public void RemoveExpired()
    {
        if (_emergencyMode && _queue[0].Expired(ws.CurrentTime))
        {
            Service.Log($"[MAO] Emergency {_queue[0].Action} expired");
            FlushEmergencyLog(_queue[0].Action, "expired");
            _emergencyMode = false;
        }

        bool checkExpired(Entry e)
        {
            if (e.Expired(ws.CurrentTime))
            {
                Service.Log($"[MAO] Action {e.Action} @ {e.Target} expired");
                return true;
            }
            return false;
        }
        _queue.RemoveAll(checkExpired);
    }

    public void FillQueue(ActionQueue queue)
    {
        if (_emergencyMode)
        {
            ref var entry = ref _queue.Ref(0);
            queue.Push(entry.Action, entry.Target, ActionQueue.Priority.ManualEmergency, 0, 0, 0, entry.TargetPos, entry.FacingAngle, true);
        }
        else
        {
            float expireOrder = 0; // we don't actually care about values, only ordering...
            foreach (ref var e in _queue.AsSpan())
                queue.Push(e.Action, e.Target, e.Definition.IsGCD ? ActionQueue.Priority.ManualGCD : ActionQueue.Priority.ManualOGCD, expireOrder++, 0, e.CastTime, e.TargetPos, e.FacingAngle, true);
        }
    }

    public bool Push(ActionID action, ulong targetId, float castTime, bool allowTargetOverride, Func<(ulong, Vector3?)> getAreaTarget, Func<ulong> targetNearest)
    {
        if (!_config.UseManualQueue)
            return false; // we don't use queue at all

        var player = ws.Party.Player();
        if (player == null)
            return false; // player is unknown, skip

        var def = ActionDefinitions.Instance[action];
        if (def == null)
            return false; // unknown action, let native queue handle it instead

        var isGCD = def.IsGCD;
        var expire = isGCD ? 1.0f : 3.0f;
        if (def.ReadyIn(ws.Client.Cooldowns, ws.Client.DutyActions) > expire)
            return false; // don't bother trying to queue something that's on cd

        if (!ResolveTarget(def, player, targetId, getAreaTarget, targetNearest, allowTargetOverride, out var target, out var targetPos))
            return false; // failed to resolve target

        var angleOverride = def.TransformAngle?.Invoke(ws, player, target, hints);

        var expireAt = ws.CurrentTime.AddSeconds(expire);
        var index = _queue.FindIndex(e => e.Definition.MainCooldownGroup == def.MainCooldownGroup); // TODO: what about alt groups and duty actions?..
        if (index < 0)
        {
            Service.Log($"[MAO] Queueing {action} @ {target}");
            _queue.Add(new(action, target, targetPos, angleOverride, def, expireAt, castTime));
            return true;
        }

        ref var e = ref _queue.Ref(index);
        if (e.Action != action || e.Target != target)
        {
            Service.Log($"[MAO] Replacing queued {e.Action} with {action} @ {target}");
            _queue.RemoveAt(index);
            _queue.Add(new(action, target, targetPos, angleOverride, def, expireAt, castTime));
        }
        else
        {
            // 這是「e.Action == action && e.Target == target」的 else，兩者可互換
            LogEmergency(action, target, def);
            // spamming oGCD - enter emergency mode
            _queue.Clear();
            _queue.Add(new(action, target, targetPos, angleOverride, def, expireAt, castTime));
            _emergencyMode = true;
        }
        return true;
    }

    public void Pop(ActionID action)
    {
        var index = _queue.FindIndex(e => e.Action == action);
        if (index >= 0)
        {
            Service.Log($"[MAO] Executed {action}");
            _queue.RemoveAt(index);
            FlushEmergencyLog(action, "executed");
        }

        if (_emergencyMode && index == 0)
            _emergencyMode = false;
    }

    private void LogEmergency(ActionID action, Actor? target, ActionDefinition def)
    {
        var info = describeBlockers?.Invoke(action, target);
        if (info != null)
            info = info.Value with { Range = def.Range };

        var key = info?.BlockKey ?? 0u;
        var now = ws.CurrentTime;
        var sameCause = action == _emergencyLogAction && key == _emergencyLogKey;
        if (sameCause && (now - _emergencyLogTime).TotalSeconds < EmergencyLogInterval)
        {
            ++_emergencySuppressed;
            return;
        }

        var repeats = sameCause ? $"（前 {(now - _emergencyLogTime).TotalSeconds:f0}s 內另有 {_emergencySuppressed} 次同因未印）" : "";
        _emergencyLogAction = action;
        _emergencyLogKey = key;
        _emergencyLogTime = now;
        _emergencySuppressed = 0;
        Service.Log($"[MAO] Entering emergency mode for {action}: {info?.Describe() ?? "拒絕原因不可得"}{repeats}");
    }

    // 有了節流之後「數 log 行數」不再等於「卡了多久」，所以結束時把吞掉的次數補報一行
    private void FlushEmergencyLog(ActionID action, string outcome)
    {
        if (_emergencySuppressed > 0)
            Service.Log($"[MAO] Emergency {action} {outcome}，期間另有 {_emergencySuppressed} 次同因未印");
        _emergencyLogAction = default;
        _emergencyLogKey = 0;
        _emergencySuppressed = 0;
    }

    private bool ResolveTarget(ActionDefinition def, Actor player, ulong targetId, Func<(ulong, Vector3?)> getAreaTarget, Func<ulong> targetNearest, bool allowSmartTarget, out Actor? target, out Vector3 targetPos)
    {
        target = null;
        targetPos = default;

        // ground targeted actions that must target specific objects
        if (def.ID.ID == (uint)BLM.AID.BetweenTheLines)
        {
            var playerLL = ws.Actors.FirstOrDefault(act => act.OwnerID == player.InstanceID && act.OID == 0x179);
            if (playerLL == null)
                return false;

            targetPos = playerLL.PosRot.XYZ();
            return true;
        }

        if (def.ID.ID == (uint)RPR.AID.Regress)
        {
            var playerGate = ws.Actors.FirstOrDefault(act => act.OwnerID == player.InstanceID && act.OID == 0x4C3);
            if (playerGate == null)
                return false;

            targetPos = playerGate.PosRot.XYZ();
            return true;
        }

        if (def.AllowedTargets.HasFlag(ActionTargets.Area))
        {
            // GT actions with range 0 must be cast on player - there are only a few of these (BLM leylines, PCT leylines, PCT PVP limit break)
            if (def.Range == 0)
            {
                targetPos = player.PosRot.XYZ();
                return true;
            }

            // ground-targeted actions have special targeting
            var (gtTarget, gtPos) = getAreaTarget();
            if (gtPos != null)
            {
                // auto cast at cursor
                targetPos = gtPos.Value;
                return true;
            }
            else if (gtTarget is not 0 and not 0xE0000000)
            {
                var t = ws.Actors.Find(gtTarget);
                if (t != null)
                {
                    // auto cast at target's position
                    targetPos = t.PosRot.XYZ();
                    return true;
                }
                return false; // if target isn't found in world, bail
            }
            else
            {
                return false; // manual targeting desired
            }
        }

        if (def.AllowedTargets == ActionTargets.Self)
        {
            // the action can only target player, don't bother with other logic...
            target = player;
            return true;
        }

        target = ws.Actors.Find(targetId);
        if (target == null && targetId is not 0u and not 0xE0000000)
            return false; // target is valid, but not found in world, bail... (TODO this shouldn't be happening really)

        // custom smart-targeting
        if (allowSmartTarget && _config.SmartTargets && def.SmartTarget != null)
            target = def.SmartTarget(ws, player, target, hints);

        // fallback: if requested, use native "target nearest" function to try to find a valid hostile target
        // this conditional ensures we don't get a false positive for holmgang (can target self or hostile) or phantom oracle invuln (can target ally, but not self)
        if (target == null && def.AllowedTargets.HasFlag(ActionTargets.Hostile) && !def.AllowedTargets.HasFlag(ActionTargets.Self))
        {
            target = ws.Actors.Find(targetNearest());
            return true;
        }

        // smart-targeting fallback: cast on self if target is not valid
        var targetInvalid = target == null || !def.AllowedTargets.HasFlag(ActionTargets.Hostile) && !target.IsAlly;
        if (targetInvalid && def.AllowedTargets.HasFlag(ActionTargets.Self))
            target = player;

        return true;
    }
}
