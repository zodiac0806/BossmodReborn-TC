using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.Interop;

namespace BossMod;

// utility that updates a world state to correspond to game state
sealed class WorldStateGameSync : IDisposable
{
    private const int ObjectTableSize = 819; // should match CS; note that different ranges are used for different purposes - consider splitting?..
    private const uint InvalidEntityId = 0xE0000000;
    private const float Thousandth = 1e-3f;

    private readonly WorldState _ws;
    private readonly ActionManagerEx _amex;
    private readonly DateTime _startTime;
    private readonly long _startQPC;
    private bool _loggedDeepDungeonBase;

    // list of actors that are present in the user's enemy list
    private readonly List<ulong> _playerEnmity = [];

    private readonly List<WorldState.Operation> _globalOps = [];
    private readonly Dictionary<ulong, List<WorldState.Operation>> _actorOps = [];
    private readonly Dictionary<ulong, Vector3> _lastCastPositions = []; // unfortunately, game only saves cast location for area-targeted spells
    private readonly Actor?[] _actorsByIndex = new Actor?[ObjectTableSize];

    private readonly Network.OpcodeMap _opcodeMap = new();
    private readonly Network.PacketInterceptor _interceptor = new();
    private readonly Network.PacketDecoderGame _decoder = new();

    private readonly ConfigListener<ReplayManagementConfig> _netConfig;
    private readonly EventSubscriptions _subscriptions;

    private unsafe delegate void ProcessPacketActorCastDelegate(uint casterId, Network.ServerIPC.ActorCast* packet);
    private readonly Hook<ProcessPacketActorCastDelegate> _processPacketActorCastHook;

    private unsafe delegate void ProcessPacketEffectResultDelegate(uint targetID, byte* packet, byte replaying);
    private readonly Hook<ProcessPacketEffectResultDelegate> _processPacketEffectResultHook;
    private readonly Hook<ProcessPacketEffectResultDelegate> _processPacketEffectResultBasicHook;

    private delegate void ProcessPacketActorControlDelegate(uint actorID, uint category, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, ulong targetID, byte replaying);
    private readonly Hook<ProcessPacketActorControlDelegate> _processPacketActorControlHook;

    private unsafe delegate void ProcessPacketNpcYellDelegate(Network.ServerIPC.NpcYell* packet);
    private readonly Hook<ProcessPacketNpcYellDelegate> _processPacketNpcYellHook;

    private unsafe delegate void ProcessEnvControlDelegate(void* self, uint index, ushort s1, ushort s2);
    private readonly Hook<ProcessEnvControlDelegate> _processEnvControlHook;

    private unsafe delegate void ProcessPacketRSVDataDelegate(byte* packet);
    private readonly Hook<ProcessPacketRSVDataDelegate> _processPacketRSVDataHook;

    private unsafe delegate void ProcessPacketOpenTreasureDelegate(uint actorID, byte* packet);
    private readonly Hook<ProcessPacketOpenTreasureDelegate> _processPacketOpenTreasureHook;

    private unsafe delegate void* ProcessSystemLogMessageDelegate(uint entityId, uint logMessageId, int* args, byte argCount);
    private readonly Hook<ProcessSystemLogMessageDelegate> _processSystemLogMessageHook;

    private unsafe delegate void* ProcessPacketFateInfoDelegate(ulong fateId, long startTimestamp, ulong durationSecs);
    private readonly Hook<ProcessPacketFateInfoDelegate> _processPacketFateInfoHook;

    private readonly unsafe delegate* unmanaged<ContainerInterface*, float> _calculateMoveSpeedMulti;

    private unsafe delegate void ProcessMapEffectDelegate(void* director, byte* packet);

    private readonly Hook<ProcessMapEffectDelegate> _processMapEffect1Hook;
    private readonly Hook<ProcessMapEffectDelegate> _processMapEffect2Hook;
    private readonly Hook<ProcessMapEffectDelegate> _processMapEffect3Hook;

    public unsafe WorldStateGameSync(WorldState ws, ActionManagerEx amex)
    {
        _ws = ws;
        _amex = amex;
        _startTime = DateTime.Now;
        // 🔴 Framework.Instance() 是 [StaticAddress(…, isPointer: true)]，回傳全域指標槽的**內容**，合法可為 null。
        // 📌 判定：這裡是「外掛載入」路徑，不是每幀路徑。Dalamud 自己要先有 Framework 才跑得起外掛，
        //    所以這一刻為 null 幾乎不可能發生；而 _startQPC 是整條世界狀態時間軸的錨點，沒有中性值可用
        //    （拿 0 當錨會讓每一幀的時間戳都偏掉，是靜默的錯誤資料，比載入失敗糟）。
        //    ⇒ 選擇擲出明確的受管理例外：Dalamud 會把它記成「外掛載入失敗」並顯示原因，遊戲照常跑；
        //    原本的裸鏈解參考則是 AccessViolationException＝直接把遊戲帶走，而且沒有任何訊息。
        //    （不選「延後到第一幀再取」是因為 _startTime／_startQPC 必須是同一刻的一對，
        //      拆成兩個時機要動到兩個 readonly 欄位與所有使用點，對一個幾乎不會發生的情況不划算。）
        var fwk = Framework.Instance();
        if (fwk == null)
            throw new InvalidOperationException("Client::System::Framework::Framework 尚未建立，無法取得世界狀態時間軸的起始 QPC。");
        _startQPC = fwk->PerformanceCounterValue;
        _interceptor.ServerIPCReceived += ServerIPCReceived;
        _interceptor.ClientIPCSent += ClientIPCSent;

        _netConfig = Service.Config.GetAndSubscribe<ReplayManagementConfig>(config =>
        {
            _interceptor.ActiveRecv = config.RecordServerPackets || config.DumpServerPackets;
            _interceptor.ActiveSend = config.DumpClientPackets;
        });
        _subscriptions = new
        (
            amex.ActionRequestExecuted.Subscribe(OnActionRequested),
            amex.ActionEffectReceived.Subscribe(OnActionEffect)
        );

        _processPacketActorCastHook = Service.Hook.HookFromSignature<ProcessPacketActorCastDelegate>("40 53 57 48 81 EC ?? ?? ?? ?? 48 8B FA 8B D1", ProcessPacketActorCastDetour);
        _processPacketActorCastHook.Enable();
        Service.Log($"[WSG] ProcessPacketActorCast address = 0x{_processPacketActorCastHook.Address:X}");

        _processPacketEffectResultHook = Service.Hook.HookFromSignature<ProcessPacketEffectResultDelegate>("48 8B C4 44 88 40 18 89 48 08", ProcessPacketEffectResultDetour);
        _processPacketEffectResultHook.Enable();
        Service.Log($"[WSG] ProcessPacketEffectResult address = 0x{_processPacketEffectResultHook.Address:X}");

        _processPacketEffectResultBasicHook = Service.Hook.HookFromSignature<ProcessPacketEffectResultDelegate>("40 53 41 54 41 55 48 83 EC 40", ProcessPacketEffectResultBasicDetour);
        _processPacketEffectResultBasicHook.Enable();
        Service.Log($"[WSG] ProcessPacketEffectResultBasic address = 0x{_processPacketEffectResultBasicHook.Address:X}");

        _processPacketActorControlHook = Service.Hook.HookFromSignature<ProcessPacketActorControlDelegate>("E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64", ProcessPacketActorControlDetour);
        _processPacketActorControlHook.Enable();
        Service.Log($"[WSG] ProcessPacketActorControl address = 0x{_processPacketActorControlHook.Address:X}");

        // alt sig - impl: "45 33 D2 48 8D 41 48"
        _processPacketNpcYellHook = Service.Hook.HookFromSignature<ProcessPacketNpcYellDelegate>("48 83 EC 68 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 0F 10 41 10", ProcessPacketNpcYellDetour);
        _processPacketNpcYellHook.Enable();
        Service.Log($"[WSG] ProcessPacketNpcYell address = 0x{_processPacketNpcYellHook.Address:X}");

        _processEnvControlHook = Service.Hook.HookFromSignature<ProcessEnvControlDelegate>("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B FA 41 0F B7 E8", ProcessEnvControlDetour);
        _processEnvControlHook.Enable();
        Service.Log($"[WSG] ProcessEnvControl address = 0x{_processEnvControlHook.Address:X}");

        _processPacketRSVDataHook = Service.Hook.HookFromSignature<ProcessPacketRSVDataDelegate>("44 8B 09 4C 8D 41 34", ProcessPacketRSVDataDetour);
        _processPacketRSVDataHook.Enable();
        Service.Log($"[WSG] ProcessPacketRSVData address = 0x{_processPacketRSVDataHook.Address:X}");

        _processSystemLogMessageHook = Service.Hook.HookFromSignature<ProcessSystemLogMessageDelegate>("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 0F B6 47 28", ProcessSystemLogMessageDetour);
        _processSystemLogMessageHook.Enable();
        Service.Log($"[WSG] ProcessSystemLogMessage address = 0x{_processSystemLogMessageHook.Address:X}");

        _processPacketOpenTreasureHook = Service.Hook.HookFromSignature<ProcessPacketOpenTreasureDelegate>("40 53 48 83 EC 20 48 8B DA 48 8D 0D ?? ?? ?? ?? 8B 52 10 E8 ?? ?? ?? ?? 48 85 C0 74 1B", ProcessPacketOpenTreasureDetour);
        _processPacketOpenTreasureHook.Enable();
        Service.Log($"[WSG] ProcessPacketOpenTreasure address = 0x{_processPacketOpenTreasureHook.Address:X}");

        _processPacketFateInfoHook = Service.Hook.HookFromSignature<ProcessPacketFateInfoDelegate>("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 0F B7 4F 10 48 8D 57 12 41 B8 ?? ?? ?? ??", ProcessPacketFateInfoDetour);
        _processPacketFateInfoHook.Enable();
        Service.Log($"[WSG] ProcessPacketFateInfo address = 0x{_processPacketFateInfoHook.Address:X}");

        _calculateMoveSpeedMulti = (delegate* unmanaged<ContainerInterface*, float>)Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 44 0F 28 D8 45 0F 57 D2");
        Service.Log($"[WSG] CalculateMovementSpeedMultiplier address = 0x{(nint)_calculateMoveSpeedMulti:X}");

        // 2026-09 台服更新後,MapEffect 處理函式從「E8/E9 跳板鏈」改成三個各自獨立、參數為 (director, packet) 的函式
        // (與國際服同構,間距 0x110)。舊簽名（跳板鏈）已不存在,會讓整個插件在建構子丟 KeyNotFoundException。
        var mapEffectAddrs = Service.SigScanner.ScanAllText("40 55 41 57 48 83 EC ?? 48 83 B9");
        if (mapEffectAddrs.Length != 3)
            throw new InvalidOperationException($"expected 3 matches for multi-MapEffect handlers, but got {mapEffectAddrs.Length}");
        _processMapEffect1Hook = Service.Hook.HookFromAddress<ProcessMapEffectDelegate>(mapEffectAddrs[0], ProcessMapEffect1Detour);
        _processMapEffect1Hook.Enable();
        _processMapEffect2Hook = Service.Hook.HookFromAddress<ProcessMapEffectDelegate>(mapEffectAddrs[1], ProcessMapEffect2Detour);
        _processMapEffect2Hook.Enable();
        _processMapEffect3Hook = Service.Hook.HookFromAddress<ProcessMapEffectDelegate>(mapEffectAddrs[2], ProcessMapEffect3Detour);
        _processMapEffect3Hook.Enable();
        Service.Log($"[WSG] MapEffect addresses = 0x{_processMapEffect1Hook.Address:X}, 0x{_processMapEffect2Hook.Address:X}, 0x{_processMapEffect3Hook.Address:X}");
    }

    public void Dispose()
    {
        _processMapEffect1Hook.Dispose();
        _processMapEffect2Hook.Dispose();
        _processMapEffect3Hook.Dispose();
        _processPacketActorCastHook.Dispose();
        _processPacketEffectResultBasicHook.Dispose();
        _processPacketEffectResultHook.Dispose();
        _processPacketActorControlHook.Dispose();
        _processPacketNpcYellHook.Dispose();
        _processEnvControlHook.Dispose();
        _processPacketRSVDataHook.Dispose();
        _processSystemLogMessageHook.Dispose();
        _processPacketOpenTreasureHook.Dispose();
        _processPacketFateInfoHook.Dispose();
        _subscriptions.Dispose();
        _netConfig.Dispose();
        _interceptor.Dispose();
    }

    public unsafe void Update(TimeSpan prevFramePerf)
    {
        // 🔴 每幀路徑。Framework.Instance() 是 [StaticAddress(…, isPointer: true)]，回傳全域指標槽的**內容**，
        //    登出／切換區域／外掛熱更新拆解過程中合法為 null。下面六個欄位原本是無防護的裸鏈解參考，
        //    null 時直接 AccessViolationException——AVE 是 corrupted-state exception，攔不到。
        //    fail-closed：這一幀整段不同步（時間戳沒有中性值可編，寫個假的會污染整條世界狀態時間軸）。
        //    _globalOps 不清空、留到下一幀照樣執行，所以只是晚一幀，不會掉事件。
        //    🔴 熱路徑，刻意不寫 log。
        var fwk = Framework.Instance();
        if (fwk == null)
            return;

        _ws.Execute(new WorldState.OpFrameStart
        (
            new(
                _startTime.AddSeconds((double)(fwk->PerformanceCounterValue - _startQPC) / _ws.QPF),
                (ulong)fwk->PerformanceCounterValue,
                fwk->FrameCounter,
                fwk->RealFrameDeltaTime,
                fwk->FrameDeltaTime,
                fwk->GameSpeedMultiplier
            ),
            prevFramePerf,
            GaugeData(),
            Camera.Instance?.CameraAzimuth.Radians() ?? default
        ));
        if (_ws.CurrentZone != Service.ClientState.TerritoryType || _ws.CurrentCFCID != GameMain.Instance()->CurrentContentFinderConditionId)
        {
            _ws.Execute(new WorldState.OpZoneChange(Service.ClientState.TerritoryType, GameMain.Instance()->CurrentContentFinderConditionId));
        }
        // ⚠️ proxy 這個區域變數在本方法裡**沒有任何使用點**（全檔唯一一次出現就是這行），
        //    但它做的是 fwk->NetworkModuleProxy->ReceiverCallback 兩層裸解參考，網路模組還沒接起來時
        //    兩層都可能是 null＝AccessViolation。沒有指示要刪既有程式碼，所以原樣留著、只補上判空；
        //    要不要整行拿掉留給後續裁決。
        var networkModuleProxy = fwk->NetworkModuleProxy;
        var proxy = networkModuleProxy != null ? networkModuleProxy->ReceiverCallback : null;
        _ = proxy;
        // 📌 Get() 回 null＝「這一刻讀不到」，與「讀到全 0」不同：讀不到時什麼都不做，保留上一次讀到的
        //    scramble，不要把它覆寫成 default（覆寫會讓後續每個封包都被解錯）。
        var scramble = Network.IDScramble.Get();
        if (scramble is { } scrambleFields && _ws.Network.IDScramble != scrambleFields)
            _ws.Execute(new NetworkState.OpIDScramble(scrambleFields));

        var count = _globalOps.Count;
        for (var i = 0; i < count; ++i)
        {
            _ws.Execute(_globalOps[i]);
        }
        _globalOps.Clear();

        _playerEnmity.Clear();
        var uiState = UIState.Instance();
        // HaterCount 是遊戲寫入的 int，Haters 是 FixedSizeArray32 —— 兩者無結構保證，夾到容量內。
        var haterCount = Math.Min(uiState->Hater.HaterCount, uiState->Hater.Haters.Length);
        for (var i = 0; i < haterCount; ++i)
            _playerEnmity.Add(uiState->Hater.Haters[i].EntityId);

        UpdateWaymarks();
        UpdateActors();
        UpdateParty();
        UpdateClient();
        UpdateDeepDungeon();
    }

    private unsafe void UpdateWaymarks()
    {
        var wm = Waymark.A;
        foreach (ref var marker in MarkingController.Instance()->FieldMarkers)
        {
            Vector3? pos = marker.Active ? new(marker.X * Thousandth, marker.Y * Thousandth, marker.Z * Thousandth) : null;
            if (_ws.Waymarks[wm] != pos)
                _ws.Execute(new WaymarkState.OpWaymarkChange(wm, pos));
            ++wm;
        }

        var sgn = Sign.Attack1;
        foreach (ref var marker in MarkingController.Instance()->Markers)
        {
            var id = SanitizedObjectID(marker.Id);
            if (_ws.Waymarks[sgn] != id)
                _ws.Execute(new WaymarkState.OpSignChange(sgn, id));
            ++sgn;
        }
    }

    private unsafe void UpdateActors()
    {
        var mgr = GameObjectManager.Instance();
        var len = _actorsByIndex.Length;
        for (var i = 0; i < len; ++i)
        {
            var actor = _actorsByIndex[i];
            var obj = mgr->Objects.IndexSorted[i].Value;

            if (obj != null && obj->EntityId == InvalidEntityId)
                obj = null; // ignore non-networked objects (really?..)

            if (obj != null && (obj->EntityId & 0xFF000000) == 0xFF000000)
            {
                Service.LogVerbose($"[WorldState] Skipping bad object #{i} with id {obj->EntityId:X}");
                obj = null;
            }
            var existing = obj != null ? _ws.Actors.Find(obj->EntityId) : null;

            if (actor != null && (obj == null || existing == null || actor.InstanceID != obj->EntityId))
            {
                _actorsByIndex[i] = null;
                RemoveActor(actor);
                actor = null;
            }
            if (obj != null)
            {
                if (actor != existing)
                    Service.Log($"[WorldState] Actor position mismatch for #{i} {actor}");

                UpdateActor(obj, i, actor);
            }
        }

        foreach (var (id, ops) in _actorOps)
            Service.Log($"[WorldState] {ops.Count} actor events for unknown entity {id:X}");
        _actorOps.Clear();
    }

    private void RemoveActor(Actor actor)
    {
        var id = actor.InstanceID;
        DispatchActorEvents(id);
        _ws.Execute(new ActorState.OpDestroy(id));
    }

    private unsafe void UpdateActor(GameObject* obj, int index, Actor? act)
    {
        var chr = obj->IsCharacter() ? (Character*)obj : null;
        var name = obj->NameString;
        var nameID = chr != null ? chr->NameId : 0;
        var classID = chr != null ? (Class)chr->ClassJob : Class.None;
        var level = chr != null ? chr->Level : 0;
        var posRot = new Vector4(obj->Position, obj->Rotation);
        var hpmp = new ActorHPMP();
        var inCombat = false;
        if (chr != null)
        {
            hpmp.CurHP = chr->Health;
            hpmp.MaxHP = chr->MaxHealth;
            hpmp.Shield = (uint)(chr->ShieldValue * 0.01f * hpmp.MaxHP);
            hpmp.CurMP = chr->Mana;
            hpmp.MaxMP = chr->MaxMana;
            inCombat = chr->InCombat;
        }
        var targetable = obj->GetIsTargetable();
        var renderflags = obj->RenderFlags;
        var friendly = chr == null || ActionManager.ClassifyTarget(chr) != ActionManager.TargetCategory.Enemy;
        var isDead = obj->IsDead();
        var hasAggro = _playerEnmity.IndexOf(obj->EntityId) >= 0;
        var target = chr != null ? SanitizedObjectID(chr->GetTargetId()) : 0; // note: when changing targets, we want to see changes immediately rather than wait for server response
        var modelState = chr != null ? new ActorModelState(chr->Timeline.ModelState, chr->Timeline.AnimationState[0], chr->Timeline.AnimationState[1]) : default;
        var eventState = obj->EventState;
        var radius = obj->GetRadius();
        var mountId = chr != null ? chr->Mount.MountId : 0u;
        var forayInfoPtr = chr != null ? chr->GetForayInfo() : null;
        var forayInfo = forayInfoPtr == null ? default : new ActorForayInfo(forayInfoPtr->Level, forayInfoPtr->Element);

        if (act == null)
        {
            var type = (ActorType)(((int)obj->ObjectKind << 8) + obj->SubKind);
            _ws.Execute(new ActorState.OpCreate(obj->EntityId, obj->BaseId, index, obj->LayoutId, name, nameID, type, classID, level, posRot, radius, hpmp, targetable, friendly, SanitizedObjectID(obj->OwnerId), obj->FateId, renderflags));
            act = _actorsByIndex[index] = _ws.Actors.Find(obj->EntityId)!;

            // note: for now, we continue relying on network messages for tether changes, since sometimes multiple changes can happen in a single frame, and some components rely on seeing all of them...
            var tether = chr != null ? new ActorTetherInfo(chr->Vfx.Tethers[0].Id, chr->Vfx.Tethers[0].TargetId) : default;
            if (tether.ID != default)
                _ws.Execute(new ActorState.OpTether(act.InstanceID, tether));
        }
        else
        {
            var id = act.InstanceID;
            if (act.NameID != nameID || act.Name != name)
                _ws.Execute(new ActorState.OpRename(id, name, nameID));
            if (act.Class != classID || act.Level != level)
                _ws.Execute(new ActorState.OpClassChange(id, classID, level));
            if (act.PosRot != posRot)
                _ws.Execute(new ActorState.OpMove(id, posRot));
            if (act.HitboxRadius != radius)
                _ws.Execute(new ActorState.OpSizeChange(id, radius));
            if (act.HPMP != hpmp)
                _ws.Execute(new ActorState.OpHPMP(id, hpmp));
            if (act.IsTargetable != targetable)
                _ws.Execute(new ActorState.OpTargetable(id, targetable));
            if (act.IsAlly != friendly)
                _ws.Execute(new ActorState.OpAlly(id, friendly));
            if (act.Renderflags != renderflags)
                _ws.Execute(new ActorState.OpRenderflags(id, renderflags));
        }
        var instanceID = act.InstanceID;
        if (act.IsDead != isDead)
            _ws.Execute(new ActorState.OpDead(instanceID, isDead));
        if (act.InCombat != inCombat)
            _ws.Execute(new ActorState.OpCombat(instanceID, inCombat));
        if (act.AggroPlayer != hasAggro)
            _ws.Execute(new ActorState.OpAggroPlayer(instanceID, hasAggro));
        if (act.ModelState != modelState)
            _ws.Execute(new ActorState.OpModelState(instanceID, modelState));
        if (act.EventState != eventState)
            _ws.Execute(new ActorState.OpEventState(instanceID, eventState));
        if (act.TargetID != target)
            _ws.Execute(new ActorState.OpTarget(instanceID, target));
        if (act.MountId != mountId)
            _ws.Execute(new ActorState.OpMount(instanceID, mountId));
        if (act.ForayInfo != forayInfo)
            _ws.Execute(new ActorState.OpForayInfo(act.InstanceID, forayInfo));

        DispatchActorEvents(instanceID);

        var castInfo = chr != null ? chr->GetCastInfo() : null;
        if (castInfo != null)
        {
            var curCast = castInfo->IsCasting
                ? new ActorCastInfo
                {
                    Action = new((ActionType)castInfo->ActionType, castInfo->ActionId),
                    TargetID = SanitizedObjectID(castInfo->TargetId),
                    Rotation = chr->CastRotation.Radians(),
                    Location = _lastCastPositions.GetValueOrDefault(act.InstanceID, castInfo->TargetLocation),
                    ElapsedTime = castInfo->CurrentCastTime,
                    TotalTime = castInfo->BaseCastTime,
                    Interruptible = castInfo->Interruptible,
                } : null;
            UpdateActorCastInfo(act, curCast);
        }

        var sm = chr != null ? chr->GetStatusManager() : null;
        if (sm != null)
        {
            // NumValidStatuses 是遊戲寫入的 byte，Status 是 FixedSizeArray60（Actor.Statuses 同樣是 60）：
            // 兩者無結構保證，夾到容量內，越界時安靜少讀。
            var numStatuses = Math.Min((int)sm->NumValidStatuses, sm->Status.Length);
            for (var i = 0; i < numStatuses; ++i)
            {
                // note: sometimes (Ocean Fishing) remaining-time is weird (I assume too large?) and causes exception in AddSeconds - so we just clamp it to some reasonable range
                // note: self-cast buffs with duration X will have duration -X until EffectResult (~0.6s later); see autorotation for more details
                ActorStatus curStatus = new();
                ref var s = ref sm->Status[i];
                if (s.StatusId != 0)
                {
                    var dur = Math.Min(Math.Abs(s.RemainingTime), 100000);
                    curStatus.ID = s.StatusId;
                    curStatus.SourceID = SanitizedObjectID(s.SourceObject);
                    curStatus.Extra = s.Param;
                    curStatus.ExpireAt = _ws.CurrentTime.AddSeconds(dur);
                }
                UpdateActorStatus(act, i, ref curStatus);
            }
        }

        var aeh = chr != null ? chr->GetActionEffectHandler() : null;
        if (aeh != null)
        {
            var len = aeh->IncomingEffects.Length;
            for (var i = 0; i < len; ++i)
            {
                ref var eff = ref aeh->IncomingEffects[i];
                ref var prev = ref act.IncomingEffects[i];
                if ((prev.GlobalSequence, prev.TargetIndex) != (eff.GlobalSequence != 0 ? (eff.GlobalSequence, eff.TargetIndex) : (0, 0)))
                {
                    var effects = new ActionEffects();
                    for (var j = 0; j < ActionEffects.MaxCount; ++j)
                        effects[j] = *(ulong*)eff.Effects.Effects.GetPointer(j);
                    _ws.Execute(new ActorState.OpIncomingEffect(act.InstanceID, i, new(eff.GlobalSequence, eff.TargetIndex, eff.Source, new((ActionType)eff.ActionType, eff.ActionId), effects)));
                }
            }
        }
    }

    private void UpdateActorCastInfo(Actor act, ActorCastInfo? cast)
    {
        var castInfo = act.CastInfo;
        if (cast == null && castInfo == null)
            return; // was not casting and is not casting

        if (cast != null && castInfo != null && cast.Action == castInfo.Action && cast.TargetID == castInfo.TargetID && cast.TotalTime == castInfo.TotalTime && Math.Abs(cast.ElapsedTime - castInfo.ElapsedTime) < 0.2)
        {
            // continuing casting same spell
            // TODO: consider *not* ignoring elapsed differences, these probably mean we're doing something wrong...
            castInfo.ElapsedTime = cast.ElapsedTime;
            return;
        }

        // update cast info
        _ws.Execute(new ActorState.OpCastInfo(act.InstanceID, cast));
    }

    private void UpdateActorStatus(Actor act, int index, ref readonly ActorStatus value)
    {
        // note: some statuses have non-zero remaining time but never tick down (e.g. FC buffs); currently we ignore that fact, to avoid log spam...
        // note: RemainingTime is not monotonously decreasing (I assume because it is really calculated by game and frametime fluctuates...), we ignore 'slight' duration increases (<1 sec)
        var prev = act.Statuses[index];
        if (prev.ID == value.ID && prev.SourceID == value.SourceID && prev.Extra == value.Extra && (value.ExpireAt - prev.ExpireAt).TotalSeconds <= 1)
        {
            act.Statuses[index].ExpireAt = value.ExpireAt;
            return;
        }

        // update status info
        _ws.Execute(new ActorState.OpStatus(act.InstanceID, index, value));
    }

    private unsafe void UpdateParty()
    {
        var replay = Service.Condition[ConditionFlag.DutyRecorderPlayback];
        var group = GroupManager.Instance()->GetGroup(replay);

        // update party members
        var playerMember = UpdatePartyPlayer(replay, group);
        UpdatePartyNormal(group, playerMember);
        UpdatePartyAlliance(group);
        UpdatePartyNPCs();

        // update limit break
        var lb = LimitBreakController.Instance();
        if (_ws.Party.LimitBreakCur != lb->CurrentUnits || _ws.Party.LimitBreakMax != lb->BarUnits)
            _ws.Execute(new PartyState.OpLimitBreakChange(lb->CurrentUnits, lb->BarUnits));
    }

    // returns player entry in game's group
    private unsafe PartyMember* UpdatePartyPlayer(bool recorderPlaybackMode, GroupManager.Group* group)
    {
        // in worldstate, player is always in slot #0
        // in game, there are several considerations:
        // - PlayerState contains character data as long as player is logged in; in playback mode, it contains actual logged-in player rather than replay's POV
        // - objecttable entry #0 is always a player; in playback mode, it contains POV object; however, sometimes that object can be non-existent (eg during zone transitions)
        // - group manager contains player's entry at arbitrary position; it can be set before player's object is created, and it's not present while solo
        var player = PartyState.EmptySlot;

        var pc = (Character*)GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (pc != null && !pc->IsCharacter())
        {
            Service.Log($"[WSG] Object #0 is not a character, this should never happen");
            pc = null;
        }

        if (!recorderPlaybackMode)
        {
            // in normal mode, the primary data source is playerstate
            var ui = UIState.Instance();
            if (ui->PlayerState.IsLoaded)
            {
                var inCutscene = Service.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Service.Condition[ConditionFlag.WatchingCutscene78] || Service.Condition[ConditionFlag.Occupied33] || Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.OccupiedInQuestEvent];
                player = new(ui->PlayerState.ContentId, ui->PlayerState.EntityId, inCutscene, ui->PlayerState.CharacterNameString);
                if (pc != null && (pc->ContentId != player.ContentId || pc->EntityId != player.InstanceId))
                    Service.Log($"[WSG] Object #0 is valid ({pc->AccountId:X}.{pc->ContentId:X}, {pc->EntityId:X8} '{pc->NameString}') but different from playerstate ({player})");
            }
            else
            {
                // player not logged in, just do some sanity checks
                if (pc != null)
                    Service.Log($"[WSG] Object #0 is valid ({pc->AccountId:X}.{pc->ContentId:X}, {pc->EntityId:X8} '{pc->NameString}') while player is not logged in");
                if (group != null && group->MemberCount > 0)
                    Service.Log($"[WGS] Group is non-empty while player is not logged in");
            }
        }
        else
        {
            // in playback mode, the primary data source is object #0
            if (pc != null)
            {
                player = new(pc->ContentId, pc->EntityId, false, pc->NameString);
            }
            // else: just assume there's no player for now...
        }

        var member = player.InstanceId != default && group != null ? group->GetPartyMemberByEntityId((uint)player.InstanceId) : null;
        ReportPartyLookupMismatch(group, player, member);
        if (member != null)
            player.InCutscene |= (member->Flags & 0x10) != default;
        UpdatePartySlot(PartyState.PlayerSlot, player);
        return member;
    }

    // 台服診斷(只觀測、不改行為)。
    // cycleapple 的 fork 主張「GroupManager 的 entity-id 查表在台服 API13 不可靠」並據此把這裡改成手動掃描,
    // 但離線反組譯不支持那個主張:台服執行檔裡 GetPartyMemberByEntityId / GetPartyMemberByContentId
    // 兩支的特徵碼各自唯一命中,函式語意也正確 —— 逐格比對 EntityId(+0x400)/ContentId(+0x3F8)、
    // 上限取 MemberCount(+0x7FDC)、步長 0x490,全部與 FFXIVClientStructs 的宣告相符。
    // 既然沒有離線證據,就不在每幀路徑上照抄那個改寫;改成在「真的發生」時留一筆 Information,
    // 把不可證的假設變成可判定的問題(使用者跑 LogLevel 1,收得到 Information)。
    private bool _reportedPartyLookupMismatch;

    private unsafe void ReportPartyLookupMismatch(GroupManager.Group* group, PartyState.Member player, PartyMember* member)
    {
        // 查表成功,或根本沒有可查的前提(沒隊伍/沒 ContentId/沒 EntityId)都不算異常
        if (member != null || group == null || group->MemberCount == 0 || player.ContentId == default || player.InstanceId == default)
        {
            _reportedPartyLookupMismatch = false;
            return;
        }

        // entity-id 查表回 null —— 用 ContentId 手動掃一遍,確認玩家是不是真的在隊伍裡
        var found = -1;
        for (var i = 0; i < group->MemberCount; ++i)
        {
            if (group->PartyMembers.GetPointer(i)->ContentId == player.ContentId)
            {
                found = i;
                break;
            }
        }

        if (found < 0)
        {
            // 玩家確實不在這個 group 裡(單人、剛換區、跨區隊友),屬於正常狀態
            _reportedPartyLookupMismatch = false;
            return;
        }

        if (_reportedPartyLookupMismatch)
            return; // 同一段狀態只回報一次,不要每幀刷

        _reportedPartyLookupMismatch = true;
        var m = group->PartyMembers.GetPointer(found);
        Service.Logger.Information($"[BMR][隊伍同步] GetPartyMemberByEntityId 對玩家回 null,但以 ContentId 掃描在第 {found} 格找得到(隊伍人數 {group->MemberCount})。玩家 EntityId={player.InstanceId:X8}、該格 EntityId={m->EntityId:X8}、ContentId={player.ContentId:X}。若這行反覆出現,代表台服的 entity-id 查表確實不可靠,隊伍身分定位(坦克輪替/指向分配)會受影響,屆時再改成手動掃描。");
    }

    private unsafe void UpdatePartyNormal(GroupManager.Group* group, PartyMember* player)
    {
        if (group == null)
            return;

        // first iterate over previous members, search for match in game state, and reconcile differences - update or remove
        for (var i = PartyState.PlayerSlot + 1; i < PartyState.MaxPartySize; ++i)
        {
            ref var m = ref _ws.Party.Members[i];
            if (m.ContentId != 0)
            {
                // slot was occupied by player => see if it's still in party; either update to current state or clear if it's no longer in party
                var member = group->GetPartyMemberByContentId(m.ContentId);
                UpdatePartySlot(i, BuildPartyMember(member));
            }
            else if (m.InstanceId != 0)
            {
                // slot was occupied by trust => see if it's still in party
                if (!HasBuddy(m.InstanceId))
                    UpdatePartySlot(i, PartyState.EmptySlot); // buddy is no longer in party => clear slot
                // else: no reason to update...
            }
            // else: slot was empty, skip
        }

        // now iterate through game state and add new members; note that there's no need to update existing, it was done in the previous loop
        for (var i = 0; i < group->MemberCount; ++i)
        {
            var member = group->PartyMembers.GetPointer(i);
            if ((player == null || member->ContentId != player->ContentId) && Array.FindIndex(_ws.Party.Members, m => m.ContentId == member->ContentId) < 0)
                AddPartyMember(BuildPartyMember(member));
            // else: member is either a player (it was handled by a different function) or already exists in party state
        }
        // consider buddies as party members too
        var ui = UIState.Instance();
        var len = ui->Buddy.DutyHelperInfo.ENpcIds.Length;
        for (var i = 0; i < len; ++i)
        {
            ref var instanceID = ref ui->Buddy.DutyHelperInfo.DutyHelpers[i].EntityId;
            if (instanceID != InvalidEntityId && _ws.Party.FindSlot(instanceID) < 0)
            {
                var obj = GameObjectManager.Instance()->Objects.GetObjectByEntityId(instanceID);
                AddPartyMember(new(0, instanceID, false, obj != null ? obj->NameString : ""));
            }
            // else: buddy is non-existent or already updated, skip
        }
    }

    private unsafe void UpdatePartyAlliance(GroupManager.Group* group)
    {
        if (group == null)
            return;

        // note: we don't support small-group alliance (should we?)
        // unlike normal party, game's alliance slots never change, so we just keep 1:1 mapping
        var isNormalAlliance = group->IsAlliance && !group->IsSmallGroupAlliance;
        for (var i = PartyState.MaxPartySize; i < PartyState.MaxAllianceSize; ++i)
        {
            var member = isNormalAlliance ? group->AllianceMembers.GetPointer(i - PartyState.MaxPartySize) : null;
            if (member != null && !member->IsValidAllianceMember())
                member = null;
            UpdatePartySlot(i, BuildPartyMember(member));
        }
    }

    private unsafe void UpdatePartyNPCs()
    {
        for (var i = PartyState.MaxAllianceSize; i < PartyState.MaxAllies; ++i)
        {
            ref var m = ref _ws.Party.Members[i];
            if (m.InstanceId != 0)
            {
                var actor = _ws.Actors.Find(m.InstanceId);
                if (actor == null || !actor.IsFriendlyNPC)
                    UpdatePartySlot(i, PartyState.EmptySlot);
            }
        }

        foreach (var actor in _ws.Actors)
        {
            if (!actor.IsFriendlyNPC)
                continue;
            if (_ws.Party.FindSlot(actor.InstanceID) == -1)
            {
                var slot = FindFreePartySlot(PartyState.MaxAllianceSize, PartyState.MaxAllies);
                if (slot > 0)
                    UpdatePartySlot(slot, new PartyState.Member(0, actor.InstanceID, false, actor.Name));
                // else
                //     Service.Log($"[WorldState]  slot for allied NPC {actor.InstanceID:X}");
            }
        }
    }

    private unsafe bool HasBuddy(ulong instanceID)
    {
        var ui = UIState.Instance();
        var len = ui->Buddy.DutyHelperInfo.ENpcIds.Length;
        for (var i = 0; i < len; ++i)
            if (ui->Buddy.DutyHelperInfo.DutyHelpers[i].EntityId == instanceID)
                return true;
        return false;
    }

    private int FindFreePartySlot(int firstSlot, int lastSlot)
    {
        for (var i = firstSlot; i < lastSlot; ++i)
            if (!_ws.Party.Members[i].IsValid())
                return i;
        return -1;
    }

    private unsafe PartyState.Member BuildPartyMember(PartyMember* m) => m != null ? new(m->ContentId, m->EntityId, (m->Flags & 0x10) != 0, m->NameString) : PartyState.EmptySlot;

    private void AddPartyMember(PartyState.Member m)
    {
        var freeSlot = FindFreePartySlot(1, PartyState.MaxPartySize);
        if (freeSlot >= 0)
            _ws.Execute(new PartyState.OpModify(freeSlot, m));
        // else
        //     Service.Log($"[WorldState] Failed to find empty slot for party member {m.ContentId:X}:{m.InstanceId:X}");
    }

    private void UpdatePartySlot(int slot, PartyState.Member m)
    {
        if (_ws.Party.Members[slot] != m)
            _ws.Execute(new PartyState.OpModify(slot, m));
    }

    [StructLayout(LayoutKind.Explicit)]
    private unsafe struct CharacterContainer
    {
        [FieldOffset(0x8)] public Character* Character;
    }

    private unsafe void UpdateClient()
    {
        var countdownAgent = AgentCountDownSettingDialog.Instance();
        float? countdown = countdownAgent != null && countdownAgent->Active ? countdownAgent->TimeRemaining : null;
        if (_ws.Client.CountdownRemaining != countdown)
            _ws.Execute(new ClientState.OpCountdownChange(countdown));

        var actionManager = ActionManager.Instance();
        if (_ws.Client.AnimationLock != actionManager->AnimationLock)
            _ws.Execute(new ClientState.OpAnimationLockChange(actionManager->AnimationLock));

        var combo = new ClientState.Combo(actionManager->Combo.Action, actionManager->Combo.Timer);
        if (_ws.Client.ComboState != combo)
            _ws.Execute(new ClientState.OpComboChange(combo));

        var uiState = UIState.Instance();
        var stats = new ClientState.Stats(uiState->PlayerState.Attributes[45], uiState->PlayerState.Attributes[46], uiState->PlayerState.Attributes[47]);
        if (_ws.Client.PlayerStats != stats)
            _ws.Execute(new ClientState.OpPlayerStatsChange(stats));

        var pc = (Character*)GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (pc != null)
        {
            // 🔑 玩家的基礎移動速度＝走路控制器的 BaseMovementSpeed。
            //    0x7118 ＝ 走路控制器在 Control 內的偏移 0x70C0 ＋ 欄位偏移 0x58。
            //    來源（台服 7.20 離線鑑識 2026-08-30，兩個互相獨立的來源一致）：
            //      ① 0x70C0：把 Control 與 PlayerMoveControllerWalk 兩個單例的 lea rip-relative
            //         靜態位址各自解出來相減（Control=0x142903460、走路控制器=0x14290A520）。
            //         工具 tools/sigscan/lea_static_delta.py，帶正負校準閘門。
            //      ② 0x58：OmenTools 的 PlayerMoveControllerWalk 欄位表 BaseMovementSpeed（decimal 88）。
            //         同一份表的 IsMoving（0x3C）已被遊戲自己的 GetTargetSpeed（0x141712970）第一條指令
            //         `cmp byte ptr [rcx+0x3c], 0` 逐字證實 ⇒ 這份欄位表對台服是可信的。
            //    🔴 舊值 0x7108 ＝ 走路控制器 +0x48，落在 CurrentSpeed（+0x44）與 BaseMovementSpeed（+0x58）
            //       之間的**未定義欄位**，比正確欄位少 0x10。實機表現是每幀在 0 與 0.45 之間交替、
            //       從來不是玩家的真實速度（約 6 碼/秒），一天洗出 9 萬行 log。
            //    ⚠️ 坐騎／飛行／游泳的基礎速度**不在這個欄位**，本行在那些狀態下讀到的仍是走路值。
            //       這是刻意的：另一個候選來源（走路控制器 +0x50 指向的子結構）無法離線證明它真的是指標，
            //       而每幀解一個沒被證實的指標＝AccessViolation＝try/catch 攔不到的當場崩潰。
            //       讀到偏低的速度只會讓尋路的 ETA 保守一點，不會壞掉；崩潰會。
            var baseSpeed = *(float*)((nint)Control.Instance() + 0x7118);
            var c8 = new CharacterContainer() { Character = pc };
            var factor = _calculateMoveSpeedMulti((ContainerInterface*)&c8);
            var speed = baseSpeed * factor;
            if (_ws.Client.MoveSpeed != speed)
                _ws.Execute(new ClientState.OpMoveSpeedChange(speed));
        }

        Span<Cooldown> cooldowns = stackalloc Cooldown[_ws.Client.Cooldowns.Length];
        _amex.GetCooldowns(cooldowns);
        if (!MemoryExtensions.SequenceEqual(_ws.Client.Cooldowns.AsSpan(), cooldowns))
        {
            if (cooldowns.IndexOfAnyExcept(default(Cooldown)) < 0)
                _ws.Execute(new ClientState.OpCooldown(true, []));
            else
                _ws.Execute(new ClientState.OpCooldown(false, CalcCooldownDifference(cooldowns, _ws.Client.Cooldowns.AsSpan())));
        }

        var dutyActions = _amex.GetDutyActions();
        if (!MemoryExtensions.SequenceEqual(_ws.Client.DutyActions.AsSpan(), dutyActions))
            _ws.Execute(new ClientState.OpDutyActionsChange(dutyActions));

        Span<byte> bozjaHolster = stackalloc byte[_ws.Client.BozjaHolster.Length];
        bozjaHolster.Clear();
        var bozjaState = PublicContentBozja.GetState();
        if (bozjaState != null)
            foreach (var action in bozjaState->HolsterActions)
                if (action != 0)
                    ++bozjaHolster[action];
        if (!MemoryExtensions.SequenceEqual(_ws.Client.BozjaHolster.AsSpan(), bozjaHolster))
            _ws.Execute(new ClientState.OpBozjaHolsterChange(CalcBozjaHolster(bozjaHolster)));

        if (!MemoryExtensions.SequenceEqual(_ws.Client.BlueMageSpells.AsSpan(), actionManager->BlueMageActions))
            _ws.Execute(new ClientState.OpBlueMageSpellsChange([.. actionManager->BlueMageActions]));

        var levels = uiState->PlayerState.ClassJobLevels;
        if (!MemoryExtensions.SequenceEqual(_ws.Client.ClassJobLevels.AsSpan(), levels))
            _ws.Execute(new ClientState.OpClassJobLevelsChange([.. levels]));

        // 🔴 FateManager.Instance() 是 [StaticAddress(…, isPointer: true)]，回傳的是全域指標槽的
        //    內容，管理器還沒建好時合法為 null（原本直接 ->CurrentFate ＝ AccessViolation，
        //    而 AVE 攔不到）。fail-closed：拿不到管理器就等同「目前沒有 FATE」。
        var fateManager = FateManager.Instance();
        var curFate = fateManager != null ? fateManager->CurrentFate : null;
        ClientState.Fate activeFate = curFate != null ? new(curFate->FateId, curFate->Location, curFate->Radius) : default;
        if (_ws.Client.ActiveFate != activeFate)
            _ws.Execute(new ClientState.OpActiveFateChange(activeFate));

        var petinfo = uiState->Buddy.PetInfo;
        var pet = new ClientState.Pet(petinfo.Pet->EntityId, petinfo.Order, petinfo.Stance);
        if (_ws.Client.ActivePet != pet)
            _ws.Execute(new ClientState.OpActivePetChange(pet));

        var focusTarget = TargetSystem.Instance()->FocusTarget;
        var focusTargetId = focusTarget != null ? SanitizedObjectID(focusTarget->GetGameObjectId()) : 0;
        if (_ws.Client.FocusTargetId != focusTargetId)
            _ws.Execute(new ClientState.OpFocusTargetChange(focusTargetId));

        if (MovementOverride.ForcedMovementDirection != null) // sig 失效時為 null(降級停用),見 MovementOverride
        {
            var forcedMovementDir = MovementOverride.ForcedMovementDirection->Radians();
            if (_ws.Client.ForcedMovementDirection != forcedMovementDir)
                _ws.Execute(new ClientState.OpForcedMovementDirectionChange(forcedMovementDir));
        }

        var contentKeyValue = uiState->PlayerState.ContentKeyValueData;
        var ckArray = new uint[]
        {
            contentKeyValue[0].Item1,
            contentKeyValue[0].Item2,
            contentKeyValue[1].Item1,
            contentKeyValue[1].Item2,
            contentKeyValue[2].Item1,
            contentKeyValue[2].Item2
        };
        if (!MemoryExtensions.SequenceEqual(ckArray, _ws.Client.ContentKeyValueData))
            _ws.Execute(new ClientState.OpContentKVDataChange(ckArray));

        var hate = uiState->Hate;
        var hatePrimary = hate.HateTargetId;
        var hateTargets = new ClientState.Hate[32];
        // HateArrayLength 是遊戲寫入的 int，HateInfo 是 FixedSizeArray32（受端 hateTargets 也是 32）：
        // 兩者無結構保證，夾到容量內。
        var hateLen = Math.Min(hate.HateArrayLength, Math.Min(hate.HateInfo.Length, hateTargets.Length));
        for (var i = 0; i < hateLen; ++i)
            hateTargets[i] = new(hate.HateInfo[i].EntityId, hate.HateInfo[i].Enmity);

        if (hatePrimary != _ws.Client.CurrentTargetHate.InstanceID || !MemoryExtensions.SequenceEqual(hateTargets, _ws.Client.CurrentTargetHate.Targets))
            _ws.Execute(new ClientState.OpHateChange(hatePrimary, hateTargets));

        var timers = actionManager->ProcTimers[1..];
        if (!MemoryExtensions.SequenceEqual(timers, _ws.Client.ProcTimers))
            _ws.Execute(new ClientState.OpProcTimersChange(timers.ToArray()));
    }

    private unsafe void UpdateDeepDungeon()
    {
        // 🔴 EventFramework.Instance() 是 [StaticAddress(…, isPointer: true)]，合法可為 null
        //    （切換區域／登出過程中全域槽會是空的）。原本直接 -> 呼叫成員函式＝AccessViolation。
        //    fail-closed：拿不到 EventFramework 就當作「不在深牢」，走下面既有的清理分支
        //    ——與真的離開深牢語意一致，不會留下上一層的殘留狀態。
        var eventFramework = EventFramework.Instance();
        var dd = eventFramework != null ? eventFramework->GetInstanceContentDeepDungeon() : null;
        if (dd != null)
        {
            var currentId = (DeepDungeonState.DungeonType)dd->DeepDungeonId;
            var fullUpdate = currentId != _ws.DeepDungeon.DungeonId;

            // 每次（重新）進入深牢印一次結構基底位址：台服的 InstanceContentDeepDungeon 欄位偏移
            // 是照國際服 CS 定義推的，對不上時整份深牢資料會是垃圾值而不會拋例外。要使用者回報這行
            // 才查得出來，所以走 Information（使用者的 LogLevel 是 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒）。
            // 📌 一次進場只印一行，不是每幀——放心留在正式版。
            if (fullUpdate || !_loggedDeepDungeonBase)
            {
                _loggedDeepDungeonBase = true;
                Service.Logger.Information($"[DD] InstanceContentDeepDungeon 基底位址 = 0x{(nint)dd:X}、DeepDungeonId = {(byte)currentId}、樓層 = {dd->Floor}");
            }

            var progress = new DeepDungeonState.DungeonProgress(dd->Floor, dd->ActiveLayoutIndex, dd->WeaponLevel, dd->ArmorLevel, dd->SyncedGearLevel, dd->HoardCount, dd->ReturnProgress, dd->PassageProgress);
            if (fullUpdate || progress != _ws.DeepDungeon.Progress)
                _ws.Execute(new DeepDungeonState.OpProgressChange(currentId, progress));

            if (fullUpdate || !MemoryExtensions.SequenceEqual(_ws.DeepDungeon.Rooms.AsSpan(), dd->MapData))
                _ws.Execute(new DeepDungeonState.OpMapDataChange(dd->MapData.ToArray()));

            Span<DeepDungeonState.PartyMember> party = stackalloc DeepDungeonState.PartyMember[DeepDungeonState.NumPartyMembers];
            // 🔴 原值要在 Sanitize **之前**留下來，理由與寶箱那份相同：
            //    SanitizeDeepDungeonRoom 把負值（＝遊戲說「不在任何房間」）壓成 0，
            //    壓完就再也分不出「不在任何房間」與「第 0 間房」——而所有版面的房號 0
            //    都沒有中心座標，於是座標校驗閘門會靜默地卡在 Unknown。
            Span<sbyte> rawPartyRooms = stackalloc sbyte[DeepDungeonState.NumPartyMembers];
            for (var i = 0; i < DeepDungeonState.NumPartyMembers; ++i)
            {
                ref var p = ref dd->Party[i];
                rawPartyRooms[i] = p.RoomIndex;
                party[i] = new(SanitizedObjectID(p.EntityId), SanitizeDeepDungeonRoom(p.RoomIndex));
            }
            if (fullUpdate || !MemoryExtensions.SequenceEqual(_ws.DeepDungeon.Party.AsSpan(), party))
                _ws.Execute(new DeepDungeonState.OpPartyStateChange(party.ToArray()));

            // ── 隊伍房號原值診斷 ──────────────────────────────────────────
            // 只在原值真的變了、而且沒超過每層上限時印。上限是為了擋「大房間裡房號每幀在
            // -1 與某個房號之間跳」那種會刷爆 log 的情況——被擋掉本身也是訊息（看行數就知道）。
            if (!MemoryExtensions.SequenceEqual(_ddDiagPartyRooms.AsSpan(), rawPartyRooms))
            {
                rawPartyRooms.CopyTo(_ddDiagPartyRooms.AsSpan());
                if (dd->Floor != _ddDiagPartyFloor)
                {
                    _ddDiagPartyFloor = dd->Floor;
                    _ddDiagPartyLines = 0;
                }
                if (++_ddDiagPartyLines <= DDPartyDiagLinesPerFloor)
                {
                    var pb = new StringBuilder(128);
                    pb.Append("[DD] 隊伍房號原值 樓層 ").Append(dd->Floor).Append(" Party[0..3].RoomIndex=");
                    for (var i = 0; i < DeepDungeonState.NumPartyMembers; ++i)
                        pb.Append(i == 0 ? "" : ", ").Append(rawPartyRooms[i]);
                    // 帶上本人座標：大廳層（無內牆 12 格）A/B 兩面座標表都不涵蓋，座標校驗整層
                    // Mismatch（2026-08-13 樓層 15/25 實證；房號原值一路正常＝「-1 塌陷」假設已排除）。
                    // 房號變化時的 (座標, 房號) 樣本正是擬合大廳格心所需的量測資料。
                    if (_ws.Party.Player() is { } pcActor)
                        pb.Append(" 本人 (").Append(pcActor.PosRot.X.ToString("f1")).Append(", ")
                          .Append(pcActor.PosRot.Z.ToString("f1")).Append(')');
                    if (_ddDiagPartyLines == DDPartyDiagLinesPerFloor)
                        pb.Append("（已達本層上限，之後不再記錄）");
                    Service.Logger.Information(pb.ToString());
                }
            }

            // ── 房間位置普查 ──────────────────────────────────────────────
            // 🔑 上面那份「隊伍房號原值」只在**房號變了**的那一幀才記，所以它的每一筆都落在
            //    房間交界上——拿來擬合房間中心是**病態的**：2026-08-14 離線實測，用它對一個
            //    格線已知正確的一般層（樓層 65）做最小平方擬合，留一法一致率是 **0.000**
            //    （工具 ~/.claude/tools/bmr_dd_hall_gridfit.py，它的校準閘門就是為此擋下來的）。
            //    要擬合中心需要的是**房間內部**的位置，也就是定時取樣而不是變化時取樣。
            // 📌 這一段只收資料、印一行，不參與任何決策。目標是「大廳層」（無內牆的 12 格版面，
            //    實測樓層 15／25／64）——那種版面 A／B 兩面座標表都不涵蓋，座標校驗整層不通過，
            //    於是小地圖只畫得出摘要、畫不出寶箱點位。
            // ⚠️ 一般層也照收：它們的表本來就通過校驗，正好當「擬合方法對不對」的對照組。
            //    沒有對照組就分不出「擬合失敗」與「擬合程式自己寫錯」。
            if (dd->Floor != _ddCensusFloor)
            {
                FlushDeepDungeonRoomCensus();
                _ddCensusFloor = dd->Floor;
                _ddCensusLayout = dd->ActiveLayoutIndex;
                _ddCensusFloorSince = _ws.CurrentTime;
                Array.Clear(_ddCensus);
            }
            // 🔴 換層後要先靜置：剛進場那一瞬間角色還在傳送中，回報的是一個與房號完全無關的
            //    固定位置（實機看到的 (0, -300) 那一組，離房中心 600~740 碼）。那種樣本會把
            //    整組平均值拉歪，而且看起來只是「離群值大了點」，不像壞掉。
            else if ((_ws.CurrentTime - _ddCensusFloorSince).TotalSeconds >= DDCensusSettleSeconds
                     && _ws.CurrentTime >= _ddCensusNextSample
                     && _ws.Party.Player() is { } censusPlayer)
            {
                for (var i = 0; i < DeepDungeonState.NumPartyMembers; ++i)
                {
                    ref var p = ref dd->Party[i];
                    if (SanitizedObjectID(p.EntityId) != censusPlayer.InstanceID)
                        continue;
                    if ((uint)p.RoomIndex >= DeepDungeonState.NumRooms)
                        break; // 遊戲說「不在任何房間」（負值）或超出範圍——那不是量測，是沒有量測
                    _ddCensusNextSample = _ws.CurrentTime.AddSeconds(DDCensusSampleInterval);
                    ref var cell = ref _ddCensus[p.RoomIndex];
                    var px = censusPlayer.PosRot.X;
                    var pz = censusPlayer.PosRot.Z;
                    if (cell.N++ == 0)
                    {
                        cell.MinX = cell.MaxX = px;
                        cell.MinZ = cell.MaxZ = pz;
                    }
                    else
                    {
                        cell.MinX = Math.Min(cell.MinX, px);
                        cell.MaxX = Math.Max(cell.MaxX, px);
                        cell.MinZ = Math.Min(cell.MinZ, pz);
                        cell.MaxZ = Math.Max(cell.MaxZ, pz);
                    }
                    cell.SumX += px;
                    cell.SumZ += pz;
                    break;
                }
            }

            Span<DeepDungeonState.PomanderState> pomanders = stackalloc DeepDungeonState.PomanderState[DeepDungeonState.NumPomanderSlots];
            for (var i = 0; i < DeepDungeonState.NumPomanderSlots; ++i)
            {
                ref var item = ref dd->Items[i];
                pomanders[i] = new(item.Count, item.Flags);
            }
            if (fullUpdate || !MemoryExtensions.SequenceEqual(_ws.DeepDungeon.Pomanders.AsSpan(), pomanders))
                _ws.Execute(new DeepDungeonState.OpPomandersChange(pomanders.ToArray()));

            Span<DeepDungeonState.Chest> chests = stackalloc DeepDungeonState.Chest[DeepDungeonState.NumChests];
            // 🔴 原值要在 Sanitize **之前**留下來：SanitizeDeepDungeonRoom 把 0xFF（空槽）壓成 0，
            //    壓完就再也分不出「空槽」與「第 0 間房的寶箱」——而那正好是我們要量的東西。
            Span<byte> rawChestRooms = stackalloc byte[DeepDungeonState.NumChests];
            var nonEmptyChests = 0;
            for (var i = 0; i < DeepDungeonState.NumChests; ++i)
            {
                ref var c = ref dd->Chests[i];
                rawChestRooms[i] = (byte)c.RoomIndex;
                if (c.ChestType != 0)
                    ++nonEmptyChests;
                chests[i] = new(c.ChestType, SanitizeDeepDungeonRoom(c.RoomIndex));
            }
            if (fullUpdate || !MemoryExtensions.SequenceEqual(_ws.DeepDungeon.Chests.AsSpan(), chests))
                _ws.Execute(new DeepDungeonState.OpChestsChange(chests.ToArray()));

            // ── 原值診斷 ──────────────────────────────────────────────────
            // 「地圖上的大房間到底有沒有寶箱，是遊戲根本沒送、還是我們畫丟了」——
            // 這是唯一能離線分辨的量測，所以印遊戲送過來的**原始** (Type,Room) 與 25 格 MapData。
            // 節流：進場、換層，以及「非空寶箱數變多」時各一次（開箱只會變少，不會刷版面）。
            // 走 Information，因為要靠使用者的 log 回報才看得到（LogLevel 1）。
            if (fullUpdate || dd->Floor != _ddDiagFloor || nonEmptyChests > _ddDiagChestCount)
            {
                var newFloor = fullUpdate || dd->Floor != _ddDiagFloor;
                _ddDiagFloor = dd->Floor;
                _ddDiagChestCount = nonEmptyChests;

                var sb = new StringBuilder(320);
                sb.Append("[DD] 寶箱原值 樓層 ").Append(dd->Floor).Append(" 版面 ").Append(dd->ActiveLayoutIndex).Append(" Chests[0..15]=");
                for (var i = 0; i < DeepDungeonState.NumChests; ++i)
                    sb.Append('(').Append(dd->Chests[i].ChestType).Append(',').Append(rawChestRooms[i]).Append(')');
                Service.Logger.Information(sb.ToString());

                if (newFloor)
                {
                    sb.Clear();
                    sb.Append("[DD] MapData 原值 樓層 ").Append(dd->Floor).Append(' ');
                    for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
                    {
                        if (i % 5 == 0)
                            sb.Append('[');
                        sb.Append(((ushort)dd->MapData[i]).ToString("X2"));
                        sb.Append(i % 5 == 4 ? ']' : ' ');
                    }
                    Service.Logger.Information(sb.ToString());
                }
            }

            if (fullUpdate || !MemoryExtensions.SequenceEqual(_ws.DeepDungeon.Magicite.AsSpan(), dd->Magicite))
                _ws.Execute(new DeepDungeonState.OpMagiciteChange(dd->Magicite.ToArray()));
        }
        else if (_ws.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.None)
        {
            // exiting deep dungeon, clean up all state
            // 離開深牢也要把最後一層的普查倒出來，否則最後一層（含大廳層）永遠記不到。
            FlushDeepDungeonRoomCensus();
            _ddCensusFloor = 255;
            _ws.Execute(new DeepDungeonState.OpProgressChange(DeepDungeonState.DungeonType.None, default));
        }
        // else: we were and still are outside deep dungeon, nothing to do
    }

    /// <summary>寶箱原值診斷用的節流狀態：上次印過的樓層與非空寶箱數。</summary>
    private byte _ddDiagFloor = 255;
    private int _ddDiagChestCount = -1;

    /// <summary>隊伍房號原值診斷用的節流狀態：上次印過的四個原值、樓層與本層已記了幾行。</summary>
    /// <remarks>初值刻意用 <c>sbyte.MinValue</c>，讓進場第一次一定會印（0 與 -1 都是會出現的合法值）。</remarks>
    private readonly sbyte[] _ddDiagPartyRooms = [sbyte.MinValue, sbyte.MinValue, sbyte.MinValue, sbyte.MinValue];
    private byte _ddDiagPartyFloor = 255;
    private int _ddDiagPartyLines;

    /// <summary>隊伍房號原值每層最多記幾行。</summary>
    private const int DDPartyDiagLinesPerFloor = 40;

    /// <summary>房間位置普查：某一間房收到的樣本統計。</summary>
    private struct DDRoomCensus
    {
        public int N;
        public double SumX, SumZ;
        public float MinX, MaxX, MinZ, MaxZ;
    }

    private readonly DDRoomCensus[] _ddCensus = new DDRoomCensus[DeepDungeonState.NumRooms];
    private byte _ddCensusFloor = 255;
    private byte _ddCensusLayout;
    private DateTime _ddCensusFloorSince;
    private DateTime _ddCensusNextSample;

    /// <summary>房間位置普查的取樣間隔（秒）。</summary>
    /// <remarks>
    /// 2 Hz。一層待個幾分鐘就是數百筆，足夠把每間房的平均位置壓到房間尺寸的零頭；
    /// 再密只是讓同一個站位重複計數（角色不動時樣本完全相同），拉不出更多資訊。
    /// </remarks>
    private const double DDCensusSampleInterval = 0.5d;

    /// <summary>換層後要靜置這麼久才開始取樣（秒）。</summary>
    private const double DDCensusSettleSeconds = 2d;

    /// <summary>
    /// 把上一層的房間位置普查倒成一行 log。
    /// </summary>
    /// <remarks>
    /// 🔴 走 <c>Information</c>：這是要靠使用者回報才看得到的量測（使用者的 LogLevel 是 1）。
    /// 一層只印一行，所以量很小。
    /// <para>
    /// 每一格印的是「樣本數／平均位置／半幅」。半幅是拿來判斷這一格的樣本到底散得多開——
    /// 平均值本身沒辦法告訴你它是「房間中心」還是「兩次穿門的中點」，半幅可以。
    /// </para>
    /// </remarks>
    private void FlushDeepDungeonRoomCensus()
    {
        if (_ddCensusFloor == 255)
            return;

        var total = 0;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
            total += _ddCensus[i].N;
        if (total == 0)
            return;

        var sb = new StringBuilder(512);
        sb.Append("[DD] 房間位置普查 樓層 ").Append(_ddCensusFloor)
          .Append(" 版面 ").Append(_ddCensusLayout)
          .Append(" 取樣 ").Append(total).Append(" 筆：");
        var first = true;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            ref var c = ref _ddCensus[i];
            if (c.N == 0)
                continue;
            if (!first)
                sb.Append('、');
            first = false;
            sb.Append('r').Append(i).Append(" n=").Append(c.N)
              .Append(" 心(").Append(((float)(c.SumX / c.N)).ToString("f1")).Append(", ")
              .Append(((float)(c.SumZ / c.N)).ToString("f1")).Append(')')
              .Append(" 幅(").Append(((c.MaxX - c.MinX) * 0.5f).ToString("f1")).Append(", ")
              .Append(((c.MaxZ - c.MinZ) * 0.5f).ToString("f1")).Append(')');
        }
        Service.Logger.Information(sb.ToString());
    }

    private byte SanitizeDeepDungeonRoom(sbyte room) => room < 0 ? (byte)0 : (byte)room;
    private ulong SanitizedObjectID(ulong raw) => raw != InvalidEntityId ? raw : 0;

    private void DispatchActorEvents(ulong instanceID)
    {
        var ops = _actorOps.GetValueOrDefault(instanceID);
        if (ops == null)
            return;
        var count = ops.Count;
        for (var i = 0; i < count; ++i)
        {
            _ws.Execute(ops[i]);
        }
        _actorOps.Remove(instanceID);
    }

    private List<(int, Cooldown)> CalcCooldownDifference(Span<Cooldown> values, ReadOnlySpan<Cooldown> reference)
    {
        var lenValues = values.Length;
        var lenReference = reference.Length;
        var max = lenValues < lenReference ? lenValues : lenReference;
        var res = new List<(int, Cooldown)>(max);
        for (int i = 0, cnt = max; i < cnt; ++i)
        {
            ref var value = ref values[i];
            if (value != reference[i])
                res.Add((i, value));
        }
        return res;
    }

    private List<(BozjaHolsterID, byte)> CalcBozjaHolster(Span<byte> contents)
    {
        var len = contents.Length;
        var res = new List<(BozjaHolsterID, byte)>(len);
        for (var i = 0; i < len; ++i)
        {
            ref var content = ref contents[i];
            if (content != 0)
                res.Add(((BozjaHolsterID)i, content));
        }
        return res;
    }

    private unsafe ClientState.Gauge GaugeData()
    {
        var curGauge = JobGaugeManager.Instance()->CurrentGauge;
        return curGauge != null ? new(Utils.ReadField<ulong>(curGauge, 8), Utils.ReadField<ulong>(curGauge, 16)) : default;
    }

    private unsafe void ServerIPCReceived(DateTime sendTimestamp, uint sourceServerActor, uint targetServerActor, ushort opcode, uint epoch, Span<byte> payload)
    {
        var id = _opcodeMap.ID(opcode);
        // targetServerActor is always a player?..
        var ipc = new NetworkState.ServerIPC(id, opcode, epoch, sourceServerActor, sendTimestamp, [.. payload]);
        if (_netConfig.Data.RecordServerPackets)
            _globalOps.Add(new NetworkState.OpServerIPC(ipc));
        if (_netConfig.Data.DumpServerPackets && (!_netConfig.Data.DumpServerPacketsPlayerOnly || sourceServerActor == UIState.Instance()->PlayerState.EntityId))
            _decoder.LogNode(_decoder.Decode(ipc, DateTime.UtcNow), "");
    }

    private unsafe void ClientIPCSent(uint opcode, Span<byte> payload)
    {
        if (_netConfig.Data.DumpClientPackets)
        {
            var sb = new StringBuilder($"Client IPC [0x{opcode:X4}]: data=");
            foreach (var b in payload)
                sb.Append($"{b:X2}");
            _decoder.LogNode(new(sb.ToString()), "");
        }
    }

    private void OnActionRequested(ClientActionRequest arg)
    {
        _globalOps.Add(new ClientState.OpActionRequest(arg));
    }

    private void OnActionEffect(ulong casterID, ActorCastEvent info)
    {
        _actorOps.GetOrAdd(casterID).Add(new ActorState.OpCastEvent(casterID, info));
    }

    private void OnEffectResult(ulong targetID, uint seq, int targetIndex)
    {
        _actorOps.GetOrAdd(targetID).Add(new ActorState.OpEffectResult(targetID, seq, targetIndex));
    }

    // 以下每一支 detour 都遵守同一個 fail-closed 約定（見 Util/DetourGuard.cs）：
    // 自訂邏輯進 try、catch 受管理例外後節流記一行 Information，**Original 一律照樣呼叫、照樣回傳其結果**。
    // ⚠️ 這不防 AccessViolationException（那攔不到），防的是受管理例外逸出到原生框架。
    //
    // 🔴 這一批用的是 Dalamud 原生的 Hook<T>（不是 Util/Hook.cs 的 HookAddress<T> 包裝），
    //    所以呼叫的是 **OriginalDisposeSafe 而不是 Original**：
    //    Dalamud/Hooking/Hook.cs 裡 Original 的文件註解寫明它會擲 ObjectDisposedException
    //    (Hook is already disposed)，而 OriginalDisposeSafe 在已 dispose 時改用
    //    Marshal.GetDelegateForFunctionPointer(this.address) 繞過它。
    //    detour 在外掛卸載、熱更新時仍可能觸發（Util/Hook.cs:13 對 HookAddress 寫的就是這件事），
    //    那一刻 Original 會擲例外並直接逸出到原生框架，遊戲會被帶走。
    //    未 dispose 時 OriginalDisposeSafe 逐字回傳 this.Original，行為完全相同、無額外配置。
    //
    // 📌 關於封包指標參數的判空（稽核工具會對這一批標 DEREF_PARAM，那是誤判，別再開一輪）：
    //    這裡除了 ActorCast 以外，每一支都是**先呼叫 Original、再讀封包**。Original 就是遊戲自己的
    //    處理函式，它已經解參考過同一個指標了 —— 指標若是 null，行程在進到我們的 try 之前就已經死在
    //    遊戲自己的碼裡。所以在那些位置補判空不會多擋下任何一次崩潰，只會讓下一輪稽核看不出差別。
    //    唯一有意義的位置是**我們比遊戲先碰到指標**的那支（ActorCast），已補在下面。
    //    ⚠️ 同理，count/長度欄位（EffectResult 的 packet[0]、MapEffect 的 *data、SystemLogMessage 的
    //    argCount）也都取自遊戲自己的封包，遊戲的 Original 用同一個值跑同一個迴圈，我們沒有更好的上界。

    private unsafe void ProcessPacketActorCastDetour(uint casterId, Network.ServerIPC.ActorCast* packet)
    {
        try
        {
            // 🔴 這是本檔唯一「我們比 Original 先解參考封包」的地方 —— 判空讓我們不會搶在遊戲之前
            //    因為 null 而崩掉（且崩在 BMR 的堆疊上）。非 null 時逐行等價。
            if (packet != null)
                _lastCastPositions[casterId] = Network.PacketDecoder.IntToFloatCoords(packet->PosX, packet->PosY, packet->PosZ);
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketActorCastDetour), ex);
        }
        _processPacketActorCastHook.OriginalDisposeSafe(casterId, packet);
    }

    private unsafe void ProcessPacketEffectResultDetour(uint targetID, byte* packet, byte replaying)
    {
        try
        {
            var count = packet[0];
            var p = (Network.ServerIPC.EffectResultEntry*)(packet + 4);
            for (var i = 0; i < count; ++i)
            {
                OnEffectResult(targetID, p->RelatedActionSequence, p->RelatedTargetIndex);
                ++p;
            }
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketEffectResultDetour), ex);
        }
        _processPacketEffectResultHook.OriginalDisposeSafe(targetID, packet, replaying);
    }

    private unsafe void ProcessPacketEffectResultBasicDetour(uint targetID, byte* packet, byte replaying)
    {
        try
        {
            var count = packet[0];
            var p = (Network.ServerIPC.EffectResultBasicEntry*)(packet + 4);
            for (var i = 0; i < count; ++i)
            {
                OnEffectResult(targetID, p->RelatedActionSequence, p->RelatedTargetIndex);
                ++p;
            }
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketEffectResultBasicDetour), ex);
        }
        _processPacketEffectResultBasicHook.OriginalDisposeSafe(targetID, packet, replaying);
    }

    private void ProcessPacketActorControlDetour(uint actorID, uint category, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, ulong targetID, byte replaying)
    {
        _processPacketActorControlHook.OriginalDisposeSafe(actorID, category, p1, p2, p3, p4, p5, p6, targetID, replaying);
        try
        {
            switch ((Network.ServerIPC.ActorControlCategory)category)
            {
                case Network.ServerIPC.ActorControlCategory.TargetIcon:
                    _actorOps.GetOrAdd(actorID).Add(new ActorState.OpIcon(actorID, p1 - Network.IDScramble.Delta, p2));
                    break;
                case Network.ServerIPC.ActorControlCategory.Tether:
                    _actorOps.GetOrAdd(actorID).Add(new ActorState.OpTether(actorID, new(p2, p3)));
                    break;
                case Network.ServerIPC.ActorControlCategory.TetherCancel:
                    // note: this seems to clear tether only if existing matches p2
                    _actorOps.GetOrAdd(actorID).Add(new ActorState.OpTether(actorID, default));
                    break;
                case Network.ServerIPC.ActorControlCategory.EObjSetState:
                    // p2 is unused (seems to be director id?), p3==1 means housing (?) item instead of event obj, p4 is housing item id
                    _actorOps.GetOrAdd(actorID).Add(new ActorState.OpEventObjectStateChange(actorID, (ushort)p1));
                    break;
                case Network.ServerIPC.ActorControlCategory.EObjAnimation:
                    _actorOps.GetOrAdd(actorID).Add(new ActorState.OpEventObjectAnimation(actorID, (ushort)p1, (ushort)p2));
                    break;
                case Network.ServerIPC.ActorControlCategory.PlayActionTimeline:
                    _actorOps.GetOrAdd(actorID).Add(new ActorState.OpPlayActionTimelineEvent(actorID, (ushort)p1));
                    break;
                case Network.ServerIPC.ActorControlCategory.ActionRejected:
                    _globalOps.Add(new ClientState.OpActionReject(new(new((ActionType)p2, p3), p6, p4 * 0.01f, p5 * 0.01f, p1)));
                    break;
                case Network.ServerIPC.ActorControlCategory.DirectorUpdate:
                    _globalOps.Add(new WorldState.OpDirectorUpdate(p1, p2, p3, p4, p5, p6));
                    break;
            }
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketActorControlDetour), ex);
        }
    }

    private unsafe void ProcessPacketNpcYellDetour(Network.ServerIPC.NpcYell* packet)
    {
        _processPacketNpcYellHook.OriginalDisposeSafe(packet);
        try
        {
            _actorOps.GetOrAdd(packet->SourceID).Add(new ActorState.OpEventNpcYell(packet->SourceID, packet->Message));
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketNpcYellDetour), ex);
        }
    }

    private unsafe void ProcessEnvControlDetour(void* self, uint index, ushort s1, ushort s2)
    {
        // note: this function is only executed for incoming packets that pass some checks (validation that currently active director is what is expected) - don't think it's a big deal?
        _processEnvControlHook.OriginalDisposeSafe(self, index, s1, s2);
        try
        {
            _globalOps.Add(new WorldState.OpEnvControl((byte)index, s1 | ((uint)s2 << 16)));
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessEnvControlDetour), ex);
        }
    }

    private unsafe void ProcessPacketRSVDataDetour(byte* packet)
    {
        _processPacketRSVDataHook.OriginalDisposeSafe(packet);
        try
        {
            _globalOps.Add(new WorldState.OpRSVData(MemoryHelper.ReadStringNullTerminated((nint)(packet + 4)), MemoryHelper.ReadString((nint)(packet + 0x34), *(int*)packet)));
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketRSVDataDetour), ex);
        }
    }

    private unsafe void ProcessPacketOpenTreasureDetour(uint playerID, byte* packet)
    {
        _processPacketOpenTreasureHook.OriginalDisposeSafe(playerID, packet);
        try
        {
            var actorID = *(uint*)(packet + 16);
            _actorOps.GetOrAdd(actorID).Add(new ActorState.OpEventOpenTreasure(actorID));
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketOpenTreasureDetour), ex);
        }
    }

    private unsafe void* ProcessSystemLogMessageDetour(uint entityId, uint messageId, int* args, byte argCount)
    {
        var res = _processSystemLogMessageHook.OriginalDisposeSafe(entityId, messageId, args, argCount);
        try
        {
            _globalOps.Add(new WorldState.OpSystemLogMessage(messageId, new Span<int>(args, argCount).ToArray()));
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessSystemLogMessageDetour), ex);
        }
        return res;
    }

    private unsafe void* ProcessPacketFateInfoDetour(ulong fateId, long startTimestamp, ulong durationSecs)
    {
        var res = _processPacketFateInfoHook.OriginalDisposeSafe(fateId, startTimestamp, durationSecs);
        try
        {
            _globalOps.Add(new ClientState.OpFateInfo((uint)fateId, DateTimeOffset.FromUnixTimeSeconds(startTimestamp).UtcDateTime));
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessPacketFateInfoDetour), ex);
        }
        return res;
    }

    private unsafe void ProcessMapEffect1Detour(void* director, byte* packet)
    {
        _processMapEffect1Hook.OriginalDisposeSafe(director, packet);
        try
        {
            ProcessMapEffect(packet, 10, 18);
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessMapEffect1Detour), ex);
        }
    }

    private unsafe void ProcessMapEffect2Detour(void* director, byte* packet)
    {
        _processMapEffect2Hook.OriginalDisposeSafe(director, packet);
        try
        {
            ProcessMapEffect(packet, 18, 34);
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessMapEffect2Detour), ex);
        }
    }

    private unsafe void ProcessMapEffect3Detour(void* director, byte* packet)
    {
        _processMapEffect3Hook.OriginalDisposeSafe(director, packet);
        try
        {
            ProcessMapEffect(packet, 26, 50);
        }
        catch (Exception ex)
        {
            DetourGuard.Report(nameof(ProcessMapEffect3Detour), ex);
        }
    }

    private unsafe void ProcessMapEffect(byte* data, byte offLow, byte offIndex)
    {
        for (var i = 0; i < *data; ++i)
        {
            var low = *(ushort*)(data + 2 * i + offLow);
            var high = *(ushort*)(data + 2 * i + 2);
            var index = data[i + offIndex];
            _globalOps.Add(new WorldState.OpEnvControl(index, low | ((uint)high << 16)));
        }
    }
}
