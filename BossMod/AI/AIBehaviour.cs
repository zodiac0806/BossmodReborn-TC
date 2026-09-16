using BossMod.Autorotation;
using BossMod.Pathfinding;
using System.Threading;

namespace BossMod.AI;

public record struct Targeting(AIHints.Enemy Target, float PreferredRange = 2.6f, Positional PreferredPosition = Positional.Any, bool PreferTanking = false);

// constantly follow master
sealed class AIBehaviour(AIController ctrl, RotationModuleManager autorot, Preset? aiPreset) : IDisposable
{
    public WorldState WorldState => autorot.Bossmods.WorldState;
    public float ForceMovementIn = float.MaxValue; // TODO: reconsider
    public Preset? AIPreset = aiPreset;
    private static readonly AIConfig _config = Service.Config.Get<AIConfig>();
    private readonly NavigationDecision.Context _naviCtx = new();
    private NavigationDecision _naviDecision;
    private bool _afkMode;
    private bool _followMaster; // if true, our navigation target is master rather than primary target - this happens e.g. in outdoor or in dungeons during gathering trash
    private WPos _masterPrevPos;
    private WPos _masterMovementStart;
    private DateTime _masterLastMoved;
    private DateTime _navStartTime; // if current time is < this, navigation won't start
    private WPos? _preDodgeAnchor; // position to try returning to once a forced dodge is over, if ReturnToPreDodgePosition is enabled
    private bool _wasForcedDodging;
    private DateTime _preDodgeAnchorExpiry;
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly Random random = new();
    private bool cancel; // used to cancel autorotation AI preset during async

    #region 深牢專用 preset 的執行期覆蓋

    private static readonly Global.DeepDungeon.AutoDDConfig _ddConfig = Service.Config.Get<Global.DeepDungeon.AutoDDConfig>();

    private Preset? _ddPreset;
    private string? _ddPresetResolvedFor;
    private bool _ddPresetMissingLogged;

    /// <summary>
    /// 深牢裡要改用的 preset；null＝沒設定，或設定的名字在 preset 庫裡找不到（照常用原本的）。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>執行期覆蓋</b>，刻意不去寫 <c>AIConfig.AIAutorotPresetName</c>：
    /// 寫設定會製造設定檔 churn，而且遊戲或外掛在深牢裡崩潰時會把使用者永久卡在切換後的狀態。
    /// 覆蓋只活在記憶體裡，離開深牢或重載外掛就自然消失。
    /// <para>
    /// ⚠️ <b>必須快取</b>：<c>PresetDatabase.AllPresets</c> 是屬性，每次存取都會
    /// <c>[.. DefaultPresets, .. UserPresets]</c> 配一個新的 List，而這裡是每幀都會走到的路徑。
    /// </para>
    /// <para>
    /// ⚠️ 快取只在<b>名字改變</b>時失效。preset 物件是不可變的（資料庫註解明講
    /// "presets in the database are immutable"，編輯會產生新物件），所以「改了 preset 內容但沒改名」
    /// 時這裡仍會拿著舊物件，直到名字變動或重新載入。為了不在每幀配置記憶體，這個取捨是刻意的。
    /// </para>
    /// </remarks>
    private Preset? ResolveDeepDungeonPreset()
    {
        var name = _ddConfig.DeepDungeonPreset;
        if (string.IsNullOrEmpty(name))
            return null;

        if (_ddPresetResolvedFor == name)
            return _ddPreset;

        _ddPresetResolvedFor = name;
        _ddPreset = null;

        var presets = autorot.Database.Presets.AllPresets;
        var count = presets.Count;
        for (var i = 0; i < count; ++i)
        {
            if (string.Equals(presets[i].Name, name, PresetDatabase.NameComparison))
            {
                _ddPreset = presets[i];
                _ddPresetMissingLogged = false;
                Service.Logger.Information($"[DD] 深牢期間改用循環預設「{name}」。");
                return _ddPreset;
            }
        }

        // 找不到＝設定裡的名字被改名或刪掉了。不是錯誤，維持原本的 preset。
        if (!_ddPresetMissingLogged)
        {
            _ddPresetMissingLogged = true;
            Service.Logger.Information($"[DD] 設定指定的深牢循環預設「{name}」不存在，維持原本的預設、不切換。");
        }
        return null;
    }

    #endregion

    public void Dispose()
    {
        cancel = true;
    }

    public async Task Execute(Actor player, Actor master)
    {
        if (await _semaphore.WaitAsync(0).ConfigureAwait(false))
        {
            try
            {
                ForceMovementIn = float.MaxValue;
                if (player.IsDead)
                    return;

                // keep master in focus
                if (_config.FocusTargetMaster)
                    FocusMaster(master);

                _afkMode = _config.AutoAFK && !master.InCombat && (WorldState.CurrentTime - _masterLastMoved).TotalSeconds > _config.AFKModeTimer;
                var gazeImminent = autorot.Hints.ForbiddenDirections.Count != 0 && autorot.Hints.ForbiddenDirections[0].activation <= WorldState.FutureTime(0.5d);
                var pyreticImminent = autorot.Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.Pyretic && autorot.Hints.ImminentSpecialMode.activation <= WorldState.FutureTime(1d);
                var misdirectionMode = autorot.Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.Misdirection && autorot.Hints.ImminentSpecialMode.activation <= WorldState.CurrentTime;
                var forbidTargeting = _config.ForbidActions || _afkMode || gazeImminent || pyreticImminent;
                var hadNavi = _naviDecision.Destination != null;

                Targeting target = default;
                if (!forbidTargeting && AIPreset != null && (!_config.ForbidAIMovementMounted || _config.ForbidAIMovementMounted && player.MountId == 0))
                {
                    target = SelectPrimaryTarget(player, master);
                    if (_config.ManualTarget)
                    {
                        var t = autorot.WorldState.Actors.Find(player.TargetID);
                        if (t != null)
                            target.Target = new AIHints.Enemy(t, 100, false);
                        else
                            target = default;
                    }
                    if (target.Target != null || TargetIsForbidden(player.TargetID))
                        autorot.Hints.ForcedTarget ??= target.Target?.Actor;
                    AdjustTargetPositional(player, ref target);
                }

                var followTarget = _config.FollowTarget;
                _followMaster = master != player;

                // note: if there are pending knockbacks, don't update navigation decision to avoid fucking up positioning
                if (player.PendingKnockbacks.Count == 0)
                {
                    var actorTarget = autorot.WorldState.Actors.Find(player.TargetID);
                    var naviDecision = followTarget && actorTarget != null
                        ? await BuildNavigationDecision(player, actorTarget, target).ConfigureAwait(false)
                        : await BuildNavigationDecision(player, master, target).ConfigureAwait(false);
                    _naviDecision = naviDecision;

                    // there is a difference between having a small positive leeway and having a negative one for pathfinding, prefer to keep positive
                    _naviDecision.LeewaySeconds = Math.Max(0, _naviDecision.LeewaySeconds - 0.1f);
                }

                var masterIsMoving = TrackMasterMovement(master);
                var moveWithMaster = masterIsMoving && _followMaster && master != player;
                ForceMovementIn = moveWithMaster || gazeImminent || pyreticImminent ? 0f : _naviDecision.LeewaySeconds;

                TrackPreDodgeAnchor(player, moveWithMaster || gazeImminent || pyreticImminent);

                if (_config.MoveDelay != 0d && !hadNavi && _naviDecision.Destination != null)
                    _navStartTime = WorldState.FutureTime(_config.MoveDelay);

                // 📌 這整段是否執行本身就是一個診斷缺口：它不執行時 autorot.Preset 完全不被碰，
                //    使用者手動掛上的預設會原封不動留著，於是「有沒有目標」那個判斷根本不在路徑上。
                //    2026-08-17 追一份實機 log 時就卡在這裡：同一次 Execute 裡 LogMovementOwnership
                //    印了 114 行、LogPresetGate 卻是 0 行（而它第一次呼叫必定會印，欄位是 bool? 起始 null）
                //    ⇒ 只能反推出這個 if 沒進來，但無從得知是被誰擋的。補一行讓下次直接看得出來。
                var presetGateRuns = !forbidTargeting && !cancel;
                LogPresetGateReachable(presetGateRuns, forbidTargeting, cancel);
                if (presetGateRuns)
                {
                    // 🔑 只掛預設，不影響走位/閃避/選目標——那些在這個判斷之外。
                    //    深牢判定用 DeepDungeon.DungeonId：它直接來自遊戲的深牢 instance content director
                    //    （不在深牢時是 None），不需要另外維護一份 territory 清單。
                    var inDeepDungeon = autorot.WorldState.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.None;
                    var autorotAllowed = !_config.AutorotOnlyInDeepDungeon || inDeepDungeon;
                    // 深牢裡改用指定的 preset；沒設定或找不到就退回原本的（不是錯誤）。
                    // 📌 與「只在深牢出招」的互動：深牢外 autorotAllowed 為 false ⇒ Preset 直接是 null，
                    //    這裡選了哪個 preset 都不影響，兩個開關同時開啟時語意是一致的。
                    var preset = inDeepDungeon ? ResolveDeepDungeonPreset() ?? AIPreset : AIPreset;
                    var hasTarget = target.Target != null;
                    LogPresetGate(hasTarget && autorotAllowed, hasTarget, autorotAllowed);
                    autorot.Preset = hasTarget && autorotAllowed ? preset : null;
                }
                UpdateMovement(player, master, target, gazeImminent || pyreticImminent, misdirectionMode ? autorot.Hints.MisdirectionThreshold : default, !forbidTargeting ? autorot.Hints.ActionsToExecute : null);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    // returns null if we're to be idle, otherwise target to attack
    private Targeting SelectPrimaryTarget(Actor player, Actor master)
    {
        // we prefer not to switch targets unnecessarily, so start with current target - it could've been selected manually or by AI on previous frames
        // if current target is not among valid targets, clear it - this opens way for future target selection heuristics
        var targetId = autorot.Hints.ForcedTarget?.InstanceID ?? player.TargetID;
        var target = autorot.Hints.PriorityTargets.FirstOrDefault(e => e.Actor.InstanceID == targetId);

        // if we don't have a valid target yet, use some heuristics to select some 'ok' target to attack
        // try assisting master, otherwise (if player is own master, or if master has no valid target) just select closest valid target
        target ??= master != player ? autorot.Hints.PriorityTargets.FirstOrDefault(t => master.TargetID == t.Actor.InstanceID) : null;
        target ??= autorot.Hints.PriorityTargets.MinBy(e => (e.Actor.Position - player.Position).LengthSq());

        // if the previous line returned no target, there aren't any priority targets at all - give up
        if (target == null)
            return default;

        // TODO: rethink all this... ai module should set forced target if it wants to switch... figure out positioning and stuff
        // now give class module a chance to improve targeting
        // typically it would switch targets for multidotting, or to hit more targets with AOE
        // in case of ties, it should prefer to return original target - this would prevent useless switches
        var targeting = new Targeting(target!, player.Role is Role.Melee or Role.Tank ? 2.6f : 24.5f);

        var pos = autorot.Hints.RecommendedPositional;
        if (pos.Target != null && targeting.Target.Actor == pos.Target)
            targeting.PreferredPosition = pos.Pos;

        return /*autorot.SelectTargetForAI(targeting) ??*/ targeting;
    }

    private void AdjustTargetPositional(Actor player, ref Targeting targeting)
    {
        if (targeting.Target == null || targeting.PreferredPosition == Positional.Any)
            return; // nothing to adjust

        if (targeting.PreferredPosition == Positional.Front)
        {
            // 'front' is tank-specific positional; no point doing anything if we're not tanking target
            if (targeting.Target.Actor.TargetID != player.InstanceID)
                targeting.PreferredPosition = Positional.Any;
            return;
        }

        // if target-of-target is player, don't try flanking, it's probably impossible... - unless target is currently casting (TODO: reconsider?)
        // skip if targeting a dummy, they don't rotate
        if (targeting.Target.Actor.TargetID == player.InstanceID && targeting.Target.Actor.CastInfo == null && targeting.Target.Actor.NameID != 541)
            targeting.PreferredPosition = Positional.Any;
    }

    // remembers the position player was standing at right before a forced dodge, so we can try to walk back to it once the dodge is over
    private void TrackPreDodgeAnchor(Actor player, bool forcedByExternalFactor)
    {
        if (!_config.ReturnToPreDodgePosition)
        {
            _preDodgeAnchor = null;
            _wasForcedDodging = false;
            return;
        }

        var forcedDodging = !forcedByExternalFactor && ForceMovementIn <= 0f && _naviDecision.Destination != null;
        if (forcedDodging && !_wasForcedDodging)
            _preDodgeAnchor = player.Position; // just started dodging - remember where we came from
        _wasForcedDodging = forcedDodging;

        if (_preDodgeAnchor == null)
            return;

        if (!forcedDodging && (player.Position - _preDodgeAnchor.Value).LengthSq() < 1f)
            _preDodgeAnchor = null; // arrived back, done
        else if (!forcedDodging && !IsAnchorSafe(_preDodgeAnchor.Value))
            _preDodgeAnchor = null; // spot became dangerous or is out of bounds - give up
        else if (_preDodgeAnchor != null && WorldState.CurrentTime > _preDodgeAnchorExpiry)
            _preDodgeAnchor = null; // took too long, give up and let normal positioning take over
        if (forcedDodging)
            _preDodgeAnchorExpiry = WorldState.FutureTime(_config.ReturnToPreDodgePositionTimeout);
    }

    // true for Gold Saucer minigames, where standing further into a safe zone than strictly necessary costs nothing meaningful (as opposed to dungeons/trials/raids, where positioning still needs to stay precise)
    private bool IsCasualContent() => autorot.Bossmods.ActiveModule?.Info?.GroupType == BossModuleInfo.GroupType.GoldSaucer;

    // true if pos is inside the arena bounds and not inside any (even future) forbidden zone
    private bool IsAnchorSafe(WPos pos)
    {
        if (!autorot.Hints.PathfindMapBounds.Contains(pos - autorot.Hints.PathfindMapCenter))
            return false;
        foreach (var z in autorot.Hints.ForbiddenZones)
            if (z.shapeDistance(pos) < 0f)
                return false;
        return true;
    }

    private async Task<NavigationDecision> BuildNavigationDecision(Actor player, Actor master, Targeting targeting)
    {
        if (_config.ForbidMovement || _config.ForbidAIMovementMounted && player.MountId != 0
            || autorot.Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.NoMovement && autorot.Hints.ImminentSpecialMode.activation <= WorldState.FutureTime(1d))
            return new() { LeewaySeconds = float.MaxValue };

        // 📌 下面那行的「玩家座標 × 亂數」看起來像筆誤，**已核對上游同形，主體刻意不改**。
        //    upstream/main 的同一段是 `new WPos(pos.X * randomO1, pos.Z * randomO2)`（只多存了一個
        //    pos 區域變數），來歷是上游 7e4a09070（2025-05-03，同一顆把這個閘門從 0.5s 放寬到 2s）。
        //    【為什麼照舊可用】這個欄位的語意是**方向**不是座標（AIHints.ForcedMovement 的註解：
        //    「character will move in specified direction」），而 MovementOverride.DirectionToDestination
        //    只取 Angle.FromDirection(X, Z)、長度整個丟掉 ⇒ 乘出來的值實際效果仍是「每幀換一個亂數
        //    方向」，冰結需要的「持續移動」有被滿足。
        //    ✅ 唯一真正會壞的退化情形（玩家站在世界原點 ⇒ 零向量 ⇒ 原地不動）已在下方單獨擋下，
        //    非退化情形一律與上游逐位元相同。
        if (autorot.Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.Freezing && autorot.Hints.ImminentSpecialMode.activation <= WorldState.FutureTime(2.1d))
        {
            var randomO1 = random.NextSingle() * 2f - 1f;
            var randomO2 = random.NextSingle() * 2f - 1f;
            var dir = new WPos(player.Position.X * randomO1, player.Position.Z * randomO2).ToVec3();
            // 🔴 退化防護：上面那個乘法在玩家剛好站在世界原點（X==0 且 Z==0）時會得到零向量，
            //    而 MovementOverride.DirectionToDestination 對 `DesiredDirection.Value == default` 是 `return null`
            //    ＝AI 完全不動；同時 AIController.Update 又只在 `ForcedMovement == null` 時才接手，
            //    所以寫進去的零向量會把平常的自動移動也一起壓掉 —— 正好是冰結期間最不該發生的事。
            //    這個欄位的語意是**方向**不是座標（AIHints.ForcedMovement：「character will move in specified
            //    direction」），DirectionToDestination 也只取 Angle.FromDirection(X, Z)、長度整個丟掉，
            //    所以退回純亂數方向與原意（每幀換一個亂數方向）完全一致。
            // ⚠️ 刻意只擋「剛好等於 default」這一個情形：其餘一律照舊，與上游（同形，來歷 7e4a09070）
            //    逐位元相同，不製造新的分岔。
            if (dir == default)
                dir = new WDir(randomO1, randomO2) is var d && d != default ? d.ToVec3() : new WDir(1f, default).ToVec3();
            autorot.Hints.ForcedMovement = dir;
            return new() { LeewaySeconds = float.MaxValue };
        }

        Actor? forceDestination = null;
        var interactTarget = autorot.Hints.InteractWithTarget;
        if (interactTarget != null)
            forceDestination = interactTarget;
        else if (_followMaster)
        {
            forceDestination = master;
        }

        // 🔴 `master != player` 這個條件不能漏。初次判定（Execute 裡的 `_followMaster = master != player`）
        //    有它，但這裡重算時原本沒有 —— 於是 solo 時（master 就是自己）只要在戰鬥中移動超過 10y，
        //    這裡就會把 _followMaster 重新設成 true，接著下面的分支拿「master」當跟隨目標，
        //    等於在自己腳下種一個權重 1.0 的目標點（GoalSingleTarget(master, ...)）。
        //    它會壓過所有小權重的走位提示（例如風箏的 0.05），表現成「AI 一直想站回原地」而不報錯。
        _followMaster = master != player && interactTarget == null && (_config.FollowDuringCombat || !master.InCombat || (_masterPrevPos - _masterMovementStart).LengthSq() > 100f) && (_config.FollowDuringActiveBossModule || autorot.Bossmods.ActiveModule?.StateMachine.ActiveState == null) && (_config.FollowOutOfCombat || master.InCombat);

        var forbiddenZoneCushion = _config.PreferredDistance + (IsCasualContent() ? _config.CasualSafetyMargin : 0f);

        // while actually dodging something, prefer moving behind the target over its front/flanks, and avoid simply backing away from it;
        // this is only a tie-breaker among otherwise equally-safe cells, so it never overrides actual AOE safety
        // 🔑 有模組正在風箏時不要加「別後退」的懲罰：那一項每遠離 1y 約 −0.5，
        //    而風箏的目標區只有 0.05，兩者放在同一個權重場裡風箏必然被碾平，
        //    而且是靜默的（使用者只看到「開了風箏但角色不退」）。
        //    只拿掉這個偏好項，「躲到目標背後」與所有實際閃避判定都不受影響。
        if (autorot.Hints.ForbiddenZones.Count != 0 && targeting.Target != null)
            autorot.Hints.GoalZones.Add(autorot.Hints.GoalDodgeDirection(targeting.Target.Actor, player.Position, penalizeRetreat: !autorot.Hints.WantKiting));

        if (_followMaster)
        {
            if (forceDestination != null && forceDestination.OID != master.OID && autorot.Hints.PathfindMapBounds.Contains(forceDestination.Position - autorot.Hints.PathfindMapCenter))
            {
                autorot.Hints.GoalZones.Add(autorot.Hints.GoalProximity(forceDestination, 3.5f, 100f));
            }
            var target = autorot.WorldState.Actors.Find(player.TargetID);
            var masterInBounds = !_config.StayWithinArenaBounds || autorot.Hints.PathfindMapBounds.Contains(master.Position - autorot.Hints.PathfindMapCenter);
            if (!_config.FollowTarget || _config.FollowTarget && target == null)
            {
                if (masterInBounds)
                    autorot.Hints.GoalZones.Add(autorot.Hints.GoalSingleTarget(master, Positional.Any, _config.FollowTarget && player.InCombat ? _config.MaxDistanceToTarget : _config.MaxDistanceToSlot));
            }
            else if (_config.FollowTarget && target != null && AIPreset == null)
            {
                var positional = _config.DesiredPositional;
                var mindist = _config.MinDistance;
                var maxdist = _config.MaxDistanceToTarget;
                if (positional is Positional.Rear or Positional.Flank && (target.CastInfo == null && target.NameID != 541u && target.TargetID == player.InstanceID || target.Omnidirectional)) // if player is target, rear/flank is usually impossible unless target is casting
                    positional = Positional.Any;
                if (masterInBounds)
                    autorot.Hints.GoalZones.Add(autorot.Hints.GoalSingleTarget(master, positional, positional != Positional.Any ? 2.6f : maxdist));

                if (mindist != default && target.InstanceID != player.InstanceID && interactTarget == null)
                {
                    var hitboxradius = target.HitboxRadius;
                    var maxAdj = hitboxradius + maxdist;
                    var min = hitboxradius + mindist;
                    var max = maxAdj > min ? maxAdj : min + 1f;
                    autorot.Hints.GoalZones.Add(autorot.Hints.GoalDonut(target.Position, min, max, 2f));
                }
            }
            if (_preDodgeAnchor != null && !_wasForcedDodging)
                autorot.Hints.GoalZones.Add(autorot.Hints.GoalProximity(_preDodgeAnchor.Value, 3f, 1.5f));
            // 診斷用：記下丟進尋路的那一刻有幾個目標區（Task.Run 之後主執行緒就會把 hints 清掉了）
            _naviGoalZoneCount = autorot.Hints.GoalZones.Count;
            return await Task.Run(() => NavigationDecision.Build(_naviCtx, WorldState, autorot.Hints, player, autorot.Bossmods.WorldState.Client.MoveSpeed, forbiddenZoneCushion: forbiddenZoneCushion, avoidFutureAOEs: _config.AvoidFutureAOEs, activationTimeCushion: _config.ActivationTimeCushion)).ConfigureAwait(false);
        }

        // TODO: remove this once all rotation modules are fixed
        if (autorot.Hints.GoalZones.Count == 0 && targeting.Target != null)
            autorot.Hints.GoalZones.Add(autorot.Hints.GoalSingleTarget(targeting.Target.Actor, targeting.PreferredPosition, targeting.PreferredRange));
        if (_preDodgeAnchor != null && !_wasForcedDodging)
            autorot.Hints.GoalZones.Add(autorot.Hints.GoalProximity(_preDodgeAnchor.Value, 3f, 1.5f));
        // 診斷用：見上面同名的那一行
        _naviGoalZoneCount = autorot.Hints.GoalZones.Count;
        return await Task.Run(() => NavigationDecision.Build(_naviCtx, WorldState, autorot.Hints, player, autorot.Bossmods.WorldState.Client.MoveSpeed, forbiddenZoneCushion, avoidFutureAOEs: _config.AvoidFutureAOEs, activationTimeCushion: _config.ActivationTimeCushion)).ConfigureAwait(false);
    }

    private void FocusMaster(Actor master)
    {
        var masterChanged = Service.TargetManager.FocusTarget?.EntityId != master.InstanceID;
        if (masterChanged)
        {
            ctrl.SetFocusTarget(master);
            _masterPrevPos = _masterMovementStart = master.Position;
            _masterLastMoved = WorldState.CurrentTime.AddSeconds(-1d);
        }
    }

    private bool TrackMasterMovement(Actor master)
    {
        // keep track of master movement
        // idea is that if master is moving forward (e.g. running in outdoor or pulling trashpacks in dungeon), we want to closely follow and not stop to cast
        var masterIsMoving = true;
        if (master.Position != _masterPrevPos)
        {
            _masterLastMoved = WorldState.CurrentTime;
            _masterPrevPos = master.Position;
        }
        else if ((WorldState.CurrentTime - _masterLastMoved).TotalSeconds > 0.5d)
        {
            // master has stopped, consider previous movement finished
            _masterMovementStart = _masterPrevPos;
            masterIsMoving = false;
        }
        // else: don't consider master to have stopped moving unless he's standing still for some small time

        return masterIsMoving;
    }

    #region 移動為什麼沒發生 —— 三個狀態轉換診斷

    // 📌 三支都走 Information（使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒），而且**只在狀態翻轉時記一行**：
    //    這些判斷每幀都會走到，每幀印等於把 log 洗掉。
    // 🔑 三行合起來可以把「角色不動」拆成互斥的三種原因，不需要實機旁觀就能定案：
    //      ① 預設集根本沒掛上（沒有主要目標）⇒ 自動移動模組整段不執行
    //      ② 預設集掛上了、AI 也讓位了，但自動移動模組沒寫出方向
    //      ③ 沒人讓位，AI 自己有目標區卻算不出目的地（尋路回 null）

    private bool? _loggedPresetGate;

    /// <summary>
    /// 把「自動循環預設被掛上／卸下」講出來。這是<b>趕路時什麼都沒發生</b>的頭號嫌疑。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>Preset</c> 被卸下時 <c>RotationModuleManager</c> 會 Dispose 掉預設集裡的所有模組，
    /// 包含「自動移動」——於是它的 <c>Execute</c> 整段不跑：<b>不寫 ForcedMovement，也不產生
    /// 移動目標視覺化</b>。使用者看到的就是「站著不動、連提示線都沒有」。
    /// ⚠️ 脫戰趕路時場上的被動怪在 <c>AIHintsBuilder.FillEnemies</c> 只拿到
    /// <c>PriorityUndesirable</c>（-3），進不了 <c>AIHints.PriorityTargets</c>
    /// ⇒ <c>SelectPrimaryTarget</c> 選不到任何目標 ⇒ 這個閘門必然關著。
    /// </remarks>
    private void LogPresetGate(bool on, bool hasTarget, bool autorotAllowed)
    {
        if (_loggedPresetGate == on)
            return;
        _loggedPresetGate = on;
        if (on)
        {
            Service.Logger.Information("[AI] 自動循環預設已掛上（有主要目標且允許出招）：預設集裡的模組從現在起每幀執行，「自動移動」模組會開始寫移動方向與標線。");
        }
        else
        {
            var why = !hasTarget
                ? "沒有主要目標（脫戰趕路時場上的被動怪優先度是 -3，進不了 PriorityTargets）"
                : "目前不允許出招（只在深牢出招的開關擋住）";
            Service.Logger.Information($"[AI] 自動循環預設已卸下：{why}。⚠️ 這代表預設集裡的「自動移動」模組**整段不執行** —— 不寫移動方向、也不畫移動目標標線，趕路完全交給 AI 自動走位處理。");
        }
    }

    private bool? _loggedPresetGateReachable;

    /// <summary>
    /// 講出「掛／卸預設的那個判斷這一段到底有沒有被執行」。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="LogPresetGate"/> 沒印任何東西時有兩種完全不同的意思，而它們的修法相反：
    /// <list type="number">
    /// <item>閘門有跑、狀態一直沒變 —— 但這不可能靜默，<c>_loggedPresetGate</c> 是 <c>bool?</c>
    /// 起始 <c>null</c>，<b>第一次呼叫必定會印</b>。</item>
    /// <item>閘門<b>整段沒被執行</b>（<c>ForbidActions</c>／AFK／凝視／Pyretic／<c>cancel</c> 任一成立）
    /// ⇒ <c>autorot.Preset</c> 這一段完全不被碰，使用者手動掛的預設原封不動留著，
    /// 「有沒有主要目標」那個判斷根本不在執行路徑上。</item>
    /// </list>
    /// 2026-08-17 查「preset 開著卻不迴避」時就卡在這個分辨上：同一次 <c>Execute</c> 裡
    /// <see cref="LogMovementOwnership"/> 印了 114 行、<see cref="LogPresetGate"/> 0 行，
    /// 只能反推出是第 2 種，但無從得知是被哪個開關擋的。
    /// 📌 走 <c>Information</c>：使用者的 LogLevel 是 1。只在翻轉時印。
    /// </remarks>
    private void LogPresetGateReachable(bool runs, bool forbidTargeting, bool cancelled)
    {
        if (_loggedPresetGateReachable == runs)
            return;
        _loggedPresetGateReachable = runs;
        if (runs)
        {
            Service.Logger.Information("[AI] 預設掛載判斷恢復執行：從現在起由 AI 依「有沒有主要目標」決定要不要掛上自動循環預設。");
        }
        else
        {
            var why = cancelled ? "AI 正在停用/切換預設（cancel）" : "禁止出招／AFK／凝視或 Pyretic 將至（forbidTargeting）";
            Service.Logger.Information($"[AI] 預設掛載判斷這一段不執行：{why}。⚠️ 這代表 autorot.Preset **完全不被 AI 碰** —— 手動掛上的預設會原封不動繼續跑，「沒有主要目標就卸下預設」那條規則在這個狀態下不成立。走位與閃避不受這裡影響（UpdateMovement 在這個判斷之外）。");
        }
    }

    private bool _loggedYieldMovement;
    /// <summary>兩個方向的長說明各印過了沒；印過之後只留短行。</summary>
    private bool _explainedYieldMovement;
    private bool _explainedTakeBackMovement;

    /// <summary>
    /// 把「AI 把移動讓給預設集的自動移動模組」講出來。
    /// </summary>
    /// <remarks>
    /// 🔴 讓位期間本 AI <b>完全不設</b> <c>NaviTargetPos</c>。若角色同時也不動，代表接手的那一方
    /// 也沒有寫出方向 —— 那就是「兩邊都不動」的殭屍狀態，必須看這一行才分得出來是誰的問題。
    /// <para>⚠️ 換手若只是因為「沒有人要求移動」（<see cref="Autorotation.MiscAI.NormalMovement.IdleNoDestination"/>）就走 <c>Verbose</c>：那是閒置常態，而且對應的 <c>[NormalMovement]</c> 說明行也降了同一級。</para>
    /// </remarks>
    private void LogMovementOwnership(bool yield)
    {
        if (yield == _loggedYieldMovement)
            return;
        _loggedYieldMovement = yield;
        // 長說明只印第一次：翻轉本身是有用的訊號（實機兩天約 3,150 行），但每次都重印
        // 整段兩百字的說明純粹是雜訊。兩個方向各記一個旗標，兩邊的說明都至少出現一次。
        bool explain;
        if (yield)
        {
            explain = !_explainedYieldMovement;
            _explainedYieldMovement = true;
        }
        else
        {
            explain = !_explainedTakeBackMovement;
            _explainedTakeBackMovement = true;
        }
        var msg = yield
            ? (explain ? "[AI] 移動擁有權交給預設集的「自動移動」模組：本 AI 這段期間完全不設導航目標。若角色同時站著不動，代表接手的那一方也沒寫出移動方向。" : "[AI] 移動擁有權交給預設集的「自動移動」模組")
            : (explain ? "[AI] 移動擁有權回到 AI 自動走位：預設集裡的「自動移動」模組沒有舉手（不在預設集裡、Destination 軌設成 None、或它這一段算不出目的地而主動交還——後者上一行會有同級的 [NormalMovement] 說明）。" : "[AI] 移動擁有權回到 AI 自動走位");
        if (Autorotation.MiscAI.NormalMovement.IdleNoDestination)
            Service.Logger.Verbose(msg);
        else
            Service.Logger.Information(msg);
    }

    /// <summary>建導航決策那一刻場上有幾個目標區；給下面那支診斷區分「沒人給方向」與「給了方向但算不出來」。</summary>
    private int _naviGoalZoneCount;
    private bool _loggedNoDestination;

    /// <summary>
    /// 把「有人給了目標區，尋路卻算不出目的地」講出來 —— 這正是趕路「走幾步就停」的形狀。
    /// </summary>
    /// <remarks>
    /// <c>NavigationDecision.Build</c> 在最佳格就是玩家腳下時回傳 <c>Destination == null</c>，
    /// 而那有兩種完全不同的成因：<b>真的已經站在最好的位置</b>，或<b>目標區根本沒被 rasterize</b>
    /// （玩家格出了尋路視窗、或本地副本與存活 List 的 race）。有目標區卻回 null 就是後者的徵兆。
    /// 🔴 2026-09-04 實機修正：上面最後那句判準寫太寬，而且「權重全 0 就是沒有 rasterize」
    /// 也是錯的。六份實機 log 共 580 筆命中，分組之後是：317 筆玩家真的就站在最高權重格、
    /// 20 筆是安全優先閘門（已畫上權重場＝否）—— 這 337 筆都是正常結果；
    /// 189 筆已畫上權重場＝是、整張場卻還是 0（畫了等於沒畫）；54 筆場上有更高的格子卻回 null
    /// —— 後面這 243 筆才是異常。判準見 <see cref="IsNaviStuck"/>。
    /// </remarks>
    private void LogNoDestination(bool evaluated, bool stuck)
    {
        // 🔴 讓位期間「連問都沒問」——本 AI 的導航決策這一段根本不會被使用，
        //    把它當成 false 會印出「導航恢復」，而那是**假的**：實機 2026-08-13 23:20:26.800
        //    那一行「導航恢復」與同一毫秒的「移動擁有權交給…」是同一件事，尋路什麼都沒改變。
        //    不知道的時候就什麼都不宣稱，維持上一次的判斷。
        //    （同款陷阱與同款解法見 MovementOverride.LogFollowPathYield。）
        if (!evaluated)
            return;
        if (stuck == _loggedNoDestination)
            return;
        _loggedNoDestination = stuck;
        Service.Logger.Information(stuck
            ? $"[AI] 導航算不出目的地：丟進尋路時場上有 {_naviGoalZoneCount} 個目標區，尋路卻回報「已經在最佳位置」⇒ 這一段不會移動。{_naviDecision.DiagSummary()}"
            : "[AI] 導航恢復：尋路重新算得出目的地。");
    }

    /// <summary>
    /// 尋路回 <c>null</c> 是不是<b>真的</b>異常。
    /// </summary>
    /// <remarks>
    /// 三個判準，任一成立就算異常：
    /// ①玩家格根本不在尋路視窗內 ⇒ 目標區對他完全沒有作用。
    /// ②目標區畫上去了，整張權重場卻還是 0（或更低）⇒ 畫了等於沒畫。
    /// ③場上有權重更高的格子，尋路卻回 null ⇒ 明明有更好的位置卻不去。
    /// 其餘情形（玩家格權重就是場上最高，而且那個最高是正的）都是<b>正確行為</b>，不該報。
    /// 🔴 ②一定要帶上 <c>DiagGoalsRasterized</c>：沒有 rasterize 而權重全 0 是
    /// 「詠唱中／玩家腳下即將被打到」的安全優先閘門（見 <c>NavigationDecision.Build</c>），
    /// 那是刻意行為，不是異常（實機 20 筆）。只判權重是 0 會把它們一起報出來。
    /// 📌 <c>Map.MaxPriority</c> 的初值就是 <c>0f</c>，所以②用 <c>&lt;= 0f</c> 是對
    /// 「從來沒有被抬高過」的精確比對，不需要 epsilon —— 用寬的 epsilon 會把「玩家站在一個
    /// 小的、真實存在的最高點」（實機看得到 0.10）誤報成異常。
    /// </remarks>
    private static bool IsNaviStuck(in NavigationDecision navi)
        => !navi.DiagPlayerInWindow
        || (navi.DiagGoalsRasterized && navi.DiagMaxPriority <= 0f)
        || navi.DiagMaxPriority > navi.DiagPlayerPriority + 1e-4f;

    #endregion

    private void UpdateMovement(Actor player, Actor master, Targeting target, bool gazeOrPyreticImminent, Angle misdirectionAngle, ActionQueue? queueForSprint)
    {
        // 🔴 讓路：預設集裡的「自動移動」模組正在負責移動時，這裡完全不做自己的移動決策。
        //    兩邊各自算目的地、逐幀交替接管，使用者看到的就是角色抖動（見 NormalMovement.OwnsMovement）。
        //    ⚠️ 只讓出「移動」這一項：目標選擇、InteractWithTarget、凝視／Pyretic 的中斷詠唱、
        //    衝刺推送全部照舊，所以這裡不是提早 return，而是只把 NaviTargetPos 壓成 null。
        //    讓出後由 NormalMovement 單獨寫 Hints.ForcedMovement ⇒ 移動只有一個擁有者。
        var yieldMovement = Autorotation.MiscAI.NormalMovement.OwnsMovement;
        LogMovementOwnership(yieldMovement);

        if (gazeOrPyreticImminent)
        {
            // gaze or pyretic imminent, drop any movement - we should have moved to safe zone already...
            ctrl.NaviTargetPos = null;
            ctrl.NaviTargetVertical = null;
            ctrl.ForceCancelCast = true;
        }
        else if (misdirectionAngle != default && _naviDecision.Destination is WPos destination)
        {
            ctrl.AllowInterruptingCastByMovement = true;
            // 🔴 這一支原本完全不碰 ForceCancelCast，於是上面那一支（凝視／Pyretic）設下的 true 會殘留下來：
            //    ctrl 這個欄位只在 AI 被停用時才走 AIController.Clear()，AI 跑著的時候**沒有任何逐幀重置點**，
            //    唯一的重置就在下面的 else。迷惑（Components.TemporaryMisdirection）與凝視可以同時存在
            //    ——ImminentSpecialMode 與 ForbiddenDirections 是兩個獨立來源，例：Ker 的 EternalDamnation——
            //    凝視過去了、迷惑還在時就會落到這一支 ⇒ 旗標卡在 true，之後每一幀只要在詠唱就被取消，
            //    直到迷惑結束掉進 else 為止。行為上不會崩，只會「該詠唱的都被莫名打斷」。
            //    進得到這一支就代表凝視／Pyretic 不迫在眉睫（上一支優先且會 return 掉這個 if 鏈），
            //    所以這裡的正解一定是 false；三支都指派之後 UpdateMovement 對這個旗標才是全覆蓋的。
            //    ⚠️ 這不會吃掉模組自己提出的取消請求：AIController 是用 |= 疊到 Hints 上，不是覆寫。
            ctrl.ForceCancelCast = false;
            var dir = destination - player.Position;
            var distSq = dir.LengthSq();
            var threshold = 45f.Degrees();
            var forceddir = WorldState.Client.ForcedMovementDirection;
            var allowMovement = forceddir.AlmostEqual(Angle.FromDirection(dir), threshold.Rad);
            if (allowMovement)
                allowMovement = CalculateUnobstructedPathLength(forceddir) >= Math.Min(3f, distSq);
            ctrl.NaviTargetPos = !yieldMovement && allowMovement && distSq >= 0.01f ? destination : null;

            float CalculateUnobstructedPathLength(Angle dir)
            {
                var start = _naviCtx.Map.WorldToGrid(player.Position);
                var startx = start.x;
                var starty = start.y;
                if (!_naviCtx.Map.InBounds(startx, starty))
                    return 0f;

                var end = _naviCtx.Map.WorldToGrid(player.Position + 100f * dir.ToDirection());
                var startG = _naviCtx.Map.PixelMaxG[_naviCtx.Map.GridToIndex(startx, starty)];
                var pixels = _naviCtx.Map.EnumeratePixelsInLine(startx, starty, end.x, end.y);
                var len = pixels.Length;
                for (var i = 0; i < len; ++i)
                {
                    ref readonly var p = ref pixels[i];
                    var px = p.x;
                    var py = p.y;
                    if (!_naviCtx.Map.InBounds(px, py) || _naviCtx.Map.PixelMaxG[_naviCtx.Map.GridToIndex(px, py)] < startG)
                    {
                        var dest = _naviCtx.Map.GridToWorld(px, py, 0.5f, 0.5f);
                        return (dest - player.Position).LengthSq();
                    }
                }
                return float.MaxValue;
            }

            // debug
            //void drawLine(WPos from, WPos to, uint color) => Camera.Instance!.DrawWorldLine(new(from.X, player.PosRot.Y, from.Z), new(to.X, player.PosRot.Y, to.Z), color);
            //var toDest = _naviDecision.Destination.Value - player.Position;
            //drawLine(player.Position, _naviDecision.Destination.Value, Colors.Safe);
            //drawLine(_naviDecision.Destination.Value, _naviDecision.Destination.Value + toDest.Normalized().OrthoL(), Colors.Safe);
            //drawLine(player.Position, ctrl.NaviTargetPos.Value, Colors.Danger);
        }
        else
        {
            var toDest = _naviDecision.Destination != null ? _naviDecision.Destination.Value - player.Position : default;
            var distSq = toDest.LengthSq();

            // avoid relocating the instant a marginally "safer" spot appears when there's no real urgency yet (see MovementUrgencyThreshold);
            // still move immediately if that's actually needed to stay in range of whatever we're following
            var mustMoveNow = _config.MovementUrgencyThreshold <= 0f || ForceMovementIn <= _config.MovementUrgencyThreshold;
            if (!mustMoveNow)
            {
                var followActor = target.Target?.Actor ?? master;
                var maxRange = target.Target != null ? _config.MaxDistanceToTarget : _config.MaxDistanceToSlot;
                mustMoveNow = followActor != player && (followActor.Position - player.Position).LengthSq() > maxRange * maxRange;
            }

            // 只在「我方負責移動」時才有意義：讓位期間目的地本來就不該由這裡產生 ⇒ 那時是「沒問」不是「沒事」。
            // 收緊判準：只有「回 null」加「場上有目標區」還不夠。六份實機 log 的 580 筆命中裡，
            // 337 筆是玩家真的就站在最高權重格、或是安全優先閘門，兩種都是正常結果。
            LogNoDestination(!yieldMovement, _naviDecision.Destination == null && _naviGoalZoneCount != 0 && IsNaviStuck(in _naviDecision));

            ctrl.NaviTargetPos = !yieldMovement && WorldState.CurrentTime >= _navStartTime && mustMoveNow ? _naviDecision.Destination : null;
            ctrl.NaviTargetVertical = master != player ? master.PosRot.Y : null;
            ctrl.AllowInterruptingCastByMovement = player.CastInfo != null && _naviDecision.LeewaySeconds <= player.CastInfo.RemainingTime - 0.5d;
            ctrl.ForceCancelCast = false;

            //var cameraFacing = _ctrl.CameraFacing;
            //var dot = cameraFacing.Dot(_ctrl.TargetRot.Value);
            //if (dot < -0.707107f)
            //    _ctrl.TargetRot = -_ctrl.TargetRot.Value;
            //else if (dot < 0.707107f)
            //    _ctrl.TargetRot = cameraFacing.OrthoL().Dot(_ctrl.TargetRot.Value) > 0 ? _ctrl.TargetRot.Value.OrthoR() : _ctrl.TargetRot.Value.OrthoL();

            // sprint, if not in combat and far enough away from destination
            if (player.InCombat ? _naviDecision.LeewaySeconds <= 0f && distSq > 25f : player != master && distSq > 400f)
            {
                queueForSprint?.Push(ActionDefinitions.IDSprint, player, ActionQueue.Priority.Minimal + 100f);
            }
        }
    }

    private bool TargetIsForbidden(ulong actorId) => autorot.Hints.ForbiddenTargets.Any(e => e.Actor.InstanceID == actorId);
}
