using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BossMod.Stormblood.Extreme.Ex5Rathalos;

// 契約:AIHints.SetPriority 只影響 BossMod 自己的 AI 模式選敵,對 RotationSolverReborn 完全無效
// (RSR 有自己的選敵邏輯),所以要真的改變 RSR 打誰只能走它的 IPC。
// 軟相依:RSR 沒裝或呼叫失敗一律靜默略過,絕不讓它弄壞模組本身。
// 🔴 這是對別的外掛的全域狀態單向寫入 —— 每一筆 AddBlacklist/Add 都必須有對應的移除路徑,
// 而 BossComponent 沒有卸載回呼 ⇒ 收尾由 Ex5Rathalos.Dispose() 負責(見檔尾)。
static class RSRPriority
{
    private static ICallGateSubscriber<uint, object>? Gate(string method)
        => Service.PluginInterface.GetIpcSubscriber<uint, object>($"RotationSolverReborn.{method}");

    private static bool _loggedUnavailable;

    private static void Invoke(string method, uint nameId, string? unavailableHint)
    {
        try
        {
            Gate(method)?.InvokeAction(nameId);
        }
        catch (IpcError)
        {
            if (unavailableHint != null && !_loggedUnavailable)
            {
                _loggedUnavailable = true;
                Service.Logger.Information($"[Ex5Rathalos] RotationSolverReborn IPC 不可用,{unavailableHint}");
            }
        }
        catch (Exception ex)
        {
            Service.Logger.Information($"[Ex5Rathalos] {method} 失敗(已忽略): {ex}");
        }
    }

    public static void Add(uint nameId) => Invoke("AddPriorityNameID", nameId, "無法把加魯拉設成優先目標,請手動集火。");
    public static void Remove(uint nameId) => Invoke("RemovePriorityNameID", nameId, null);

    // 只加優先度不會讓 RSR 從已經咬住的目標換開(優先度只是讓目標不被濾掉);尾巴是掛在同一具
    // 本體上的 Part,光靠優先度永遠搶不到注意力 ⇒ 要真的換目標只能把本體整個排除。
    public static void AddBlacklist(uint nameId) => Invoke("AddBlacklistNameID", nameId, "無法在砍尾巴時把本體排除,請手動切目標。");
    public static void RemoveBlacklist(uint nameId) => Invoke("RemoveBlacklistNameID", nameId, null);
}

class Mangle(BossModule module) : Components.SimpleAOEs(module, (uint)AID.MangleVisual, new AOEShapeCone(10f, 60.Degrees()));
class GarulaRush(BossModule module) : Components.ChargeAOEs(module, (uint)AID.GarulaRush, 4f);
class Lullaby(BossModule module) : Components.SimpleAOEs(module, (uint)AID.Lullaby, 3.7f);
class HeadButt(BossModule module) : Components.SimpleAOEs(module, (uint)AID.HeadButt, new AOEShapeRect(4.92f, 1.5f));

class Rush(BossModule module) : Components.GenericAOEs(module)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == (uint)AID.RushVisual1 || spell.Action.ID == (uint)AID.RushVisual2)
        {
            var delay = spell.Action.ID == (uint)AID.RushVisual1 ? 0.6f : 1.3f;

            var chargeDir = spell.LocXZ - caster.Position;
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeRect(chargeDir.Length() + 6, 4.5f), caster.Position, chargeDir.ToAngle(), Module.CastFinishAt(spell, delay)));
        }
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == (uint)AID.Rush1 || spell.Action.ID == (uint)AID.Rush2)
            _aoe.Clear();
    }
}

class TailSwing(BossModule module) : Components.GenericAOEs(module, (uint)AID.TailSwingVisual)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeCone(11, 90.Degrees()), caster.Position, spell.Rotation - 90.Degrees(), WorldState.FutureTime(1.9f)));
        }

        if (spell.Action.ID == (uint)AID.TailSwing)
            _aoe.Clear();
    }
}

class SweepingFlames(BossModule module) : Components.GenericAOEs(module, (uint)AID.SweepingFlamesVisual)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeCone(11, 60.Degrees()), caster.Position, spell.Rotation, WorldState.FutureTime(1.5f)));
        }

        if (spell.Action.ID == (uint)AID.SweepingFlames)
            _aoe.Clear();
    }
}

class Mangle2(BossModule module) : Components.GenericAOEs(module, (uint)AID.Mangle2Visual)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeCone(9, 45.Degrees()), caster.Position, spell.Rotation, Module.CastFinishAt(spell, 0.6f)));
        }
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == (uint)AID.Mangle2)
            _aoe.Clear();
    }
}

class FireballStack1(BossModule module) : Components.StackWithCastTargets(module, (uint)AID.FireballBossFirst, 5);
class FireballStack2(BossModule module) : Components.StackWithCastTargets(module, (uint)AID.FireballBossRest, 5);
class FirePuddle(BossModule module) : Components.VoidzoneAtCastTarget(module, 5, (uint)AID.FireballFirst, m => m.Enemies(OID.Fireball).Where(e => e.EventState != 7), 0.5d)
{
    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        base.OnEventCast(caster, spell);

        if (spell.Action.ID == (uint)AID.FireballRest)
            _predictedByEvent.Add((WorldState.Actors.Find(spell.MainTargetID)?.Position ?? spell.TargetXZ, WorldState.FutureTime(CastEventToSpawn)));
    }
}

// 機制:躲到加魯拉(屍體也算)後面擋住自己與火龍之間的視線,否則必死。
// 🔴 KingOfTheSkiesVisual 是無詠唱的瞬發技 ⇒ OnCastStarted／OnCastFinished 永遠不會被呼叫,
// 只能掛 OnEventCast,並自行造一個 7 秒的結算計時。
class KingOfTheSkies(BossModule module) : Components.GenericLineOfSightAOE(module, (uint)AID.KingOfTheSkiesVisual, 100)
{
    public bool Resolved;

    // 原點＝本體(玩家是對著龍的方向躲),不是任何 Helper。
    // 倒數這 7 秒龍會起飛盤旋,每幀重讀它的位置會讓安全區一直轉、站不住 ⇒ 倒數開始時鎖定一次。
    private WPos? _lockedOrigin;
    private WPos GetOrigin() => _lockedOrigin ??= Module.PrimaryActor.Position;

    // 值域契約:以下四個常數都是**刻意放寬的估計值,不是量出來的**。安全區畫得比真實的小只是
    // 站位麻煩一點,畫得比真實的大才會死人 ⇒ 要調只准往保守方向調。
    private const float BlockerSafetyPad = 10f;
    private static readonly Angle AngularSafetyPad = 60f.Degrees();
    private static readonly Angle MaxHalfWidth = 89f.Degrees(); // 留在半平面以內
    private const float GarulaHalfBodyLength = 6f;
    private const float GarulaHalfBodyWidth = 4f;

    private static void ApplySafetyPad(List<(float Distance, Angle Dir, Angle HalfWidth)> visibility)
    {
        for (var i = 0; i < visibility.Count; ++i)
        {
            var v = visibility[i];
            var pad = Math.Min(BlockerSafetyPad, v.Distance * 0.7f);
            var widenedHalfWidth = v.HalfWidth + AngularSafetyPad;
            visibility[i] = (Math.Max(v.Distance - pad, 0f), v.Dir, widenedHalfWidth < MaxHalfWidth ? widenedHalfWidth : MaxHalfWidth);
        }
    }

    // 倒下的加魯拉不是一條線也不是一個圓:前腳會往一側張開。用一個有朝向的矩形近似,取四個角。
    // 角點順序走周長(頭+w、尾+w、尾-w、頭-w),照相鄰邊連線才會畫出矩形而不是交叉的蝴蝶結。
    private static WPos[] GarulaBodyCorners(Actor garula)
    {
        var facing = garula.Rotation.ToDirection();
        var perp = new WDir(facing.Z, -facing.X); // rotate 90 degrees
        var lenOffset = GarulaHalfBodyLength * facing;
        var widthOffset = GarulaHalfBodyWidth * perp;
        return
        [
            garula.Position + lenOffset + widthOffset,
            garula.Position - lenOffset + widthOffset,
            garula.Position - lenOffset - widthOffset,
            garula.Position + lenOffset - widthOffset,
        ];
    }

    // 用四個角相對於原點的角度跨距算遮蔽扇形,而不是用 asin(半徑/距離) 的圓盤近似 ——
    // 這樣屍體側面朝著原點時擋得寬、頭尾朝著原點時擋得窄,自然反映實際朝向。
    // 角度一律量成「相對第一個角的帶號偏移」(DistanceToAngle 正規化到 (-180,180]),
    // 避開 ±180 繞回造成的合併錯誤。
    private static (float Distance, Angle Dir, Angle HalfWidth) BodyVisibility(WPos origin, WPos[] corners)
    {
        var minDist = float.MaxValue;
        var refDir = Angle.FromDirection(corners[0] - origin);
        var minOffset = 0f;
        var maxOffset = 0f;
        for (var i = 0; i < corners.Length; ++i)
        {
            var toCorner = corners[i] - origin;
            var dist = toCorner.Length();
            if (dist < minDist)
                minDist = dist;
            var offset = refDir.DistanceToAngle(Angle.FromDirection(toCorner)).Rad;
            if (offset < minOffset)
                minOffset = offset;
            if (offset > maxOffset)
                maxOffset = offset;
        }
        var halfWidth = (maxOffset - minOffset) / 2f;
        var centerDir = refDir + ((minOffset + maxOffset) / 2f).Radians();
        return (minDist, centerDir, halfWidth.Radians());
    }

    private void RebuildSegmentVisibility(WPos origin, List<Actor> garulas)
    {
        Visibility.Clear();
        var count = garulas.Count;
        for (var i = 0; i < count; ++i)
            Visibility.Add(BodyVisibility(origin, GarulaBodyCorners(garulas[i])));
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            // 屍體擋視線與活著時一樣,而且結算前不會消失 ⇒ 不要濾掉已死的。
            var origin = GetOrigin();
            var garulas = Module.Enemies(OID.Garula);
            Modify(origin, garulas.Select(e => (Position: e.Position, HitboxRadius: e.HitboxRadius)), WorldState.FutureTime(7));
            RebuildSegmentVisibility(origin, garulas);
            ApplySafetyPad(Visibility);
            Resolved = false;
            // 🔴 Modify() 只更新原始遮蔽資料,不會建出 ActiveAOEs／AI 走位真正讀的 Safezones ——
            // 那是基底類別在 OnCastStarted/OnCastFinished 裡呼叫 AddSafezone() 建的,而本機制
            // 沒有詠唱條、那兩個回呼不會觸發 ⇒ 必須自己補呼叫,否則安全區一直是空的。
            Safezones.Clear();
            AddSafezone(NextExplosion);
        }

        if (spell.Action.ID == (uint)AID.KingOfTheSkies)
        {
            Modify(null, []);
            Safezones.Clear();
            Resolved = true;
            _lockedOrigin = null; // 本場若再來一次就重新鎖定
        }
    }

    // 加魯拉在這 7 秒內會動,只在 OnEventCast 拍一次快照到結算時就過期了 ⇒ 每幀重算。
    // Safezones 也要跟著重建,否則它會以同樣的方式過期。
    public override void Update()
    {
        if (Origin != null && !Resolved)
        {
            var origin = GetOrigin();
            var garulas = Module.Enemies(OID.Garula);
            Modify(origin, garulas.Select(e => (Position: e.Position, HitboxRadius: e.HitboxRadius)), NextExplosion);
            RebuildSegmentVisibility(origin, garulas);
            ApplySafetyPad(Visibility);
            Safezones.Clear();
            AddSafezone(NextExplosion);
        }
    }

    // 畫出判定所依據的原始幾何(原點、屍體矩形、原點到四角的射線),而不是只給安全／危險的結論,
    // 這樣站位不對時看得出是哪一邊算錯。射線是**加寬之前**的原始扇形。
    public override void DrawArenaBackground(int pcSlot, Actor pc)
    {
        base.DrawArenaBackground(pcSlot, pc);
        if (Origin is not { } origin)
            return;

        Arena.AddCircle(origin, 1f, Colors.Object, 2f);
        foreach (var garula in Module.Enemies(OID.Garula))
        {
            var corners = GarulaBodyCorners(garula);
            for (var i = 0; i < corners.Length; ++i)
            {
                Arena.AddLine(corners[i], corners[(i + 1) % corners.Length], Colors.Danger, 3f);
                Arena.AddCircle(corners[i], 0.4f, Colors.Safe, 2f);
                Arena.AddLine(origin, corners[i], Colors.Other1, 1f);
            }
        }
    }
}

class Adds(BossModule module) : Components.AddsMulti(module, [(uint)OID.SteppeYamaa, (uint)OID.SteppeYamaa1, (uint)OID.SteppeSheep, (uint)OID.SteppeCoeurl, (uint)OID.Garula]);

// 執行緒契約:本元件全部在框架執行緒上跑(Update／事件回呼),RSR 的 IPC 也在同一條執行緒上同步呼叫。
class TargetHints(BossModule module) : BossComponent(module)
{
    private Actor? Tail;
    private uint _prioritizedGarulaNameId;
    private uint _prioritizedTailNameId;
    private bool _blacklistedBossForTail;
    private bool _blacklistedBossPreKots;
    private readonly HashSet<uint> _blacklistedAddNameIds = [];
    private static readonly uint[] AddOIDs = [(uint)OID.SteppeYamaa, (uint)OID.SteppeYamaa1, (uint)OID.SteppeSheep, (uint)OID.SteppeCoeurl];
    private readonly HashSet<uint> _blacklistedForKotsCountdown = [];
    private static readonly uint[] KotsCountdownBlacklistOIDs = [(uint)OID.SteppeYamaa, (uint)OID.SteppeYamaa1, (uint)OID.SteppeSheep, (uint)OID.SteppeCoeurl, (uint)OID.Garula, (uint)OID.Boss];
    // 一旦觀察到就黏住:不希望因為量表／暈眩狀態在下一幀查不到就把「去砍尾巴」收回去。
    private bool _toppled;
    private int _toppleGaugeValue = -1; // -1 ＝ 目前讀不到,只用於畫面顯示
    private readonly Ex5RathalosConfig _config = Service.Config.Get<Ex5RathalosConfig>();
    // 同樣黏住:KingOfTheSkies.Origin 只在結算前那 7 秒非 null,直接查會在量表開始累積時就翻回 false。
    private bool _kotsHasResolvedOnce;

    // 「墜地」量表是遊戲共用的 _ContentGauge,沒有對應的 FFXIVClientStructs 結構,只能直接走節點樹。
    // 契約:NodeList[6] ＝ 數值文字、NodeList[7] ＝ 標籤文字;讀值前先驗標籤是「墜地」,
    // 免得這個共用視窗正在顯示別的內容時拿到無關的數字。讀不到時回 false,由 SID.Toppled 兜底。
    private static unsafe bool TryReadToppleGauge(out int value)
    {
        value = 0;
        var addon = (AtkUnitBase*)Service.GameGui.GetAddonByName("_ContentGauge").Address;
        if (addon == null || !addon->IsVisible)
            return false;

        var nodes = addon->UldManager.NodeList;
        if (nodes == null || addon->UldManager.NodeListCount < 8)
            return false;

        // 🔴 先驗節點型別再轉型:AtkTextNode 比 AtkResNode 大,對非文字節點讀 NodeText 會越界。
        var labelNode = nodes[7];
        var valueNode = nodes[6];
        if (labelNode == null || valueNode == null || labelNode->Type != NodeType.Text || valueNode->Type != NodeType.Text)
            return false;

        var label = (AtkTextNode*)labelNode;
        var valueText = (AtkTextNode*)valueNode;
        if (label->NodeText.ToString() != "墜地")
            return false;

        return int.TryParse(valueText->NodeText.ToString(), out value);
    }

    public override void Update()
    {
        if (TryReadToppleGauge(out var gaugeValue))
        {
            _toppleGaugeValue = gaugeValue;
            if (gaugeValue >= 100)
                _toppled = true;
        }
        else
        {
            _toppleGaugeValue = -1;
        }

        // _ContentGauge 可能在量表填滿的那一瞬間就收起來,輪詢有機會最後只讀到 97~99 ⇒ 另外直接查
        // SID.Toppled(約 20 秒的暈眩),兩個訊號誰先到都算數。
        if (Module.PrimaryActor.FindStatus(SID.Toppled) != null)
            _toppled = true;

        if (!_kotsHasResolvedOnce && (Module.FindComponent<KingOfTheSkies>()?.Resolved ?? false))
            _kotsHasResolvedOnce = true;

        var bossLowHp = Module.PrimaryActor.HPRatio < _config.StopAttackHpPercent / 100f;

        // 天空王者還沒結算血量就已經過低:整個量表／尾巴機制都還沒開始,現在打本體沒有任何好處,
        // 直接把本體排除讓 RSR 改打別的(加魯拉出現後由下面那段確保打的是加魯拉)。
        // 天空王者結算後或血量回升就解除。
        if (_config.FarmScales && !_kotsHasResolvedOnce && bossLowHp && !_blacklistedBossPreKots)
        {
            RSRPriority.AddBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossPreKots = true;
        }
        else if ((!_config.FarmScales || _kotsHasResolvedOnce || !bossLowHp) && _blacklistedBossPreKots)
        {
            RSRPriority.RemoveBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossPreKots = false;
        }

        // 天空王者倒數這段期間,下面 AddAIHints 的 PriorityForbidden 對 RSR 無效 ⇒ 用 IPC 把場上
        // 所有東西都排除,RSR 沒東西可打,AI 走位才有機會真的去躲。
        // 這段與 FarmScales 無關,是基本的存活修正,永遠生效。
        var kotsCountdownActive = Module.FindComponent<KingOfTheSkies>() is { Origin: not null, Resolved: false };
        if (kotsCountdownActive)
        {
            foreach (var enemy in Module.Enemies(KotsCountdownBlacklistOIDs))
                if (!enemy.IsDead && _blacklistedForKotsCountdown.Add(enemy.NameID))
                    RSRPriority.AddBlacklist(enemy.NameID);
        }
        else if (_blacklistedForKotsCountdown.Count > 0)
        {
            foreach (var nameId in _blacklistedForKotsCountdown)
                RSRPriority.RemoveBlacklist(nameId);
            _blacklistedForKotsCountdown.Clear();
        }

        // 加魯拉一出現就集火:屍體擋視線與活著時一樣,而且早點倒下安全點會離隊伍比較近。
        // 判 IsTargetable 而不是只判「存在且沒死」:實體可能在真的可選取之前就進了 Module.Enemies。
        var garula = Module.Enemies(OID.Garula).FirstOrDefault(e => !e.IsDead && e.IsTargetable);
        if (garula != null && _prioritizedGarulaNameId != garula.NameID)
        {
            if (_prioritizedGarulaNameId != default)
                RSRPriority.Remove(_prioritizedGarulaNameId);
            _prioritizedGarulaNameId = garula.NameID;
            RSRPriority.Add(_prioritizedGarulaNameId);
        }
        else if (garula == null && _prioritizedGarulaNameId != default)
        {
            RSRPriority.Remove(_prioritizedGarulaNameId);
            _prioritizedGarulaNameId = default;
        }

        // 只加優先度擋不住 RSR 先用範圍技清小怪(加魯拉會因此逃走)⇒ 加魯拉在場時把小怪一起排除,
        // 牠一離場(死亡或逃走)就解除,讓小怪能正常清掉。
        if (garula != null)
        {
            foreach (var add in Module.Enemies(AddOIDs))
                if (!add.IsDead && _blacklistedAddNameIds.Add(add.NameID))
                    RSRPriority.AddBlacklist(add.NameID);
        }
        else if (_blacklistedAddNameIds.Count > 0)
        {
            foreach (var nameId in _blacklistedAddNameIds)
                RSRPriority.RemoveBlacklist(nameId);
            _blacklistedAddNameIds.Clear();
        }

        // 用 _toppled(SID.Toppled 暈眩)當閘門,而不是「尾巴可以選取了」:尾巴可選取會早很多,
        // 太早把輸出拉走反而拖慢累積量表。等真的倒地再去砍尾巴。
        var tailUp = Tail != null && !Tail.IsDead;
        if (_toppled && tailUp && _prioritizedTailNameId != Tail!.NameID)
        {
            if (_prioritizedTailNameId != default)
                RSRPriority.Remove(_prioritizedTailNameId);
            _prioritizedTailNameId = Tail.NameID;
            RSRPriority.Add(_prioritizedTailNameId);
        }
        else if ((!_toppled || !tailUp) && _prioritizedTailNameId != default)
        {
            RSRPriority.Remove(_prioritizedTailNameId);
            _prioritizedTailNameId = default;
        }

        if (_toppled && tailUp && !_blacklistedBossForTail)
        {
            RSRPriority.AddBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossForTail = true;
        }
        else if ((!_toppled || !tailUp) && _blacklistedBossForTail)
        {
            RSRPriority.RemoveBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossForTail = false;
        }
    }

    // 🔴 把所有寫進 RSR 的狀態還原。RSR 的黑名單是**全域**的,小怪的 NameID(草原羊等)在外域也用得到,
    // 漏掉一筆就會讓使用者的 RSR 在遊戲任何地方都不打那種怪,而且完全沒有徵兆。
    // BossComponent 沒有卸載回呼 ⇒ 滅團／離開副本／外掛卸載都靠 Ex5Rathalos.Dispose() 呼叫這裡。
    public void ClearIpcState()
    {
        if (_prioritizedGarulaNameId != default)
        {
            RSRPriority.Remove(_prioritizedGarulaNameId);
            _prioritizedGarulaNameId = default;
        }
        if (_prioritizedTailNameId != default)
        {
            RSRPriority.Remove(_prioritizedTailNameId);
            _prioritizedTailNameId = default;
        }
        if (_blacklistedBossForTail || _blacklistedBossPreKots)
        {
            RSRPriority.RemoveBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossForTail = false;
            _blacklistedBossPreKots = false;
        }
        foreach (var nameId in _blacklistedAddNameIds)
            RSRPriority.RemoveBlacklist(nameId);
        _blacklistedAddNameIds.Clear();
        foreach (var nameId in _blacklistedForKotsCountdown)
            RSRPriority.RemoveBlacklist(nameId);
        _blacklistedForKotsCountdown.Clear();
    }

    // 現場狀態列:不必開 Dalamud log 就看得出目前在做什麼。
    public override void AddGlobalHints(GlobalHints hints)
    {
        if (_prioritizedGarulaNameId != default)
            hints.Add("加魯拉優先擊殺中");
        if (_blacklistedBossPreKots)
            hints.Add("血量偏低,暫停攻擊本體等待天空王者/加魯拉");
        if (_kotsHasResolvedOnce && !_toppled && _toppleGaugeValue >= 0)
            hints.Add($"墜地量表 {_toppleGaugeValue}/100");
        if (_prioritizedTailNameId != default)
            hints.Add("尾巴優先擊殺中");
    }

    public override void OnActorTargetable(Actor actor)
    {
        if (actor.OID == (uint)OID.WyvernsTail)
            Tail = actor;
    }

    public override void OnActorUntargetable(Actor actor)
    {
        if (actor.OID == (uint)OID.WyvernsTail)
            Tail = null;
    }

    // 本體死亡時 Update() 不一定還有下一幀可以自己收尾,直接在死亡事件上還原。
    public override void OnActorDeath(Actor actor)
    {
        if (actor == Module.PrimaryActor)
            ClearIpcState();
    }

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        // 天空王者倒數期間禁止所有敵人,讓 AI 只在乎走到安全點,不要留在原地清小怪。
        if (Module.FindComponent<KingOfTheSkies>() is { Origin: not null, Resolved: false })
        {
            foreach (var enemy in hints.PotentialTargets)
                hints.SetPriority(enemy.Actor, AIHints.Enemy.PriorityForbidden);
        }

        if (actor.HPRatio < 0.25f)
            hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.MegaPotion), null, ActionQueue.Priority.VeryHigh);

        if (_toppled && Tail != null && !Tail.IsDead)
        {
            hints.SetPriority(Tail, 1);
            hints.SetPriority(Module.PrimaryActor, AIHints.Enemy.PriorityForbidden);
        }

        foreach (var garula in Module.Enemies(OID.Garula))
            if (!garula.IsDead)
                hints.SetPriority(garula, 1);
    }
}

class Ex5RathalosStates : StateMachineBuilder
{
    public Ex5RathalosStates(BossModule module) : base(module)
    {
        TrivialPhase()
            .ActivateOnEnter<Mangle>()
            .ActivateOnEnter<GarulaRush>()
            .ActivateOnEnter<Lullaby>()
            .ActivateOnEnter<HeadButt>()
            .ActivateOnEnter<Rush>()
            .ActivateOnEnter<TailSwing>()
            .ActivateOnEnter<Mangle2>()
            .ActivateOnEnter<FireballStack1>()
            .ActivateOnEnter<FireballStack2>()
            .ActivateOnEnter<FirePuddle>()
            .ActivateOnEnter<KingOfTheSkies>()
            .ActivateOnEnter<SweepingFlames>()
            .ActivateOnEnter<Adds>()
            .ActivateOnEnter<TargetHints>();
    }
}

// Maturity 必須 >= 使用者設定的 MinMaturity(預設 Contributed)模組才會真的啟動;
// 低於門檻的模組仍會列在「支援的戰鬥」清單裡,但戰鬥中完全不會被建立。
[ModuleInfo(BossModuleInfo.Maturity.Contributed, Contributors = "ported from awgil/ffxiv_bossmod", GroupType = BossModuleInfo.GroupType.CFC, GroupID = 475, NameID = 7221)]
public class Ex5Rathalos(WorldState ws, Actor primary) : BossModule(ws, primary, new(100, 100), new ArenaBoundsCircle(24.5f))
{
    // 模組銷毀(滅團、離開副本、外掛卸載)是 RSR 全域黑名單唯一保證會跑到的收尾點。
    protected override void Dispose(bool disposing)
    {
        FindComponent<TargetHints>()?.ClearIpcState();
        base.Dispose(disposing);
    }
}
