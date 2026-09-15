using BossMod.Pathfinding;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;

using static FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon;

namespace BossMod.Global.DeepDungeon;

enum OID : uint
{
    CairnPalace = 0x1EA094,
    BeaconHoH = 0x1EA9A3,
    PylonEO = 0x1EB867,
    SilverCoffer = 0x1EA13D,
    GoldCoffer = 0x1EA13E,

    /// <summary>
    /// 埋藏的寶藏本體（<b>還埋著、看不見的那個點</b>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 名字取得容易誤導：它<b>不是</b>「魔陶器：感知寶藏照出來的光點」這種衍生特效物件，
    /// 而是寶藏埋藏處本身的事件物件。台服 <c>EObjName</c> 第 2007542 列的名稱是<b>空的</b>
    /// （＝沒有互動提示、不可選取），與 <see cref="BandedCoffer"/> 有名字（「埋藏的寶藏」）
    /// 形成對比；<c>EObj</c> 那一列的 <c>Data</c> 與 <c>EventHighAddition</c> 也都是 0，
    /// 而銅／銀／金寶箱與 <see cref="BandedCoffer"/> 都是 <c>Data=983600</c>（開箱事件）。
    /// <para>
    /// 交叉驗證：NecroLens 把同一個值命名為 <c>AccursedHoard</c>、PalacePal 則把 2007542
    /// 與 2007543 一起歸為 <c>EType.Hoard</c>。三份來源一致。
    /// </para>
    /// </remarks>
    BandedCofferIndicator = 0x1EA1F6,

    /// <summary>
    /// 已現形、可以互動取得的埋藏寶藏（台服 <c>EObjName</c> 2007543 ＝「埋藏的寶藏」）。
    /// </summary>
    BandedCoffer = 0x1EA1F7,
}

enum SID : uint
{
    Silence = 7,
    Pacification = 620,
    ItemPenalty = 1094,

    PhysicalDamageUp = 53,
    DamageUp = 61,
    DreadBeastAura = 2056, // unnamed status, displays red fog vfx on actor
    EvasionUp = 2402, // applied by Peculiar Light from orthos diplocaulus

    StoneCurse = 437, // petrification (on enemies)
    AutoHealPenalty = 1097,
}

public abstract class AutoClear : ZoneModule
{
    public readonly int LevelCap;

    public static readonly HashSet<uint> BronzeChestIDs = [
        // PotD
        782, 783, 784, 785, 786, 787, 788, 789, 790, 802, 803, 804, 805,
        // HoH
        1036, 1037, 1038, 1039, 1040, 1041, 1042, 1043, 1044, 1045, 1046, 1047, 1048, 1049,
        // EO
        1541, 1542, 1543, 1544, 1545, 1546, 1547, 1548, 1549, 1550, 1551, 1552, 1553, 1554
    ];
    /// <summary>
    /// 已現形（魔陶器：全景／踩過）的陷阱事件物件。
    /// </summary>
    /// <remarks>
    /// 📌 <c>0x1EBEDB</c>（2014939）是 NecroLens 有、這裡原本沒有的一個。台服 <c>EObjName</c>
    /// 第 2014939 列存在、名稱與其他六個陷阱一樣是空的（＝不可選取的裝飾/機關物件），
    /// 形狀一致所以補進來。
    /// ⚠️ 這是<b>離線查表</b>的結論，不是實機看過：假設不成立時的失敗方向是
    /// 「多一個永遠不會命中的比對」，不會誤標任何東西。
    /// </remarks>
    public static readonly HashSet<uint> RevealedTrapOIDs = [0x1EA08E, 0x1EA08F, 0x1EA090, 0x1EA091, 0x1EA092, 0x1EA9A0, 0x1EB864, 0x1EBEDB];

    protected readonly List<(Actor Source, float Inner, float Outer)> Donuts = [];
    protected readonly List<(Actor Source, float Radius)> Circles = [];
    protected readonly List<(Actor Source, float Radius)> KnockbackZones = [];
    protected readonly List<(Actor Source, AOEShape Zone)> Voidzones = [];
    private readonly List<Gaze> Gazes = [];
    protected readonly List<Actor> Interrupts = [];
    protected readonly List<Actor> Stuns = [];
    protected readonly List<(Actor Actor, DateTime Timeout)> Spikes = [];
    protected readonly List<Actor> HintDisabled = [];
    private readonly List<Actor> LOS = [];
    private readonly List<WPos> IgnoreTraps = [];

    private readonly Dictionary<ulong, (WPos, Bitmap)> _losCache = [];

    public record class Gaze(Actor Source, AOEShape Shape);

    protected static readonly AutoDDConfig Config = Service.Config.Get<AutoDDConfig>();
    private readonly EventSubscriptions _subscriptions;
    private readonly WPos[] _trapsCurrentZone = [];

    private readonly Dictionary<ulong, PomanderID> _chestContentsGold = [];
    private readonly Dictionary<ulong, int> _chestContentsSilver = [];
    private readonly HashSet<ulong> _openedChests = [];
    private readonly HashSet<ulong> _fakeExits = [];
    private PomanderID? _lastChestContentsGold;
    private bool _lastChestMagicite;
    private bool _trapsHidden = true;

    /// <summary>
    /// 這一層的埋藏寶藏已經被挖出來了（系統訊息 7274「發現了埋藏的寶藏！」）。
    /// </summary>
    /// <remarks>
    /// 這只是<b>其中一個</b>停止標示的條件，不是唯一條件——挖出來之後實體通常也會離開
    /// <c>ObjectTable</c>，而且 <see cref="_openedChests"/> 也會收到 <c>EventOpenTreasure</c>。
    /// 三者任一成立就不再標，所以就算台服這條系統訊息沒有觸發（同檔 7248 就有前例），
    /// 失敗形式也只是「多標一下下」，不會標到錯的地方。
    /// </remarks>
    private bool _hoardFound;

    private readonly List<(Wall Wall, bool Rotated)> Walls = [];

    /// <summary>
    /// 每個房間格子的中心世界座標；null＝這一格在本層的版面裡沒有房間，或還沒載入。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這裡以前是 <c>List&lt;WPos&gt;</c>，而且**只在 <c>room &gt; 0</c> 時 Add** ——
    /// 也就是索引跟房號對不起來（第 0 格沒房間時，清單第 0 筆其實是第 1 間房）。
    /// 那個欄位從頭到尾沒有任何地方讀取，所以錯位一直沒被發現。改成 25 格對齊陣列。
    /// <para>
    /// 🔴 <b>座標來源是上游從國際服 dump 出來的寫死數值</b>（<see cref="LoadedFloors"/>），
    /// 台服不保證相同。所有用到這份座標的功能都必須先過
    /// <see cref="CheckRoomCoords"/> 的校驗閘門。
    /// </para>
    /// </remarks>
    private readonly WPos?[] RoomCenters = new WPos?[DeepDungeonState.NumRooms];

    private readonly List<WPos> ProblematicTrapLocations = [];

    private int Kills;
    private int DesiredRoom;
    private bool BetweenFloors;
    private (int from, int to) _lastPathfindFailureLogged = (-1, -1);

    /// <summary>目前的目標房間是誰決定的。</summary>
    protected enum DestinationSource
    {
        /// <summary>使用者自己在小地圖上點的。</summary>
        User,
        /// <summary>被「探索所有房間」覆寫掉了。</summary>
        FullClear,
        /// <summary>使用者沒指定，由「自動前往通道石」填入。</summary>
        AutoPassage
    }

    private DestinationSource _destinationSource;

    /// <summary>上一次樓層尋路是不是找不到路（連通的房間還沒探索到時是正常現象）。</summary>
    private bool _lastPathfindFailed;

    /// <summary>
    /// 「已經走到下層傳送裝置旁邊了」的距離門檻（碼，已扣掉雙方 hitbox）。
    /// </summary>
    /// <remarks>
    /// 📌 與寶箱那條用同一個數字，兩者是同一個語意（「站到旁邊了」）。
    /// <para>
    /// 🔑 這只是「要不要把它設成互動目標」的門檻，<b>不是</b>能不能互動的判斷：
    /// 真正的判斷在 <c>Plugin.CheckInteractRange</c>，那裡問的是遊戲自己的
    /// <c>EventFramework::CheckInteractRange</c>。所以這個數字寬一點的代價只是
    /// 「多設一個互動目標、遊戲說還不夠近、於是什麼都不送」，窄一點的代價卻是
    /// 「走到了卻永遠不點」——寧可寬。
    /// </para>
    /// <para>
    /// ⚠️ 真的要走到這麼近，是靠 AutoPassage 既有的 <c>GoalSingleTarget(c.Position, 2f, 0.5f)</c>
    /// 那個半徑 2y 的平台；這個門檻比它寬，所以不會出現「AI 認為到了、這裡認為還沒到」的僵局。
    /// </para>
    /// </remarks>
    private const float PassageInteractDistance = 3.5f;

    /// <summary>
    /// 「走過去開寶箱」這個目標區的作用半徑（碼）。
    /// </summary>
    /// <remarks>
    /// 沿用原本平台函式的 25y，作用範圍完全不變 —— 這一版只把<b>形狀</b>從平台換成斜坡。
    /// 📌 2026-08-16 起 <see cref="AIHints.GoalProximity"/> 同步上游 abee5e9fe 把衰減曲線
    /// 由線性改成平方（weight = 1 - (d/r)²），斜坡在目的地附近變平：1y 處每碼落差由
    /// 0.04 掉到約 0.0032（弱約 12.5 倍）。深牢寶箱這條路徑<b>刻意不吃那條曲線</b>，
    /// 改用本檔自帶的 <see cref="GoalProximityLinear"/> 維持線性，避免 .57 修掉的
    /// 「靠近寶箱就站著不動」因為梯度變弱而回歸。上游的平方曲線只用在一般戰鬥路徑的
    /// 其餘呼叫點，本檔不受影響。
    /// </remarks>
    private const float TreasureApproachRadius = 25f;

    /// <summary>
    /// 「走過去開寶箱」的最高權重（就在寶箱上，隨距離線性遞減到 <see cref="TreasureApproachRadius"/> 歸零）。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意維持原本平台的 1：新的權重在每一點都 ≤ 舊值，所以不可能壓過原本壓不過的目標區
    /// （趕路 10／0.5、坦克拉怪 12、傳送裝置 0.5＋0.25、閃避偏好 0.5）。
    /// 調高它等於「開箱比趕路重要」，那是行為變更，要另外裁決。
    /// </remarks>
    private const float TreasureApproachWeight = 1f;

    /// <summary>
    /// 線性衰減的目標區：就在 <paramref name="destination"/> 上等於
    /// <paramref name="maxWeight"/>，隨距離線性遞減，到
    /// <paramref name="maxDistance"/> 歸零、之外恆為 0。
    /// </summary>
    /// <remarks>
    /// 🔴 這是 <see cref="AIHints.GoalProximity"/> 在 2026-08-16 之前的形狀，刻意複製一份留在
    /// 深牢這邊。上游 abee5e9fe 把本體換成平方曲線之後，目的地附近的梯度會弱掉約 12.5 倍，
    /// 而深牢趕路正是靠這段梯度才走得動（.57 修掉的「靠近寶箱就站著不動」）。
    /// ⚠️ 要改這裡之前先確認症狀不會回歸；一般戰鬥路徑請照常用 <see cref="AIHints.GoalProximity"/>。
    /// </remarks>
    private static Func<WPos, float> GoalProximityLinear(WPos destination, float maxDistance, float maxWeight)
    {
        var invDist = 1f / maxDistance;
        return p =>
        {
            var dist = (p - destination).Length();
            var weight = 1f - Math.Clamp(invDist * dist, 0f, 1f);
            return maxWeight * weight;
        };
    }

    protected struct PlayerImmuneState
    {
        public DateTime RoleBuffExpire; // 0 if not active
        public DateTime JobBuffExpire; // 0 if not active
        public bool KnockbackPenalty;

        public readonly bool ImmuneAt(DateTime time) => KnockbackPenalty || RoleBuffExpire > time || JobBuffExpire > time;
    }

    private readonly PlayerImmuneState[] _playerImmunes = new PlayerImmuneState[4];

    private ObstacleMapManager _obstacles;

    /// <summary>進深牢時請 WrathCombo 讓出自動循環的橋接（軟依賴，沒裝就靜默跳過）。</summary>
    private readonly WrathComboBridge _wrathCombo = new();

    protected DeepDungeonState Palace => World.DeepDungeon;

    protected AutoClear(WorldState ws, int LevelCap) : base(ws)
    {
        this.LevelCap = LevelCap;
        _obstacles = new(ws);

        _subscriptions = new(
            ws.SystemLogMessage.Subscribe(OnSystemLogMessage),
            ws.Actors.CastStarted.Subscribe(OnCastStarted),
            ws.Actors.CastFinished.Subscribe(OnCastFinished),
            ws.Actors.CastEvent.Subscribe(OnEventCast),
            ws.Actors.Added.Subscribe(OnActorCreated),
            ws.Actors.InCombatChanged.Subscribe(OnActorCombatChanged),
            ws.Actors.StatusGain.Subscribe(OnActorStatusGain),
            ws.Actors.StatusLose.Subscribe(OnActorStatusLose),
            ws.Actors.IsDeadChanged.Subscribe(op =>
            {
                if (!op.IsAlly && op.IsDead)
                    ++Kills;
            }),
            ws.Actors.EventOpenTreasure.Subscribe(OnOpenTreasure),
            ws.Actors.EventObjectAnimation.Subscribe(OnEObjAnim),
            ws.DeepDungeon.MapDataChanged.Subscribe(_ =>
            {
                BetweenFloors = false;

                // 🔴 換層時該歸零的那組狀態原本**只**掛在系統訊息 7248（傳送開始）上，
                //    而 7248 在台服不保證觸發（同一個成因已經害過房間座標沿用上一層）。
                //    後果最嚴重的是 _trapsHidden：用過一次「魔陶器：咒印解除／全景」之後
                //    它被設成 false，若沒有任何東西把它設回 true，
                //    **之後每一層的資料庫陷阱迴避都靜默關閉**——設定頁照樣打勾。
                //    這裡改用「遊戲回報的樓層變了」當驅動，與 7248 互為備援（誰先到都只跑一次）。
                if (_floorStateFor != Palace.Floor)
                {
                    _floorStateFor = Palace.Floor;
                    ResetFloorState();
                }
                // 🔴 判準是「載入的是不是這一層這個版面」，不是「Walls 是不是空的」。
                //    舊的 `if (Walls.Count == 0)` 只在第一次載入，之後完全依賴 ClearState() 去清空，
                //    而 ClearState 是由系統訊息 7248（傳送開始）驅動的 —— 那條在台服沒有觸發，
                //    於是整輪探索都沿用第一層的房間座標。
                //    實測後果：19:23 那場天之御柱 31~40，只有第 31 層（版面 1）通過座標校驗，
                //    32~39 層全部不通過、偏差 300~760 碼 —— 而同一組樓層的兩份鏡像版面
                //    （RoomsA／RoomsB）中心相距約 845 碼，正好對得上。
                //    寶箱點位與各房敵人數因此在九層裡有八層完全不顯示。
                if (_loadedLayout != (Palace.Floor, Palace.Progress.Tileset))
                    LoadWalls();
            })
        );

        _trapsCurrentZone = GeneratedTrapData.Traps.TryGetValue(ws.CurrentZone, out var locations) ? locations : [];

        // 🔴 這裡刻意沒有內建的「問題陷阱」種子清單，不要好心把它加回來。
        //    原本 BadTraps.cs 那 6 個座標不帶副本／樓層限定，而深牢的座標空間是跨副本共用的：
        //    拿它們去比對本檔用的 GeneratedTrapData（14176 筆），每一點都對得上 3~6 個 zone 的
        //    真實陷阱，其中 (-374.9, 302.2) 同時命中死者宮殿（562/593/594/595）與天之御柱（772/782）。
        //    灌進 IgnoreTraps 等於在從沒驗證過的樓層關掉那些點的陷阱迴避
        //    （CollectWalkTrapPoints 與 AddNearbyTrapCircles 都會直接跳過被忽略的點），
        //    所以整份資料連同 BadTraps.cs 一起移除。要復活的話必須先補上 zone／樓層限定。
        IgnoreTraps.AddRange(ProblematicTrapLocations);
    }

    protected override void Dispose(bool disposing)
    {
        // 🔴 最先做:vnavmesh 的移動開關是**全域**的,留在 false 等於使用者的 vnavmesh 從此不會動,
        //    而且完全沒有錯誤訊息。擺在 Stop() 之前,免得 Stop() 擲例外時連帶跳過還原。
        ReleaseNavPause();

        // 我們自己叫起來的移動不要留給下一個場景。
        // 🔴 Dispose 期間的 IPC 要整個包起來：外掛卸載時對方可能已經先走一步，
        //    這裡擲例外會打斷後面的 Dispose 鏈（全艦隊踩過的形狀）。
        if (WalkActive)
        {
            try
            {
                DeepDungeonNav.Stop();
            }
            catch (Exception ex)
            {
                Service.Log($"[DD nav] Dispose 時停止移動失敗（可忽略）: {ex.Message}");
            }
        }

        // 🔴 離開深牢／卸載外掛都要把租約還回去。WrathComboBridge 內部已經把
        //    「WrathCombo 先卸載了」的情況包起來（IpcError 與非 IpcError 都接）。
        _wrathCombo.Dispose();

        _subscriptions.Dispose();
        _obstacles.Dispose();
        base.Dispose(disposing);
    }

    protected virtual void OnCastStarted(Actor actor) { }

    protected virtual void OnCastFinished(Actor actor) { }
    protected virtual void OnEventCast(Actor actor, ActorCastEvent ev) { }

    private void OnActorStatusGain(Actor actor, int index)
    {
        var status = actor.Statuses[index];

        switch (status.ID)
        {
            case (uint)WHM.SID.Surecast:
            case (uint)WAR.SID.ArmsLength:
                var slot1 = World.Party.FindSlot(actor.InstanceID);
                if (slot1 >= 0)
                    _playerImmunes[slot1].RoleBuffExpire = status.ExpireAt;
                break;
            case (uint)WAR.SID.InnerStrength:
                var slot2 = World.Party.FindSlot(actor.InstanceID);
                if (slot2 >= 0)
                    _playerImmunes[slot2].JobBuffExpire = status.ExpireAt;
                break;
            // Knockback Penalty floor effect
            case 1096:
            case 1512:
                var slot3 = World.Party.FindSlot(actor.InstanceID);
                if (slot3 >= 0)
                    _playerImmunes[slot3].KnockbackPenalty = true;
                break;
        }

        OnStatusGain(actor, status);
    }

    protected virtual void OnStatusGain(Actor actor, ActorStatus status) { }

    private void OnActorStatusLose(Actor actor, int index)
    {
        var status = actor.Statuses[index];
        OnStatusLose(actor, status);
    }

    protected virtual void OnStatusLose(Actor actor, ActorStatus status) { }

    protected virtual void OnActorCombatChanged(Actor actor) { }

    private void OnSystemLogMessage(WorldState.OpSystemLogMessage op)
    {
        switch (op.MessageId)
        {
            case 7222: // pomander overcap
                _lastChestContentsGold = (PomanderID)op.Args[0];
                break;
            case 7248: // transference initiated
                ClearState();
                break;
            case 7255: // safety used
            case 7256: // sight used
                _trapsHidden = false;
                break;
            // 「發現了埋藏的寶藏！」——台服 LogMessage 第 7274 列逐字查表確認。
            // NecroLens 做同一件事是比對訊息字串結尾，這裡直接用訊息 id，不受語系影響。
            case 7274:
                _hoardFound = true;
                break;
            // 滿額訊息是一族一個 id：魔陶器（全深牢共用）7222、EO 亞靈複製體 10287、
            // 天之御柱的魔石是 9208（9206~9209 那組 HoH 專屬家族，鄰居 9217 假傳送燈籠
            // 就是本檔已在用的那個）。只接 10287 時 HoH 魔石滿箱完全沒反應——
            // 2026-08-13 實機 19:35:44 此訊息開火而這裡沒接，同晚 21:59 魔陶器側正常自動使用。
            case 9208: // HoH magicite overcap
            case 10287: // demiclone overcap
                _lastChestMagicite = true;
                break;
        }
    }

    private void OnOpenTreasure(Actor chest)
    {
        _openedChests.Add(chest.InstanceID);
        ForgetOpenedChest(chest);
    }

    /// <summary>
    /// 開箱之後把 <see cref="_chestSeen"/> 對應的那一格扣回去，免得累積值讓開過的箱永遠留在小地圖上。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只有座標校驗通過時才扣。</b>把實體歸屬到房間靠的就是那份座標，
    /// 閘門沒過時做這件事等於用一份我們剛剛才宣告不可信的資料去<b>刪</b>標記——
    /// 扣錯格子會把另一個還沒開的箱從小地圖上抹掉，那正是這一輪要修的失敗形式。
    /// ⚠️ 代價是閘門沒過的樓層（例如大房間層）開過的箱會殘留到換層。
    /// <b>殘留一個開過的標記可以接受，該有的箱消失不行。</b>
    /// </remarks>
    private void ForgetOpenedChest(Actor chest)
    {
        if (_coordMemoState != RoomCoordState.Ok)
            return;
        var slot = ChestSlotForOID(chest.OID);
        if (slot < 0)
            return;
        var room = NearestRoom(chest.Position, RoomTolerance);
        if (room < 0)
            return;
        ref var seen = ref _chestSeen[room * Minimap.ChestTypeSlots + slot];
        if (seen > 0)
            --seen;
    }

    /// <summary>本層每一格每一型別「最多同時看過幾個寶箱」。索引與 <see cref="Minimap.ChestSeen"/> 相同。</summary>
    private readonly int[] _chestSeen = new int[DeepDungeonState.NumRooms * Minimap.ChestTypeSlots];

    /// <summary>算當幀清單用的暫存區，避免每幀配置。</summary>
    private readonly int[] _chestSeenScratch = new int[DeepDungeonState.NumRooms * Minimap.ChestTypeSlots];

    /// <summary>
    /// 把遊戲當幀的寶箱清單併進 <see cref="_chestSeen"/>（取大值）。
    /// </summary>
    /// <remarks>
    /// 🔴 取大值而不是覆蓋：遊戲的 <c>Chests[]</c> 會縮，覆蓋等於跟著它一起把標記抹掉。
    /// </remarks>
    private void UpdateChestSeen()
    {
        Array.Clear(_chestSeenScratch);
        Minimap.CountChests(Palace, _chestSeenScratch);
        for (var i = 0; i < _chestSeen.Length; ++i)
        {
            if (_chestSeenScratch[i] > _chestSeen[i])
                _chestSeen[i] = _chestSeenScratch[i];
        }
    }

    private void OnEObjAnim(Actor actor, ushort p1, ushort p2)
    {
        // fake beacon deactivation; accompanied by system log #9217 but it does not indicate a specific actor
        if (actor.OID == (uint)OID.BeaconHoH && p1 == 0x0400 && p2 == 0x0800)
            _fakeExits.Add(actor.InstanceID);
    }

    protected virtual void OnChangeFloors() { }

    /// <summary>
    /// 這一層已經跑過 <see cref="ResetFloorState"/> 的樓層號；255＝還沒跑過。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這個記號就分不出「換層了」與「同一層又揭開一間房」——
    /// <c>MapDataChanged</c> 每揭開一間房就會觸發一次。
    /// </remarks>
    private byte _floorStateFor = 255;

    /// <summary>
    /// <b>換一層</b>就該歸零的東西。
    /// </summary>
    /// <remarks>
    /// 從兩條路進來：系統訊息 7248（傳送開始，走 <see cref="ClearState"/>）與
    /// <c>MapDataChanged</c> 偵測到樓層變化。兩者互為備援，<see cref="_floorStateFor"/> 保證
    /// 同一層只跑一次。刻意<b>不</b>含房間座標／牆壁／AOE 清單——那幾樣是
    /// 「真的傳送」才該作廢的，而且座標與牆壁由 <see cref="LoadWalls"/> 自己負責重載。
    /// </remarks>
    private void ResetFloorState()
    {
        // 換層了，上一層算出來的路徑一律作廢；我們發起的移動也停掉
        // （角色會被傳走，繼續照舊路徑走是沒有意義而且可能有害的）
        if (WalkActive)
            RequestWalkStop();
        _walkMessage = null;
        _walkTargetRoom = -1;
        _walkCorridor.Clear();
        _stuckMessage = null;
        _stuckSince = default;

        IgnoreTraps.Clear();
        IgnoreTraps.AddRange(ProblematicTrapLocations);
        DesiredRoom = 0;
        Kills = 0;
        Array.Fill(_playerImmunes, default);
        _lastChestContentsGold = null;
        _lastChestMagicite = false;
        _magiciteSpendLogged = (-1, 0);
        ResetPomanderAttempts();
        ResetMagiciteAttempts();
        _pomanderGiveUp.Clear();
        _magiciteGiveUp = false;
        _chestContentsGold.Clear();
        _chestContentsSilver.Clear();
        _trapsHidden = true;
        _hoardFound = false;
        _pullLoggedFloor = 255;
        _lastTargetCorrection = default;
        _openerLoggedFloor = 255;
        _aggroAvoidLoggedFloor = 255;
        _aggroCrossLoggedFloor = 255;
        _aggroSuppressUntil = default;
        _aggroProgressSample = null;
        _aggroInside.Clear();
        _aggroInteractSince = default;
        _aggroInteractFuseBlown = false;
        _aggroInteractLoggedFloor = 255;
        _openedChests.Clear();
        _fakeExits.Clear();
        // 🔴 寶箱累積值是「本層看過什麼」，換層一定要歸零，否則上一層的標記會跟著下來。
        Array.Clear(_chestSeen);
        // 換層／重新進場都讓寶箱摘要再印一次。簽章本身含樓層，同一輪往下走本來就會變；
        // 這裡歸零處理的是「離開再回到同一層、內容剛好一模一樣」那種會被吃掉的情況。
        _chestDiagSignature = 0;
        OnChangeFloors();
    }

    private void ClearState()
    {
        ResetFloorState();

        Donuts.Clear();
        Circles.Clear();
        Gazes.Clear();
        Interrupts.Clear();
        Stuns.Clear();
        Spikes.Clear();
        HintDisabled.Clear();
        LOS.Clear();
        Walls.Clear();
        Array.Clear(RoomCenters);
        // 🔴 座標清掉了，「已載入的是哪一層哪個版面」也一定要跟著作廢。
        //    少了這一行就是靜默失效：Update 那邊的重載判準是
        //    `_loadedLayout != (Palace.Floor, Palace.Progress.Tileset)`，
        //    而 ClearState 走完之後這兩者仍然相等 ⇒ LoadWalls 永遠不會被叫回來，
        //    RoomCenters／Walls 就這樣空著過完整層（寶箱點位、各房敵人數、
        //    手動導航驗證一起消失，而且沒有任何訊息說為什麼）。
        //    ⚠️ 台服目前很少走到：驅動 ClearState 的系統訊息 7248 在台服沒有觸發，
        //    現在靠的是「樓層變了就重載」那條備援。這一行是把備援失效時的洞補起來。
        _loadedLayout = (255, 255);
        ResetCoordGate();
        BetweenFloors = true;
    }

    protected void AddGaze(Actor Source, AOEShape Shape) => Gazes.Add(new(Source, Shape));
    protected void AddGaze(Actor Source, float Radius) => AddGaze(Source, new AOEShapeCircle(Radius));

    protected void AddLOS(Actor Source, float Range)
    {
        if (Config.AutoLOS)
            AddLOSFromTerrain(Source, Range);
        else
            Circles.Add((Source, Range));
    }

    private bool OpenGold => Config.GoldCoffer;
    private bool OpenSilver
    {
        get
        {
            // disabled
            if (!Config.SilverCoffer)
                return false;

            // sanity check
            if (World.Party.Player() is not { } player)
                return false;

            // explosive silver chests deal 70% max hp damage
            if (player.HPMP.CurHP <= player.HPMP.MaxHP * 0.7f)
                return false;

            // upgrade weapon if desired
            if (Palace.Progress.WeaponLevel + Palace.Progress.ArmorLevel < 198)
                return true;

            return Palace.DungeonId switch
            {
                DeepDungeonState.DungeonType.HOH or DeepDungeonState.DungeonType.EO => Palace.Floor >= 7, // magicite/demiclones start dropping on floor 7
                _ => false,
            };
        }
    }

    private bool OpenBronze => Config.BronzeCoffer;

    public override bool WantDrawExtra() => Config.EnableMinimap && !Palace.IsBossFloor;

    public sealed override string WindowName() => "BMR DD minimap###Zone module";

    /// <summary>上一次印出來的小地圖寶箱摘要簽章；0＝這一層還沒印過。</summary>
    private ulong _chestDiagSignature;

    /// <summary>
    /// 把小地圖「算出來要畫什麼」寫進 log。
    /// </summary>
    /// <remarks>
    /// 🔴 這一行是用來把「資料 → 繪製決策」的斷點一刀切開的：
    /// log 說某一格要畫、使用者卻看不到 ⇒ 斷在<b>渲染側</b>；
    /// log 根本沒提那一格 ⇒ 斷在<b>資料側</b>。
    /// （後者理論上不該發生，而這一行的存在正是為了抓這種「不該發生」。）
    /// <para>
    /// 🔴 節流靠 <see cref="Minimap.ChestDiagSignature"/>：<see cref="DrawExtra"/> 每幀都跑，
    /// 沒有節流會每秒刷幾十行。簽章相同時連字串都不會組出來——比對的只是一個 ulong。
    /// </para>
    /// 📌 走 <c>Information</c>：使用者的 LogLevel 是 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒。
    /// </remarks>
    private void LogChestDiagnostic(Minimap minimap)
    {
        if (minimap.ChestDiagSignature == _chestDiagSignature)
            return;
        _chestDiagSignature = minimap.ChestDiagSignature;
        Service.Logger.Information(minimap.FormatChestDiagnostic());
    }

    public override void DrawExtra()
    {
        var player = World.Party.Player()!;

        var coords = CheckRoomCoords(player, out var coordDistance, out _);

        int[]? roomEnemies = null;
        if (Config.ShowRoomEnemies)
        {
            UpdateRoomEnemies(coords, World.CurrentTime);
            if (_roomEnemiesValid)
                roomEnemies = _roomEnemies;
        }

        var hoardSpots = ComputeHoardSpots(coords, out var hoardDetected);

        // 🔴 要在建 Minimap 之前併，繪製端才看得到這一幀新出現的寶箱。
        UpdateChestSeen();

        // 🔴 Minimap 是 record class、每幀重建，所以「上次印過什麼」這種狀態只能放在呼叫端
        //    （同款先例：_floorStateFor）。
        var minimap = new Minimap(Palace, player, DesiredRoom, Config, ComputeChestSpots(coords), roomEnemies, hoardSpots, _chestSeen);
        var targetRoom = minimap.Draw();
        LogChestDiagnostic(minimap);

        if (targetRoom >= 0)
        {
            DesiredRoom = targetRoom;
            _destinationSource = DestinationSource.User;
            // 使用者重新指定目標＝那次放棄的說明已經沒有意義了
            _stuckMessage = null;
            _stuckSince = default;
        }

        // 🔴 偵測到寶藏但放不上小地圖時要講出來，而且要講清楚「世界標記還在」——
        //    否則使用者只會看到小地圖上什麼都沒有，而以為整個功能壞了。
        if (hoardDetected > 0 && hoardSpots == null)
            ImGui.TextColored(ColorUnknownText,
                Loc.T("DD_HoardPositionUnavailable", "Accursed Hoard: detected on this floor, but it cannot be placed on the minimap here (the built-in room coordinates do not match this map). The marker in the world is unaffected."));

        // 座標對不上時要說出來，否則使用者只會看到「寶箱一直是半透明的」而不知道為什麼
        if (coords == RoomCoordState.Mismatch)
            ImGui.TextColored(ColorUnknownText,
                string.Format(Loc.T("DD_CoordMismatch", "Coffer positions unavailable on this floor: the built-in room coordinates do not match this map (you are {0:f0}y from the centre of the room the game says you are in)."), coordDistance));

        // 🔴 誠實性：偵測範圍外的房間根本不在 ObjectTable 裡，「沒有數字」不等於「已清空」。
        //    這一行是常駐的，因為它說的是這個標示的根本限制，不是偶發狀況。
        if (Config.ShowRoomEnemies)
            ImGui.TextColored(ColorUnknownText, roomEnemies != null
                ? Loc.T("DD_EnemyCountCaveat", "Enemy counts only cover what is currently loaded around you - no number does not mean the room is clear.")
                : Loc.T("DD_EnemyCountUnavailable", "Enemies per room cannot be shown on this floor (room coordinates do not match)."));

        DrawNavigationStatus(player);

        if (Config.ManualRoomWalk)
            DrawWalkControls(player, coords);

        ImGui.Text($"Kills: {Kills}");

        var maxPull = Config.MaxPull;

        ImGui.SetNextItemWidth(200);
        if (ImGui.DragInt(Loc.T("Max mobs to pull") + "###MaxPull", ref maxPull, 0.05f, 0, 15))
        {
            Config.MaxPull = maxPull;
            Config.Modified.Fire();
        }

        if (ImGui.Button(Loc.T("DD_ReloadObstacles", "Reload obstacles")))
        {
            _obstacles.Dispose();
            _obstacles = new(World);
        }

        if (player == null)
            return;

        var (entry, data) = _obstacles.Find(player.PosRot.XYZ());
        if (entry == null)
        {
            ImGui.SameLine();
            UIMisc.HelpMarker(() => "Obstacle map missing for floor!", Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle);
        }

        if (data != null && data.PixelSize != 0.5f)
        {
            ImGui.SameLine();
            UIMisc.HelpMarker(() => $"Wrong resolution for map; should be 0.5, got {data.PixelSize}", Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle);
        }

        if (ImGui.Button(Loc.T("DD_SetClosestTrapIgnored", "Set closest trap location as ignored")))
        {
            WPos? pos = null;
            var minDistanceSq = float.MaxValue;
            var countProblematic = ProblematicTrapLocations.Count;
            // 🔴 PalacePal 補上來的點也要能被忽略。少了這一段的話，社群資料裡的誤報
            //    在這顆按鈕面前是隱形的——按下去只會抓到旁邊那個內建點，使用者按幾次都沒用。
            void PickClosest(WPos[] source)
            {
                var len = source.Length;
                for (var i = 0; i < len; ++i)
                {
                    ref readonly var trap = ref source[i];
                    var isProblematic = false;
                    for (var j = 0; j < countProblematic; ++j)
                    {
                        if (trap == ProblematicTrapLocations[j])
                        {
                            isProblematic = true;
                            break;
                        }
                    }

                    if (isProblematic)
                        continue;

                    var distanceSq = (trap - player.Position).LengthSq();

                    if (distanceSq < minDistanceSq)
                    {
                        minDistanceSq = distanceSq;
                        pos = trap;
                    }
                }
            }

            PickClosest(_trapsCurrentZone);
            PickClosest(_palTraps);

            if (pos is WPos position)
            {
                pos = position.Rounded(0.1f);
                ProblematicTrapLocations.Add(position);
                IgnoreTraps.Add(position);
            }
        }
    }

    /// <summary>「不知道／不會動」用的灰字。不用警示色——這些多半不是錯誤，只是沒在動。</summary>
    private static readonly Vector4 ColorUnknownText = new(0.72f, 0.72f, 0.72f, 1f);

    #region 手動「走到目標房間」

    /// <summary>手動導航的狀態。</summary>
    private enum WalkState
    {
        Idle,
        /// <summary>已經叫 vnavmesh 算路徑，等結果。</summary>
        Pathfinding,
        /// <summary>路徑驗過了，交給 vnavmesh 在走。</summary>
        Moving
    }

    private WalkState _walkState;
    private string? _walkMessage;
    private Task<List<Vector3>>? _walkTask;

    /// <summary>
    /// 按下停止時遞增，讓還在背景算的那條路徑作廢。
    /// </summary>
    /// <remarks>
    /// 🔑 這就是為什麼刻意不用 <c>SimpleMove.PathfindAndMoveTo</c>：那個端點算完會自己開走，
    /// 呼叫端攔不到，於是「按了停止、幾秒後角色自己走起來」。改成自己持有那個 Task，
    /// 停止時只要對不上世代就整條丟掉，問題在結構上消失。
    /// </remarks>
    private int _walkGeneration;
    private int _walkTaskGeneration;

    /// <summary>用活的連通旗標算出來的房間走廊：路徑點只准落在這些房間裡。</summary>
    private HashSet<int> _walkCorridor = [];
    private int _walkTargetRoom = -1;

    private DateTime _walkStopEnforceUntil = DateTime.MinValue;
    private DateTime _walkNextEnforce = DateTime.MinValue;

    private bool WalkActive => _walkState != WalkState.Idle;

    #region 「按住暫停」對手動導航的接管

    /// <summary>
    /// 我們是不是正握著 vnavmesh 的移動開關（也就是曾經把它關掉、還沒還原）。
    /// </summary>
    /// <remarks>
    /// 🔴 這個旗標就是「還原欠帳」的帳本：<b>只有</b>在 <see cref="DeepDungeonNav.SetMovementAllowed"/>
    /// 真的回報成功之後才准設為 true，否則會記到一筆我們其實沒欠的帳，
    /// 之後那次「還原」就變成把別人刻意關掉的開關硬打開。
    /// <para>
    /// 📌 這個旗標只說「我們有沒有接手」，<b>不說是用哪一條路接手的</b>——那要看
    /// <see cref="_navPauseLease"/>：非 <see cref="Guid.Empty"/>＝走租約，
    /// <see cref="Guid.Empty"/>＝走舊版 vnavmesh 的全域開關。
    /// </para>
    /// </remarks>
    private bool _navPauseHeld;

    /// <summary>接手／還原失敗後的重試節流；<see cref="DateTime.MinValue"/>＝可以立刻試。</summary>
    private DateTime _navPauseNextTry = DateTime.MinValue;

    /// <summary>
    /// 這一串連續失敗已經記過 log 了。
    /// </summary>
    /// <remarks>
    /// ⚠️ 沒有這個旗標的話，失敗分支會<b>每 100ms 印一行</b>（＝每秒 10 行）。
    /// 使用者跑 LogLevel 1，這些是 <c>Information</c>，會真的灌進他的 log 檔裡把別的線索淹掉。
    /// 一串連續失敗只值得講一次；狀態一有變化（接手成功／還原成功／不再想暫停）就清掉，
    /// 下次再壞會重新講。
    /// </remarks>
    private bool _navPauseFailureLogged;

    /// <summary>接手／還原失敗時的重試間隔。</summary>
    private const double NavPauseRetryMs = 100d;

    /// <summary>
    /// 我們跟 vnavmesh 借到的移動租約；<see cref="Guid.Empty"/>＝手上沒有租約
    /// （＝沒接手，或接手走的是舊版 vnavmesh 那條全域開關的路）。
    /// </summary>
    /// <remarks>
    /// 🔑 租約解掉的是「BMR 死在 <c>false</c> 上就永遠沒人還」這個結構性缺陷。舊路徑
    /// <c>Path.SetMovementAllowed(false)</c> 是對 vnavmesh <b>全域</b>欄位的單向寫入，
    /// 失效形式是它<b>靜默站著不動</b>（<c>Nav.Pathfind</c> 正常、<c>Path.IsRunning</c> 回
    /// <see langword="true"/>、log 一個字都沒有）。租約有 5 分鐘的硬性上限，逾時 vnavmesh
    /// 自動還原成<b>使用者自己的值</b>（不是寫死的 <c>true</c>），並寫一行 Information 指名是誰壓著。
    /// </remarks>
    private Guid _navPauseLease;

    /// <summary>下一次該送續約心跳的時刻（<c>Environment.TickCount64</c> 座標系）。</summary>
    /// <remarks>
    /// 🔴 刻意<b>不用</b> <c>World.CurrentTime</c>：那是 frame 時間戳，在回放視窗裡會跳來跳去，
    /// 而 vnavmesh 的租約到期時刻用的正是 <c>Environment.TickCount64</c>。兩邊同一個座標系，
    /// 才不會出現「我以為續約了、對方其實早就把它掃掉」。
    /// </remarks>
    private long _navPauseRenewAtTick;

    /// <summary>續約間隔（30 秒＝vnavmesh 租期 5 分鐘的十分之一，也就是它建議的 RenewIntervalHintMs）。</summary>
    /// <remarks>
    /// ⚠️ 間隔不可以接近租期：vnavmesh 的 Renew 第一件事是掃除過期租約，間隔接近租期時
    /// 第一次心跳<b>必定</b>失敗——那不是競態，是每次都會發生。
    /// </remarks>
    private const long NavPauseRenewIntervalMs = 30_000L;

    /// <summary>「這個 vnavmesh 沒有租約端點」這件事已經講過了（每個模組實例只講一次）。</summary>
    private bool _navPauseLegacyLogged;

    /// <summary>
    /// 只保護 <see cref="_navPauseHeld"/>／<see cref="_navPauseLease"/>／
    /// <see cref="_navPauseRenewAtTick"/> 這三個欄位。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>鎖內只准拍快照與寫回：不呼叫 IPC、不碰 ImGui、不做 I/O、不寫 log。</b>
    /// IPC 端點跑在對方的執行緒上而且對方自己也持鎖，在我們的鎖裡呼叫它等於把兩把鎖串起來。
    /// 📌 現況這三個欄位其實只有繪製執行緒（<c>Plugin.DrawUI</c> → <c>Update</c>）與
    /// <c>Dispose</c> 會碰，但「憑證＋到期時間」這個形狀在艦隊裡已經以裸字典的形式壞過兩次，
    /// 成本這麼低就先上鎖。
    /// </remarks>
    private readonly object _navPauseGate = new();

    /// <summary>
    /// 移動租約的心跳。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>必須在 <see cref="UpdateNavPause"/> 的每一條提早返回之前跑到。</b>
    /// 對帳式的主體在「想暫停＝正握著」的穩態下每一幀都會提早 return，心跳擺在那之後
    /// 等於永遠送不出去——而失效是<b>靜默</b>的：vnavmesh 在租期滿 5 分鐘時自己放開，
    /// 使用者按著暫停鍵，角色卻突然開始走。
    /// <para>
    /// 🔑 續約回 <see langword="false"/> 代表<b>那把租約已經不在了</b>（逾時被掃掉，或 vnavmesh
    /// 重載過），<b>不可以當成續約成功</b>。這裡只把帳本清掉，讓同一幀後面的對帳自然地
    /// 重新借一把——而不是自己在這裡重借，免得兩個地方都在管接手。
    /// </para>
    /// <para>
    /// 📌 一趟深牢可能遠超過 5 分鐘，但這支跟的是「暫停鍵按著多久」而不是「跑圖多久」：
    /// 租約只在真的壓住移動的那段期間存在。即使如此仍然要續約——按著暫停鍵去泡茶
    /// 是完全可能的，而逾時的表現正好是「角色自己走起來」。
    /// </para>
    /// </remarks>
    private void RenewNavPauseLease()
    {
        Guid lease;
        lock (_navPauseGate)
        {
            lease = _navPauseLease;
            if (lease == Guid.Empty || Environment.TickCount64 < _navPauseRenewAtTick)
                return;
            // 先把下一次的時刻推後，免得續約失敗的那一幀之後變成每幀重送。
            _navPauseRenewAtTick = Environment.TickCount64 + NavPauseRenewIntervalMs;
        }

        // 🔴 IPC 一律在鎖外呼叫（對方自己也持鎖）。
        if (DeepDungeonNav.RenewSuppression(lease))
            return;

        lock (_navPauseGate)
        {
            // ⚠️ 只在帳本還指著同一把時才清，免得誤銷中途換過的新租約。
            if (_navPauseLease != lease)
                return;
            _navPauseHeld = false;
            _navPauseLease = Guid.Empty;
            _navPauseRenewAtTick = 0L;
        }

        _navPauseNextTry = DateTime.MinValue;
        Service.Logger.Information($"[DD] 按住暫停：移動租約 {lease} 續約失敗（那把已經不在了——逾時被掃掉，或 vnavmesh 重載過）。已丟掉帳本，同一幀的對帳會重新借一把。");
    }

    /// <summary>
    /// 把「按住暫停鍵」這件事套用到手動導航（＝vnavmesh 正在走的那條路）。
    /// </summary>
    /// <remarks>
    /// <b>為什麼要另外做一份</b>：<c>MovementOverride</c> 的閘門擋的是「BMR 自己注入移動輸入」，
    /// 而手動導航的角色是 <b>vnavmesh</b> 在走的，那條路徑完全不經過 BMR 的 detour ⇒ 擋不到。
    /// 深牢自動探索（AI 自己一間間清）走的是 <c>AIHints.ForcedMovement</c>，
    /// <b>已經</b>被 <c>MovementOverride.DirectionToDestination</c> 的閘門涵蓋，不必也不該在這裡重複處理。
    /// <para>
    /// 🔑 <b>寫成「每幀對帳」而不是「按下／放開的邊緣觸發」是刻意的</b>：邊緣觸發要為每一條
    /// 離開路徑（路徑走完、使用者按停止、樓層切換、模組停用、設定改成「無」、例外）各補一次還原，
    /// 漏掉任何一條就會把 vnavmesh 的全域開關永久留在 <c>false</c>——而那個失敗是<b>靜默</b>的
    /// （使用者只會發現「我的 vnavmesh 從此不會動了」，沒有任何錯誤訊息）。
    /// 對帳式只有一條規則「想暫停 ≠ 正握著就去修正它」，上面每一種情況都讓 <c>wantPause</c>
    /// 自然變成 false，於是<b>下一幀自動還原</b>，不必逐條列舉。
    /// </para>
    /// <para>
    /// 🔴 <b>只在 vnavmesh 現在確實允許移動時才接手。</b>問到 <c>false</c> 代表別的外掛
    /// （或使用者自己在 vnavmesh 的除錯視窗裡）刻意關著它，這時我們什麼都不做、也不記帳，
    /// 免得放開 Alt 時替別人把開關打開。⚠️ 問不到（<c>null</c>）當成「可以接手」——
    /// 那是「不知道」不是「別人握著」，見 <see cref="DeepDungeonNav.GetMovementAllowed"/>。
    /// </para>
    /// <para>
    /// 📌 <b>放開後多快繼續</b>：還原是同一幀送出去的 IPC，vnavmesh 在它自己的
    /// <c>FollowPath.Update</c> 裡讀那個欄位 ⇒ 最慢下一幀（約 16ms）就恢復移動，
    /// 而且是<b>從角色當下位置沿原路徑續走</b>——路徑點沒有被清掉，不重算、也不倒回去。
    /// </para>
    /// <para>
    /// 🔴 <b>雙軌</b>：先跟 vnavmesh 借移動租約（v7.20.0.37 起才有的端點），借不到<b>而且</b>
    /// 原因是「端點不存在」時，才退回舊的 <c>Path.SetMovementAllowed</c> 全域開關那條路，
    /// 行為與加租約之前一字不差。⚠️ 端點存在但拒發租約（例如租約數已達上限）
    /// <b>不可以</b>退回舊路徑——那條寫的是全域開關，正是租約要消滅的東西。
    /// </para>
    /// <para>
    /// 🔑 <b>租約那條不必記得還</b>：BMR 當掉／被卸載／漏掉某一條離開路徑，租約最多 5 分鐘就逾時，
    /// vnavmesh 自動還原成<b>使用者自己的值</b>並寫一行 Information 指名是誰壓著。
    /// 舊路徑沒有這層保險，所以下面「還原失敗就重試到成功」那段只對舊路徑成立。
    /// </para>
    /// </remarks>
    private void UpdateNavPause()
    {
        // 🔴 續約心跳必須放在**任何**提早返回之前：下面那個「帳對上了就 return」是穩態時
        //    每一幀都會走到的路徑，把心跳擺在它後面等於租約永遠不會被續，5 分鐘後
        //    vnavmesh 自己放開——使用者按著暫停鍵，角色卻突然開始走，而且沒有任何訊息。
        RenewNavPauseLease();

        // 只有「路徑已經交出去、vnavmesh 正在走」才有東西可暫停。
        // Pathfinding 階段還沒有路徑，這時關掉全域開關只會白白影響到別的外掛。
        var wantPause = _walkState == WalkState.Moving
            && MovementOverride.Instance is { AutoMovementPaused: true };

        // 🔴 鎖內只拍快照就出來：底下每一條路都要呼叫 IPC，而對方自己也持鎖。
        bool held;
        Guid lease;
        lock (_navPauseGate)
        {
            held = _navPauseHeld;
            lease = _navPauseLease;
        }

        if (wantPause == held)
        {
            // 帳對上了＝沒有欠帳也沒有待辦，順手把「失敗已記過」清掉，
            // 讓下一串失敗還講得出話來。
            _navPauseFailureLogged = false;
            return;
        }

        var now = World.CurrentTime;
        if (_navPauseNextTry != DateTime.MinValue && now < _navPauseNextTry)
            return;

        if (wantPause)
        {
            // 別人刻意關著 ⇒ 不接手、不記帳（null＝問不到，視同可以接手）
            if (DeepDungeonNav.GetMovementAllowed() == false)
            {
                _navPauseNextTry = now.AddMilliseconds(NavPauseRetryMs);
                return;
            }

            // ── 主線：跟 vnavmesh 借一把移動租約 ──────────────────────
            switch (DeepDungeonNav.TryAcquireSuppression(out var fresh))
            {
                case DeepDungeonNav.SuppressionResult.Acquired:
                    // 🔴 借到租約**還沒有壓住任何東西**（新租約對受控值「沒有意見」），
                    //    要真的暫停必須接著送 SetLeasedMovementAllowed(false)。
                    if (!DeepDungeonNav.SetLeasedMovementAllowed(fresh, false))
                    {
                        // 剛借到就不在了（幾乎不可能）——交回去、節流重試，而且不記帳。
                        DeepDungeonNav.ReleaseSuppression(fresh);
                        _navPauseNextTry = now.AddMilliseconds(NavPauseRetryMs);
                        if (!_navPauseFailureLogged)
                        {
                            _navPauseFailureLogged = true;
                            Service.Logger.Information("[DD] 按住暫停：向 vnavmesh 借到移動租約，但緊接著的 Path.SetLeasedMovementAllowed 沒有生效（那把租約已經不在了），已交回並稍後重試。");
                        }
                        return;
                    }

                    lock (_navPauseGate)
                    {
                        _navPauseHeld = true;
                        _navPauseLease = fresh;
                        _navPauseRenewAtTick = Environment.TickCount64 + NavPauseRenewIntervalMs;
                    }
                    _navPauseNextTry = DateTime.MinValue;
                    _navPauseFailureLogged = false;
                    Service.Logger.Information($"[DD] 按住暫停：已用移動租約 {fresh} 請 vnavmesh 暫停手動導航。路徑點保留，放開按鍵即從原地續走；就算 BMR 這時候當掉，租約逾時後 vnavmesh 也會自己還原成使用者的設定。");
                    return;

                case DeepDungeonNav.SuppressionResult.Refused:
                    // 🔴 端點在、只是不發租約 ⇒ **不可以**退回舊路徑（那條寫的是全域開關，
                    //    正是租約要消滅的東西）。節流重試並把原因講出來；
                    //    vnavmesh 自己會在 Warning 裡列出目前的持有者名單。
                    _navPauseNextTry = now.AddMilliseconds(NavPauseRetryMs);
                    if (!_navPauseFailureLogged)
                    {
                        _navPauseFailureLogged = true;
                        Service.Logger.Information("[DD] 按住暫停：vnavmesh 拒絕發移動租約（多半是租約數已達上限，某個外掛只借不還）。手動導航這一段不受暫停鍵影響——要停請按「停止移動」，並看 vnavmesh 的 log 找出持有者。");
                    }
                    return;
            }

            // ── 退路：這個 vnavmesh 沒有租約端點（版本較舊，或根本沒安裝）──────
            //    以下與加入租約之前完全相同，包含記帳規則與訊息文字；
            //    「你的 vnavmesh 太舊」那行診斷等舊路徑真的成功之後才講——
            //    NotSupported 也可能只是「vnavmesh 根本沒安裝」，那時講它是不成立的。
            if (!DeepDungeonNav.SetMovementAllowed(false))
            {
                // 送不出去就**不要**記帳：沒關成功卻記成「我們握著」，放開時會憑空打開一個
                // 我們從來沒關過的開關。節流重試，避免每幀丟例外（沒安裝時 IPC 是靠擲例外回報的）。
                _navPauseNextTry = now.AddMilliseconds(NavPauseRetryMs);
                if (!_navPauseFailureLogged)
                {
                    _navPauseFailureLogged = true;
                    Service.Logger.Information("[DD] 按住暫停：vnavmesh 的 Path.SetMovementAllowed 叫不動（端點不存在或擲例外），手動導航這一段不受暫停鍵影響——要停請按「停止移動」。");
                }
                return;
            }

            lock (_navPauseGate)
            {
                _navPauseHeld = true;
                _navPauseLease = Guid.Empty;
                _navPauseRenewAtTick = 0L;
            }
            _navPauseNextTry = DateTime.MinValue;
            _navPauseFailureLogged = false;
            Service.Logger.Information("[DD] 按住暫停：已請 vnavmesh 暫停手動導航（Path.SetMovementAllowed=false）。路徑點保留，放開按鍵即從原地續走。");
            if (!_navPauseLegacyLogged)
            {
                _navPauseLegacyLogged = true;
                Service.Logger.Information("[DD] 按住暫停：這個 vnavmesh 沒有移動租約端點（v7.20.0.37 以前的版本），改用舊的全域開關。⚠️ 舊路徑沒有逾時保險——BMR 若在暫停期間當掉，vnavmesh 會留在「不移動」而且沒有任何訊息。建議更新 vnavmesh。");
            }
            return;
        }

        // ── 還原 ──────────────────────────────────────────────────────
        if (lease != Guid.Empty)
        {
            // 🔑 租約路徑不必「重試到成功」：交回失敗（多半是 vnavmesh 已經卸載）最多讓它再壓
            //    5 分鐘，逾時 vnavmesh 會自己還原成使用者的值並寫一行 log。所以這裡無條件銷帳，
            //    不會留下「欠帳卡住」的狀態。
            var released = DeepDungeonNav.ReleaseSuppression(lease);
            lock (_navPauseGate)
            {
                _navPauseHeld = false;
                _navPauseLease = Guid.Empty;
                _navPauseRenewAtTick = 0L;
            }
            _navPauseNextTry = DateTime.MinValue;
            _navPauseFailureLogged = false;
            Service.Logger.Information(released
                ? $"[DD] 按住暫停：已交回移動租約 {lease}，vnavmesh 回到使用者自己的設定，手動導航從原地續走。"
                : $"[DD] 按住暫停：交回移動租約 {lease} 時 vnavmesh 說那把已經不在了（多半是它重載過，或租約已經逾時），沒有事情要做。");
            return;
        }

        if (DeepDungeonNav.SetMovementAllowed(true))
        {
            lock (_navPauseGate)
            {
                _navPauseHeld = false;
                _navPauseLease = Guid.Empty;
                _navPauseRenewAtTick = 0L;
            }
            _navPauseNextTry = DateTime.MinValue;
            _navPauseFailureLogged = false;
            Service.Logger.Information("[DD] 按住暫停：已還原 vnavmesh 的移動開關（Path.SetMovementAllowed=true），手動導航從原地續走。");
            return;
        }

        // 🔴 還原失敗是唯一「不還原就會留下永久災情」的分支，所以預設是**保住欠帳並重試**。
        //    唯一可以放手的情況是 vnavmesh 根本不在了——那個欄位隨著它的實例一起消失，
        //    沒有任何東西需要還原；繼續重試只會每幀丟例外。
        if (!DeepDungeonNav.IsInstalled())
        {
            lock (_navPauseGate)
            {
                _navPauseHeld = false;
                _navPauseLease = Guid.Empty;
                _navPauseRenewAtTick = 0L;
            }
            _navPauseNextTry = DateTime.MinValue;
            _navPauseFailureLogged = false;
            Service.Logger.Information("[DD] 按住暫停：還原移動開關時發現 vnavmesh 已經不在了，欠帳一併作廢（它的狀態隨實例消失，不需要還原）。");
            return;
        }

        _navPauseNextTry = now.AddMilliseconds(NavPauseRetryMs);
        if (!_navPauseFailureLogged)
        {
            _navPauseFailureLogged = true;
            Service.Logger.Information("[DD] 按住暫停：還原 vnavmesh 移動開關失敗，稍後重試（在還原成功之前不會放棄）。");
        }
    }

    /// <summary>
    /// 模組收攤時的最後一道保險：把還沒還回去的移動開關還原。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="UpdateNavPause"/> 的對帳只在 <c>Update()</c> 跑得到的時候有效。
    /// 離開深牢／卸載外掛時 <c>Update()</c> 就此不再被呼叫，對帳沒有機會執行 ⇒ 這裡補一次。
    /// 沒有這一段的話「按著 Alt 的瞬間傳送出深牢」就會把 vnavmesh 的全域開關永久留在 false。
    /// ⚠️ 只在<b>我們確實握著</b>時才動它（<see cref="_navPauseHeld"/>），不要無條件寫 true——
    /// 那會把別的外掛刻意關著的開關打開。
    /// <para>
    /// 📌 走租約時這裡是「交回租約」而不是「把開關寫回 <c>true</c>」——交回失敗也不要緊，
    /// 逾時 vnavmesh 會自己還原成使用者的值。
    /// </para>
    /// </remarks>
    private void ReleaseNavPause()
    {
        bool held;
        Guid lease;
        lock (_navPauseGate)
        {
            held = _navPauseHeld;
            lease = _navPauseLease;
            _navPauseHeld = false;
            _navPauseLease = Guid.Empty;
            _navPauseRenewAtTick = 0L;
        }

        if (!held)
            return;

        try
        {
            // 租約：交回就好，交回失敗也只是讓它壓到逾時。舊路徑：只能把全域開關寫回去。
            if (lease != Guid.Empty)
                DeepDungeonNav.ReleaseSuppression(lease);
            else
                DeepDungeonNav.SetMovementAllowed(true);
        }
        catch (Exception ex)
        {
            Service.Log($"[DD nav] 還原 vnavmesh 移動開關失敗（可忽略）: {ex.Message}");
        }
    }

    #endregion

    #region 卡住偵測

    /// <summary>認定「沒在動」的位移門檻（碼）。</summary>
    private const float StuckMoveThreshold = 1.5f;

    /// <summary>連續沒動這麼久（秒）就放棄本次目標。</summary>
    private const float StuckSeconds = 6f;

    private WPos _stuckLastPos;
    private DateTime _stuckSince;
    private string? _stuckMessage;

    /// <summary>
    /// 一直沒走到就放棄，並且說出來。
    /// </summary>
    /// <remarks>
    /// 🔑 這同時是「台服障礙物地圖可能對不上」的緩解措施：那份地圖是上游從國際服產的，
    /// 對不上時的表現是尋路把角色頂在牆上原地磨，<b>不會有任何錯誤訊息</b>，
    /// 使用者只看到角色卡住不動。與其猜地圖對不對，不如直接觀測「有沒有真的在前進」。
    /// <para>
    /// ⚠️ 只在「AI 開著、不在戰鬥、而且確實有目標房間」時計時：
    /// AI 沒開時角色本來就不會動，戰鬥中站著打也不是卡住 —— 把那兩種算成卡住是說謊。
    /// </para>
    /// </remarks>
    private void UpdateStuckDetection()
    {
        var player = World.Party.Player();
        var navigating = player != null
            && Config.Enable
            && !BetweenFloors
            && !Palace.IsBossFloor
            && DesiredRoom > 0
            && !player.InCombat
            && AI.AIManager.Instance?.Beh != null;

        if (!navigating)
        {
            _stuckSince = default;
            return;
        }

        var now = World.CurrentTime;
        if (_stuckSince == default || (player!.Position - _stuckLastPos).LengthSq() > StuckMoveThreshold * StuckMoveThreshold)
        {
            _stuckLastPos = player!.Position;
            _stuckSince = now;
            return;
        }

        if ((now - _stuckSince).TotalSeconds < StuckSeconds)
            return;

        var abandoned = DesiredRoom;
        _stuckSince = default;
        DesiredRoom = 0;
        _destinationSource = DestinationSource.User;
        _stuckMessage = string.Format(
            Loc.T("DD_StuckAbandoned", "Gave up on room {0}: no progress for {1:f0}s. The floor's obstacle map may not match this map; pick a destination again to retry."),
            abandoned, StuckSeconds);
        Service.Logger.Information($"[DD] 卡住偵測：{StuckSeconds} 秒內位移不足 {StuckMoveThreshold}y，放棄前往房間 {abandoned}（樓層 {Palace.Floor}、版面 {Palace.Progress.Tileset}）");
    }

    #endregion

    public override void Update()
    {
        base.Update();

        // 🔴 擺在最前面是刻意的：這一支是 vnavmesh 全域移動開關的「還原對帳」，
        //    後面任何一段擲例外都不該連帶讓開關留在關閉狀態。
        UpdateNavPause();

        UpdateStopWatchdog();
        UpdateStuckDetection();
        UpdatePalacePal();
        UpdatePalacePalVisibleHoards();

        // 純顯示，不影響任何決策。放在這裡是因為它必須每幀跑，
        // 而且要早於 Plugin.DrawUI 尾端的 Camera.DrawWorldPrimitives。
        // 📌 刻意不受 Config.Enable（＝自動化模組總開關）影響，與小地圖同一個立場：
        //    只想看標示、不想被自動移動的人才是這個功能的主要對象。
        DrawHoardOverlay();

        // 只有真的在深牢裡才壓住 WrathCombo；模組本身只在深牢區域存在，
        // 但過場／讀取中 DungeonId 會是 0，那時不該接管
        _wrathCombo.Update(Config.SuspendWrathCombo && Palace.DungeonId != DeepDungeonState.DungeonType.None, World.CurrentTime);

        switch (_walkState)
        {
            case WalkState.Pathfinding:
                PollWalkPathfind();
                break;
            case WalkState.Moving:
                // vnavmesh 沒有「到了」的回呼，路徑點走完就是到了（或被停掉了）
                if (!DeepDungeonNav.IsPathRunning())
                {
                    _walkState = WalkState.Idle;
                    _walkMessage = null;
                }
                break;
        }
    }

    /// <summary>
    /// 使用者按下停止之後，持續補送停止直到真的停了。
    /// </summary>
    /// <remarks>
    /// 送一次就好嗎？我們自己這條路徑是的（見 <see cref="_walkGeneration"/>）。
    /// 但別的外掛可能也在用 vnavmesh，而使用者按停止的意思就是「現在給我停下來」，
    /// 所以窗口內只要偵測到還在走就補送。窗口 3 秒，每 100ms 一次，
    /// 確認既沒在算也沒在走就提早收工，不會空轉滿 3 秒。
    /// </remarks>
    private void UpdateStopWatchdog()
    {
        if (_walkStopEnforceUntil == DateTime.MinValue)
            return;

        var now = World.CurrentTime;
        if (now >= _walkStopEnforceUntil)
        {
            _walkStopEnforceUntil = DateTime.MinValue;
            return;
        }

        if (now < _walkNextEnforce)
            return;
        _walkNextEnforce = now.AddMilliseconds(100);

        var running = DeepDungeonNav.IsPathRunning();
        if (running)
            DeepDungeonNav.Stop();
        else if (!DeepDungeonNav.IsSimpleMovePathfinding())
            _walkStopEnforceUntil = DateTime.MinValue;
    }

    private void PollWalkPathfind()
    {
        if (_walkTask is not { IsCompleted: true } task)
            return;
        _walkTask = null;

        // 這段期間使用者按過停止 ⇒ 整條作廢
        if (_walkTaskGeneration != _walkGeneration)
        {
            _walkState = WalkState.Idle;
            return;
        }

        if (task.IsFaulted)
        {
            // 讀一次 Exception 把它「觀察掉」，順便留下真正的原因；
            // 只判 IsFaulted 而不碰 Exception 會留下 unobserved task exception。
            Service.Log($"[DD nav] vnavmesh 算路徑失敗: {task.Exception?.InnerException?.Message ?? task.Exception?.Message}");
            SetWalkBlocked(Loc.T("DD_WalkNoRoute", "vnavmesh could not find a route to that room."));
            return;
        }

        if (task.IsCanceled || task.Result is not { Count: > 0 } path)
        {
            SetWalkBlocked(Loc.T("DD_WalkNoRoute", "vnavmesh could not find a route to that room."));
            return;
        }

        // 🔴 路徑點驗證——整個功能的安全核心，不可省略。
        if (ValidateWalkPath(path) is string reason)
        {
            SetWalkBlocked(reason);
            return;
        }

        if (!DeepDungeonNav.MoveAlong(path))
        {
            SetWalkBlocked(Loc.T("DD_WalkHandoffFailed", "vnavmesh refused the route."));
            return;
        }

        _walkState = WalkState.Moving;
        _walkMessage = null;
    }

    /// <summary>
    /// 🔴 檢查 vnavmesh 給的路徑有沒有跑出「房間走廊」。
    /// </summary>
    /// <remarks>
    /// <b>為什麼需要這一步</b>：vnavmesh 的導航網格快取鍵在同一組樓層的 10 層裡是相同的，
    /// 但門與牆是<b>逐層不同</b>的——也就是它手上那份網格很可能是<b>上一層</b>的。
    /// 拿那份網格算出來的路會大方地穿過這一層其實關著的門，而且完全不報錯。
    /// <para>
    /// 檢查方式：先用<b>活的連通旗標</b>做房間層 BFS 算出「從現在這間走到目標該經過哪些房間」，
    /// 再逐一驗證每個路徑點最近的房間中心是否落在那個集合裡。跨的房間越多，
    /// 舊網格繞錯路的機會越大，所以這個檢查在跨房版本比單跳版本更重要。
    /// </para>
    /// <para>📌 房間歸屬用「最近的房間中心」而不設距離上限：門口那種夾在兩間中間的點也要有歸屬。</para>
    /// </remarks>
    /// <returns>null＝通過；否則是要顯示給使用者看的拒絕原因。</returns>
    private string? ValidateWalkPath(List<Vector3> path)
    {
        var count = path.Count;
        for (var i = 0; i < count; ++i)
        {
            var w = path[i];
            var room = NearestRoom(new WPos(w.X, w.Z), float.MaxValue);
            if (room < 0 || !_walkCorridor.Contains(room))
                return string.Format(
                    Loc.T("DD_WalkPathLeavesCorridor", "Refusing to move: vnavmesh's route leaves the corridor of rooms that are actually connected on this floor (waypoint {0} of {1} lands in room {2}). Its navigation mesh is probably still the one from the previous floor."),
                    i + 1, count, room);
        }
        return ValidateWalkPathTraps(path);
    }

    #region 路徑的線段級陷阱檢查

    /// <summary>
    /// 陷阱圓的半徑（碼）。<b>AI 迴避與手動導航的線段檢查共用這一個值。</b>
    /// </summary>
    /// <remarks>
    /// 🔴 兩邊用不同半徑的話，「AI 會閃但手動導航說沒問題」（或反過來）
    /// 都會變成使用者眼中的自相矛盾，而且沒有任何地方看得出來為什麼。
    /// 原本兩處都是各自寫死的 <c>2f</c>，抽成常數是為了讓它們不可能再走散。
    /// </remarks>
    private const float TrapCircleRadius = 2f;

    /// <summary>
    /// 這一次驗證要比對哪些陷阱點。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 兩個來源，與 AI 迴避完全一致：<b>資料庫陷阱</b>（受 <c>TrapHints</c> 與
    /// 「本層還沒被全景照出來」<see cref="_trapsHidden"/> 兩個條件管），
    /// 以及<b>已現形的陷阱實體</b>（<see cref="RevealedTrapOIDs"/>，AI 那邊不看 <c>TrapHints</c>，
    /// 這裡也不看）。使用者忽略掉的點（<see cref="IgnoreTraps"/>）一律排除。
    /// </para>
    /// <para>
    /// 🔴 <b>人已經站在裡面的陷阱圓要跳過。</b>資料庫有誤報時（那顆「把最近的陷阱設為忽略」
    /// 按鈕就是為此存在的），人可能正站在一個標記點上——不跳過的話第一段永遠是髒的，
    /// 這顆按鈕會<b>永久失效</b>而且原因看起來像「這條路有陷阱」。
    /// 拒絕移動也救不了「已經站在上面」，所以跳過不損失任何安全性。
    /// </para>
    /// </remarks>
    private List<WPos> CollectWalkTrapPoints(WPos? playerPos)
    {
        List<WPos> res = [];

        bool StandingOn(WPos t) => playerPos is WPos p && t.InCircle(p, TrapCircleRadius);

        if (Config.TrapHints && _trapsHidden)
        {
            var ignoreCount = IgnoreTraps.Count;
            void AddFrom(WPos[] source)
            {
                var len = source.Length;
                for (var i = 0; i < len; ++i)
                {
                    var trap = source[i];
                    var ignored = false;
                    for (var j = 0; j < ignoreCount; ++j)
                    {
                        if (IgnoreTraps[j].AlmostEqual(trap, 1f))
                        {
                            ignored = true;
                            break;
                        }
                    }
                    if (!ignored && !StandingOn(trap))
                        res.Add(trap);
                }
            }

            AddFrom(_trapsCurrentZone);
            AddFrom(_palTraps);
        }

        foreach (var a in World.Actors)
        {
            if (RevealedTrapOIDs.Contains(a.OID) && !StandingOn(a.Position))
                res.Add(a.Position);
        }

        return res;
    }

    /// <summary>點到線段的距離（只算 XZ 平面）。</summary>
    private static float DistanceToSegment(WPos p, WPos a, WPos b)
    {
        var ab = b - a;
        var lenSq = ab.LengthSq();
        if (lenSq < 1e-6f)
            return (p - a).Length();
        var t = Math.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }

    /// <summary>
    /// 🔴 路徑<b>線段</b>會不會經過已知陷阱。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>為什麼不能只驗頂點</b>：<see cref="ValidateWalkPath"/> 原本只檢查每個路徑點落在哪一間房，
    /// 而 vnavmesh 給的是<b>轉折點</b>——兩個轉折點之間可以是幾十碼的直線，
    /// 中間踩過什麼完全沒有被看過。頂點全部乾淨與「這條路乾淨」是兩回事。
    /// </para>
    /// <para>
    /// 📌 第一段的起點是<b>玩家目前位置</b>，因為 <c>Path.MoveTo</c> 就是從角色現在的位置
    /// 走向第一個路徑點；vnavmesh 回傳的清單有沒有含起點在這裡不必知道
    /// （含的話第一段長度接近 0，不影響結論）。
    /// </para>
    /// <para>
    /// 🔴 <b>只拒絕，不繞路。</b>拒絕之後把原因寫給使用者看（不能靜默），
    /// 由使用者自己決定要走過去、還是把那個點設為忽略。
    /// </para>
    /// </remarks>
    private string? ValidateWalkPathTraps(List<Vector3> path)
    {
        var count = path.Count;
        if (count == 0)
            return null;

        var player = World.Party.Player();
        WPos? playerPos = player != null ? player.Position : null;

        var traps = CollectWalkTrapPoints(playerPos);
        if (traps.Count == 0)
            return null;

        // 路徑的包圍盒（外擴一個陷阱半徑）先濾一遍。同一個區域的陷阱表有數百筆而且橫跨整組樓層，
        // 大部分根本不在這條路附近。
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;
        void Extend(WPos q)
        {
            minX = Math.Min(minX, q.X);
            maxX = Math.Max(maxX, q.X);
            minZ = Math.Min(minZ, q.Z);
            maxZ = Math.Max(maxZ, q.Z);
        }
        if (playerPos is WPos pp0)
            Extend(pp0);
        for (var i = 0; i < count; ++i)
            Extend(new WPos(path[i].X, path[i].Z));
        minX -= TrapCircleRadius;
        maxX += TrapCircleRadius;
        minZ -= TrapCircleRadius;
        maxZ += TrapCircleRadius;

        var prev = playerPos ?? new WPos(path[0].X, path[0].Z);
        var trapCount = traps.Count;
        for (var i = 0; i < count; ++i)
        {
            var cur = new WPos(path[i].X, path[i].Z);
            for (var j = 0; j < trapCount; ++j)
            {
                var t = traps[j];
                if (t.X < minX || t.X > maxX || t.Z < minZ || t.Z > maxZ)
                    continue;
                var dist = DistanceToSegment(t, prev, cur);
                if (dist <= TrapCircleRadius)
                {
                    Service.Logger.Information(
                        $"[DD] 手動導航拒絕：第 {i + 1}/{count} 段（{prev.X:f1},{prev.Z:f1} → {cur.X:f1},{cur.Z:f1}）" +
                        $"距已知陷阱 ({t.X:f1},{t.Z:f1}) 只有 {dist:f1}y（門檻 {TrapCircleRadius}y）。");
                    return string.Format(
                        Loc.T("DD_WalkPathNearTrap", "Refusing to move: leg {0} of {1} of vnavmesh's route passes {2:f1}y from a known trap (the threshold is {3:f1}y). Nothing is moved. Walk that stretch yourself, or use \"set closest trap location as ignored\" if you know that marker is wrong."),
                        i + 1, count, dist, TrapCircleRadius);
                }
            }
            prev = cur;
        }
        return null;
    }

    #endregion

    private void SetWalkBlocked(string message)
    {
        _walkState = WalkState.Idle;
        _walkMessage = message;
    }

    private void StartWalk(Actor player, int targetRoom)
    {
        var playerRoom = FindPlayerRoom(player);
        if (playerRoom < 0)
        {
            SetWalkBlocked(Loc.T("DD_WalkRoomUnknown", "The game has not reported which room you are in yet."));
            return;
        }

        // 房間層 BFS，只認活的連通旗標（也就是這一層真的打開的門）
        var rooms = new FloorPathfind(Palace.Rooms).Pathfind(playerRoom, targetRoom);
        if (rooms.Count == 0)
        {
            SetWalkBlocked(Loc.T("DD_BlockedNoPath", "No route to that room yet - the rooms in between have not been revealed."));
            return;
        }

        if (!TryGetRoomDestination(player, targetRoom, out var dest))
        {
            SetWalkBlocked(Loc.T("DD_WalkNoDestinationPoint", "The position of that room is unknown on this floor."));
            return;
        }

        var task = DeepDungeonNav.Pathfind(player.PosRot.XYZ(), dest);
        if (task == null)
        {
            SetWalkBlocked(Loc.T("DD_WalkNoRoute", "vnavmesh could not find a route to that room."));
            return;
        }

        _walkCorridor = [playerRoom, .. rooms];
        _walkTargetRoom = targetRoom;
        _walkTask = task;
        _walkTaskGeneration = _walkGeneration;
        _walkState = WalkState.Pathfinding;
        _walkMessage = null;
        Service.Logger.Information($"[DD] 手動導航：房間 {playerRoom} → {targetRoom}，走廊 [{string.Join(", ", _walkCorridor)}]");
    }

    /// <summary>
    /// 目標房間要走到哪一個世界座標。
    /// </summary>
    /// <remarks>
    /// 目標房有通道石而且實體已經在 <c>ObjectTable</c> 裡，就走到實體旁邊——
    /// 「下層解鎖後點傳送標記那一格」正是這個功能的主要使用情境。
    /// 🔴 <b>只是走過去，絕不自動互動</b>：要不要下樓由使用者自己點。
    /// </remarks>
    private bool TryGetRoomDestination(Actor player, int room, out Vector3 dest)
    {
        dest = default;

        if (Palace.Rooms[room].HasFlag(RoomFlags.Passage))
        {
            foreach (var a in World.Actors)
            {
                if (a.OID is not ((uint)OID.CairnPalace or (uint)OID.BeaconHoH or (uint)OID.PylonEO))
                    continue;
                if (_fakeExits.Contains(a.InstanceID))
                    continue;
                if (NearestRoom(a.Position, RoomTolerance) != room)
                    continue;
                dest = a.PosRot.XYZ();
                return true;
            }
        }

        if (RoomCenters[room] is not WPos c)
            return false;

        // 房間中心只有 X／Z，高度要問 vnavmesh 的地板查詢。
        // ⚠️ 探測起點的 Y 要高於地形，否則會從地板底下往下找而落空。
        // 查不到就退回玩家目前的高度——深牢單層是平的，這個退路夠用，也比猜一個數字誠實。
        dest = DeepDungeonNav.TryPointOnFloor(new Vector3(c.X, player.PosRot.Y + 2f, c.Z), out var onFloor)
            ? onFloor
            : new Vector3(c.X, player.PosRot.Y, c.Z);
        return true;
    }

    private void RequestWalkStop()
    {
        ++_walkGeneration; // 還在背景算的路徑就此作廢
        _walkTask = null;
        _walkState = WalkState.Idle;
        _walkMessage = null;
        DeepDungeonNav.Stop();
        _walkStopEnforceUntil = World.CurrentTime.AddSeconds(3d);
        _walkNextEnforce = DateTime.MinValue;
    }

    private DateTime _vnavProbedAt = DateTime.MinValue;
    private bool _vnavInstalled;
    private bool _vnavMeshReady;

    /// <summary>
    /// 探測 vnavmesh 在不在、網格好了沒。
    /// </summary>
    /// <remarks>
    /// ⚠️ 刻意<b>不長期快取</b>——使用者可能中途裝上或停用外掛，沿用舊判定會顯示錯的原因。
    /// 但也不能每幀直接探：沒安裝時 <c>InvokeFunc</c> 是靠<b>擲例外</b>回報的，
    /// 每幀丟兩個例外只為了畫一行灰字並不划算。取 0.5 秒重探一次，
    /// 短到使用者感覺不出延遲，又不會變成每幀成本。
    /// </remarks>
    private void ProbeVnav()
    {
        var now = World.CurrentTime;
        if (_vnavProbedAt != DateTime.MinValue && (now - _vnavProbedAt).TotalSeconds < 0.5d)
            return;
        _vnavProbedAt = now;
        _vnavInstalled = DeepDungeonNav.IsInstalled();
        _vnavMeshReady = _vnavInstalled && DeepDungeonNav.IsMeshReady();
    }

    /// <summary>按鈕不能按的原因；null＝可以按。</summary>
    private string? GetWalkBlockedReason(Actor player, RoomCoordState coords)
    {
        if (DesiredRoom <= 0)
            return Loc.T("DD_WalkPickRoomFirst", "Pick a destination room on the map first.");

        // 🔑 三態分開講：要去裝外掛／只要等一下／裝好了但這一層不能信任，處置完全不同
        ProbeVnav();
        if (!_vnavInstalled)
            return Loc.T("DD_WalkNoVnav", "vnavmesh is not installed or not loaded, so walking is unavailable.");
        if (!_vnavMeshReady)
            return Loc.T("DD_WalkMeshNotReady", "vnavmesh's navigation mesh is not ready yet (still loading, or there is no mesh for this area).");
        if (coords != RoomCoordState.Ok)
            return Loc.T("DD_WalkCoordsUnverified", "The built-in room coordinates do not check out on this floor, so a route cannot be verified. Walking is disabled here.");

        var playerRoom = FindPlayerRoom(player);
        if (playerRoom < 0)
            return Loc.T("DD_WalkRoomUnknown", "The game has not reported which room you are in yet.");
        if (playerRoom == DesiredRoom)
            return Loc.T("DD_WalkAlreadyThere", "You are already in that room.");
        return null;
    }

    private void DrawWalkControls(Actor player, RoomCoordState coords)
    {
        // 已經在算／在走的時候不要再給「走過去」——重複按只會把自己的路徑重下一次
        var blocked = WalkActive ? null : GetWalkBlockedReason(player, coords);
        if (WalkActive)
        {
            // 只留停止鈕，狀態由下面那行說明
        }
        else if (blocked != null)
        {
            ImGui.TextColored(ColorUnknownText, blocked);
        }
        else
        {
            if (ImGui.Button(string.Format(Loc.T("DD_WalkToRoom", "Walk to room {0}"), DesiredRoom)))
                StartWalk(player, DesiredRoom);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(Loc.T("DD_WalkToRoomTooltip", "Walks there and stops on arrival. It does not open coffers, does not use the {0}, and does not start the next leg by itself.\n\nThe route does not avoid mobs. It is checked against known traps before anything moves - not just the corners, but every stretch in between - and the whole route is refused (with the reason shown here) if any of it passes too close to one. It is never re-routed around them.\nIf you run NecroLens with automatic coffer opening, walking past a coffer will make both plugins reach for it."), PassageName));
            ImGui.SameLine();
        }

        // 停止一律可按：Path.Stop 對「本來就沒在動」是安全的無操作，
        // 而按鈕變灰的那半秒恰好是最想反悔的半秒。
        if (ImGui.Button(Loc.T("DD_WalkStop", "Stop moving")))
            RequestWalkStop();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("DD_WalkStopTooltip", "Asks vnavmesh to stop. Safe to press at any time. Note this also stops movement started by other plugins."));

        if (_walkMessage != null)
            ImGui.TextColored(ColorUnknownText, _walkMessage);
        else if (_walkState == WalkState.Pathfinding)
            ImGui.TextColored(ColorUnknownText, Loc.T("DD_WalkComputing", "Computing a route..."));
        else if (_walkState == WalkState.Moving)
            ImGui.Text(string.Format(Loc.T("DD_WalkMoving", "Walking to room {0}."), _walkTargetRoom));
    }

    #endregion

    /// <summary>
    /// 小地圖下方的狀態列：目標房間是哪一間、由誰決定的、以及<b>為什麼現在沒在動</b>。
    /// </summary>
    /// <remarks>
    /// 🔑 存在的理由是「不會動」這件事原本完全沒有回饋——使用者點了格子、角色不動，
    /// 而原因可能是模組沒開、BMR 的 AI 沒開、在戰鬥中、路還沒探到，
    /// 或者目標剛被「探索所有房間」無條件蓋掉。這幾種的處置完全不同，
    /// 合併成一句「沒在動」等於沒說。
    /// </remarks>
    private void DrawNavigationStatus(Actor player)
    {
        if (DesiredRoom <= 0)
        {
            ImGui.TextColored(ColorUnknownText, Loc.T("DD_NoDestination", "No destination room set (click a room on the map)."));
            return;
        }

        var source = _destinationSource switch
        {
            DestinationSource.FullClear => Loc.T("DD_DestFromFullClear", " (set by \"reveal all rooms\", which overrides your pick)"),
            // 深牢內就用這一座傳送裝置的真名（每座不同），沒看過實體才退回通用詞
            DestinationSource.AutoPassage => string.Format(Loc.T("DD_DestFromAutoPassage", " (set by \"navigate to {0}\")"), PassageName),
            _ => "",
        };
        ImGui.Text(string.Format(Loc.T("DD_Destination", "Destination: room {0}{1}"), DesiredRoom, source));

        // 為什麼沒在動。順序＝由外而內，先講最根本的那一個。
        string? blocked = null;
        if (!Config.Enable)
            blocked = Loc.T("DD_BlockedModuleOff", "The Auto-DeepDungeon module is switched off, so nothing will move.");
        else if (BetweenFloors)
            blocked = Loc.T("DD_BlockedBetweenFloors", "Changing floors.");
        else if (AI.AIManager.Instance?.Beh == null)
            blocked = Loc.T("DD_BlockedAIOff", "Navigation only happens while BMR's AI is running; it is currently off.");
        else if (player.InCombat && Config.MaxPull == 0)
            blocked = Loc.T("DD_BlockedInCombat", "Navigation is paused during combat (\"max mobs to pull\" is 0).");
        else if (TravelBlockedByHP(player))
            blocked = string.Format(Loc.T("DD_BlockedLowHP", "Travelling is paused below {0}% HP."), Config.StopTravelBelowHPPercent);
        else if (_lastPathfindFailed)
            blocked = Loc.T("DD_BlockedNoPath", "No route to that room yet - the rooms in between have not been revealed.");

        if (blocked != null)
            ImGui.TextColored(ColorUnknownText, blocked);

        // 🔑 「戰鬥中不趕路」與「戰鬥中不走位」是兩回事，而 MaxPull 的舊文案讀起來像後者。
        //    這一行是為了讓使用者不必猜：閃避走位永遠開著。
        if (Config.MaxPull == 0 && player.InCombat)
            ImGui.TextColored(ColorUnknownText, Loc.T("DD_CombatMovementNote", "(Dodging and combat positioning are always on - this only pauses travelling to the destination room.)"));

        if (_stuckMessage != null)
            ImGui.TextColored(ColorUnknownText, _stuckMessage);

        DrawKiteStatus();

        // 🔑 使用者回報過「BMR 鎖住我的設定」。租約持有期間 WrathCombo 側的設定確實會被鎖，
        //    那是租約機制的正常表現——但在此之前完全看不出來是誰鎖的、怎麼解。
        //    握著租約就直說，並寫出解除方式。
        if (_wrathCombo.Active)
            ImGui.TextColored(ColorUnknownText, Loc.T("DD_WrathLeaseHeld",
                "Holding WrathCombo's lease - its settings stay locked while this is active. Untick \"pause WrathCombo's auto-rotation\" above to hand control back immediately."));
    }

    /// <summary>
    /// 風箏對當前目標被停用時說出來。
    /// </summary>
    /// <remarks>
    /// 🔑 使用者的體感是「風箏壞了」，實際上是「這隻怪被判定為遠程平砍所以刻意不風箏」。
    /// 不寫出來的話兩者長得一模一樣，只能去翻 log。
    /// ⚠️ 只在資訊夠新時顯示（自動循環模組沒在跑時那個狀態會凍住，顯示過期狀態＝說謊）。
    /// </remarks>
    private void DrawKiteStatus()
    {
        if ((World.CurrentTime - Autorotation.xan.DeepDungeonAI.SuppressionAt).TotalSeconds > 2d)
            return;

        var text = Autorotation.xan.DeepDungeonAI.Suppression switch
        {
            Autorotation.xan.DeepDungeonAI.KiteSuppression.HardcodedList
                => Loc.T("DD_KiteOffList", "Kiting: off for this enemy (listed as not using melee autos)."),
            Autorotation.xan.DeepDungeonAI.KiteSuppression.ObservedRanged
                => Loc.T("DD_KiteOffObserved", "Kiting: off for this enemy (observed hitting you from out of melee range)."),
            _ => null,
        };
        if (text != null)
            ImGui.TextColored(ColorUnknownText, text);
    }

    #region 傳送裝置的名稱

    /// <summary>
    /// 目前這座深牢的傳送裝置叫什麼，以及那是哪一座的。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>每一座深牢的傳送裝置官方名稱都不一樣</b>，寫死任何一個都會在別座變成錯的：
    /// 死者宮殿是「傳送石塚」、天之逆焰是「傳送燈籠」、厄運迷宮是「傳送裝置」
    /// （已逐一對台服 EObjName 表核對過 OID：0x1EA094／0x1EA9A3／0x1EB867）。
    /// <para>
    /// 🔑 名稱直接取自遊戲物件本身（<c>Actor.Name</c> ← <c>GameObject.NameString</c>），
    /// <b>不查 Lumina</b>：<c>Service.LuminaSheet</c> 寫死 <c>Language.English</c>，
    /// 在台服客戶端上要不到繁中字串。物件自己的名字就是遊戲當下顯示的那個，最可信。
    /// </para>
    /// <para>
    /// 📌 記住看過的名字（以深牢種類為鍵）：傳送裝置只有靠近時才會串流進 ObjectTable，
    /// 不快取的話大部分時間都只能顯示通用詞。換一座深牢就重新解析。
    /// </para>
    /// </remarks>
    private (DeepDungeonState.DungeonType Dungeon, string Name)? _passageName;

    private void RememberPassageName(Actor passage)
    {
        if (string.IsNullOrEmpty(passage.Name))
            return;
        if (_passageName is { } cached && cached.Dungeon == Palace.DungeonId && cached.Name == passage.Name)
            return;
        _passageName = (Palace.DungeonId, passage.Name);
    }

    /// <summary>
    /// 顯示用的傳送裝置名稱；還沒看過實體時退回通用詞。
    /// </summary>
    /// <remarks>⚠️ 通用詞刻意不是任何一座的專名，寧可籠統也不要在別座說錯。</remarks>
    protected string PassageName =>
        _passageName is { } n && n.Dungeon == Palace.DungeonId
            ? n.Name
            : Loc.T("DD_PassageGeneric", "the exit to the next floor");

    #endregion

    #region 保命藥水

    /// <summary>
    /// 保命藥水的候選，<b>依偏好順序</b>。
    /// </summary>
    /// <remarks>
    /// 📌 全部是 <c>ActionDefinitions</c> 已經註冊過的藥水（<c>RegisterPotion</c>），
    /// 所以冷卻、詠唱時間、動作鎖都有現成定義可查，不必自己算。
    /// 每個 ActionID 的 <c>ID</c> 若 ≥ 1000000 代表 HQ 版本。
    /// <para>
    /// 🔑 之所以要「一串」而不是「該座專屬那一瓶」：原本的寫法是
    /// <c>DungeonId switch { POTD =&gt; 頂級治療劑, HOH =&gt; 上級治療劑, EO =&gt; 聖級治療劑 }</c>，
    /// 於是人在天之御柱、包裡有 366 個 HQ 頂級治療劑，卻只會去找上級治療劑 ——
    /// 沒有就靜默什麼都不做。一般治療劑在深牢內是可以用的，沒有理由排除。
    /// </para>
    /// <para>⚠️ 順序是「效果由大到小」：頂級 &gt; 聖級 &gt; 上級，同一階 HQ 優先。</para>
    /// </remarks>
    private static readonly ActionID[] EmergencyPotions = [
        ActionDefinitions.IDPotionMax,     // HQ 頂級治療劑（死者宮殿專屬，但一般場合也能用）
        new(ActionType.Item, 13637),       // NQ 頂級治療劑
        ActionDefinitions.IDPotionHyper,   // HQ 聖級治療劑（厄運迷宮）
        new(ActionType.Item, 38956),
        ActionDefinitions.IDPotionSuper,   // HQ 上級治療劑（天之逆焰）
        new(ActionType.Item, 23167),
    ];

    private bool _loggedNoPotion;

    /// <summary>
    /// 血量過低時推一瓶藥水。
    /// </summary>
    /// <remarks>
    /// 門檻沿用原本寫死的 30%（與搬移前完全相同，不趁機改行為）。
    /// 冷卻用 <c>ActionDefinition.ReadyIn</c> 查，不自己記時間——藥品共用冷卻群組，
    /// 自己記一定會跟遊戲脫節。
    /// </remarks>
    private unsafe void UpdateEmergencyPotion(Actor player, AIHints hints)
    {
        if (player.HPMP.MaxHP == 0 || player.HPRatio > 0.3f)
            return;
        if (player.FindStatus((uint)SID.ItemPenalty) != null)
            return; // 藥品封印層

        var defs = ActionDefinitions.Instance;
        foreach (var aid in EmergencyPotions)
        {
            var def = defs[aid];
            if (def == null)
                continue;
            if (def.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) > 0f)
                continue; // 共用冷卻還沒好

            // ⚠️ 這是唯讀的原生查詢，不保存任何指標；換區途中會回 0，
            //    那時就當作沒有這瓶（安全退化，不會誤用）。
            var baseId = aid.ID % 1000000u;
            var hq = aid.ID >= 1000000u;
            if (FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetInventoryItemCount(baseId, hq, false, false) <= 0)
                continue;

            hints.ActionsToExecute.Push(aid, player, ActionQueue.Priority.VeryHigh);
            _loggedNoPotion = false;
            return;
        }

        if (!_loggedNoPotion)
        {
            _loggedNoPotion = true;
            Service.Logger.Information("[DD] 血量低於 30% 但背包裡沒有任何可用的治療劑（或全部還在冷卻），無法自動補血。");
        }
    }

    #endregion

    /// <summary>血量低於門檻就不趕路（門檻 0＝停用）。</summary>
    /// <summary>
    /// 把一份陷阱點清單裡「在尋路視窗附近、而且沒有被使用者忽略」的那些加成禁區圓。
    /// </summary>
    /// <remarks>
    /// 🔴 半徑要蓋得住尋路視窗，否則視窗角落的陷阱不會進 forbidden zone，
    /// 表現是「大部分陷阱會閃、偶爾一個不閃」而不是整個功能壞掉。
    /// 深牢用的是 <c>AIHints.DefaultBounds</c> ＝ <c>ArenaBoundsSquare(30f)</c>，
    /// 也就是以玩家為中心、半邊長 30y 的方形；角落離中心 30·√2 ≒ 42.4y
    /// ⇒ 30y 的查詢半徑蓋不到角落，取 45y。
    /// </remarks>
    private void AddNearbyTrapCircles(WPos[] source, Actor player, List<Func<WPos, float>> traps)
    {
        var count = source.Length;
        var countIgnoreTraps = IgnoreTraps.Count;
        for (var i = 0; i < count; ++i)
        {
            var trap = source[i];
            if (!trap.InCircle(player.Position, 45f))
                continue;

            var shouldIgnore = false;
            for (var j = 0; j < countIgnoreTraps; ++j)
            {
                if (IgnoreTraps[j].AlmostEqual(trap, 1f))
                {
                    shouldIgnore = true;
                    break;
                }
            }

            if (!shouldIgnore)
                traps.Add(ShapeDistance.Circle(trap, TrapCircleRadius));
        }
    }

    private bool TravelBlockedByHP(Actor player)
    {
        var pct = Config.StopTravelBelowHPPercent;
        if (pct <= 0 || player.HPMP.MaxHP == 0)
            return false;
        return player.HPMP.CurHP * 100f < player.HPMP.MaxHP * pct;
    }

    /// <summary>
    /// 使用者現在有沒有按著「強制趕路」那顆鍵。
    /// </summary>
    /// <remarks>
    /// 🔑 使用者裁決的原話是「不然做成按著指定按鍵時 一直觸發?」——語意是一個<b>明確的授權</b>：
    /// 按住期間三道趕路閘門（戰鬥中／拉怪數上限／低血量）一律放行，放開立刻恢復。
    /// <para>
    /// 🔴 只解除<b>趕路</b>。自動開箱、自動使用傳送裝置這兩個「互動」開關完全不受影響——
    /// 那是為了讓這顆鍵永遠不可能把人意外送到下一層。
    /// 閃避與戰鬥走位根本不經過這條路徑（它們在 <c>AIHints.ForbiddenZones</c> 與 <c>AIBehaviour</c>），
    /// 所以這顆鍵按或不按對它們是逐字相同的。
    /// </para>
    /// <para>
    /// ⚠️ 讀 ImGui 的按鍵狀態要有 ImGui context。這支的呼叫鏈是
    /// <c>Plugin.DrawUI → AIHintsBuilder.Update → ZoneModule.CalculateAIHints → 本檔</c>，
    /// 整條都在 <c>UiBuilder.Draw</c> 回呼裡（<c>MovementOverride.IsForceUnblocked</c> 也是同一個回呼），
    /// 所以是安全的。搬到別的時機點呼叫之前要先確認這件事。
    /// </para>
    /// </remarks>
    private static bool ForceTravelHeld()
        => Config.ForceTravelKey != ActionTweaksConfig.ModifierKey.None
        && MovementOverride.IsModifierHeld(Config.ForceTravelKey);

    /// <summary>上一次記過的強制趕路狀態；用來只在<b>翻轉</b>時記一行 log。</summary>
    private bool _loggedForceTravel;

    /// <summary>
    /// 把「強制趕路鍵被按下／放開」講出來。
    /// </summary>
    /// <remarks>
    /// 📌 走 <c>Information</c>：使用者的 LogLevel 是 1。這一行的用途是讓「我按著鍵怎麼還是不動」
    /// 這種回報能立刻分辨出「鍵根本沒被認到」與「認到了但別的東西擋住」。
    /// 🔴 只在翻轉時印 —— 這支每幀都會被呼叫到。
    /// </remarks>
    private void LogForceTravel(bool held)
    {
        if (held == _loggedForceTravel)
            return;
        _loggedForceTravel = held;
        Service.Logger.Information(held
            ? $"[DD] 按鍵觸發：啟動（{Config.ForceTravelKey}）——趕路的三道閘門（戰鬥中／拉怪數上限／低血量）這段期間一律放行；開箱與使用傳送裝置仍照各自的設定。"
            : "[DD] 按鍵觸發：放開——趕路恢復照設定判斷。");
    }

    /// <summary>
    /// 這一種魔陶器可不可以被自動用掉來騰位置。
    /// </summary>
    /// <remarks>
    /// ⚠️ 以前這是寫死在本檔的 15 種白名單，使用者完全無法調整。現在改成逐魔陶器的設定
    /// （<see cref="AutoDDConfig.PomanderAutoUse"/>），<b>預設值就是原本那 15 種</b>再加上
    /// 逐條讀 <c>DeepDungeonItem</c> 效果描述判定為「用了無害或有益」的那幾種。
    /// </remarks>
    private static bool CanAutoUse(PomanderID p) => Config.CanAutoUsePomander(p);

    #region 滿額時消耗魔石／亞靈複製體

    /// <summary>
    /// 天之逆焰的四種魔石裡，<b>留到最後才用</b>的那一種：奧汀。
    /// </summary>
    /// <remarks>
    /// 查表依據：<c>DeepDungeon</c> 表第 2 列（天之御柱）的 <c>MagiciteSlot[0..3]</c> ＝ 1,2,3,4，
    /// 指向 <c>DeepDungeonMagicStone</c> 第 1..4 列＝伊弗利特／泰坦／迦樓羅／<b>奧汀</b>。
    /// <para>
    /// 📌 為什麼留最後：BOSS 層伊弗利特／泰坦／迦樓羅只造成傷害，奧汀是直接打倒當前 BOSS，
    /// 所以奧汀的邊際價值最高。⚠️ 這個機制差異是<b>使用者提供的</b>，
    /// <c>DeepDungeonMagicStone</c> 的 <c>Tooltip</c> 只寫「用於召喚 X 的魔石」，查不到效果差異。
    /// 假設不成立時的失敗方向是「消耗順序不是最佳」，不會多消耗任何一顆。
    /// </para>
    /// </remarks>
    private const byte MagiciteKeepLastHoH = 4;

    /// <summary>
    /// 厄運迷宮的三種亞靈複製體裡留到最後的那一個：洋蔥騎士。
    /// </summary>
    /// <remarks>
    /// 查表依據：<c>DeepDungeon</c> 表第 3 列（正統優雷卡）的 <c>MagiciteSlot[0..2]</c> ＝ 1,2,3，
    /// 指向 <c>DeepDungeonDemiclone</c> 第 1..3 列＝烏內／多加／<b>洋蔥騎士</b>亞靈複製體
    /// （台服官方譯名，非「洋蔥劍士」）。第 4 槽是 0 ＝ 厄運迷宮只有三種。
    /// </remarks>
    private const byte MagiciteKeepLastEO = 3;

    /// <summary>上一次記錄過的消耗選擇，用來避免每幀刷 log。</summary>
    private (int Slot, byte Kind) _magiciteSpendLogged = (-1, 0);

    /// <summary>
    /// 魔石／亞靈複製體已經滿三個時，該用掉哪一格。
    /// </summary>
    /// <returns>槽位 0..2；沒有可用的、或這個副本根本沒有魔石時回 <c>null</c>。</returns>
    /// <remarks>
    /// 順位：先用「留到最後」以外的；全部都是那一種時才用它（此時再撿一顆新的淨值不虧）。
    /// 📌 死者宮殿沒有魔石（<c>DeepDungeon</c> 表第 1 列的 <c>MagiciteSlot</c> 全 0），
    /// 所以在那裡一律回 null，不會誤用任何東西。
    /// </remarks>
    private int? PickMagiciteSlotToSpend()
    {
        var keepLast = Palace.DungeonId switch
        {
            DeepDungeonState.DungeonType.HOH => MagiciteKeepLastHoH,
            DeepDungeonState.DungeonType.EO => MagiciteKeepLastEO,
            _ => (byte)0
        };
        if (keepLast == 0)
            return null;

        var fallback = -1;
        for (var i = 0; i < DeepDungeonState.NumMagicites; ++i)
        {
            var kind = Palace.Magicite[i];
            if (kind == 0)
                continue;
            if (kind != keepLast)
                return i;
            if (fallback < 0)
                fallback = i;
        }
        return fallback >= 0 ? fallback : null;
    }

    #region 自動使用的重試節流（魔陶器與魔石共用同一套形狀）

    /// <summary>兩次送出之間至少隔多久。成功與否遊戲都不會明講，只能用「數量掉了沒」回推。</summary>
    private static readonly TimeSpan SpecialItemRetryInterval = TimeSpan.FromSeconds(3);

    /// <summary>連續幾次送出後數量都沒掉就放棄（本層內不再嘗試該項目）。</summary>
    private const int SpecialItemMaxAttempts = 3;

    private PomanderID _pomanderAttemptKind;
    private int _pomanderAttempts;
    private DateTime _pomanderNextAttempt;
    private readonly HashSet<PomanderID> _pomanderGiveUp = [];
    private int _magiciteAttempts;
    private DateTime _magiciteNextAttempt;
    private bool _magiciteGiveUp;

    /// <summary>候選消失（用成功了、或走遠了）就把計數歸零；放棄名單保留到換層。</summary>
    private void ResetPomanderAttempts()
    {
        _pomanderAttemptKind = PomanderID.None;
        _pomanderAttempts = 0;
        _pomanderNextAttempt = default;
    }

    private void ResetMagiciteAttempts()
    {
        _magiciteAttempts = 0;
        _magiciteNextAttempt = default;
    }

    private bool ShouldAttemptPomander(PomanderID p)
    {
        if (_pomanderGiveUp.Contains(p))
            return false;
        if (_pomanderAttemptKind != p)
        {
            _pomanderAttemptKind = p;
            _pomanderAttempts = 0;
            _pomanderNextAttempt = default;
        }
        if (World.CurrentTime < _pomanderNextAttempt)
            return false;
        if (_pomanderAttempts >= SpecialItemMaxAttempts)
        {
            _pomanderGiveUp.Add(p);
            Service.Logger.Information(
                $"[DD] 魔陶器自動使用：「{p}」連續 {SpecialItemMaxAttempts} 次送出數量都沒變" +
                "（本層可能禁用專用道具，或這種魔陶器現在不可使用，例如沒有魔法效果時的魔法效果解除）。" +
                "本層放棄這一種，該寶箱維持跳過。");
            return false;
        }
        ++_pomanderAttempts;
        _pomanderNextAttempt = World.CurrentTime + SpecialItemRetryInterval;
        return true;
    }

    private bool ShouldAttemptMagicite()
    {
        if (_magiciteGiveUp)
            return false;
        if (World.CurrentTime < _magiciteNextAttempt)
            return false;
        if (_magiciteAttempts >= SpecialItemMaxAttempts)
        {
            _magiciteGiveUp = true;
            Service.Logger.Information(
                $"[DD] 魔石自動使用：連續 {SpecialItemMaxAttempts} 次送出數量都沒變（本層可能禁用，例如結界層）。本層放棄。");
            return false;
        }
        ++_magiciteAttempts;
        _magiciteNextAttempt = World.CurrentTime + SpecialItemRetryInterval;
        return true;
    }

    #endregion

    /// <summary>要記進 log 的魔石／亞靈複製體名稱（直接讀客戶端資料表，不維護譯名）。</summary>
    private string MagiciteName(byte kind)
    {
        var name = Palace.DungeonId == DeepDungeonState.DungeonType.EO
            ? Service.LuminaRow<Lumina.Excel.Sheets.DeepDungeonDemiclone>(kind)?.TitleCase.ToString()
            : Service.LuminaRow<Lumina.Excel.Sheets.DeepDungeonMagicStone>(kind)?.Name.ToString();
        return string.IsNullOrEmpty(name) ? $"#{kind}" : name;
    }

    #endregion

    private void IterAndExpire<T>(List<T> items, Func<T, bool> expire, Action<T> action, Action<T>? onRemove = null)
    {
        for (var i = items.Count - 1; i >= 0; --i)
        {
            var item = items[i];
            if (expire(item))
            {
                items.RemoveAt(i);
                onRemove?.Invoke(item);
            }
            else
                action(item);
        }
    }

    protected virtual void OnActorCreated(Actor c)
    {
        if (c.OID is (uint)OID.BeaconHoH or (uint)OID.BandedCofferIndicator)
            IgnoreTraps.Add(c.Position);
    }

    private DateTime CastFinishAt(Actor c) => World.FutureTime(c.CastInfo!.NPCRemainingTime);

    protected virtual void CalculateExtraHints(int playerSlot, Actor player, AIHints hints) { }

    public override void CalculateAIHints(int playerSlot, Actor player, AIHints hints)
    {
        if (!Config.Enable)
            return;

        // 🔴 保命藥水排在其他閘門**之前**。
        //    它原本掛在 DeepDungeonAI（自動循環模組）上，而那整條管線被
        //    `AIBehaviour` 的 `Preset = target.Target != null ? … : null` 關掉——沒有目標就不跑。
        //    踩到陷阱多半正是趕路、沒有目標的時候，也就是說它在最需要的那一刻保證不會觸發。
        //    實機 log 直證：整場 1091 行風箏診斷裡「沒有主要目標」出現 0 次
        //    ＝這個模組從來沒有在無目標時執行過。
        //    Boss 層與換層途中一樣會被打，所以也不受下面兩個條件限制。
        UpdateEmergencyPotion(player, hints);

        if (Palace.IsBossFloor || BetweenFloors)
            return;

        // 按住指定按鍵＝「現在交給你走」。見 ForceTravelHeld。
        var forceTravel = ForceTravelHeld();
        LogForceTravel(forceTravel);

        bool canNavigate;

        if (Config.MaxPull == 0)
        {
            canNavigate = !player.InCombat;
        }
        else
        {
            var count = 0;
            var countTargets = hints.PotentialTargets.Count;
            for (var i = 0; i < countTargets; ++i)
            {
                var target = hints.PotentialTargets[i];
                if (target.Actor.AggroPlayer && !target.Actor.IsDeadOrDestroyed)
                {
                    ++count;
                    if (count >= Config.MaxPull)
                        break;
                }
            }
            canNavigate = count < Config.MaxPull;
        }

        // 按住的期間三道趕路閘門一律放行（戰鬥中／拉怪數上限在 canNavigate 裡，低血量在下面）。
        // 🔴 刻意在這裡覆寫而不是逐處加 `|| forceTravel`：canNavigate 這個變數下面還餵給
        //    TryPullTarget（坦克主動走去拉怪），而「按住鍵＝我要你現在走」對那條路徑的語意
        //    與趕路完全一致 —— 它同樣只是移動，開火與否仍由自動選怪的設定決定。
        if (forceTravel)
            canNavigate = true;

        var countWalls = Walls.Count;
        for (var i = 0; i < countWalls; ++i)
        {
            var wall = Walls[i];
            var w = wall.Wall;
            hints.AddForbiddenZone(ShapeDistance.Rect(w.Position, (wall.Rotated ? 90f : default).Degrees(), w.Depth, w.Depth, 20f));
        }

        // 血量低於門檻就不趕路（只擋趕路，戰鬥走位與閃避不受影響）；按住強制趕路鍵時一併放行
        _travelGoalAdded = false;
        if (canNavigate && (forceTravel || !TravelBlockedByHP(player)))
            HandleFloorPathfind(player, hints);

        // 仇恨迴避只在「這一幀真的有加趕路目標區」時才疊。見 AddAggroAvoidance。
        AddAggroAvoidance(player, hints);

        DrawAOEs(playerSlot, player, hints);
        CalculateExtraHints(playerSlot, player, hints);

        var isStunned = IsPlayerTransformed(player) || player.Statuses.Any(s => s.ID is (uint)SID.Silence or (uint)SID.Pacification);
        var isOccupied = player.InCombat || isStunned;

        Actor? coffer = null;
        Actor? hoardLight = null;
        Actor? passage = null;
        List<Func<WPos, float>> revealedTraps = [];

        PomanderID? pomanderToUseHere = null;
        int? magiciteSlotToSpend = null;

        foreach (var a in World.Actors)
        {
            if (_chestContentsGold.TryGetValue(a.InstanceID, out var pid) && Palace.GetPomanderState(pid).Count == 3 && a.IsTargetable)
            {
                if (CanAutoUse(pid))
                    pomanderToUseHere ??= pid;
                continue;
            }

            if (_chestContentsSilver.ContainsKey(a.InstanceID) && Palace.Magicite.All(m => m > 0))
            {
                // 這顆銀寶箱裡是魔石／亞靈複製體（遊戲用「無法獲得更多的了」那條系統訊息告訴我們的），
                // 而三個槽都滿了 ⇒ 用掉一個騰位置，下一幀這顆箱子就會回到可開清單。
                // 🔴 只在遊戲已經明講過內容物的箱子上做。沒有那條訊息就什麼都不動，
                //    失敗方向是「維持現狀，箱子照舊跳過」而不是白白消耗資源。
                if (Config.AutoUseMagiciteWhenCapped && a.IsTargetable)
                    magiciteSlotToSpend ??= PickMagiciteSlotToSpend();
                continue;
            }

            if (_openedChests.Contains(a.InstanceID) || _fakeExits.Contains(a.InstanceID))
                continue;

            var oid = a.OID;
            if (a.IsTargetable && (
                oid == (uint)OID.GoldCoffer && OpenGold ||
                oid == (uint)OID.SilverCoffer && OpenSilver && player.HPMP.CurHP > player.HPMP.MaxHP * 0.7f ||
                BronzeChestIDs.Contains(a.OID) && OpenBronze ||
                // ⚠️ 埋藏的寶藏以前這裡沒有接任何條件，銅銀金三個框全關也照樣處理
                oid == (uint)OID.BandedCoffer && Config.BandedCoffer
            ))
            {
                if ((coffer?.DistanceToHitbox(player) ?? float.MaxValue) > a.DistanceToHitbox(player))
                    coffer = a;
            }

            if (a.OID == (uint)OID.BandedCofferIndicator)
                hoardLight = a;

            if (a.OID is (uint)OID.CairnPalace or (uint)OID.BeaconHoH or (uint)OID.PylonEO && (passage?.DistanceToHitbox(player) ?? float.MaxValue) > a.DistanceToHitbox(player))
            {
                passage = a;
                RememberPassageName(a);
            }

            if (RevealedTrapOIDs.Contains(a.OID))
                revealedTraps.Add(ShapeDistance.Circle(a.Position, 2f));
        }

        var fullClear = false;
        if (Config.FullClear)
        {
            var unexplored = FindNextUnexploredRoom(player);
            if (unexplored > 0)
            {
                // ⚠️ 這是**無條件覆寫**，使用者剛剛在小地圖上點的目標會被蓋掉。
                //    以前這件事完全沒有回饋（點了沒反應），現在記下來由狀態列說明。
                if (DesiredRoom != unexplored)
                    _destinationSource = DestinationSource.FullClear;
                DesiredRoom = unexplored;
                fullClear = true;
            }
        }
        if (Config.TrapHints && _trapsHidden)
        {
            var traps = new List<Func<WPos, float>>(_trapsCurrentZone.Length + _palTraps.Length);
            AddNearbyTrapCircles(_trapsCurrentZone, player, traps);
            // PalacePal 補上來的點與內建表在這裡完全同一個語意（跨層聯集的「這裡可能有陷阱」），
            // 所以走同一條路、同一個半徑、同一份忽略清單。
            AddNearbyTrapCircles(_palTraps, player, traps);

            if (traps.Count != 0)
                hints.AddForbiddenZone(ShapeDistance.Union(traps));
        }

        if (coffer != null)
        {
            if (_lastChestContentsGold is PomanderID p)
            {
                _chestContentsGold[coffer.InstanceID] = p;
                _lastChestContentsGold = null;
                return;
            }

            if (_lastChestMagicite)
            {
                // TODO figure out why the system log args arent working
                _chestContentsSilver[coffer.InstanceID] = 1;
                _lastChestMagicite = false;
                return;
            }
        }

        // 🔴 這兩條推送**不能每幀重推**。原本是「條件成立就每幀 Push」，實測三個症狀
        //    （使用者 2026-08-13 回報）：①本層禁用專用道具時永遠送不出去，卻以 VeryHigh
        //    佔住行動佇列＝卡佇列；②遊戲拒絕是靜默的，我們永遠不知道該收手；
        //    ③使用者手動用的那一瞬間我們的推送也在飛＝連用兩個。
        //    ⇒ 改成 3 秒一次、連續 3 次數量都沒掉就本層放棄並記 log。
        if (Config.AllowPomander && !isStunned && pomanderToUseHere is PomanderID p2 && player.FindStatus((uint)SID.ItemPenalty) == null)
        {
            if (ShouldAttemptPomander(p2))
                hints.ActionsToExecute.Push(new ActionID(ActionType.Pomander, (uint)p2), null, ActionQueue.Priority.VeryHigh);
        }
        else if (pomanderToUseHere == null)
            ResetPomanderAttempts();

        // 魔石／亞靈複製體：獨立開關（預設開），守衛與上面那條同款——變身／暈眩中不送、
        // 身上有「物品使用封印」時不送。
        // ⚠️ ActionType.Magicite 的 id 是「槽位 + 1」（註冊的是 1..3），
        //    ActionManagerEx 會再減回 0 基底交給 UseStone —— 那條 0 基底是離線反組譯確認的。
        if (Config.AutoUseMagiciteWhenCapped && !isStunned && magiciteSlotToSpend is int mslot
            && player.FindStatus((uint)SID.ItemPenalty) == null)
        {
            var kind = Palace.Magicite[mslot];
            if (_magiciteSpendLogged != (mslot, kind))
            {
                _magiciteSpendLogged = (mslot, kind);
                Service.Logger.Information(
                    $"[DD] 魔石滿額：用掉第 {mslot + 1} 格的「{MagiciteName(kind)}」（型別 {kind}）以便重新開啟銀寶箱。" +
                    $"目前三格＝[{Palace.Magicite[0]}, {Palace.Magicite[1]}, {Palace.Magicite[2]}]");
            }
            if (ShouldAttemptMagicite())
                hints.ActionsToExecute.Push(new ActionID(ActionType.Magicite, (uint)(mslot + 1)), null, ActionQueue.Priority.VeryHigh);
        }
        else if (magiciteSlotToSpend == null)
            ResetMagiciteAttempts();

        // 「走過去」與「開起來」是兩件事，分開判斷。
        // ⚠️ 拆分前這裡是一條式子：`(AutoMoveTreasure && canNavigate) || 距離 < 3.5f`
        //    （`&&` 比 `||` 優先），而它同時決定移動目標與互動目標 ——
        //    於是關掉「自動移動至寶箱」之後，只要人走到寶箱 3.5y 內仍然會自動開箱，
        //    與那個標籤的字面意思不符。
        // 🔴 `InteractWithTarget` 本身也會讓 AI 走過去（AIBehaviour 把它當 forceDestination），
        //    所以「只開不走」不能無條件設它，必須限制在已經走到旁邊的情況，否則等於從後門
        //    把移動又加回來。
        // 📌 兩個新開關都預設 true，而且這樣拆**對預設值是逐案等價的**，不是「大致一樣」：
        //    拆分前唯一會多加 GoalZone 的情況是「已經走到 3.5y 內、但 wantMove 為 false」，
        //    而當時那個 `GoalSingleTarget(pos, 25f)` 是**平台函式**（25y 內一律回傳 weight、外面回 0，
        //    見 AIHints.GoalSingleTarget），人站在 3.5y 處時整個鄰域都在平台內、權重全部相同，
        //    對尋路沒有任何方向性影響 ⇒ 少加這一個 GoalZone 不改變行為。
        // 🔴🔴 上面那段「平台內權重全部相同、對尋路沒有方向性影響」的觀察**是對的，而那正是 bug 本身**
        //    （2026-08-13 實機定案）：權重相同的範圍只要把玩家包進去，`ThetaStar.PrefillH` 就會給
        //    玩家格 `HScore == 0`，`Execute()` 一步都不跑、直接回傳起點，`GetFirstWaypoints` 回
        //    `(null, null)` ⇒ **Destination 是 null ⇒ 角色不走、不畫標線、不報錯**。
        //    也就是說「走過去開寶箱」在人進入 25y 之後就會停住 —— 使用者回報的「走幾步就停」。
        //    ⇒ 下面改用 GoalProximityLinear（隨距離線性遞減的梯度），詳見那一行。
        Actor? moveToCoffer = null;
        Actor? openCoffer = null;
        if (coffer is Actor t && !IsPlayerTransformed(player))
        {
            // 按住強制趕路鍵時連「自動移動至寶箱」這個開關本身也一併放行——
            // 那顆鍵的語意是「現在交給你走」，而不是「把已經開著的東西再開一次」。
            // ⚠️ 開箱**不**跟著被強制：openCoffer 仍然要 Config.AutoOpenTreasure。
            //    使用者把開箱關掉是刻意的（例如只想被帶到箱子旁邊自己決定），不可以從這裡繞過去。
            var wantMove = forceTravel || (Config.AutoMoveTreasure && canNavigate);
            if (wantMove)
                moveToCoffer = t;
            if (Config.AutoOpenTreasure && (wantMove || player.DistanceToHitbox(t) < 3.5f))
                openCoffer = t;
        }

        Actor? usePassage = null;
        // 同上：按住時連「戰鬥中」與「自動移動至下層傳送裝置」開關一起放行，
        // 但真的按下傳送裝置仍然只看 Config.AutoUsePassage（在下面），按住這顆鍵不會意外下一層。
        if (Palace.PassageActive && (forceTravel || (!player.InCombat && Config.AutoPassage)))
        {
            if (DesiredRoom == 0)
            {
                // 📌 這個只在使用者沒指定時才填，不會蓋掉使用者自己點的目標
                DesiredRoom = Array.FindIndex(Palace.Rooms, d => d.HasFlag(RoomFlags.Passage));
                if (DesiredRoom > 0)
                    _destinationSource = DestinationSource.AutoPassage;
            }

            if (passage is Actor c && !fullClear)
            {
                hints.GoalZones.Add(hints.GoalSingleTarget(c.Position, 2f, 0.5f));
                // give pathfinder a little help lmao
                hints.GoalZones.Add(hints.GoalSingleTarget(c.Position, 25f, 0.25f));
                // 通道石比寶箱近就先去通道石——連互動也一起讓開，否則 InteractWithTarget
                // 還是會把 AI 拉回寶箱那邊，等於這個「先去通道石」完全沒效果
                if (player.DistanceToHitbox(c) < player.DistanceToHitbox(coffer) && !Config.OpenChestsFirst)
                {
                    moveToCoffer = null;
                    openCoffer = null;
                }

                // 🔴 「到了就自動點下去」——獨立開關，預設關。
                //    只在「已經走到互動距離內」才設，理由與上面寶箱那條完全相同：
                //    InteractWithTarget 本身會變成 AI 的 forceDestination，無條件設它等於
                //    從後門多加一條移動來源。走過去是上面那兩個 GoalZone 的事，這一行只負責最後那一下。
                // 📌 這一行住在 `passage is Actor c && !fullClear` 裡面是刻意的：全清模式
                //    還有房間沒逛完時連走過去都不做，自然更不該替使用者點下去。
                // ⚠️ 遊戲接著跳的「要前往下一層嗎」確認視窗不由 BMR 回答（全庫沒有那條管線），
                //    使用者仍然要自己按「是」——設定的 tooltip 有寫明。
                if (Config.AutoUsePassage && !IsPlayerTransformed(player) && player.DistanceToHitbox(c) < PassageInteractDistance)
                    usePassage = c;
            }
        }

        // 🔴🔴 這裡原本是 `GoalSingleTarget(moveTarget.Position, 25f)` ＝ **25y 內全部同分的平台**，
        //    而平台只要把玩家包進去，尋路就會回報「已經在最佳位置」、完全不移動（機制見上面那段註解）。
        //    症狀：寶箱在 25y 外時角色會走過去，一踏進 25y 就停住 —— 也就是使用者說的「走幾步就停」，
        //    而且一路上不報錯、連移動標線都不會畫。
        //    ⇒ 改用線性遞減的目標區：距離愈近權重愈高（寶箱處 = TreasureApproachWeight，25y 處歸零），
        //      整段都有梯度，尋路每一幀都算得出「往寶箱再靠近一格」。
        //    🔴 刻意用本檔自帶的 GoalProximityLinear，不是 AIHints.GoalProximity —— 後者從
        //      2026-08-16 起同步上游改成平方曲線，寶箱附近的梯度會弱掉約 12.5 倍，那正好就是
        //      上面那個症狀的成因。詳見 TreasureApproachRadius 的註解。
        //    📌 這個換法對其他目標區是**保守**的：新的權重在每一點都 ≤ 舊的平台值（同樣的最大值、
        //      同樣的作用半徑），所以不可能壓過原本壓不過的東西；差別只在平台變成斜坡。
        //    📌 對照組：同一份 hints 裡的坦克拉怪用的是 `GoalSingleTarget(target, 2.6y, 12)`，
        //      那個小圈**不會包住玩家**（拉怪距離十幾碼），所以它一直都是好的 —— 實機 23:29:34
        //      拉怪會動、趕路不動，差別就在這裡，不是擁有權、也不是座標。
        if (moveToCoffer is Actor moveTarget)
            hints.GoalZones.Add(GoalProximityLinear(moveTarget.Position, TreasureApproachRadius, TreasureApproachWeight));

        // 🔑 寶箱優先於傳送裝置，而且這正好就是既有的優先度：傳送裝置比寶箱近而且沒勾
        //    「優先開啟寶箱」時，上面已經把 openCoffer 清成 null 了 ⇒ 那種情況輪得到傳送裝置。
        //    反過來說寶箱該先開時 openCoffer 還在，這裡就不會把它蓋掉。
        if (openCoffer is Actor openTarget)
            hints.InteractWithTarget = openTarget;
        else if (usePassage is Actor passageTarget)
            hints.InteractWithTarget = passageTarget;

        if (revealedTraps.Count > 0)
            hints.AddForbiddenZone(ShapeDistance.Union(revealedTraps));

        // 直感魔石照出來的埋藏寶藏光點：純移動，所以歸「自動移動至寶箱」管，
        // 但埋藏的寶藏整個關掉時也不必再走過去
        if (!IsPlayerTransformed(player) && canNavigate && Config.AutoMoveTreasure && Config.BandedCoffer && hoardLight is Actor h && Palace.GetPomanderState(PomanderID.Intuition).Active)
            hints.GoalZones.Add(hints.GoalSingleTarget(h.Position, 2f, 10f));

        var shouldTargetMobs = Config.AutoClear switch
        {
            AutoDDConfig.ClearBehavior.Passage => !Palace.PassageActive,
            AutoDDConfig.ClearBehavior.Leveling => player.Level < LevelCap || !Palace.PassageActive,
            AutoDDConfig.ClearBehavior.All => true,
            _ => false
        };

        // 🔴 目標修正必須放在下面那個提早返回**之前**。
        //    `player.InCombat` 一成立，下面整段選目標邏輯就完全不執行 —— 這正是
        //    「交戰前鎖定的怪，被別的怪視線仇恨引起來之後不會改目標」的直接成因：
        //    ForcedTarget 每幀都被 AIHints.Clear() 清成 null，而這裡提早返回、不重設它，
        //    於是 ExecuteHints 那條路徑整幀不碰硬目標，遊戲裡的鎖定就一直停在交戰前選的那一隻；
        //    輸出（WrathCombo 打的是當前硬目標）與跟著目標走的走位一起指過去，
        //    把原本還沒醒的那隻也拉起來 ＝ 同時引戰多隻。
        if (CorrectTargetToAggroedEnemy(player, hints))
            return;

        // 🔴 開怪第一擊也必須放在提早返回之前，而且刻意不放在 TryPullTarget 裡：
        //    TryPullTarget 只有在「玩家手上沒有敵性目標」的那些幀才跑得到（下面那個提早返回
        //    在鎖定目標之後就成立了），也就是每次選定目標大約只執行一幀；
        //    而「走到射程內」是選定目標之後好幾秒的事，放在那裡等於永遠等不到射程。
        //    這裡每幀都會評估，走進 20y 的那一幀就會推出去。
        TryOpenerShot(player, hints, shouldTargetMobs);

        if (player.InCombat || World.Actors.Find(player.TargetID) is Actor t2 && !t2.IsAlly)
            return;

        Actor? bestTarget = null;

        void pickBetterTarget(Actor t)
        {
            if (player.DistanceToHitbox(t) < player.DistanceToHitbox(bestTarget))
                bestTarget = t;
        }

        var counttargets = hints.PotentialTargets.Count;
        for (var i = 0; i < counttargets; ++i)
        {
            var pp = hints.PotentialTargets[i];
            // enemy is petrified, any damage will kill
            if (pp.Actor.FindStatus((uint)SID.StoneCurse)?.ExpireAt > World.FutureTime(1.5d))
                pickBetterTarget(pp.Actor);

            // pomander of storms was used, enemy can't autoheal; any damage will kill
            else if (pp.Actor.FindStatus((uint)SID.AutoHealPenalty) != null && pp.Actor.HPMP.CurHP < 10)
                pickBetterTarget(pp.Actor);

            // if player does not have a target, prioritize everything so that AI picks one - skip dangerous enemies
            else if (shouldTargetMobs)
            {
                var hasDangerousStatus = false;
                var len = pp.Actor.Statuses.Length;
                ref var statuses = ref pp.Actor.Statuses;
                for (var j = 0; j < len; ++j)
                {
                    if (IsDangerousOutOfCombatStatus(statuses[j].ID))
                    {
                        hasDangerousStatus = true;
                        break;
                    }
                }

                if (!hasDangerousStatus)
                {
                    pickBetterTarget(pp.Actor);
                }
            }
        }
        hints.ForcedTarget = bestTarget;

        TryPullTarget(player, hints, bestTarget, canNavigate);
    }

    #region 主動走過去開拉

    /// <summary>
    /// 走到目標旁邊的距離（碼，量到 hitbox）。
    /// </summary>
    /// <remarks>
    /// 與 <c>AIBehaviour.SelectPrimaryTarget</c> 給近戰／坦克的 <c>PreferredRange</c> 同值，
    /// 這樣「拉怪走過去」與「打起來之後 AI 自己維持的距離」不會互相拉扯。
    /// </remarks>
    private const float PullRange = 2.6f;

    /// <summary>
    /// 遠程／法系／治療的接近距離（碼，量到 hitbox）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>脆皮不能用 2.6y</b>：那個值是給坦克與近戰的（他們本來就要貼臉）。
    /// 遠程職業只要進得了開怪技的射程就夠了，走到臉上等於白白挨第一輪平砍。
    /// <para>
    /// 🔑 取 15y 的理由：所有遠程／法系／治療的開怪技射程都是 25y（見 <see cref="OpenerShots"/>），
    /// 留 10y 餘裕吸收「怪會動」與深牢轉角擋視線；同時 15y 落在風箏環
    /// （<c>KiteMinDistance</c> 9y ~ <c>KiteMaxDistance</c> 25y，見 <c>xan/AI/DeepDungeon.cs</c>）
    /// <b>之內</b>，所以開打之後風箏接手時不會立刻把人往回拉，兩段是連續的。
    /// </para>
    /// <para>
    /// 📌 實際上多數情況根本走不到這裡就開火了：<see cref="TryOpenerShot"/> 是<b>照技能自己的射程</b>
    /// 判斷的（25y），所以怪一進 25y 就會出手，這個 15y 只是提供「往那邊走」的梯度。
    /// </para>
    /// </remarks>
    private const float RangedPullRange = 15f;

    /// <summary>
    /// 這個職業該走到離目標多近。坦克／近戰維持 <see cref="PullRange"/>，其餘用 <see cref="RangedPullRange"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Role.None</c>（理論上進不了深牢）落在遠程那一側；那種情況 <see cref="OpenerShots"/>
    /// 回空陣列＝不會出手，最多只是站遠一點，不會有動作。
    /// </remarks>
    private static float PullApproachRange(Actor player) => player.Role switch
    {
        Role.Tank or Role.Melee => PullRange,
        _ => RangedPullRange
    };

    /// <summary>
    /// 拉怪目標區的權重。
    /// </summary>
    /// <remarks>
    /// 🔴 必須大於 <see cref="HandleFloorPathfind"/> 非戰鬥時的趕路權重（10），否則整件事會靜默沒有效果：
    /// 兩者都是<b>平台函式</b>而且 <c>GoalZones</c> 是<b>相加</b>的（見 <c>NavigationDecision.RasterizeGoalZones</c>），
    /// 權重比趕路小的話，怪在身後時趕路那一片永遠比較高，角色就直接走過去不理它。
    /// 取 12 而不是更大，是為了讓「怪在前進方向上」時兩者相加（22）仍然明顯優於「只有怪」（12），
    /// 也就是同樣要拉的話優先拉順路的那一隻。
    /// </remarks>
    private const float PullWeight = 12f;

    /// <summary>這一層已經記過一次「開始拉怪」診斷；255＝還沒記過。</summary>
    private byte _pullLoggedFloor = 255;

    /// <summary>
    /// 主動走過去把選好的目標拉起來（全職業）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>為什麼非得走這條路</b>：被動怪在 <c>AIHintsBuilder.FillEnemies</c> 拿到的優先度是
    /// <c>PriorityUndesirable</c>（-3），永遠進不了 <c>AIHints.PriorityTargets</c>；
    /// 於是 <c>AIBehaviour.SelectPrimaryTarget</c> 選不到它，
    /// <c>BuildNavigationDecision</c> 尾端那條「沒有人給目標區就走向目標」的退路
    /// （<c>GoalZones.Count == 0 &amp;&amp; targeting.Target != null</c>）也不會觸發——
    /// 而且本模組每幀都會加趕路目標區，那個 <c>Count == 0</c> 條件在深牢裡本來就永遠不成立。
    /// 再加上使用者若開了 <c>ForbidActions</c>，AIBehaviour 的整個選目標分支都被跳過。
    /// ⇒ 主動接近只能由本模組自己加目標區，不能指望 AI 那邊。
    /// </para>
    /// <para>
    /// 🔴 <b>閘門全部是「不成立就什麼都不做」</b>：關掉、已經在戰鬥、變身中、
    /// 已經拉滿（<c>canNavigate</c>）、血量低於門檻、目標不在尋路視窗內——任一成立就直接退出，
    /// 退回今天的行為（只設 <c>ForcedTarget</c>、不移動）。沒有任何一條的失敗方向是「亂走」。
    /// </para>
    /// <para>
    /// 📌 <b>2026-08-17 起不再限定坦克。</b>原本硬性限定坦克的理由是「非坦克走過去是送死」，
    /// 但那個顧慮的真正來源是<b>距離</b>不是職業：現在接近距離改成看職能
    /// （<see cref="PullApproachRange"/>），遠程／法系／治療停在 15y 開火而不是走到臉上，
    /// 顧慮就不成立了。深牢單人清怪本來就每個職業都要打，治療也一樣。
    /// </para>
    /// <para>
    /// 📌 <c>SetPriority(target, 0)</c> 是為了讓這隻怪對「有開自動循環」的使用者變成合法目標
    /// （-3 同時代表「AOE 禁打」）。對 <c>ForbidActions</c> 的使用者它不改變任何事——
    /// 那條路徑整段被跳過——移動完全來自下面那個目標區。
    /// </para>
    /// </remarks>
    private void TryPullTarget(Actor player, AIHints hints, Actor? target, bool canNavigate)
    {
        if (!Config.TankPull || target == null)
            return;

        // 🔴 血量閘門要連拉怪一起擋：原本 StopTravelBelowHPPercent 只擋趕路，
        //    而「低血時不要主動去撿下一隻怪」正是那個設定的字面意思。
        if (!canNavigate || TravelBlockedByHP(player))
            return;

        if (player.InCombat || IsPlayerTransformed(player))
            return;

        // 目標不在尋路視窗（預設 ArenaBoundsSquare(30)）裡時，目標區在整張圖上恆為 0 ＝完全沒有效果。
        // 明著擋掉是為了連帶不要去動那種怪的優先度——構不到的怪不該被推薦給自動循環。
        if (!hints.PathfindMapBounds.Contains(target.Position - hints.PathfindMapCenter))
            return;

        var approach = PullApproachRange(player);
        hints.SetPriority(target, 0);
        hints.GoalZones.Add(hints.GoalSingleTarget(target, approach, PullWeight));

        // 每層記一行就好：要的是「這個功能今天真的有動」，不是逐隻怪的流水帳。
        if (_pullLoggedFloor != Palace.Floor)
        {
            _pullLoggedFloor = Palace.Floor;
            Service.Logger.Information(
                $"[DD] 主動開拉：樓層 {Palace.Floor} 首次主動接近目標「{target.Name}」（OID {target.OID:X}），" +
                $"距離 {player.DistanceToHitbox(target):f1}y、職能 {player.Role}、" +
                $"目標區權重 {PullWeight}、接近距離 {approach}y。");
        }
    }

    #endregion

    #region 已仇恨目標修正

    /// <summary>
    /// 上一次修正過的「原鎖定目標 → 新目標」配對，存 <c>InstanceID</c>。
    /// </summary>
    /// <remarks>
    /// 只用來讓診斷「一次事件印一行」，<b>不參與任何判斷</b>——就算它是髒的，最壞也只是少印／多印一行。
    /// 🔴 刻意存 <c>ulong</c> 而不是 <c>Actor</c>：跨幀保存原生物件參考是紅線，
    /// 而這個欄位活得比任何一隻怪都久（換層才歸零）。
    /// </remarks>
    private (ulong From, ulong To) _lastTargetCorrection;

    /// <summary>
    /// 交戰前鎖定的怪還沒仇恨我、卻已經被<b>別的</b>怪引戰時，把鎖定改成已經打起來的那一隻。
    /// </summary>
    /// <returns>
    /// 有做修正時回 <see langword="true"/>；此時呼叫端必須<b>立刻返回</b>，
    /// 不要讓後面的選目標邏輯把 <c>ForcedTarget</c> 覆寫掉。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼一定要做在 <c>ForcedTarget</c> 這條路徑上</b>：
    /// <c>AIBehaviour.SelectPrimaryTarget</c> 本身有「不必要就不換目標」的黏著行為，
    /// 但它整段掛在 <c>forbidTargeting</c>（<c>AIConfig.ForbidActions</c>）之後——
    /// 開著 ForbidActions 讓 BMR 只負責走位、輸出交給 WrathCombo 的玩法下，那條分支從不執行。
    /// 相對地 <c>Hints.ForcedTarget</c> 是由 <c>Plugin.ExecuteHints</c> <b>無條件</b>寫進
    /// <c>TargetSystem->Target</c> 的（見 <c>Config</c> 的 DrawHeader 註解），兩種玩法都會生效。
    /// </para>
    /// <para>
    /// 🔑 <b>「已經仇恨我」的判準用 <c>Actor.AggroPlayer</c></b>，與上面 MaxPull 閘門同源：
    /// 它來自 <c>UIState.Hater</c>（玩家 UI 上的敵人列表），也就是遊戲自己已經解析好的仇恨列表，
    /// 不需要另外猜。<c>AIHintsBuilder.FillEnemies</c> 也正是用它把怪的優先度從
    /// <c>PriorityUndesirable</c> 拉到 0 的，所以這裡選出來的怪對自動循環一定是合法目標。
    /// </para>
    /// <para>
    /// 🔴 <b>防 ping-pong</b>：只在「當前目標還沒跟我打起來」時才修正。
    /// 一旦改成已仇恨的那隻，下一幀 <c>current.AggroPlayer</c> 就是 <see langword="true"/>，
    /// 這裡直接返回 ＝ 修正最多發生一次，不會在兩隻怪之間來回跳。
    /// 「已經打起來」刻意用 <c>AggroPlayer || InCombat</c> 兩個條件的<b>聯集</b>：
    /// 仇恨列表是每幀從 UI 狀態重讀的，剛開打那一瞬間可能還沒進列表，
    /// 光看 <c>AggroPlayer</c> 會在那一幀把鎖定從正在打的怪身上拔走。
    /// 組隊時也順帶保守：隊友拉著、還沒轉到我身上的怪 <c>InCombat</c> 為真，一樣不動它。
    /// </para>
    /// <para>
    /// 📌 <b>與石化／自癒懲罰那兩個「一下就死」分支的優先級</b>：兩者實際上不會相遇。
    /// 那兩個分支住在 <c>player.InCombat</c> 提早返回的<b>後面</b>，只有「不在戰鬥中而且沒有敵性目標」
    /// 時才跑得到；而本修正成立的前提是有怪對我有仇恨 ⇒ 必定在戰鬥中 ⇒ 那兩個分支今天本來就被跳過。
    /// 因此把本修正排在最前面不會奪走它們任何一次執行機會。
    /// 反過來說，本修正<b>刻意不</b>在已仇恨的怪之間再按石化／瀕死排序，只取最近的：
    /// 那兩個分支是替「反正要走過去」的非戰鬥選怪服務的，戰鬥中把近戰從貼臉的怪身上
    /// 支開去打半場外的石化怪，代價遠大於省下的那一刀。
    /// </para>
    /// <para>
    /// 📌 <b>沒有設定開關</b>：深牢自動化裡「放著已經咬住我的怪不管，去把新的怪引起來」
    /// 沒有任何合法用途，這是純缺陷修正。唯一連帶的行為改變是：戰鬥中手動點一隻<b>還沒仇恨</b>的怪，
    /// 下一幀會被拉回正在打的那隻——那與缺陷本身是同一個形狀，刻意不留例外。
    /// </para>
    /// </remarks>
    private bool CorrectTargetToAggroedEnemy(Actor player, AIHints hints)
    {
        // 只處理「已經鎖定了一隻敵人」的情況。沒有目標時不插手：那是既有選目標邏輯
        // 與 AI 的地盤，在這裡替它決定會把「戰鬥中不自動選怪」整個換掉，超出本修正的範圍。
        if (World.Actors.Find(player.TargetID) is not Actor current || current.IsAlly)
            return false;

        // 目標自己也已經打起來了 ⇒ 這就是正在交戰的那隻，不要換。
        // 目標已死時刻意放行：那時候把鎖定接到還活著的仇恨怪身上正是我們要的。
        if ((current.AggroPlayer || current.InCombat) && !current.IsDeadOrDestroyed)
            return false;

        // PotentialTargets 由 AIHintsBuilder.FillEnemies 在本模組之前填好，
        // 已經濾掉不可選取／友方／死亡的 actor ⇒ 這裡挑出來的必定通得過 ExecuteHints 的
        // IsTargetable 檢查，不會出現「設了 ForcedTarget 卻靜默沒生效、然後每幀重試」。
        Actor? best = null;
        var bestDist = float.MaxValue;
        var count = hints.PotentialTargets.Count;
        for (var i = 0; i < count; ++i)
        {
            var candidate = hints.PotentialTargets[i].Actor;
            if (!candidate.AggroPlayer || candidate.IsDeadOrDestroyed || candidate.InstanceID == current.InstanceID)
                continue;

            var dist = player.DistanceToHitbox(candidate);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        if (best == null)
            return false;

        hints.ForcedTarget = best;

        // 診斷：一次修正事件印一行。頻率天然很低（只在被引戰／原目標倒下時發生），
        // 配對去重是為了擋住「硬目標因為任何理由沒被套用」時可能出現的每幀重印。
        var key = (current.InstanceID, best.InstanceID);
        if (_lastTargetCorrection != key)
        {
            _lastTargetCorrection = key;
            Service.Logger.Information(
                $"[DD] 目標修正：樓層 {Palace.Floor}，原鎖定「{current.Name}」（OID {current.OID:X}、" +
                $"距離 {player.DistanceToHitbox(current):f1}y、尚未仇恨）改為已仇恨的「{best.Name}」" +
                $"（OID {best.OID:X}、距離 {bestDist:f1}y）。");
        }

        return true;
    }

    #endregion

    #region 開怪第一擊

    /// <summary>這一層已經記過一次「開怪第一擊」診斷；255＝還沒記過。</summary>
    private byte _openerLoggedFloor = 255;

    // 逐職業的開怪技候選，**依序**嘗試：先遠程、再退回近戰基本 GCD。
    // 執行期用 ActionDefinition.IsUnlocked 挑第一個「這個等級真的會了」的，
    // 所以同一張表同時涵蓋 L1 與 L100（例如龍騎 L15 以下沒有貫穿尖，就退回蒼天龍尾）。
    private static readonly ActionID[] OpenerPLD = [ActionID.MakeSpell(PLD.AID.ShieldLob), ActionID.MakeSpell(PLD.AID.FastBlade)];
    private static readonly ActionID[] OpenerWAR = [ActionID.MakeSpell(WAR.AID.Tomahawk), ActionID.MakeSpell(WAR.AID.HeavySwing)];
    private static readonly ActionID[] OpenerDRK = [ActionID.MakeSpell(DRK.AID.Unmend), ActionID.MakeSpell(DRK.AID.HardSlash)];
    private static readonly ActionID[] OpenerGNB = [ActionID.MakeSpell(GNB.AID.LightningShot), ActionID.MakeSpell(GNB.AID.KeenEdge)];
    private static readonly ActionID[] OpenerMNK = [ActionID.MakeSpell(MNK.AID.Bootshine)];
    private static readonly ActionID[] OpenerDRG = [ActionID.MakeSpell(DRG.AID.PiercingTalon), ActionID.MakeSpell(DRG.AID.TrueThrust)];
    private static readonly ActionID[] OpenerNIN = [ActionID.MakeSpell(NIN.AID.ThrowingDagger), ActionID.MakeSpell(NIN.AID.SpinningEdge)];
    private static readonly ActionID[] OpenerSAM = [ActionID.MakeSpell(SAM.AID.Enpi), ActionID.MakeSpell(SAM.AID.Hakaze)];
    private static readonly ActionID[] OpenerRPR = [ActionID.MakeSpell(RPR.AID.Harpe), ActionID.MakeSpell(RPR.AID.Slice)];
    private static readonly ActionID[] OpenerVPR = [ActionID.MakeSpell(VPR.AID.WrithingSnap), ActionID.MakeSpell(VPR.AID.SteelFangs)];
    private static readonly ActionID[] OpenerBRD = [ActionID.MakeSpell(BRD.AID.HeavyShot)];
    private static readonly ActionID[] OpenerMCH = [ActionID.MakeSpell(MCH.AID.SplitShot)];
    private static readonly ActionID[] OpenerDNC = [ActionID.MakeSpell(DNC.AID.Cascade)];
    private static readonly ActionID[] OpenerBLM = [ActionID.MakeSpell(BLM.AID.Blizzard1)];
    private static readonly ActionID[] OpenerSMN = [ActionID.MakeSpell(SMN.AID.Ruin1)];
    private static readonly ActionID[] OpenerRDM = [ActionID.MakeSpell(RDM.AID.Jolt), ActionID.MakeSpell(RDM.AID.Riposte)];
    private static readonly ActionID[] OpenerPCT = [ActionID.MakeSpell(PCT.AID.FireInRed)];
    private static readonly ActionID[] OpenerWHM = [ActionID.MakeSpell(WHM.AID.Stone)];
    private static readonly ActionID[] OpenerSCH = [ActionID.MakeSpell(SCH.AID.Ruin1)];
    private static readonly ActionID[] OpenerAST = [ActionID.MakeSpell(AST.AID.Malefic)];
    private static readonly ActionID[] OpenerSGE = [ActionID.MakeSpell(SGE.AID.Dosis)];

    /// <summary>
    /// 這個職業的開怪技候選，<b>依偏好順序</b>（遠程優先，最後才是近戰基本 GCD）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>這張表是算出來的，不是憑記憶寫的。</b>來源是
    /// <c>~/.claude/tools/bmr_job_openers.py</c>：它同時解析 <c>BossMod/ActionQueue/*/&lt;JOB&gt;.cs</c> 的
    /// <c>enum AID</c> 尾註（等級／射程／GCD／目標型別）與 <c>Definitions()</c> 裡真的
    /// <c>RegisterSpell</c> 過的集合，取「已註冊 ∧ GCD ∧ 單體 ∧ 敵對」中等級最低的那一顆。
    /// <b>沒註冊＝<c>ActionDefinitions.Instance[aid]</c> 回 null＝靜默不出手</b>，所以不能用猜的。
    /// 21 顆 AID 另外用 <c>tools/exd/tc_action_names.py</c> 對台服 <c>Action.csv</c> 查過確實存在。
    /// </para>
    /// <para>
    /// ⚠️ 刻意連基礎職一起對應（<c>GLA</c>/<c>MRD</c>/<c>PGL</c>/<c>LNC</c>/<c>ARC</c>/<c>THM</c>/
    /// <c>CNJ</c>/<c>ACN</c>/<c>ROG</c>）：深牢從 1 級打起，L15~L29 這段玩家的 <c>Class</c>
    /// 是基礎職而不是進階職，只寫進階職會在那一段靜默不出手。
    /// </para>
    /// <para>
    /// 📌 <b>武僧／格鬥家是唯一沒有遠程開怪技的職業</b>（全職業唯一）——它每一招敵對技都是
    /// <c>range 3</c>，L35 的疾風迅雷擊雖然是 20y 但那是<b>位移技</b>而且 <c>targets=party/hostile</c>，
    /// 拿來開怪等於把自己丟過去。所以武僧走近戰距離用連擊開，和其他近戰在 L15 以下的處理一致。
    /// </para>
    /// </remarks>
    private static ActionID[] OpenerShots(Class c) => c switch
    {
        Class.GLA or Class.PLD => OpenerPLD,
        Class.MRD or Class.WAR => OpenerWAR,
        Class.DRK => OpenerDRK,
        Class.GNB => OpenerGNB,
        Class.PGL or Class.MNK => OpenerMNK,
        Class.LNC or Class.DRG => OpenerDRG,
        Class.ROG or Class.NIN => OpenerNIN,
        Class.SAM => OpenerSAM,
        Class.RPR => OpenerRPR,
        Class.VPR => OpenerVPR,
        Class.ARC or Class.BRD => OpenerBRD,
        Class.MCH => OpenerMCH,
        Class.DNC => OpenerDNC,
        Class.THM or Class.BLM => OpenerBLM,
        Class.ACN or Class.SMN => OpenerSMN,
        Class.RDM => OpenerRDM,
        Class.PCT => OpenerPCT,
        Class.CNJ or Class.WHM => OpenerWHM,
        Class.SCH => OpenerSCH,
        Class.AST => OpenerAST,
        Class.SGE => OpenerSGE,
        _ => []
    };

    /// <summary>
    /// 走到定位之後真的按下第一顆技能，把選好的被動怪拉起來。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>這是「開拉」缺的另一半</b>：<see cref="TryPullTarget"/> 只做兩件事——
    /// <c>SetPriority(target, 0)</c> 與加一個目標區。前者只對「有開 BMR 自動循環」的人有意義，
    /// 後者只是走過去。使用者常態是 <c>ForbidActions</c>（BMR 只走位、不出招）＋ WrathCombo 出招，
    /// 而 WrathCombo 的自動循環有自己的「只在戰鬥中」閘門
    /// （<c>AutoRotationController.Run</c>：<c>if (cfg.InCombatOnly &amp;&amp; NotInCombat &amp;&amp; !CombatBypass) return;</c>）
    /// ⇒ <b>被動怪身上沒有任何一方會按第一下</b>，表現就是走到怪旁邊站著對看。
    /// 主動怪因為靠近就自己撲上來，所以看起來正常——這個缺口只在被動怪身上顯形。
    /// 設定的字面承諾是「開拉」，所以把第一擊補上。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼推 <c>Hints.ActionsToExecute</c> 就會生效</b>：這條佇列由
    /// <c>Plugin.DrawUI</c> → <c>ActionManagerEx.FinishActionGather()</c> 每幀<b>無條件</b>消費，
    /// 再由 <c>ActionManagerEx.UpdateDetour</c> 送出，<b>完全不看 <c>ForbidActions</c></b>。
    /// （<c>AIBehaviour.Execute</c> 那個 <c>!forbidTargeting ? …ActionsToExecute : null</c>
    /// 是傳給 <c>UpdateMovement</c> 判斷要不要為了施法停下來的，<b>不是</b>執行端——
    /// 看到它就以為 ForbidActions 會擋掉出招是會誤判的。）
    /// 同一條路正是本模組保命藥水（<see cref="UpdateEmergencyPotion"/>）在用的。
    /// </para>
    /// <para>
    /// 🔴 <b>安全設計：最壞情況是「不按」，不會是「亂按」。</b>
    /// ①只有四個坦克職業有對應技能，其餘職業直接不推；
    /// ②職業／等級／冷卻／射程<b>全部交給 <c>ActionQueue</c> 自己查</b>
    /// （<c>IsUnlocked</c>＋<c>ReadyIn</c>＋<c>CanExecute</c> 的 <c>Range</c> 比對），不自己重寫；
    /// ③優先級用 <c>VeryLow</c>＝「沒別的可按才按」，排在保命藥水（<c>VeryHigh</c>）與任何
    /// 自動循環之後，不可能延遲到它們（<c>FindBest</c> 依優先度由高而低掃，低優先度的候選
    /// 進不了別人的 deadline）；
    /// ④只打「已經鎖定、還沒仇恨我、而且不帶危險增益」的那一隻——
    /// 危險增益那條與 <c>CalculateAIHints</c> 選怪時的排除條件同源，
    /// 否則會出現「選怪時刻意跳過它、開拉卻去把它叫醒」的自相矛盾。
    /// </para>
    /// <para>
    /// 📌 <b>與其他兩段邏輯的分工</b>：這一擊打下去之後目標就會進仇恨列表，
    /// <see cref="CorrectTargetToAggroedEnemy"/> 的「目標已仇恨就不換」條件隨即成立、不會來搶；
    /// 同時 <c>player.InCombat</c> 變真，本函式與 <see cref="TryPullTarget"/> 的閘門一起關上
    /// （MaxPull=0 時 <c>canNavigate</c> 也直接變 false），不會出現「打了一下又跑去拉下一隻」。
    /// </para>
    /// </remarks>
    private void TryOpenerShot(Actor player, AIHints hints, bool shouldTargetMobs)
    {
        // 沿用「坦克主動拉怪」這個開關：它的字面承諾就是「選好目標之後也主動走過去開拉」。
        // shouldTargetMobs 一起看，是為了尊重「通道開啟後停止清怪」那類設定——
        // 那些模式下 AutoClear 本來就不選怪，開拉當然也不該自己去點火。
        if (!Config.TankPull || !shouldTargetMobs)
            return;

        // 與 TryPullTarget 完全同一組前置條件。
        // 📌 canNavigate 不必再看：它在戰鬥外恆為 true（MaxPull=0 時就是 !InCombat，
        //    MaxPull>0 時戰鬥外的仇恨數是 0），而 InCombat 這裡已經擋掉了。
        if (player.InCombat || IsPlayerTransformed(player))
            return;

        if (TravelBlockedByHP(player))
            return;

        // 開拉的對象＝目前鎖定的那隻，也就是上面的選怪邏輯挑出來、正走過去的那一隻。
        if (World.Actors.Find(player.TargetID) is not Actor target || target.IsAlly || target.IsDeadOrDestroyed)
            return;

        // 已經咬住我的怪不用再開一次（InCombat 一起看，理由同目標修正那邊的仇恨列表落差）。
        if (target.AggroPlayer || target.InCombat)
            return;

        // 選怪時刻意跳過的危險增益怪，開拉也一樣不碰。
        var lenStatuses = target.Statuses.Length;
        ref var statuses = ref target.Statuses;
        for (var i = 0; i < lenStatuses; ++i)
        {
            if (IsDangerousOutOfCombatStatus(statuses[i].ID))
                return;
        }

        // 依序挑第一個「這個等級真的會了、而且射程也到了」的候選。
        // 🔴 IsUnlocked 這一關不能省：不查的話 L1 龍騎會一直推還沒學會的貫穿尖，
        //    FindBest 會把它整個丟掉（同樣的 IsUnlocked 檢查），而我們也就永遠不會
        //    退回蒼天龍尾 —— 失敗形式是「低等級整段不開怪」，而且完全沒有訊息。
        var defs = ActionDefinitions.Instance;
        var dist = player.DistanceToHitbox(target);
        var candidates = OpenerShots(player.Class);
        var countCand = candidates.Length;
        for (var i = 0; i < countCand; ++i)
        {
            var aid = candidates[i];
            var def = defs[aid];
            if (def == null)
                continue;
            if (!def.IsUnlocked(World, player))
                continue;

            // 射程沒到就換下一個候選（近戰退路的射程比較短，通常會在這裡被擋掉，
            // 等走近了才輪到它）。佇列自己也會擋（CanExecute 的 Range 比對用的是同一個式子：
            // 中心距離 > Range + 兩邊 hitbox ⇔ DistanceToHitbox > Range），這裡先擋一次
            // 純粹是為了讓下面的診斷不要在還在跑路的時候就宣稱開了火。
            if (def.Range > 0f && dist > def.Range)
                continue;

            // 🔑 castTime 要照實申報：法系開怪技是 1.5~2.5 秒讀條，申報之後
            //    FindBest 的 `candidate.CastTime > hints.MaxCastTime` 才擋得住
            //    「玩家自己按著方向鍵」那種必定被打斷的情況（moveImminent 時 MaxCastTime 是 0），
            //    ActionManagerEx 的 PreventMovingWhileCasting 也才知道這一發需要停下來。
            hints.ActionsToExecute.Push(aid, target, ActionQueue.Priority.VeryLow, castTime: def.CastTime);

            // 每層記一行：要的是「這個功能今天真的有動」，不是逐隻怪的流水帳（與主動開拉同慣例）。
            if (_openerLoggedFloor != Palace.Floor)
            {
                _openerLoggedFloor = Palace.Floor;
                Service.Logger.Information(
                    $"[DD] 開怪第一擊：樓層 {Palace.Floor}，對「{target.Name}」（OID {target.OID:X}、" +
                    $"距離 {dist:f1}y）推送 {aid}（職業 {player.Class}、職能 {player.Role}、" +
                    $"射程 {def.Range}y、讀條 {def.CastTime:f1}s、候選 #{i + 1}/{countCand}）。" +
                    $"最終是否送出仍由動作佇列的冷卻與可用性檢查決定。");
            }
            return;
        }
    }

    #endregion

    private void DrawAOEs(int playerSlot, Actor player, AIHints hints)
    {
        IterAndExpire(HintDisabled, g => g.CastInfo == null, g =>
        {
            var count = hints.ForbiddenZones.Count;
            for (var i = 0; i < count; ++i)
            {
                var fz = hints.ForbiddenZones[i];
                if (fz.Source == g.InstanceID)
                {
                    hints.ForbiddenZones.Remove(fz);
                    break;
                }
            }
        });

        IterAndExpire(Gazes, g => g.Source.CastInfo == null, d =>
        {
            if (d.Shape.Check(player.Position, d.Source))
                hints.ForbiddenDirections.Add((player.AngleTo(d.Source), 45f.Degrees(), CastFinishAt(d.Source)));
        });

        IterAndExpire(Donuts, d => d.Source.CastInfo == null, d =>
        {
            hints.AddForbiddenZone(ShapeDistance.Donut(d.Source.Position.Quantized(), d.Inner, d.Outer), CastFinishAt(d.Source));
        });

        IterAndExpire(Circles, d => d.Source.CastInfo == null, d =>
        {
            hints.AddForbiddenZone(ShapeDistance.Circle(d.Source.Position.Quantized(), d.Radius), CastFinishAt(d.Source));

            // some enrages are way bigger than pathfinding map size (e.g. slime explosion is 60y)
            // in these cases, if the player is inside the aoe, add a goal zone telling it to GTFO as far as possible
            if (d.Radius >= 30)
            {
                var distToSource = (player.Position - d.Source.Position).Length();
                if (distToSource <= d.Radius)
                {
                    var desiredDistance = distToSource + 10f;
                    hints.GoalZones.Add(p =>
                    {
                        var dist = (p - d.Source.Position).Length();
                        return dist >= desiredDistance ? 100f : default;
                    });
                }
            }
        });

        IterAndExpire(Interrupts, d => d.CastInfo == null, d =>
        {
            if (hints.FindEnemy(d) is { } e)
                e.ShouldBeInterrupted = true;
        });

        IterAndExpire(Stuns, d => d.CastInfo == null, d =>
        {
            if (hints.FindEnemy(d) is { } e)
                e.ShouldBeStunned = true;
        });

        IterAndExpire(LOS, d => d.CastInfo == null, caster =>
        {
            if (!_losCache.TryGetValue(caster.InstanceID, out var dangermap))
                return;

            var origin = dangermap.Item1;
            var map = dangermap.Item2;

            hints.AddForbiddenZone(p =>
            {
                var offset = (p - origin) / map.PixelSize;
                return map[(int)offset.X, (int)offset.Z] ? -10 : 10;
            }, CastFinishAt(caster));
        }, d => _losCache.Remove(d.InstanceID));

        IterAndExpire(Voidzones, d => d.Source.IsDeadOrDestroyed, d =>
        {
            hints.AddForbiddenZone(d.Zone, d.Source.Position.Quantized(), d.Source.Rotation);
        });

        IterAndExpire(KnockbackZones, d => d.Source.CastInfo == null, kb =>
        {
            var castFinish = CastFinishAt(kb.Source);
            if (_playerImmunes[playerSlot].ImmuneAt(castFinish))
                return;

            hints.AddForbiddenZone(ShapeDistance.Circle(kb.Source.Position, kb.Radius), castFinish);
        });

        IterAndExpire(Spikes, t => t.Timeout <= World.CurrentTime, t =>
        {
            if (hints.FindEnemy(t.Actor) is { } enemy)
                enemy.Spikes = true;
        });
    }

    private static bool IsPlayerTransformed(Actor player) => player.Statuses.Any(Autorotation.RotationModuleManager.IsTransformStatus);
    private static bool IsDangerousOutOfCombatStatus(uint statusRaw) => statusRaw is (uint)SID.DamageUp or (uint)SID.DreadBeastAura or (uint)SID.PhysicalDamageUp;

    /// <summary>
    /// 本人目前在第幾間房。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不能用 <c>Palace.Party[0]</c>。</b>那個陣列是遊戲的隊伍名單順序，本人不保證在 0 號槽——
    /// 組隊進深牢時 0 號很可能是別人，於是整個樓層尋路會從<b>別人的房間</b>算起，
    /// 得到的方向指示是錯的而且完全不報錯（角色只是往奇怪的方向走）。
    /// 正解是拿 EntityId 比對，小地圖畫玩家標記時本來就是這樣找的。
    /// </remarks>
    /// <returns>房號 0..24；找不到本人時回 -1（剛換層、資料尚未同步等）。</returns>
    protected int FindPlayerRoom(Actor player) => FindPlayerRoom(player, out _, out _);

    /// <inheritdoc cref="FindPlayerRoom(Actor)"/>
    /// <param name="reason">回 -1 時的死因；正常回房號時是 <see cref="RoomCoordUnknownReason.None"/>。</param>
    /// <param name="rawRoom">遊戲回報的房號原值（本人不在名單裡時回 -1），只給診斷用。</param>
    protected int FindPlayerRoom(Actor player, out RoomCoordUnknownReason reason, out int rawRoom)
    {
        var len = Palace.Party.Length;
        for (var i = 0; i < len; ++i)
        {
            ref readonly var p = ref Palace.Party[i];
            if (p.EntityId == player.InstanceID)
            {
                rawRoom = p.Room;
                if (p.Room < DeepDungeonState.NumRooms)
                {
                    reason = RoomCoordUnknownReason.None;
                    return p.Room;
                }
                reason = RoomCoordUnknownReason.RoomOutOfRange;
                return -1;
            }
        }
        rawRoom = -1;
        reason = RoomCoordUnknownReason.PlayerNotInParty;
        return -1;
    }

    /// <summary>房間座標校驗閘門的結果。</summary>
    protected enum RoomCoordState
    {
        /// <summary>還不知道——版面資料還沒載入，或遊戲還沒回報本人所在的房號。</summary>
        Unknown,
        /// <summary>本人的世界座標與遊戲回報的房號對得起來，可以拿座標做房間歸屬。</summary>
        Ok,
        /// <summary>對不起來：這一層的寫死座標不適用，任何依賴它的顯示都必須退化。</summary>
        Mismatch
    }

    /// <summary>
    /// <see cref="RoomCoordState.Unknown"/> 的死因碼。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這個列舉存在的理由是「Unknown 以前完全不留痕跡」。</b>
    /// 2026-08-13 天之御柱 15 層實機事件：整層只有進層那一瞬間的一行「不通過」，之後到換層為止
    /// <b>再也沒有任何一行閘門記錄</b>——因為 Unknown 兩條 return 都在記錄點之前就跳出去了，
    /// 既不印 log 也不更新 <see cref="_coordGateLoggedState"/>。於是「座標真的對不上」與
    /// 「遊戲根本沒給房號」在 log 上長得一模一樣，只能猜。
    /// <para>
    /// ⚠️ 最需要被分辨出來的是 <see cref="NoRoomCenter"/> ＋ 回報房號 0：
    /// <c>WorldStateGameSync.SanitizeDeepDungeonRoom</c> 把遊戲的<b>負值</b>（＝「不在任何房間」）
    /// 壓成 0，而 <b>40 個樓層組、80 個版面裡的房號 0 全部沒有中心座標</b>（離線實算），
    /// 所以「遊戲說我不在任何房間」會靜默地變成「第 0 間房，可是這一面沒有第 0 間房」，
    /// 然後永遠停在 Unknown。這條路徑不印出來就永遠查不到。
    /// </para>
    /// </remarks>
    protected enum RoomCoordUnknownReason
    {
        /// <summary>沒有問題（不是 Unknown）。</summary>
        None,
        /// <summary>本人的 EntityId 不在遊戲的深牢隊伍名單裡。</summary>
        PlayerNotInParty,
        /// <summary>遊戲回報的房號超出 0..24。</summary>
        RoomOutOfRange,
        /// <summary>這一層的版面座標還沒載入（<see cref="_activeFace"/> 是 -1）。</summary>
        NoFaceLoaded,
        /// <summary>版面表裡這一格是 <c>default</c>，沒有中心座標。</summary>
        NoRoomCenter
    }

    /// <summary>把死因碼翻成 log 用的中文。刻意不進在地化——這是寫給 log 的，不是介面文字。</summary>
    private static string UnknownReasonText(RoomCoordUnknownReason reason) => reason switch
    {
        RoomCoordUnknownReason.PlayerNotInParty => "本人不在遊戲的深牢隊伍名單裡",
        RoomCoordUnknownReason.RoomOutOfRange => "遊戲回報的房號超出 0..24",
        RoomCoordUnknownReason.NoFaceLoaded => "這一層的版面座標還沒載入",
        RoomCoordUnknownReason.NoRoomCenter => "版面表裡這一格沒有中心座標",
        _ => "（無）",
    };

    /// <summary>
    /// 房間中心座標容許誤差的<b>下限</b>（單位 y）；實際值由 <see cref="RoomTolerance"/> 逐版面實算。
    /// </summary>
    /// <remarks>
    /// ⚠️ 原本這是唯一的常數，註解寫「房間間距實測約 55~58y」——<b>那個數字是錯的</b>。
    /// 2026-08-10 拿 <see cref="LoadedFloors"/> 全部版面實算最近鄰房距，結果是 31.5~87.4y，
    /// 而 35y 的歸屬半徑在厄運迷宮有 25.2% 的格子不夠用（最大需要 40.2y），
    /// 死者宮殿 0.8%、天之逆焰 0%。⇒ 固定 35y 在厄運迷宮會讓四分之一的寶箱／敵人
    /// <b>歸屬不到任何房間而被靜默丟掉</b>。
    /// <para>
    /// 保留 35f 當下限是刻意的：容許誤差只會變大不會變小，所以今天能正確歸屬的東西
    /// 明天一定還能，這個改動不可能讓既有行為退步。
    /// </para>
    /// </remarks>
    private const float RoomCenterToleranceFloor = 35f;

    /// <summary>容許誤差的上限，避免只載到兩三間房時算出荒謬的大值。</summary>
    private const float RoomCenterToleranceCap = 80f;

    /// <summary>
    /// 目前這個版面的房間歸屬容許誤差（單位 y），由 <see cref="ApplyFace"/> 實算。
    /// </summary>
    private float RoomTolerance = RoomCenterToleranceFloor;

    #region 鏡像版面自我校準

    /// <summary>本層那一組樓層的兩份鏡像版面；null＝這一層沒有可用的座標資料。</summary>
    private Tileset<Wall>? _faceA, _faceB;

    /// <summary>目前套用的是哪一面：0＝RoomsA、1＝RoomsB、-1＝沒有版面資料。</summary>
    private int _activeFace = -1;

    /// <summary>連續幾次評分都顯示「另一面才對」；到門檻才真的換面（遲滯）。</summary>
    private int _faceSwitchStreak;

    /// <summary>
    /// 上面那個連續計數是針對哪一個「遊戲回報房號」累積的。
    /// </summary>
    /// <remarks>
    /// 🔴 房號一變就把計數歸零。換層途中最危險的組合是「上一層的殘留位置 ＋ 新一層的房號」，
    /// 而那種組合下房號本來就會跳動；要求整段遲滯期間房號不變，就把那條路徑幾乎堵死了。
    /// </remarks>
    private int _faceSwitchRoom = -1;

    /// <summary>
    /// 換面需要連續吻合幾次。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不能只看一幀。</b>剛換層那一瞬間角色還在傳送途中，位置是上一層的殘值或中間態，
    /// 拿它去選面會<b>主動選錯</b>。兩份鏡像版面中心相距約 845y，正常遊玩時
    /// 不可能連續 8 次都落在錯的那一面附近，所以這個遲滯足以濾掉傳送中的雜訊。
    /// </remarks>
    private const int FaceSwitchConfirmFrames = 8;

    /// <summary>上一次記錄過的閘門狀態；null＝還沒記過。用來做「狀態變化才記一行」。</summary>
    private RoomCoordState? _coordGateLoggedState;

    /// <summary>上一次記錄過的 Unknown 死因，讓「同樣是 Unknown 但換了死因」也記得下來。</summary>
    private RoomCoordUnknownReason _coordGateLoggedReason;

    /// <summary>這一層是否曾經通過過校驗。沒通過過就沒有東西好遲滯——那是「這一層的座標不適用」。</summary>
    private bool _coordGateEverOk;

    /// <summary>最後一次「實算結果就是 Ok」的時間。</summary>
    private DateTime _coordGateOkAt;

    /// <summary>
    /// 閘門降級的遲滯時間（秒）：通過過的樓層要連續這麼久都不通過，才真的退化。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只對「降級」方向遲滯，升級（Ok）永遠即時。</b>所以這道遲滯不會讓任何東西
    /// 比今天更早被信任，只會讓已經信任過的東西不要因為單幀雜訊被抽掉。
    /// <para>
    /// 成因：實機 log 裡 16／17／18／19 層每次<b>過房間邊界</b>都會翻一次
    /// （距離 21~34y、容許 37.7y，失敗的是 <c>nearest != reported</c> 那個條件——
    /// 遊戲回報的房號與「離本人最近的房間」在邊界上本來就會短暫不一致）。
    /// 每翻一次 <see cref="ComputeChestSpots"/>／<see cref="ComputeHoardSpots"/>／
    /// <c>UpdateRoomEnemies</c> 就整組退化一幀，小地圖上已定位的標記因此<b>連環閃爍</b>。
    /// </para>
    /// <para>
    /// 📌 用時間而不是幀數：幀率不固定，用幀數的話高幀率機器的遲滯會短得多。
    /// </para>
    /// </remarks>
    private const double CoordGateHoldSeconds = 2d;

    private void ResetCoordGate()
    {
        _faceA = _faceB = null;
        _activeFace = -1;
        _faceSwitchStreak = 0;
        _faceSwitchRoom = -1;
        _coordGateLoggedState = null;
        _coordGateLoggedReason = RoomCoordUnknownReason.None;
        _coordGateEverOk = false;
        _coordGateOkAt = default;
        RoomTolerance = RoomCenterToleranceFloor;
        Array.Clear(_centerFitted);
        // 記憶化的結果是針對上一份版面算的，換層／換面時一律作廢
        _coordMemoAt = default;
        _coordMemoState = RoomCoordState.Unknown;
        _coordMemoDistance = -1f;
        _coordMemoRoom = -1;
        _doorGateLogged = null;

        // 大廳層的狀態也一起歸零。半套狀態（新樓層 + 舊大廳格心）與上面那條註解是同一個病。
        _hallMode = false;
        _hallCenterSource = HallCenterSource.None;
        _hallFitCells = 0;
        _hallFloorSince = default;
        _hallNextSample = default;
        _hallNextFit = default;
        Array.Clear(_hallSampleN);
        Array.Clear(_hallSampleX);
        Array.Clear(_hallSampleZ);
    }

    /// <summary>對某一面評分：本人離「遊戲回報的那間房」多遠、離本人最近的是哪一間。</summary>
    /// <returns>該面沒有這間房的座標時回 <c>null</c>。</returns>
    private (float Distance, int Nearest, bool Ok)? ScoreFace(Tileset<Wall> face, WPos pos, int reportedRoom)
    {
        var reported = face[reportedRoom].Center;
        if (reported == default)
            return null;

        var distance = (reported.Position - pos).Length();

        var nearest = -1;
        var bestSq = float.MaxValue;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            var c = face[i].Center;
            if (c == default)
                continue;
            var dsq = (c.Position - pos).LengthSq();
            if (dsq < bestSq)
            {
                bestSq = dsq;
                nearest = i;
            }
        }

        return (distance, nearest, distance <= RoomTolerance && nearest == reportedRoom);
    }

    #endregion

    /// <summary>
    /// 🔴 <b>台服座標校驗閘門。</b>
    /// </summary>
    /// <remarks>
    /// <see cref="LoadedFloors"/> 那份房間座標是上游從國際服 dump 出來寫死的，
    /// <b>台服會不會一樣沒有人驗過</b>。假設不成立時的失敗形式是「寶箱點畫在錯的房間」
    /// ——看起來像功能正常運作，只是位置不對，比不畫還糟。
    /// <para>
    /// 做法：拿遊戲自己回報的「本人在第幾間房」對上那一間的中心座標。兩者對得上，
    /// 才准用這份座標做房間歸屬。除了距離，還要求<b>離本人最近的房間中心就是遊戲說的那一間</b>
    /// ——後者才是子格點位映射真正依賴的性質（座標整體平移時距離可能還過得了關，
    /// 但最近的會變成別間）。
    /// </para>
    /// <para>📌 校驗沒過不是錯誤，是「這一層退化成房級標示」，而且必須讓使用者看得見原因。</para>
    /// <para>
    /// 🔴 <b>這個函式有副作用，所以一幀只准跑一次</b>——它會累加鏡像面自我校準的連續計數
    /// （<see cref="_faceSwitchStreak"/>）。同一幀被呼叫兩次，
    /// <see cref="FaceSwitchConfirmFrames"/> 那道遲滯就等於只剩一半，
    /// 而失敗形式是「換層途中被殘留座標騙去換面」，完全不報錯。
    /// 本函式因此對 <c>World.CurrentTime</c> 記憶化，實算在 <see cref="ComputeRoomCoords"/>。
    /// </para>
    /// </remarks>
    protected RoomCoordState CheckRoomCoords(Actor player, out float distance, out int reportedRoom)
    {
        if (_coordMemoAt != World.CurrentTime)
        {
            _coordMemoAt = World.CurrentTime;
            _coordMemoState = ComputeRoomCoords(player, out _coordMemoDistance, out _coordMemoRoom);
        }
        distance = _coordMemoDistance;
        reportedRoom = _coordMemoRoom;
        return _coordMemoState;
    }

    private DateTime _coordMemoAt;
    private RoomCoordState _coordMemoState;
    private float _coordMemoDistance = -1f;
    private int _coordMemoRoom = -1;

    private RoomCoordState ComputeRoomCoords(Actor player, out float distance, out int reportedRoom)
    {
        distance = -1f;
        reportedRoom = FindPlayerRoom(player, out var reason, out var rawRoom);
        var pos = player.Position;

        // 大廳層沒有固定格心表時，拿本人在房內的位置即時擬合（自我校準閘門在 TryFitHallCenters）。
        // 🔴 必須放在下面那個 early return **之前**：擬合成功之前 _activeFace 就是 -1，
        //    放在後面等於永遠收不到樣本，而失敗形式是「大廳永遠停在摘要模式」——完全不報錯。
        AccumulateHallSample(reportedRoom, pos);

        // 🔴 這兩條路徑以前是直接 `return Unknown`——不印 log、也不動 _coordGateLoggedState。
        //    現在改成走到底下同一個記錄點，才有辦法在下一次實跑時一行定死因。
        //    ⚠️ 但**不能**順手在這裡把 _faceSwitchStreak 歸零：原本這兩條路徑不碰它，
        //    歸零會改變鏡像面自我校準的既有行為。
        if (reportedRoom < 0 || _activeFace < 0)
        {
            if (reportedRoom >= 0)
                reason = RoomCoordUnknownReason.NoFaceLoaded;
            return FinishRoomCoords(RoomCoordState.Unknown, reason, rawRoom, pos, -1, -1f, out distance);
        }

        var active = _activeFace == 0 ? _faceA : _faceB;
        var score = active != null ? ScoreFace(active, pos, reportedRoom) : null;

        // ── 鏡像面自我校準 ────────────────────────────────────────────────
        // 目前這一面對不上時，看看另一面對不對得上。兩面中心相距約 845y ⇒ 只有一面可能吻合，
        // 巧合吻合實務上不可能發生。連續 FaceSwitchConfirmFrames 次都指向另一面才真的換，
        // 免得被傳送途中的座標騙走。
        if (score is not { Ok: true } && _faceA != null && _faceB != null)
        {
            var otherIdx = 1 - _activeFace;
            var other = otherIdx == 0 ? _faceA : _faceB;
            var otherScore = ScoreFace(other, pos, reportedRoom);
            if (otherScore is { Ok: true })
            {
                if (_faceSwitchRoom != reportedRoom)
                {
                    _faceSwitchRoom = reportedRoom;
                    _faceSwitchStreak = 0;
                }
                if (++_faceSwitchStreak >= FaceSwitchConfirmFrames)
                {
                    Service.Logger.Information(
                        $"[DD] 鏡像版面自我校準：改用版面 {(otherIdx == 0 ? "A" : "B")}（遊戲回報的 Progress.Tileset 是 {Palace.Progress.Tileset}）。" +
                        $"樓層 {Palace.Floor}、回報房號 {reportedRoom}、本人 ({pos.X:f1}, {pos.Z:f1})、" +
                        $"原本那面距離 {(score?.Distance ?? -1f):f1}y/最近房 {(score?.Nearest ?? -1)}、" +
                        $"改用那面距離 {otherScore.Value.Distance:f1}y/最近房 {otherScore.Value.Nearest}");
                    ApplyFace(otherIdx);
                    _faceSwitchStreak = 0;
                    // ⚠️ ApplyFace 會重算容許誤差，所以要用新的那份重新評分，不能沿用上面那次
                    score = ScoreFace(other, pos, reportedRoom);
                }
            }
            else
            {
                _faceSwitchStreak = 0;
            }
        }
        else
        {
            _faceSwitchStreak = 0;
        }

        if (score is not { } s)
            return FinishRoomCoords(RoomCoordState.Unknown, RoomCoordUnknownReason.NoRoomCenter, rawRoom, pos, -1, -1f, out distance);

        return FinishRoomCoords(s.Ok ? RoomCoordState.Ok : RoomCoordState.Mismatch,
            RoomCoordUnknownReason.None, rawRoom, pos, s.Nearest, s.Distance, out distance);
    }

    /// <summary>
    /// 套上降級遲滯、記錄狀態變化，並交出最終的閘門狀態。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>三條路徑（Ok／Mismatch／Unknown）一律走這裡</b>——以前 Unknown 直接 return，
    /// 結果是「整層沉默」這種最難查的失敗形式。集中在一個出口才保證每一種結果都留得下痕跡。
    /// </remarks>
    /// <param name="raw">實算出來的狀態，還沒套遲滯。</param>
    /// <param name="nearest">離本人最近的房間；-1＝這次沒算（Unknown 路徑會另外補算供診斷用）。</param>
    /// <param name="rawDistance">與回報房中心的距離；-1＝沒算。</param>
    private RoomCoordState FinishRoomCoords(RoomCoordState raw, RoomCoordUnknownReason reason, int rawRoom, WPos pos, int nearest, float rawDistance, out float distance)
    {
        distance = rawDistance;

        // ── 降級遲滯 ──────────────────────────────────────────────────────
        // 通過過的樓層，單幀的不通過／不知道不立刻退化；Ok 方向維持即時。
        var state = raw;
        var heldFor = 0d;
        if (raw == RoomCoordState.Ok)
        {
            _coordGateEverOk = true;
            _coordGateOkAt = World.CurrentTime;
        }
        else if (_coordGateEverOk)
        {
            heldFor = (World.CurrentTime - _coordGateOkAt).TotalSeconds;
            if (heldFor < CoordGateHoldSeconds)
                state = RoomCoordState.Ok;
        }

        // 🔴 診斷是「狀態變化才記一行」而不是「一層記一次」。
        //    一層記一次記到的必然是樓層載入後的**第一幀**，而那一幀角色還在傳送中——
        //    量到的是一個與回報房號無關的固定位置（實機 log 裡「最近的房間」恆為 3 或 10
        //    就是這個特徵），於是把「量測時機不對」誤報成「座標對不上」。
        //    記的是**遲滯之後**的狀態，也就是其他功能真的看到的那個值——記原始值會與行為對不上。
        if (_coordGateLoggedState != state || (state == RoomCoordState.Unknown && _coordGateLoggedReason != reason))
        {
            _coordGateLoggedState = state;
            _coordGateLoggedReason = reason;

            var face = FaceName();
            // 要使用者回報才查得出台服座標對不對，所以走 Information（使用者的 LogLevel 是 1）。
            if (state == RoomCoordState.Unknown)
            {
                // Unknown 時 ScoreFace 沒有交出任何數字，所以另外補算「本人其實離哪一間最近」。
                // 只在狀態翻轉那一幀跑，25 次迴圈的成本無關緊要。
                var (nearRoom, nearDist) = NearestFaceRoomForDiag(pos);
                // 🔴 房號 0 要特別點名：遊戲回報「不在任何房間」的負值會被
                //    SanitizeDeepDungeonRoom 壓成 0，而所有版面的房號 0 都沒有中心座標。
                var zeroHint = rawRoom == 0 && reason == RoomCoordUnknownReason.NoRoomCenter
                    ? "（⚠️ 房號 0 也可能是遊戲回報「不在任何房間」的負值被正規化的結果）" : "";
                Service.Logger.Information(
                    $"[DD] 房間座標校驗無法判定：樓層 {Palace.Floor}、Progress.Tileset {Palace.Progress.Tileset}、" +
                    $"實際採用版面 {face}、原因 {UnknownReasonText(reason)}{zeroHint}、" +
                    $"遊戲回報房號 {rawRoom}、本人 ({pos.X:f1}, {pos.Z:f1})、" +
                    $"最近的房間是 {nearRoom}（距離 {nearDist:f1}y）、容許誤差 {RoomTolerance:f1}y");
            }
            else
            {
                var heldNote = state == RoomCoordState.Mismatch && _coordGateEverOk
                    ? $"、已遲滯 {heldFor:f1}s 仍未回到通過" : "";
                Service.Logger.Information(
                    $"[DD] 房間座標校驗{(state == RoomCoordState.Ok ? "通過" : "不通過")}：樓層 {Palace.Floor}、" +
                    $"Progress.Tileset {Palace.Progress.Tileset}、實際採用版面 {face}、" +
                    $"遊戲回報房號 {rawRoom}、本人 ({pos.X:f1}, {pos.Z:f1})、與該房中心距離 {rawDistance:f1}y、" +
                    $"最近的房間是 {nearest}、容許誤差 {RoomTolerance:f1}y{heldNote}");
            }
        }

        return state;
    }

    /// <summary>診斷用：離某個座標最近的房間與距離，直接掃<b>版面表原始中心</b>。</summary>
    /// <remarks>
    /// ⚠️ 刻意不用 <see cref="NearestRoom"/>——那個掃的是 <see cref="RoomCenters"/>，
    /// 裡面可能混了網格擬合補出來的值。診斷要問的是「原始表怎麼說」。
    /// </remarks>
    private (int Room, float Distance) NearestFaceRoomForDiag(WPos pos)
    {
        var face = _activeFace == 0 ? _faceA : _activeFace == 1 ? _faceB : null;
        if (face == null)
            return (-1, -1f);

        var best = -1;
        var bestSq = float.MaxValue;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            var c = face[i].Center;
            if (c == default)
                continue;
            var dsq = (c.Position - pos).LengthSq();
            if (dsq < bestSq)
            {
                bestSq = dsq;
                best = i;
            }
        }
        return best < 0 ? (-1, -1f) : (best, MathF.Sqrt(bestSq));
    }

    /// <summary>
    /// 離某個世界座標最近的房間格子。
    /// </summary>
    /// <param name="maxDistance">超過這個距離就當作不屬於任何房間。</param>
    /// <returns>房號 0..24；沒有任何房間中心資料、或全都太遠時回 -1。</returns>
    protected int NearestRoom(WPos p, float maxDistance)
    {
        var best = -1;
        var bestSq = maxDistance == float.MaxValue ? float.MaxValue : maxDistance * maxDistance;
        for (var i = 0; i < RoomCenters.Length; ++i)
        {
            if (RoomCenters[i] is not WPos c)
                continue;
            var dsq = (c - p).LengthSq();
            if (dsq < bestSq)
            {
                bestSq = dsq;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// 房間格子的像素／碼換算。
    /// </summary>
    /// <remarks>
    /// ⚠️ 房間中心的實測間距在 55~68y 之間跳（<see cref="LoadedFloors"/> 的座標本來就
    /// <b>不在完美網格上</b>），所以這是個近似值，不是精確換算。間距偏大的房間裡，
    /// 靠邊的寶箱會被下面的夾邊處理擋在格子內——寧可貼邊也不要溢出到隔壁格，
    /// 溢出會讓人以為寶箱在別間房。
    /// </remarks>
    private const float CellPixelsPerYalm = Minimap.CellPixels / 55f;

    /// <summary>
    /// 把 <c>ObjectTable</c> 裡看得到的寶箱實體歸屬到房間，並換算成格內的像素位置。
    /// </summary>
    /// <returns>
    /// <b>座標校驗沒過時回 null</b>——寧可整層退化成「地圖說有、位置不明」，
    /// 也不要把寶箱畫在錯的房間。
    /// </returns>
    /// <remarks>
    /// 📌 遠處房間還沒串流進 <c>ObjectTable</c> 時這裡自然就數不到，
    /// 那些寶箱會留在上排的「還沒找到」摘要裡——這是預期行為，不是缺陷。
    /// </remarks>
    private List<ChestSpot>? ComputeChestSpots(RoomCoordState coords)
    {
        if (coords != RoomCoordState.Ok)
            return null;

        // 留邊，免得圖示被格子邊緣切掉
        const float limit = Minimap.CellHalfPixels - 11f;

        List<ChestSpot> res = [];
        foreach (var a in World.Actors)
        {
            if (_openedChests.Contains(a.InstanceID))
                continue;
            var slot = ChestSlotForOID(a.OID);
            if (slot < 0)
                continue;
            var room = NearestRoom(a.Position, RoomTolerance);
            if (room < 0 || RoomCenters[room] is not WPos center)
                continue;

            // 世界座標 +X＝東＝畫面右、+Z＝南＝畫面下（拿 LoadedFloors 的相鄰房中心對照過：
            // 房號 +1 的中心 X 較大、房號 +5 的中心 Z 較大）
            var d = a.Position - center;
            var off = new Vector2(d.X * CellPixelsPerYalm, d.Z * CellPixelsPerYalm);
            off = Vector2.Clamp(off, new Vector2(-limit), new Vector2(limit));
            res.Add(new(room, slot, off));
        }
        return res;
    }

    #region PalacePal 共享資料

    /// <summary>
    /// 與內建表視為同一個點的距離門檻（碼）。
    /// </summary>
    /// <remarks>內建表台服 143/143 實測正確，PalacePal 多出來的多半是社群新點，重疊的部分要去掉才不會重複計算。</remarks>
    private const float PalaceDedupeRange = 1f;

    /// <summary>重新向 PalacePal 取一次資料的間隔（秒）。</summary>
    /// <remarks>
    /// ⚠️ 不能每幀取：沒安裝時 <c>InvokeFunc</c> 是靠<b>擲例外</b>回報的。
    /// 也不能只取一次：PalacePal 的清單會隨著玩下去長大，而且使用者可能中途才啟用它。
    /// </remarks>
    private const double PalaceRefreshSeconds = 10d;

    /// <summary>PalacePal 提供、且不與內建表重複的陷阱點。</summary>
    private WPos[] _palTraps = [];

    private DateTime _palNextRefresh;
    private bool? _palAvailableLogged;

    /// <summary>「現在看得到什麼」重問一次的間隔（秒）。比陷阱那條短：這份是當下的觀測，不是資料庫。</summary>
    private const double PalaceVisibleRefreshSeconds = 2d;

    /// <summary>可接受的快照年齡（毫秒）。超過就當「不知道」——對方沒在跑時端點照樣回得出舊值。</summary>
    private const int PalaceVisibleMaxAgeMillis = 1000;

    /// <summary>PalacePal 回報「現在看得到」的埋藏寶藏位置，尚未與 BMR 自己看到的去重。</summary>
    private Vector3[] _palVisibleHoards = [];

    private DateTime _palVisibleNextRefresh;
    private bool? _palVisibleLogged;

    /// <summary>
    /// 向 PalacePal 重新問一次「這一趟這一層現在真的看得到哪些埋藏寶藏」。
    /// </summary>
    /// <remarks>
    /// 🔑 為什麼要問別人：BMR 的 <c>World.Actors</c> 會丟掉 EntityId 無效的物件，而 Dalamud 的
    /// <c>ObjectTable</c> 不會 —— 埋藏處那個沒名字的事件物件是否一定帶有效 EntityId，離線證不了。
    /// 🔴 拿不到就整組清空，不留殘值。
    /// </remarks>
    private void UpdatePalacePalVisibleHoards()
    {
        if (!Config.UsePalacePalVisibleHoard || !Config.ShowAccursedHoard)
        {
            _palVisibleHoards = [];
            _palVisibleNextRefresh = default;
            _palVisibleLogged = null;
            return;
        }

        var now = World.CurrentTime;
        if (now < _palVisibleNextRefresh)
            return;
        _palVisibleNextRefresh = now.AddSeconds(PalaceVisibleRefreshSeconds);

        var territory = (ushort)World.CurrentZone;
        var hoards = PalacePalIpc.GetVisibleHoards(territory, PalaceVisibleMaxAgeMillis);
        _palVisibleHoards = hoards?.ToArray() ?? [];

        var available = hoards != null;
        if (_palVisibleLogged != available)
        {
            _palVisibleLogged = available;
            Service.Logger.Information(available
                ? $"[DD pal] PalacePal 可見快照可用（區域 {territory}）：現在看得到的埋藏寶藏 {_palVisibleHoards.Length} 個。"
                : $"[DD pal] PalacePal 可見快照取不到（沒安裝／沒有「現在看得到什麼」那組端點／版本低於 {PalacePalIpc.SupportedVisibleApiVersion}／快照超過 {PalaceVisibleMaxAgeMillis} 毫秒），埋藏寶藏只用 BMR 自己看到的。");
        }
    }

    /// <summary>
    /// 向 PalacePal 重新要一次這個區域的陷阱座標。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>拿不到就整組清空，不留上一次的殘值。</b>使用者停用 PalacePal 之後還照著它的舊資料
    /// 閃躲，是拿一份我們已經無法確認的資料在做決策——失敗形式是安靜地維持一個看不出來源的行為。
    /// 清空之後就退回內建表，也就是今天的行為。
    /// <para>
    /// 🔴 <b>刻意只取陷阱，不取埋藏寶藏。</b>2026-08-10 使用者裁決拿掉資料庫記載的寶藏顯示
    /// （原話：「埋藏寶藏 地圖不用放預測 你這不是每一格都畫了嗎」），既然沒有消費端，
    /// 就不要每 10 秒對 PalacePal 發一次沒人要的 IPC。
    /// <see cref="PalacePalIpc.GetHoards"/> 的包裝有刻意留著（目前無呼叫端），理由寫在那裡。
    /// </para>
    /// </remarks>
    private void UpdatePalacePal()
    {
        if (!Config.UsePalacePal)
        {
            _palTraps = [];
            _palAvailableLogged = null;
            return;
        }

        var now = World.CurrentTime;
        if (now < _palNextRefresh)
            return;
        _palNextRefresh = now.AddSeconds(PalaceRefreshSeconds);

        var territory = (ushort)World.CurrentZone;
        var traps = PalacePalIpc.GetTraps(territory);
        var available = traps != null;

        _palTraps = traps != null ? DedupeTraps(traps) : [];

        if (_palAvailableLogged != available)
        {
            _palAvailableLogged = available;
            Service.Logger.Information(available
                ? $"[DD pal] PalacePal 資料可用（區域 {territory}）：陷阱 {traps?.Count ?? 0} 筆、與內建表去重後多出 {_palTraps.Length} 筆。"
                : $"[DD pal] PalacePal 資料取不到（沒安裝／合約版本不是 {PalacePalIpc.SupportedApiVersion}／對方出錯），退回 BMR 內建的陷阱表。");
        }
    }

    /// <summary>
    /// 把 PalacePal 的清單去掉與內建表重複的、以及自己內部重複的點。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用 <see cref="PalaceDedupeRange"/> 見方的格子先篩候選，再對候選做<b>原本那個</b>
    /// <c>AlmostEqual</c> 判定——判定本身沒有被換掉，格子只是候選過濾器。
    /// 直接兩層迴圈是 O(n·m)，而這是掛在每 10 秒一次的主執行緒路徑上；
    /// 兩份清單各數百到數千筆時那是幾百萬次浮點比較，會變成看得見的頓一下。
    /// </remarks>
    private WPos[] DedupeTraps(List<Vector3> src)
    {
        static (int, int) Cell(WPos p) => ((int)MathF.Floor(p.X / PalaceDedupeRange), (int)MathF.Floor(p.Z / PalaceDedupeRange));

        // 格子 → 落在該格的點。查詢時掃 3×3 鄰域，涵蓋所有可能落在 ±range 方框內的點。
        Dictionary<(int, int), List<WPos>> grid = [];
        void Index(WPos p)
        {
            var c = Cell(p);
            if (!grid.TryGetValue(c, out var bucket))
                grid[c] = bucket = [];
            bucket.Add(p);
        }

        bool Duplicate(WPos p)
        {
            var (cx, cz) = Cell(p);
            for (var dx = -1; dx <= 1; ++dx)
            {
                for (var dz = -1; dz <= 1; ++dz)
                {
                    if (!grid.TryGetValue((cx + dx, cz + dz), out var bucket))
                        continue;
                    for (var k = 0; k < bucket.Count; ++k)
                    {
                        if (bucket[k].AlmostEqual(p, PalaceDedupeRange))
                            return true;
                    }
                }
            }
            return false;
        }

        for (var i = 0; i < _trapsCurrentZone.Length; ++i)
            Index(_trapsCurrentZone[i]);

        List<WPos> res = [];
        var count = src.Count;
        for (var i = 0; i < count; ++i)
        {
            var p = new WPos(src[i].X, src[i].Z);
            if (Duplicate(p))
                continue;
            Index(p); // 也擋掉 PalacePal 自己清單裡的重複
            res.Add(p);
        }
        return [.. res];
    }

    #endregion

    #region 埋藏的寶藏

    /// <summary>
    /// 這一幀看得到的埋藏寶藏實體。<b>每次使用前都要先呼叫 <see cref="CollectHoardActors"/> 重填。</b>
    /// </summary>
    /// <remarks>
    /// 用一份重複使用的清單而不是每次配置新的：世界疊加層走的是每幀路徑，
    /// 而深牢一層最多也只有一個埋藏寶藏，為它每幀配置一個 <c>List</c> 是白花的。
    /// 📌 裡面放的是 BMR 自己的 <see cref="Actor"/> 鏡像物件（純受管資料），
    /// <b>不是原生指標</b>，所以「不跨幀保存原生指標」那條紅線在這裡不適用；
    /// 即使如此也只在同一次呼叫內用完就丟。
    /// </remarks>
    private readonly List<Actor> _hoardActors = [];

    /// <summary>
    /// 隱藏點與已現形寶箱視為「同一個寶藏」的距離（碼）。
    /// </summary>
    /// <remarks>
    /// 兩者並存時只畫一個，否則同一個位置會疊出兩圈——那看起來像是有兩個寶藏。
    /// 取 5y 是寬鬆值：同一層不會有第二個埋藏寶藏，所以寧可多合併也不要漏合併。
    /// </remarks>
    private const float HoardDedupeRangeSq = 5f * 5f;

    /// <summary>
    /// 重新收集目前該標示的埋藏寶藏實體到 <see cref="_hoardActors"/>。
    /// </summary>
    /// <remarks>
    /// 停止標示的條件有三個，任一成立就不收：
    /// <list type="number">
    /// <item>實體已經不在 <c>ObjectTable</c> 裡（挖走之後的常態）；</item>
    /// <item><see cref="_openedChests"/> 收到過這個實體的 <c>EventOpenTreasure</c>；</item>
    /// <item>這一層已經跳過「發現了埋藏的寶藏！」系統訊息（<see cref="_hoardFound"/>）。</item>
    /// </list>
    /// 🔴 <b>刻意不做「沒用魔陶器：感知寶藏就不標」這種閘門。</b>本函式的唯一資料來源是實體
    /// 在不在 <c>ObjectTable</c> 裡：在就標、不在就什麼都不畫。因此「沒照出來的時候實體到底
    /// 會不會出現在 <c>ObjectTable</c>」這個離線證不了的問題，最壞情況只是<b>這個功能不顯示</b>，
    /// 不會把標記畫到錯的地方。
    /// </remarks>
    private void CollectHoardActors()
    {
        _hoardActors.Clear();
        _hoardPalSpots.Clear();
        _hoardAnyPositions.Clear();
        if (_hoardFound)
            return;

        foreach (var a in World.Actors)
        {
            if (a.OID is not ((uint)OID.BandedCofferIndicator or (uint)OID.BandedCoffer))
                continue;
            // 🔴 已開過的也要進去重基準，否則 PalacePal 那份會把它重新畫一次。
            _hoardAnyPositions.Add(a.Position);
            if (_openedChests.Contains(a.InstanceID))
                continue;
            _hoardActors.Add(a);
        }

        // 去重：已現形的寶箱優先，旁邊那個隱藏點就不用再畫了。
        for (var i = _hoardActors.Count - 1; i >= 0; --i)
        {
            if (_hoardActors[i].OID != (uint)OID.BandedCofferIndicator)
                continue;
            for (var j = 0; j < _hoardActors.Count; ++j)
            {
                if (j == i || _hoardActors[j].OID != (uint)OID.BandedCoffer)
                    continue;
                if ((_hoardActors[j].Position - _hoardActors[i].Position).LengthSq() <= HoardDedupeRangeSq)
                {
                    _hoardActors.RemoveAt(i);
                    break;
                }
            }
        }

        MergePalVisibleHoards();
    }

    /// <summary>這一幀所有寶藏 OID 實體的位置（<b>含已開過的</b>），只當去重基準。</summary>
    private readonly List<WPos> _hoardAnyPositions = [];

    /// <summary>只有 PalacePal 看得到的那幾個埋藏寶藏位置，生命週期與 <see cref="_hoardActors"/> 相同。</summary>
    private readonly List<Vector3> _hoardPalSpots = [];

    /// <summary>
    /// 把 PalacePal 看得到、而 BMR 自己沒看到的那幾個補進 <see cref="_hoardPalSpots"/>。
    /// </summary>
    /// <remarks>
    /// 📌 正常情況下這裡一個都留不下來：兩邊讀的是同一張物件表，BMR 掃的範圍還更大。
    /// 真的留下東西時，log 的「可見快照可用」那行與畫面上多出來的標記就是證據。
    /// </remarks>
    private void MergePalVisibleHoards()
    {
        for (var i = 0; i < _palVisibleHoards.Length; ++i)
        {
            var p = _palVisibleHoards[i];
            var pos = new WPos(p.X, p.Z);
            var dup = false;
            for (var j = 0; j < _hoardAnyPositions.Count; ++j)
            {
                if ((_hoardAnyPositions[j] - pos).LengthSq() <= HoardDedupeRangeSq)
                {
                    dup = true;
                    break;
                }
            }
            if (!dup)
                _hoardPalSpots.Add(p);
        }
    }

    /// <summary>
    /// 把埋藏寶藏歸屬到房間並換算成格內像素位置，規則與 <see cref="ComputeChestSpots"/> 完全相同。
    /// </summary>
    /// <param name="detected">
    /// 這一幀實際偵測到幾個埋藏寶藏實體，<b>與能不能定位到房間無關</b>。
    /// 用來區分「這層沒有／還沒走到」與「有，但這層的座標對不上所以放不上小地圖」。
    /// </param>
    /// <returns>座標校驗沒過或功能關閉時回 <c>null</c>——寧可不畫，也不要畫在錯的房間。</returns>
    private List<HoardSpot>? ComputeHoardSpots(RoomCoordState coords, out int detected)
    {
        detected = 0;
        if (!Config.ShowAccursedHoard)
            return null;

        CollectHoardActors();
        detected = _hoardActors.Count + _hoardPalSpots.Count;

        if (coords != RoomCoordState.Ok)
            return null;

        // 留邊，免得圖示被格子邊緣切掉（與寶箱同一個常數）
        const float limit = Minimap.CellHalfPixels - 11f;

        // 🔴🔴 這裡**只**放遊戲自己擺出來的事件物件，不要再加 PalacePal 資料庫記載的「可能有」。
        //    2026-08-10 使用者裁決移除，原話：「埋藏寶藏 地圖不用放預測 你這不是每一格都畫了嗎」
        //    ——那份清單是整座深牢跨樓層的聯集，攤到單層的 25 格上幾乎格格命中，
        //    實測畫面就是每一格都有一個青色菱形。詳細理由寫在 HoardKind 的列舉註解上。
        List<HoardSpot> res = [];

        for (var i = 0; i < _hoardActors.Count; ++i)
        {
            var a = _hoardActors[i];
            var room = NearestRoom(a.Position, RoomTolerance);
            if (room < 0 || RoomCenters[room] is not WPos center)
                continue;

            var d = a.Position - center;
            var off = new Vector2(d.X * CellPixelsPerYalm, d.Z * CellPixelsPerYalm);
            off = Vector2.Clamp(off, new Vector2(-limit), new Vector2(limit));
            res.Add(new(room, off, a.OID == (uint)OID.BandedCoffer ? HoardKind.Revealed : HoardKind.Buried));
        }

        // PalacePal 只給座標，分不出埋著還是已現形 ⇒ 一律畫成把握較低的那一態（空心）。
        for (var i = 0; i < _hoardPalSpots.Count; ++i)
        {
            var q = _hoardPalSpots[i];
            var qpos = new WPos(q.X, q.Z);
            var qroom = NearestRoom(qpos, RoomTolerance);
            if (qroom < 0 || RoomCenters[qroom] is not WPos qcenter)
                continue;

            var qd = qpos - qcenter;
            var qoff = new Vector2(qd.X * CellPixelsPerYalm, qd.Z * CellPixelsPerYalm);
            qoff = Vector2.Clamp(qoff, new Vector2(-limit), new Vector2(limit));
            res.Add(new(qroom, qoff, HoardKind.Buried));
        }

        return res;
    }

    // ── 世界疊加層 ────────────────────────────────────────────────────────
    // NecroLens 的圈半徑是 2y（它的註解寫「Make Hoards bigger」，一般寶箱是 1y），這裡沿用，
    // 這樣兩個外掛同時開著也不會出現兩種尺寸的圈。
    private const float HoardMarkerRadius = 2f;
    private const float HoardMarkerThickness = 2f;
    private const float HoardOutlineExtra = 2f;

    /// <summary>地面圈之外再往上拉一小段的立柱高度（碼）。</summary>
    /// <remarks>
    /// 埋藏寶藏是隱形的，光有貼地的圈在俯角小的時候會被壓成一條線、遠一點就看不見。
    /// 立柱給這個標記一個明確的「上」，也讓它在人還沒走近時就找得到。
    /// </remarks>
    private const float HoardMarkerStem = 1.6f;

    // 埋藏寶藏的標示色。刻意不用 Colors.* 的語意色（那些是使用者可調的危險／安全色，
    // 借來當「這裡有東西」會在使用者改色之後變成謊話），也刻意選成與 PalacePal 的
    // 埋藏寶藏預設色（青色）同一系，讓兩邊看起來是同一件事。
    // ⚠️ ImGui 的 uint 顏色是 ABGR：這個值是 R=0x30 G=0xE0 B=0xF0。
    private const uint ColorHoard = 0xFFF0E030u;

    /// <summary>
    /// 在世界上畫出埋藏寶藏的位置。
    /// </summary>
    /// <remarks>
    /// 從 <see cref="Update"/> 呼叫：<c>Plugin.DrawUI</c> 的順序是
    /// <c>Camera.Update</c> →（本函式所在的）<c>ZoneModule.Update</c> → … →
    /// <c>Camera.DrawWorldPrimitives</c>，所以矩陣是當幀的、線也一定會被 flush 出去。
    /// <para>
    /// 📌 <c>CalculateAIHints</c> 也在同一個窗口內，但那條路徑在<b>有 boss 模組正在進行中的時候
    /// 整段被跳過</b>（見 <c>AIHintsBuilder.Update</c>），拿它當顯示用的繪製點會多一個
    /// 與顯示無關的失效條件。
    /// </para>
    /// <para>
    /// ⚠️ 隱藏 UI／過場時不畫：<c>DrawWorldPrimitives</c> 本身沒有這個閘門，
    /// 而 BMR 既有的世界繪製都是從 <c>WindowSystem.Draw</c> 底下發出的（那裡有閘門）。
    /// 不自己擋的話，這會是第一個在過場動畫上畫線的東西。
    /// </para>
    /// </remarks>
    private void DrawHoardOverlay()
    {
        if (!Config.ShowAccursedHoard || Palace.IsBossFloor || BetweenFloors)
            return;

        if (Service.GameGui.GameUiHidden
            || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Service.Condition[ConditionFlag.WatchingCutscene]
            || Service.Condition[ConditionFlag.WatchingCutscene78])
            return;

        if (Camera.Instance is not { } camera)
            return;

        // 🔴🔴 這裡**只**畫遊戲自己擺出來的事件物件。
        //    2026-08-10 使用者裁決拿掉 PalacePal 資料庫記載的「可能有」圈（小地圖與世界疊加層都拿掉），
        //    原話：「埋藏寶藏 地圖不用放預測 你這不是每一格都畫了嗎」。
        //    除了噪音之外還有一個獨立理由：**PalacePal 自己就會畫它的世界標記**，BMR 再畫是雙份。
        //    詳細裁決紀錄寫在 Minimap.cs 的 HoardKind 列舉上。
        CollectHoardActors();
        for (var i = 0; i < _hoardActors.Count; ++i)
        {
            var p = _hoardActors[i].PosRot;
            DrawHoardMarker(camera, new Vector3(p.X, p.Y, p.Z));
        }
        for (var i = 0; i < _hoardPalSpots.Count; ++i)
            DrawHoardMarker(camera, _hoardPalSpots[i]);
    }

    /// <summary>
    /// 一個埋藏寶藏的地面標記：圈 ＋ 中心叉 ＋ 立柱，全部先畫深色外框再畫本體。
    /// </summary>
    /// <remarks>
    /// 外框做法與 <c>UIRotationWindow.DrawPathSegment</c> 相同（先粗深色、再細亮色）——
    /// 疊加層底下是 3D 場景，沒有外框的細線在亮地板上會整條消失。
    /// 全部是線，不畫任何半透明色塊，維持與 NecroLens 一致的「不疊顏色」語彙。
    /// </remarks>
    private static void DrawHoardMarker(Camera camera, Vector3 center)
    {
        const float outline = HoardMarkerThickness + HoardOutlineExtra;

        camera.DrawWorldCircle(center, HoardMarkerRadius, Colors.Shadows, outline);
        camera.DrawWorldCircle(center, HoardMarkerRadius, ColorHoard, HoardMarkerThickness);

        // 中心的叉：圈只說「這附近」，交叉點才說「就是這裡挖」。
        const float d = HoardMarkerRadius * 0.5f;
        var c1 = center + new Vector3(-d, 0f, -d);
        var c2 = center + new Vector3(d, 0f, d);
        var c3 = center + new Vector3(-d, 0f, d);
        var c4 = center + new Vector3(d, 0f, -d);
        var top = center + new Vector3(0f, HoardMarkerStem, 0f);

        camera.DrawWorldLine(c1, c2, Colors.Shadows, outline);
        camera.DrawWorldLine(c3, c4, Colors.Shadows, outline);
        camera.DrawWorldLine(center, top, Colors.Shadows, outline);
        camera.DrawWorldLine(c1, c2, ColorHoard, HoardMarkerThickness);
        camera.DrawWorldLine(c3, c4, ColorHoard, HoardMarkerThickness);
        camera.DrawWorldLine(center, top, ColorHoard, HoardMarkerThickness);
    }

    #endregion

    #region 房間內的敵人數

    /// <summary>
    /// 每個房間目前偵測到幾隻活著的敵人。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>「0」的意思是「現在偵測不到」，不是「已經清空」。</b><c>ObjectTable</c> 只含串流進來的實體，
    /// 遠處房間的怪根本不在裡面。UI 因此只畫正向標記（有偵測到才寫數字），
    /// 並常駐一行說明——絕不能讓使用者把空白讀成清完了。
    /// </remarks>
    private readonly int[] _roomEnemies = new int[DeepDungeonState.NumRooms];

    /// <summary>上面那份資料現在可不可信（座標校驗過了、而且掃過至少一次）。</summary>
    private bool _roomEnemiesValid;

    private DateTime _roomEnemiesSweptAt;

    /// <summary>
    /// 重新統計每個房間的敵人數。
    /// </summary>
    /// <remarks>
    /// ⚠️ 節流到 0.4 秒一次。小地圖是每幀重畫的，但敵人數不需要每幀重算——
    /// 全表掃描放在每幀路徑上是白花成本（<c>World.Actors</c> 動輒上百個）。
    /// 📌 判定條件對齊 <c>AIHintsBuilder.FillEnemies</c>（可選取、非友方、沒死），
    /// 另外限定 <c>ActorType.Enemy</c> 以排掉寵物、陸行鳥、事件物件與寶箱。
    /// </remarks>
    private void UpdateRoomEnemies(RoomCoordState coords, DateTime now)
    {
        if (coords != RoomCoordState.Ok)
        {
            _roomEnemiesValid = false;
            return;
        }

        if (_roomEnemiesValid && (now - _roomEnemiesSweptAt).TotalSeconds < 0.4d)
            return;
        _roomEnemiesSweptAt = now;

        Array.Clear(_roomEnemies);
        foreach (var a in World.Actors)
        {
            if (a.Type != ActorType.Enemy || a.IsAlly || !a.IsTargetable || a.IsDeadOrDestroyed)
                continue;
            var room = NearestRoom(a.Position, RoomTolerance);
            if (room < 0)
                continue;
            ++_roomEnemies[room];
        }
        _roomEnemiesValid = true;
    }

    #endregion

    /// <summary>寶箱實體的 OID 對應到哪一個型別槽；不是寶箱回 -1。</summary>
    /// <remarks>
    /// 📌 綁帶寶箱（藏寶庫）刻意不算——它不在遊戲的深牢寶箱清單裡，
    /// 混進來會讓「地圖說有幾個」與「看到幾個」對不起來。
    /// </remarks>
    private static int ChestSlotForOID(uint oid) =>
        BronzeChestIDs.Contains(oid) ? 0
        : oid == (uint)OID.SilverCoffer ? 1
        : oid == (uint)OID.GoldCoffer ? 2
        : -1;

    /// <summary>這一格有房間（旗標非零）而且還沒被探索過。</summary>
    private static bool IsUnexploredRoom(RoomFlags d) => (byte)d > 0 && !d.HasFlag(RoomFlags.Revealed);

    /// <summary>
    /// 「前往下一層前探索所有房間」下一間要去哪。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 原本是 <c>Array.FindIndex</c>，也就是<b>房號最小</b>的那一間未探索房。房號是 5x5 格子的
    /// 列優先編號，與「離我多遠」沒有任何關係 ⇒ 全清模式會在地圖上來回橫跳
    /// （走到 23 號房，然後因為 2 號房剛冒出來又整條走回去）。
    /// 改成拿 <see cref="FloorPathfind"/>（同一張房間圖、同一組活的連通旗標）做 BFS，
    /// 挑<b>路徑距離最近</b>的那一間。
    /// </para>
    /// <para>
    /// 🔴 <b>降級語意＝退回原本的行為，不是退回「不動」。</b>兩種情況會退回 <c>Array.FindIndex</c>：
    /// ①遊戲還沒回報本人在哪一間房（剛換層），②BFS 一間未探索房都走不到——
    /// 中間那幾間自己也還沒探索，拿不到它們的連通旗標，這在一層剛開始時是<b>常態</b>而不是錯誤
    /// （<see cref="HandleFloorPathfind"/> 對同一件事的註解也是這樣寫的）。
    /// 所以最壞情況就是今天的行為。
    /// </para>
    /// <para>
    /// ⚠️ 這裡<b>不</b>判斷「實際上走不走得過去」——那是 <see cref="HandleFloorPathfind"/> 的事。
    /// 兩邊讀的是同一張圖，所以不會出現「這裡說可以、那裡說不行」。
    /// </para>
    /// </remarks>
    private int FindNextUnexploredRoom(Actor player)
    {
        var playerRoom = FindPlayerRoom(player);
        if (playerRoom >= 0)
        {
            var nearest = new FloorPathfind(Palace.Rooms).NearestRoom(playerRoom, IsUnexploredRoom);
            if (nearest > 0)
                return nearest;
        }
        // 退回原本的「房號最小」——呼叫端的 `> 0` 閘門與以前逐字相同
        return Array.FindIndex(Palace.Rooms, IsUnexploredRoom);
    }

    private void HandleFloorPathfind(Actor player, AIHints hints)
    {
        var playerRoom = FindPlayerRoom(player);
        // 找不到本人就不要瞎猜起點——寧可這一幀不給方向提示，也不要從錯的房間算路線
        if (playerRoom < 0)
            return;

        if (DesiredRoom == playerRoom || DesiredRoom == 0)
        {
            DesiredRoom = 0;
            _destinationSource = DestinationSource.User;
            _lastPathfindFailed = false;
            return;
        }

        var path = new FloorPathfind(Palace.Rooms).Pathfind(playerRoom, DesiredRoom);
        _lastPathfindFailed = path.Count == 0;
        if (path.Count == 0)
        {
            // expected while the connecting rooms haven't been explored/revealed yet - only log once per (from, to) pair to avoid spamming every frame
            if (_lastPathfindFailureLogged != (playerRoom, DesiredRoom))
            {
                _lastPathfindFailureLogged = (playerRoom, DesiredRoom);
                Service.Log($"uh-oh, no path from {playerRoom} to {DesiredRoom}");
            }
            return;
        }
        _lastPathfindFailureLogged = (-1, -1);
        var next = path[0];
        Direction d;
        if (next == playerRoom + 1)
            d = Direction.East;
        else if (next == playerRoom - 1)
            d = Direction.West;
        else if (next == playerRoom + 5)
            d = Direction.South;
        else if (next == playerRoom - 5)
            d = Direction.North;
        else
        {
            Service.Log($"pathfinding instructions are nonsense: {string.Join(", ", path)}");
            DesiredRoom = 0;
            return;
        }

        // 🔑 趕路場的權重原本固定是 10，壓過場上所有戰鬥走位（風箏 0.05、閃避偏好 0.5…）。
        //    MaxPull > 1 時這會變成「被兩隻怪咬著仍然全速趕路」——使用者設定的是
        //    「還能再拉幾隻」，不是「戰鬥中也照跑」。戰鬥中把權重降到 0.5，
        //    讓它退成一個溫和的方向偏好，戰鬥走位重新拿回主導權。
        //    ⚠️ MaxPull == 0 的人完全不受影響：那種設定下戰鬥中根本不會走到這裡。
        var travelWeight = player.InCombat ? 0.5f : 10f;
        // 仇恨迴避的前提就是「真的在趕路」，而能跑到這裡就代表所有趕路閘門都過了。
        _travelGoalAdded = true;
        hints.GoalZones.Add(p =>
        {
            var pp = player.Position;
            var improvement = d switch
            {
                Direction.North => pp.Z - p.Z,
                Direction.South => p.Z - pp.Z,
                Direction.East => p.X - pp.X,
                Direction.West => pp.X - p.X,
                _ => 0,
            };
            return improvement > 10f ? travelWeight : 0f;
        });

        AddDoorWaypoints(player, hints, playerRoom, next, d, travelWeight);
    }

    #region 仇恨迴避

    /// <summary>這一幀 <see cref="HandleFloorPathfind"/> 真的加了趕路目標區。</summary>
    /// <remarks>
    /// 🔴 仇恨迴避必須靠在這上面，不能只看 <c>canNavigate</c>：沒有趕路目標區的時候，
    /// 負權重是場上唯一非零的東西，於是「站在圈內」會變成唯一的減分項 ⇒ 角色會在原地
    /// 漂走去遠離被動怪。那是一個沒人要求的行為，而且它的失敗形式是「不聲不響自己走掉」。
    /// </remarks>
    private bool _travelGoalAdded;

    /// <summary>這一層已經記過仇恨迴避的啟用診斷；255＝還沒記過。</summary>
    private byte _aggroAvoidLoggedFloor = 255;

    /// <summary>這一層已經記過「避不開」的診斷；255＝還沒記過。</summary>
    private byte _aggroCrossLoggedFloor = 255;

    /// <summary>
    /// 同一幀最多納入幾隻怪的仇恨圈。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>每幀成本</b>的閘門，不是美觀問題。目標區函式會被 <c>RasterizeGoalZones</c>
    /// 對<b>每一個像素的兩個角</b>各呼叫一次（而且分兩輪 pass），120×120 的圖就是每幀幾萬次；
    /// 乘上怪數就是直接的倍數。一層不乏 20~30 隻怪，全部加進去是每幀百萬次距離運算。
    /// <para>
    /// 🔑 取 6 的理由：尋路視窗只有 ±30y，能真正堵住路線的就是最近那幾隻；比較遠的那些
    /// 在走到之前每幀都會重新排序，所以並不會被永久忽略。漏掉的失敗方向也是安全的：
    /// 沒包到的怪就退回今天的行為（直穿）。
    /// </para>
    /// </remarks>
    private const int MaxAggroCircles = 6;

    /// <summary>
    /// 視覺怪扇形的<b>半角</b>（弧度）＝45°。
    /// </summary>
    /// <remarks>
    /// 🔑 NecroLens 的 <c>ESPObject.SightRadian</c> 是 <c>1.571</c>（π/2），那是<b>全角</b>：
    /// 它的 <c>DrawConeFromCenterPoint</c> 從 <c>Rotation + π/4</c> 掃到
    /// <c>Rotation + π/4 - 1.571</c> ＝ <c>Rotation - π/4</c>，所以中心線是 <c>Rotation</c>、
    /// 半角是 π/4。**照抄 1.571 當半角會畫出兩倍寬的扇形**，而那個錯誤是靜默的
    /// （只會表現成「繞得比預期遠」）。
    /// </remarks>
    private static readonly Angle SightHalfAngle = 45f.Degrees();

    /// <summary>
    /// 死者宮殿擬態怪的偵測距離加成（碼）：在設定的半徑之上再加這麼多。
    /// </summary>
    /// <remarks>
    /// 📌 移植 NecroLens <c>ESPObject.AggroDistance()</c>：
    /// <c>HitboxRadius + (Type == ESPType.Mimic &amp;&amp; DeepDungeonUtil.InPotD ? 14f : 10f)</c>
    /// —— 也就是<b>只有死者宮殿的擬態怪</b>是 hitbox+14，其餘一律 hitbox+10。
    /// 上游的原始註解寫「Expect PotD Mimics ... 14.6」，14 是它取的保守值。
    /// <para>
    /// 🔑 這裡刻意寫成<b>加成 4</b> 而不是絕對值 14：使用者的半徑是可調的（0~20），
    /// 寫死 14 會讓「把滑條調到 15 以上」的人得到<b>擬態怪比一般怪還寬鬆</b>的怪結果。
    /// 半徑維持預設 10 時，這裡算出來就是 14，與 NecroLens 逐字相同。
    /// </para>
    /// <para>
    /// ⚠️ 天之御柱與正統優雷卡<b>不吃</b>這個加成（NecroLens 也不吃）——
    /// 上游只對死者宮殿量到 14.6 這個值。
    /// </para>
    /// </remarks>
    private const float MimicExtraRadius = 4f;

    /// <summary>
    /// 這個 <see cref="Actor.OID"/> 是不是深牢的擬態怪。
    /// </summary>
    /// <remarks>
    /// 🔴 鍵是 <b><c>BNpcBase</c></b>（BMR 的 <c>Actor.OID</c> ＝ <c>GameObject.BaseId</c>），
    /// <b>不是</b> <c>BNpcName</c>（那是 <c>Actor.NameID</c>，<see cref="MobAggroData"/> 用的鍵）。
    /// 兩者搞混會靜默全 miss。
    /// <para>
    /// 📌 值逐字對應 NecroLens <c>DataIds.MimicIDs</c>（含它 2026-08-13 補滿整個區塊那次修正）。
    /// 呼叫端有死者宮殿的閘門，所以實務上只有 5831~5835 那一段會命中；其餘區塊保留是為了
    /// 兩邊的表能逐字對照，將來哪個副本也要加成時不必重新考古。
    /// </para>
    /// <para>
    /// 📌 5831~5835 的 <c>ModelChara</c> 在台服 <c>exd-tc/7.20/BNpcBase.csv</c> 是
    /// 648/648/648/1526/1527（寶箱模型，銀 1526、金 1527），與 NecroLens 記錄的驗證一致。
    /// </para>
    /// </remarks>
    private static bool IsMimicOid(uint oid) => oid switch
    {
        2566u or 6362u or 6363u => true,        // 上游既有值（寶藏地圖迷宮一帶），來源不明
        >= 5831u and <= 5835u => true,          // 死者宮殿
        >= 9042u and <= 9051u => true,          // 天之御柱
        >= 15996u and <= 16005u => true,        // 正統優雷卡
        18889u or 18890u => true,               // 巡禮道
        _ => false,
    };

    /// <summary>
    /// 迴避形狀被抑制多久（秒）。見 <see cref="_aggroSuppressUntil"/>。
    /// </summary>
    private const double AggroSuppressSeconds = 6d;

    /// <summary>「原地不動」判定：這麼多秒內位移不到 <see cref="AggroStuckDistance"/> 就算卡住。</summary>
    private const double AggroStuckSeconds = 2.5d;

    /// <summary>「原地不動」判定的位移門檻（碼）。</summary>
    private const float AggroStuckDistance = 1.5f;

    /// <summary>
    /// 「互動中暫停卡住計時」最多能連續暫停多久（秒）——保險絲，見 <see cref="IsInteracting"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 這條保險絲修的是「我挑錯 Condition flag」這個風險：只要有任何一個旗標在深牢趕路時
    /// 是<b>長期為真</b>的，暫停就會變成永久，<b>安全裝置 #2 直接死掉</b>而且完全沒有徵兆。
    /// 有了上限，最壞情況退回成「抑制晚了 15 秒才生效」，仍然是有界的。
    /// </remarks>
    private const double AggroInteractPauseCap = 15d;

    /// <summary>目前這一段「互動中」是什麼時候開始的；<c>default</c>＝現在不在互動。</summary>
    private DateTime _aggroInteractSince;

    /// <summary>這一段互動已經燒斷保險絲（超過 <see cref="AggroInteractPauseCap"/>）。</summary>
    private bool _aggroInteractFuseBlown;

    /// <summary>這一層已經記過「互動中暫停計時」的診斷；255＝還沒記過。</summary>
    private byte _aggroInteractLoggedFloor = 255;

    /// <summary>
    /// 玩家現在是不是正在做一件「站著不動是正常的」的事（開寶箱、用魔陶器／魔石…）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>2026-08-18 實機回報：22 次「讓路」裡有 15 次是互動誤判。</b>
    /// 安全裝置 #2 量的是「有沒有真的在前進」，而開箱／用道具的那兩三秒位移正好是 0
    /// ⇒ 被當成「繞不過去」，把迴避整個關掉 6 秒。那 6 秒剛好就是離開寶箱繼續趕路的那段。
    /// <para>
    /// 🔑 修法刻意選<b>暫停計時</b>而不是「互動中跳過抑制判定」：互動與卡死的因果方向
    /// 目前<b>沒有離線證據</b>（也可能是先卡住、才順手去開箱）。暫停計時的最壞情況只是
    /// <b>抑制晚幾秒才觸發</b>；「跳過判定」的最壞情況是<b>抑制永遠不觸發</b> ＝ 真的卡死時
    /// 站在禁區邊緣不動。前者可回復，後者不可。
    /// </para>
    /// <para>
    /// ⚠️ 旗標集合是<b>推定</b>的（見報告）：台服深牢開箱到底會亮哪幾個 <c>ConditionFlag</c>
    /// 沒有離線證據。挑得太少＝誤判照舊（現況，不會更糟）；挑到一個長期為真的＝安全裝置死掉，
    /// 所以另外有 <see cref="AggroInteractPauseCap"/> 這條保險絲兜底。
    /// <c>CastInfo</c> 一併算進來，是因為魔陶器／魔石是走「使用道具」的詠唱，不是事件互動。
    /// </para>
    /// </remarks>
    private static bool IsInteracting(Actor player)
        => player.CastInfo != null
        || Service.Condition[ConditionFlag.Occupied]
        || Service.Condition[ConditionFlag.Occupied30]
        || Service.Condition[ConditionFlag.OccupiedInEvent]
        || Service.Condition[ConditionFlag.OccupiedInQuestEvent]
        || Service.Condition[ConditionFlag.Occupied33]
        || Service.Condition[ConditionFlag.Occupied38]
        || Service.Condition[ConditionFlag.Occupied39];

    /// <summary>抑制到這個時間點之前都不加迴避形狀；<c>default</c>＝沒在抑制。</summary>
    private DateTime _aggroSuppressUntil;

    /// <summary>上次取樣的位置與時間，用來判「有沒有真的在前進」。</summary>
    private (WPos Pos, DateTime At)? _aggroProgressSample;

    /// <summary>
    /// 上一次真的評估過的那一幀，玩家人在誰的偵測形狀裡（<c>InstanceID</c>）。
    /// </summary>
    /// <remarks>
    /// 🔴 這份名單存在的唯一理由是<b>讓 v2/v3 可以被證偽</b>。<c>.77</c>（負權重版）有一行
    /// 「趕路中進入了『某某』的仇恨圈（距離 X）」的 Information，那行就是證明負權重是死碼的
    /// 唯一證據（九層全中、進圈距離清一色 9.4~9.5y ＝ 路線一度都沒彎過）。
    /// <c>.78</c> 改禁區時連同整段判定一起刪掉，於是「禁區到底有沒有讓路線彎」變成無法觀測。
    /// <para>
    /// ⚠️ 這裡刻意用 <see cref="List{T}"/> 而不是 <c>HashSet</c>：元素最多
    /// <see cref="MaxAggroCircles"/> 個，線性掃比雜湊便宜，而且
    /// <c>HashSet.RemoveWhere</c> 需要一個捕捉 <c>this</c> 的 lambda ＝ <b>每幀一次委派配置</b>。
    /// </para>
    /// <para>
    /// ⚠️ 早退的那幾條路徑（設定關閉／不在趕路／抑制中）<b>刻意不清空這份名單</b>：
    /// <see cref="_travelGoalAdded"/> 在正常一趟裡本來就會斷斷續續（例如走去開箱那幾幀），
    /// 清空會讓同一隻怪反覆重印 ＝ 洗版。只有換層（<c>ResetFloorState</c>）才歸零。
    /// </para>
    /// </remarks>
    private readonly List<ulong> _aggroInside = [];

    /// <summary>本幀重算的「人在誰的形狀裡」，用來和 <see cref="_aggroInside"/> 對帳。</summary>
    private readonly List<ulong> _aggroInsideFrame = [];

    /// <summary>
    /// 趕路時把「還沒發現我的怪」的偵測範圍畫成<b>禁區</b>，讓路線真的繞開。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴🔴 <b>2026-08-17 全部重寫：上一版用負權重 <c>GoalZones</c>，實機證明是死碼。</b>
    /// 實機 log（天之御柱 31~39 層）九層全中、進圈距離清一色 9.4~9.5y ＝路線一度都沒彎過。
    /// 根因：<c>PixelPriority</c> <b>完全不參與路徑成本</b>。它只出現在兩個地方——
    /// <c>ThetaStar.CalculateScore</c> 的<b>目的地</b>分級，以及 <c>PrefillH</c> 把
    /// 「權重 ＝ <c>MaxPriority</c> 的格子」當成 A* 的目標集合（H=0）。
    /// 每一步的成本 <c>VisitNeighbour</c> 用的是純幾何的 <c>_deltaGSide</c>／<c>_deltaGDiag</c>。
    /// ⇒ 負權重只能改變「瞄哪一格」，改不了「怎麼走過去」，而走法永遠是最短路徑。
    /// </para>
    /// <para>
    /// 🔑 <b>唯一能讓路徑偏折的機制是 <c>PixelMaxG</c>（也就是禁區）</b>，因為只有它會沿路徑
    /// 累積：<c>VisitNeighbour</c> 把 <c>PathLeeway = min(父的 leeway, min(destG, parentG) - 累積G)</c>
    /// 一路帶下去，<c>leeway &lt;= 0</c> 就讓 <c>CalculateScore</c> 從 <c>SafeMaxPrio</c>(9)
    /// 掉到 <c>UltimatelySafe</c>(2)。分級是 <c>CompareNodeScores</c> 的第一比較鍵，
    /// 壓過距離 ⇒ <b>繞得過去時一定選繞的</b>。這就是既有 AOE 閃避讓路徑彎曲的同一條路。
    /// </para>
    /// <para>
    /// 🔴 <b>三個安全裝置，缺一個就會踩到已知的壞行為</b>：
    /// <list type="number">
    /// <item><b>玩家已經在裡面的形狀一律不加</b>。這是為了 <c>NavigationDecision.Build</c> 的
    /// <c>if (playerInWindow &amp;&amp; PixelMaxG[playerCell] is &gt;= 1f or &lt; 0f)</c>——
    /// 玩家格落在禁區（g=0）時<b>所有目標區整批不畫</b>，導航退成純逃離模式，
    /// 表現是「不趕路了，自己往後跑」。跳過含玩家的形狀讓玩家格永遠是 <c>float.MaxValue</c>，
    /// 這條分支<b>結構上</b>進不去。副作用剛好也是想要的語意：<b>已經站在人家眼皮下就別再閃，直接走</b>。</item>
    /// <item><b>卡住就抑制</b>。禁區是硬的，所以「繞不過去」的自然結果是走到邊緣停住——
    /// 那違反「繞不開必須照走」。所以量「有沒有真的在前進」：<see cref="AggroStuckSeconds"/> 秒內
    /// 位移不到 <see cref="AggroStuckDistance"/>y 就把迴避整個關掉 <see cref="AggroSuppressSeconds"/> 秒，
    /// 讓它照舊行為直穿。抑制期間有滯後（一次關滿 6 秒）所以不會逐幀開關 ＝ 不震盪。
    /// ⚠️ <b>互動期間（開箱／用魔陶器…）暫停這個計時</b>，否則站著開箱的兩三秒會被當成卡死
    /// （實機回報 22 次讓路裡 15 次是這樣來的）。見 <see cref="IsInteracting"/>。</item>
    /// <item><b>只在趕路的那些幀</b>（<see cref="_travelGoalAdded"/>）而且非戰鬥。
    /// 戰鬥中一根汗毛都不動，陷阱迴避與風箏不受影響。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 🔑 <b>形狀分兩種，這才是使用者點出「怪物有面對方向」的價值</b>：視覺怪
    /// （<c>MobAggroKind.Sight</c>，表裡 439/695）用<b>面向扇形</b>——頂點在怪身上、
    /// 中心線就是牠的朝向、<b>半角 45°</b>（NecroLens 的 <c>SightRadian</c> 1.571 是<b>全角</b>，
    /// 它的繪製碼從 <c>Rotation + π/4</c> 掃到 <c>Rotation - π/4</c>，所以中心線是 <c>Rotation</c>）。
    /// 聽覺與接近用整個圓。表裡查不到的退回整個圓（保守）。
    /// 扇形的面積只有圓的四分之一，所以走廊裡「貼牆／繞背」變成可行解——
    /// 這同時也大幅降低上面第 2 個裝置被觸發的機率。
    /// </para>
    /// <para>
    /// 📌 <b>每幀成本</b>：禁區函式被 <c>RasterizeForbiddenZones</c> 對每個像素的兩個角各呼叫一次
    /// （兩輪 pass），與目標區同量級。扇形比圓多一次 <c>atan2</c>／角度正規化，
    /// 大約是圓的兩到三倍常數成本，所以 <see cref="MaxAggroCircles"/> 維持 6 不放寬。
    /// </para>
    /// </remarks>
    private void AddAggroAvoidance(Actor player, AIHints hints)
    {
        if (Config.AggroAvoidRadius <= 0f || !Config.AggroAvoid || !_travelGoalAdded || player.InCombat)
        {
            _aggroProgressSample = null;
            _aggroInteractSince = default;
            _aggroInteractFuseBlown = false;
            return;
        }

        var now = World.CurrentTime;
        if (_aggroSuppressUntil != default && now < _aggroSuppressUntil)
            return; // 抑制期間：完全不加形狀＝逐字照舊行為

        var pullTargetId = player.TargetID;
        var radius = Config.AggroAvoidRadius;

        Span<int> picked = stackalloc int[MaxAggroCircles];
        Span<float> pickedDist = stackalloc float[MaxAggroCircles];
        var pickedCount = 0;

        var countTargets = hints.PotentialTargets.Count;
        for (var i = 0; i < countTargets; ++i)
        {
            var a = hints.PotentialTargets[i].Actor;
            // 已仇恨的不畫：那是戰鬥，不是要迴避的東西。
            if (a.AggroPlayer || a.InCombat || a.IsDeadOrDestroyed)
                continue;
            if (a.InstanceID == pullTargetId)
                continue;
            if (!hints.PathfindMapBounds.Contains(a.Position - hints.PathfindMapCenter))
                continue;

            var d = player.DistanceToHitbox(a);
            if (pickedCount < MaxAggroCircles)
            {
                picked[pickedCount] = i;
                pickedDist[pickedCount] = d;
                ++pickedCount;
            }
            else
            {
                var worst = 0;
                for (var j = 1; j < MaxAggroCircles; ++j)
                {
                    if (pickedDist[j] > pickedDist[worst])
                        worst = j;
                }
                if (d < pickedDist[worst])
                {
                    picked[worst] = i;
                    pickedDist[worst] = d;
                }
            }
        }

        int cones = 0, circles = 0, unknown = 0, skippedInside = 0, mimics = 0;
        var pp = player.Position;
        _aggroInsideFrame.Clear();
        // 死者宮殿的擬態怪偵測距離比別的怪遠，見 MimicExtraRadius。每幀算一次就好。
        var inPotd = Palace.DungeonId == DeepDungeonState.DungeonType.POTD;

        for (var i = 0; i < pickedCount; ++i)
        {
            var a = hints.PotentialTargets[picked[i]].Actor;
            var isMimic = inPotd && IsMimicOid(a.OID);
            if (isMimic)
                ++mimics;
            var effRadius = isMimic ? radius + MimicExtraRadius : radius;
            var r = a.HitboxRadius + effRadius;
            var kind = MobAggroData.Lookup(a.NameID);

            Func<WPos, float> shape;
            if (kind == MobAggroKind.Sight)
            {
                // 半角 45°：NecroLens 的 SightRadian(1.571) 是全角，中心線是怪的朝向。
                shape = ShapeDistance.Cone(a.Position, r, a.Rotation, SightHalfAngle);
            }
            else
            {
                shape = ShapeDistance.Circle(a.Position, r);
            }

            // 🔴 安全裝置 #1：玩家已經在裡面就不要加這一個形狀（理由見 remarks）。
            //    ShapeDistance 的慣例是「負值＝在裡面」。
            if (shape(pp) <= 0f)
            {
                ++skippedInside;
                // 📌 進圈量測：每隻怪每次「進入」只印一行，離開之後再進去才會再印。
                //    這是唯一能證偽「禁區真的讓路線彎開了」的觀測值 —— 見 _aggroInside 的 remarks。
                _aggroInsideFrame.Add(a.InstanceID);
                if (!_aggroInside.Contains(a.InstanceID))
                {
                    _aggroInside.Add(a.InstanceID);
                    var shapeName = kind switch
                    {
                        MobAggroKind.Sight => "扇形偵測範圍（半角 45°／全角 90°）",
                        null => "圓形偵測範圍（表外退回圓）",
                        _ => "圓形偵測範圍",
                    };
                    Service.Logger.Information(
                        $"[DD] 仇恨迴避進圈：樓層 {Palace.Floor}，趕路中進入了「{a.Name}」" +
                        $"（OID {a.OID:X}）的{shapeName}（距離 {pickedDist[i]:f1}y、" +
                        $"半徑 hitbox+{effRadius:f1}y{(isMimic ? "，死者宮殿擬態怪加成" : "")}）。" +
                        "本幀起這個形狀被跳過（安全裝置 #1）。");
                }
                continue;
            }

            if (kind == MobAggroKind.Sight)
                ++cones;
            else if (kind == null)
                ++unknown;
            else
                ++circles;

            // activation 用預設值：RasterizeForbiddenZones 會把它夾到 current ⇒ g = 0 ⇒ 真的擋。
            hints.AddForbiddenZone(shape, default, a.InstanceID);
        }

        // 「離開了就重置」：本幀沒再算到人在裡面的，從名單移除，下次再進去會重新印一行。
        // ⚠️ 怪死掉／離開尋路視窗／被更近的六隻擠出候選名單，在這裡都算「離開」——
        //    最壞情況只是同一隻怪多印一行，不會漏印。
        for (var i = _aggroInside.Count - 1; i >= 0; --i)
        {
            if (!_aggroInsideFrame.Contains(_aggroInside[i]))
                _aggroInside.RemoveAt(i);
        }

        var added = cones + circles + unknown;
        if (added == 0)
        {
            _aggroProgressSample = null;
            return;
        }

        // 🔴 安全裝置 #2：量「有沒有真的在前進」，卡住就抑制（不是偵測不可達，是偵測結果）。
        //    ⚠️ 互動期間（開箱／用魔陶器…）站著不動是正常的，那段時間不累積 stuck 時間，
        //       互動結束從零重計。理由與取捨見 IsInteracting 的 remarks。
        var interacting = IsInteracting(player);
        if (interacting)
        {
            if (_aggroInteractSince == default)
                _aggroInteractSince = now;
            else if ((now - _aggroInteractSince).TotalSeconds >= AggroInteractPauseCap)
            {
                // 保險絲燒斷：訊號黏住了，不再暫停，讓安全裝置 #2 照常跑。
                interacting = false;
                if (!_aggroInteractFuseBlown)
                {
                    _aggroInteractFuseBlown = true;
                    Service.Logger.Information(
                        $"[DD] 仇恨迴避互動暫停逾時：樓層 {Palace.Floor}，" +
                        $"「互動中」訊號已經連續為真超過 {AggroInteractPauseCap:f0} 秒 ⇒ " +
                        "判定是旗標挑錯（黏住），停止暫停、恢復卡住偵測。");
                }
            }
        }
        else
        {
            _aggroInteractSince = default;
            _aggroInteractFuseBlown = false;
        }

        if (interacting)
        {
            // 暫停＝把取樣基準推到現在，於是這段時間完全不計入「原地不動」。
            _aggroProgressSample = (pp, now);
            if (_aggroInteractLoggedFloor != Palace.Floor)
            {
                _aggroInteractLoggedFloor = Palace.Floor;
                Service.Logger.Information(
                    $"[DD] 仇恨迴避暫停卡住計時：樓層 {Palace.Floor}，偵測到互動中" +
                    $"（開箱／用魔陶器之類），本層首次。互動期間不累積「{AggroStuckSeconds:f1} 秒內" +
                    $"位移不足 {AggroStuckDistance:f1}y」的時間，互動結束從零重計。");
            }
        }
        else if (_aggroProgressSample is { } sample)
        {
            if ((pp - sample.Pos).LengthSq() >= AggroStuckDistance * AggroStuckDistance)
            {
                _aggroProgressSample = (pp, now); // 有前進，重新起算
            }
            else if ((now - sample.At).TotalSeconds >= AggroStuckSeconds)
            {
                _aggroSuppressUntil = now.AddSeconds(AggroSuppressSeconds);
                _aggroProgressSample = null;
                Service.Logger.Information(
                    $"[DD] 仇恨迴避讓路：樓層 {Palace.Floor}，{AggroStuckSeconds:f1} 秒內只移動了不到 " +
                    $"{AggroStuckDistance:f1}y（本幀 {added} 個迴避形狀）⇒ 判定繞不過去，" +
                    $"接下來 {AggroSuppressSeconds:f0} 秒不迴避、照舊行為直穿。");
                return;
            }
        }
        else
        {
            _aggroProgressSample = (pp, now);
        }

        // 「避不開」的實證資料：趕路中我方已經站在某隻未仇恨怪的偵測範圍內。
        // 🔑 這一版它不再是失敗（形狀被跳過是刻意的安全裝置 #1），但它仍然是
        //    「這一層有沒有真的閃得開」的觀測值，也是 v2 決定門檻的依據，所以保留。
        //    每層一行，不每幀洗版。
        if (skippedInside > 0 && _aggroCrossLoggedFloor != Palace.Floor)
        {
            _aggroCrossLoggedFloor = Palace.Floor;
            Service.Logger.Information(
                $"[DD] 仇恨迴避避不開：樓層 {Palace.Floor}，趕路中已經站在 {skippedInside} 隻未仇恨怪的" +
                $"偵測範圍內（那些形狀本幀刻意跳過，避免玩家格落進禁區讓目標區整批消失）。");
        }

        if (_aggroAvoidLoggedFloor != Palace.Floor)
        {
            _aggroAvoidLoggedFloor = Palace.Floor;
            Service.Logger.Information(
                $"[DD] 仇恨迴避啟用：樓層 {Palace.Floor}，半徑 hitbox+{radius:f1}y（禁區，非軟成本）、" +
                $"本幀 {added} 個形狀＝扇形 {cones} 隻／圓 {circles} 隻／表外退回圓 {unknown} 隻" +
                $"，另有 {skippedInside} 隻因為玩家已在其範圍內而跳過（上限 {MaxAggroCircles}）" +
                $"；候選裡有 {mimics} 隻死者宮殿擬態怪（半徑另加 {MimicExtraRadius:f0}y）。");
        }
    }

    #endregion

    #region 門口路點鏈

    /// <summary>
    /// 走廊「車道」的半寬（碼）。
    /// </summary>
    /// <remarks>
    /// 這不是量到的走廊寬度，是<b>偏好帶</b>的寬度：帶內的格子多拿一份權重，帶外照舊。
    /// 取寬一點是刻意的——真正的走廊比這窄的話，整條走廊都在帶內（正確）；
    /// 比這寬的話，最多只是偏好走中間（無害）。
    /// </remarks>
    private const float LaneHalfWidth = 6f;

    /// <summary>
    /// 路點的「到了」半徑（碼）：走到這麼近就把那個路點<b>撤掉</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 這是防呆的核心。路點是平台函式，站在平台上時整片權重相同 ⇒ 沒有東西叫角色繼續往前，
    /// 表現會是「走到門口就停住不動」（然後被卡住偵測當成障礙物地圖對不上）。
    /// 撤掉之後最高權重回到「往前走」那一片，角色自然接著走。
    /// 撤除半徑刻意大於平台半徑，避免在邊界上一幀加一幀撤。
    /// </remarks>
    private const float WaypointArriveRadius = 6f;

    private const float FarDoorRadius = 5f;
    private const float NextCenterRadius = 10f;

    /// <summary>路點權重相對趕路權重的倍率。</summary>
    /// <remarks>
    /// 🔴 <b>每一個都必須小於 1</b>：這樣任何單一路點都壓不過「往前走」那一片，
    /// 也就不可能出現「因為某個路點而停在原地」。它們只在<b>同時也是往前的格子</b>上疊加生效。
    /// </remarks>
    private const float WaypointWeightFactor = 0.5f;

    /// <summary>沿行進方向的分量（正＝朝那個方向）。</summary>
    private static float AlongAxis(Direction d, WDir off) => d switch
    {
        Direction.North => -off.Z,
        Direction.South => off.Z,
        Direction.East => off.X,
        _ => -off.X,
    };

    /// <summary>垂直於行進方向的分量。</summary>
    private static float AcrossAxis(Direction d, WDir off) => d is Direction.North or Direction.South ? off.X : off.Z;

    private static Wall DoorOnSide(RoomData<Wall> room, Direction d) => d switch
    {
        Direction.North => room.North,
        Direction.South => room.South,
        Direction.East => room.East,
        _ => room.West,
    };

    private static Direction Opposite(Direction d) => d switch
    {
        Direction.North => Direction.South,
        Direction.South => Direction.North,
        Direction.East => Direction.West,
        _ => Direction.East,
    };

    /// <summary>
    /// 某間房在某一側的門口世界座標。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 📌 <b>資料來源就是 <see cref="LoadedFloors"/> 的四個方向欄位</b>——
    /// <see cref="ApplyFace"/> 在「那一側沒有連通旗標」時把同一個值當成關著的門加進
    /// <see cref="Walls"/>，所以它們是門的位置。反過來說，我們要走的那一側<b>一定</b>有連通旗標
    /// （<see cref="FloorPathfind"/> 只走有旗標的邊），因此這個座標<b>不會</b>同時是禁區。
    /// </para>
    /// <para>
    /// 🔴 這裡重跑一次離線閘門（<c>tools/bmr_deepdungeon_doors.py</c> 的 G1／G2）：
    /// 門口相對房中心必須真的落在那一側，而且同軸分量要大於側向分量。
    /// 對不上就回 false ＝這一項完全不加，退回原本的方向啟發式。
    /// 2026-08-10 對 tc-7.20 全部 80 個版面、2240 個門口欄位離線實算：G1 一致率 1.0000
    /// （四向全對調的負對照 0.0000）、G2 同軸支配 96.3%、中心→門口中位 18.2y。
    /// </para>
    /// <para>
    /// ⚠️ 比對的中心用<b>版面表自己的</b> <c>Center</c>，不是 <see cref="RoomCenters"/>——
    /// 後者可能是網格擬合補出來的，拿補值去驗原始值等於用兩把不同的尺。
    /// </para>
    /// </remarks>
    private bool TryGetDoor(int room, Direction side, out WPos door)
    {
        door = default;
        if ((uint)room >= DeepDungeonState.NumRooms)
            return false;

        var face = _activeFace == 0 ? _faceA : _activeFace == 1 ? _faceB : null;
        if (face == null)
            return false;

        var data = face[room];
        if (data.Center == default)
            return false;

        var w = DoorOnSide(data, side);
        if (w == default)
            return false;

        var off = w.Position - data.Center.Position;
        var along = AlongAxis(side, off);
        if (along <= 1f || Math.Abs(AcrossAxis(side, off)) >= along)
            return false;

        door = w.Position;
        return true;
    }

    /// <summary>上一次記錄過的門口路點閘門結果；null＝還沒記過（每層重設）。</summary>
    private bool? _doorGateLogged;

    /// <summary>
    /// 把「下一個門口 → 走廊另一端 → 下一間房中心」這條路點鏈疊到趕路權重場上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>為什麼原本的啟發式不夠</b>：<c>improvement &gt; 10y</c> 是一個半平面平台，
    /// 「往北 10 碼以上」的格子<b>全部同分</b>，所以尋路只會挑最近的那一格，
    /// 橫向要不要對齊門口完全沒有訊號——角色因此常常沿著北牆磨到門口旁邊才轉過去。
    /// 車道那一項就是補上橫向的訊號。
    /// </para>
    /// <para>
    /// 🔴 <b>全部是加法，而且每一項都小於等於趕路權重</b>：
    /// 任一閘門沒過就是少加一項，權重場退回原本的樣子＝今天的行為。
    /// 沒有任何一條路徑會因為這個功能而<b>主動</b>把角色帶去別的地方。
    /// </para>
    /// </remarks>
    private void AddDoorWaypoints(Actor player, AIHints hints, int playerRoom, int next, Direction d, float travelWeight)
    {
        // 🔴 前置＝座標校驗閘門。沒過代表這一層的寫死座標對不上，門口座標一樣不能信。
        var door = default(WPos);
        var gateOk = CheckRoomCoords(player, out _, out _) == RoomCoordState.Ok
            && TryGetDoor(playerRoom, d, out door);

        if (_doorGateLogged != gateOk)
        {
            _doorGateLogged = gateOk;
            Service.Logger.Information(gateOk
                ? $"[DD] 門口路點：樓層 {Palace.Floor} 啟用（版面 {FaceName()}、房間 {playerRoom} 往 {d} 到 {next}）。"
                // 🔑 大廳層是「本來就沒有門」而不是「閘門沒過」，兩者要分得出來——
                //    大廳 12 格互通，門口路點對它沒有意義，只留方向場才是正解。
                : _hallMode
                    ? $"[DD] 門口路點：樓層 {Palace.Floor} 是大廳層（無內牆 12 格），沒有門口座標可用，只保留方向場。"
                    : $"[DD] 門口路點：樓層 {Palace.Floor} 不啟用，退回原本的方向啟發式（座標校驗或門口自我檢查沒過）。");
        }

        if (!gateOk)
            return;

        var pos = player.Position;
        var laneAxisIsX = d is Direction.North or Direction.South;
        var laneCoord = laneAxisIsX ? door.X : door.Z;

        // ① 走廊車道：既要往 d 前進，又要橫向對齊門口。
        //    刻意沿用上面那條「至少再前進 10y」的門檻——車道是套在它上面的濾網，不是另一套規則，
        //    所以車道永遠是「原本就會被接受的格子」的子集。
        hints.GoalZones.Add(p =>
        {
            var pp = player.Position;
            var improvement = d switch
            {
                Direction.North => pp.Z - p.Z,
                Direction.South => p.Z - pp.Z,
                Direction.East => p.X - pp.X,
                Direction.West => pp.X - p.X,
                _ => 0f,
            };
            if (improvement <= 10f)
                return 0f;
            var lateral = Math.Abs((laneAxisIsX ? p.X : p.Z) - laneCoord);
            return lateral <= LaneHalfWidth ? travelWeight * WaypointWeightFactor : 0f;
        });

        // ② 走廊的另一端＝下一間房那一側的門口。實測兩側門口相距約 0.42 倍中心距（中位 ~25y），
        //    也就是說它已經在下一間房裡面，走到那裡遊戲回報的房號就會換。
        if (TryGetDoor(next, Opposite(d), out var farDoor) && !farDoor.InCircle(pos, WaypointArriveRadius))
            hints.GoalZones.Add(hints.GoalSingleTarget(farDoor, FarDoorRadius, travelWeight * WaypointWeightFactor));

        // ③ 下一間房的中心。
        //    ⚠️ 只用版面表真的有的中心，不用網格擬合補出來的（<see cref="_centerFitted"/>）——
        //    2026-08-15 留一法實測補值誤差中位 1.9~11.6y、最糟 28.7y，
        //    拿它當移動路點是把「顯示可以將就」的容忍度誤用到「走位」上。
        if (!_centerFitted[next] && RoomCenters[next] is WPos c && !c.InCircle(pos, NextCenterRadius))
            hints.GoalZones.Add(hints.GoalSingleTarget(c, NextCenterRadius, travelWeight * WaypointWeightFactor));
    }

    #endregion

    /// <summary>
    /// <see cref="RoomCenters"/>／<see cref="Walls"/> 目前載入的是哪一層、哪個版面。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這個記號就沒辦法判斷「該不該重載」。同一組樓層的兩份鏡像版面中心相距約 845 碼，
    /// 沿用上一層的版面會讓所有依賴座標的功能（寶箱點位、各房敵人數、手動導航驗證）
    /// 全部靜默失效。
    /// </remarks>
    private (byte Floor, byte Tileset) _loadedLayout = (255, 255);

    private void LoadWalls()
    {
        Service.Log($"loading walls for current floor...");
        Walls.Clear();
        // 🔴 座標也要一起作廢。半套狀態（新樓層 + 舊座標）比完全沒有資料更糟：
        //    校驗閘門會拿舊座標去比，得到「不通過」而不是「不知道」。
        Array.Clear(RoomCenters);
        ResetCoordGate();
        _loadedLayout = (Palace.Floor, Palace.Progress.Tileset);

        // ── 大廳層（無內牆的稠密 12 格版面）────────────────────────────────
        // 🔴 上游原本在這裡用 `Palace.Progress.Tileset == 2` 判大廳（並印 "hall of fallacies"），
        //    那在台服是**死條件**：2026-08-14 天之御柱 1~40 層實機 log 離線實算，四個大廳層
        //    （15／17／28／34）回報的 Progress.Tileset **全是 0**，那行從頭到尾一次都沒印過。
        //    後果是大廳層照樣去載一般層的 A／B 座標表，而大廳的實體位置在第三象限
        //    （X>0 且 Z>0 —— 全部 80 個版面裡，A 面恆在 X<0/Z>0、B 面恆在 X>0/Z<0，
        //    兩面都不涵蓋大廳），於是座標校驗恆差 665~723y，整層退化成摘要模式。
        //    ⇒ 那個判斷式已移除，改用 MapData 自己的形狀判定（見 <see cref="DetectHall"/>）。
        _hallMode = DetectHall();
        if (_hallMode)
        {
            _hallFloorSince = World.CurrentTime;
            var table = BuildHallCentersFromTable();
            if (table != null)
                ApplyHallFace(table, HallCenterSource.FixedTable, "固定表（天之御柱大廳，離線擬合）");
            else
                Service.Logger.Information(
                    $"[DD] 大廳層偵測到：樓層 {Palace.Floor}、迷宮 {Palace.DungeonId}，" +
                    $"但沒有這個迷宮的大廳格心表 ⇒ 改用本層即時擬合" +
                    $"（要 {HallFitMinCells} 格以上、每格 {HallFitMinSamplesPerCell} 筆以上樣本才會啟用）。");
            // 🔴 大廳層不載一般層的 A／B 表——載了就是「用差 700y 的座標去比」，
            //    而那正是這個功能要修掉的病。
            return;
        }

        var floorset = Palace.Floor / 10;
        var key = $"{(int)Palace.DungeonId}.{floorset + 1}";
        if (!LoadedFloors.Walls.TryGetValue(key, out var floor))
        {
            Service.Log($"unable to load floorset {key}");
            return;
        }

        // 兩面都留著，之後由 CheckRoomCoords 拿本人位置自我校準決定用哪一面。
        _faceA = floor.RoomsA;
        _faceB = floor.RoomsB;

        // 起手仍然照遊戲回報的 Progress.Tileset 選面＝維持既有行為；
        // 認不得的值退回 A 面（以前是整個不載入，那會讓所有依賴座標的功能一起消失）。
        var initial = Palace.Progress.Tileset == 1 ? 1 : 0;
        if (Palace.Progress.Tileset > 1)
            Service.Logger.Information($"[DD] 認不得的版面編號 {Palace.Progress.Tileset}，先套用版面 A，交給座標自我校準判斷。");
        ApplyFace(initial);
    }

    /// <summary>
    /// 把某一面鏡像版面套進 <see cref="RoomCenters"/> 與 <see cref="Walls"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>中心座標與牆壁必須同時換</b>。兩者是同一份版面資料，只換一半就會回到
    /// 「新樓層 + 舊座標」那種半套狀態——閘門會得到「不通過」而不是「不知道」。
    /// </remarks>
    private void ApplyFace(int face)
    {
        var tileset = face == 0 ? _faceA : _faceB;
        if (tileset == null)
            return;

        _activeFace = face;
        Walls.Clear();
        Array.Clear(RoomCenters);
        Array.Clear(_centerFitted);

        var len = Palace.Rooms.Length;
        for (var i = 0; i < len; ++i)
        {
            ref var room = ref Palace.Rooms[i];
            var roomdata = tileset[i];
            // 中心座標不管房間探索了沒都先存下來（索引＝房號）；牆壁仍然只對已知的房間算
            if (roomdata.Center != default)
                RoomCenters[i] = roomdata.Center.Position;

            if (room > 0)
            {
                if (roomdata.North != default && !room.HasFlag(RoomFlags.ConnectionN))
                    Walls.Add((roomdata.North, false));
                if (roomdata.South != default && !room.HasFlag(RoomFlags.ConnectionS))
                    Walls.Add((roomdata.South, false));
                if (roomdata.East != default && !room.HasFlag(RoomFlags.ConnectionE))
                    Walls.Add((roomdata.East, true));
                if (roomdata.West != default && !room.HasFlag(RoomFlags.ConnectionW))
                    Walls.Add((roomdata.West, true));
            }
        }

        FillMissingRoomCenters();
        RoomTolerance = ComputeRoomTolerance();
    }

    #region 房間中心的補值與容許誤差

    /// <summary>這一格的中心是網格擬合補出來的，不是 <see cref="LoadedFloors"/> 真的有的值。</summary>
    private readonly bool[] _centerFitted = new bool[DeepDungeonState.NumRooms];

    /// <summary>
    /// 網格擬合的最大容許殘差（碼）。已知中心對不上線性網格到這個程度就整個不補。
    /// </summary>
    /// <remarks>
    /// 實測房間中心<b>不在完美網格上</b>，所以擬合本來就有殘差；殘差大到某個地步就代表
    /// 這一面根本不是規則網格，補出來的座標會比沒有更糟（會把寶箱畫進錯的房間）。
    /// ⇒ 那時寧可留 null 讓那一格退化成「地圖說有、位置不明」。
    /// <para>
    /// 🔴 <b>原本的 15f 是拍腦袋的值，而且是死鍵</b>：2026-08-15 把
    /// <see cref="LoadedFloors"/> 的 80 個版面全量離線重算（工具
    /// <c>~/.claude/tools/ddmap/gridfit_residual.py</c>，校準閘門之一就是重現實機
    /// log 印過的那筆成功擬合 <c>X=-402.1+53.3·col／Z=186.2+56.9·row／殘差 14.1y</c>），
    /// 最大殘差分布是 <b>min 0.0／p50 19.9／p90 23.2／max 26.5y</b> ——
    /// 15f 讓 <b>70/80 個版面直接放棄</b>，實機 log 也對得上（兩份 log 共 154 行擬合訊息，
    /// <b>150 行是「放棄」</b>、只有 4 行成功，而那 4 行全是王層的幽靈補值）。
    /// </para>
    /// <para>
    /// 📌 <b>30f 的依據</b>：①它蓋得住整個實測母體（26.5y）還留一點餘裕，
    /// 而 30f 以上沒有任何資料可以講，所以不再往上放；
    /// ②品質用留一法實測過——把每個已知房心輪流拿掉、用其餘的擬合回頭預測它，
    /// 1680 個留一房的補值誤差中位 1.9~11.6y（最糟那一個 28.7y），
    /// 再把補出來的座標放回 <see cref="NearestRoom"/> 的歸屬裡實算面積：
    /// <b>1680/1680 全部「救回的面積 &gt; 偷走的面積」</b>，
    /// 殘差 25~30y 那一桶的救回率中位仍有 83.5%。
    /// 判準不是「殘差夠不夠小」，是<b>不補的話那一格是 100% 歸屬錯</b>，所以補歪一點仍然淨賺。
    /// </para>
    /// </remarks>
    private const float GridFitMaxResidual = 30f;

    /// <summary>5×5 房間格的四個角（房號 0／4／20／24）。</summary>
    /// <remarks>
    /// 🔴 <b>這四格不是「沒 dump 到的房間」，是這張地圖根本沒有的格子</b>，所以永遠不補：
    /// <list type="bullet">
    /// <item>離線：<see cref="LoadedFloors"/> 的 <b>80 個版面（3 迷宮 × 40 組樓層 × 2 面）
    /// 全部</b>剛好 21 格有座標，缺的<b>永遠</b>是這四角，沒有任何一個版面缺別的格子。</item>
    /// <item>實機：台服 242 行 <c>MapData</c> 原值（193 非王層／21 王層／28 個樓層 0）裡，
    /// <b>193 個非王層對四角一次都沒設過旗標</b>；
    /// 唯一會設的是<b>王層</b>——王層的地圖是退化的兩格
    /// （<c>[0]=0x22 ConnectionS|Passage</c>、<c>[5]=0xC1 ConnectionN|Home|Revealed</c>），
    /// 房號 0 在那裡是「往下一層的傳送點」而不是 5×5 網格上的房間。</item>
    /// </list>
    /// ⇒ 舊碼的 <c>(byte)Palace.Rooms[i] == 0</c> 那一條擋不住王層的房號 0，
    /// 於是實機在天之御柱 60／70 層真的補出過一格<b>幽靈房心</b>
    /// （而且王層載到的還是<c>下一組</c>樓層的座標表，因為 key 用的是 <c>Floor / 10</c>）。
    /// 放寬門檻之前一定要先把這一條補上，否則幽靈房心會從 10/80 個版面擴散到 80/80。
    /// </remarks>
    private static bool IsGridCorner(int room) => room is 0 or 4 or 20 or 24;

    /// <summary>上一次印出來的網格擬合結局；一樣就不再印（翻轉才印）。</summary>
    /// <remarks>
    /// ⚠️ 刻意<b>不</b>在 <see cref="ResetCoordGate"/> 裡清掉：清了就會變成每層都印一次同樣的話
    /// （實機一份 log 曾經連印 33 次一模一樣的放棄訊息）。
    /// 樓層／版面的上下文不會因此遺失——同一個 <see cref="ApplyFace"/> 裡
    /// <see cref="ComputeRoomTolerance"/> 那行本來就每次都帶著樓層與版面印出來。
    /// </remarks>
    private string? _gridFitLogged;

    /// <summary>
    /// 用已知的房間中心做 5×5 線性網格擬合，補上「地圖說有房間但座標表是 default」的格子。
    /// </summary>
    /// <remarks>
    /// <see cref="LoadedFloors"/> 是上游一格一格 dump 出來的；缺格的後果是
    /// <see cref="NearestRoom"/> 把那間房裡的東西<b>歸屬到隔壁房</b>——靜默畫錯，比不畫更糟。
    /// <para>
    /// 做法：X 只跟 col 有關、Z 只跟 row 有關（房號＝5×row+col，已由反組譯確認是線性格號），
    /// 兩軸各做一次最小平方直線擬合。殘差超過 <see cref="GridFitMaxResidual"/> 就整個放棄。
    /// </para>
    /// <para>
    /// 📌 <b>台服現況：這條路目前一格都補不到，那是正常的。</b>2026-08-15 全量離線掃描證實
    /// 80 個版面「缺的格子」與四角完全重合（見 <see cref="IsGridCorner"/>），
    /// 也就是說<b>上游的表其實沒有 dump 缺口</b>。機制留著是為了「哪天真的有缺口」，
    /// 所以結局訊息要能分辨「沒東西可補」與「有東西可補但放棄了」——
    /// 舊版兩種情形一個沉默、一個印「缺格維持未知」，剛好把最容易誤導人的那面留在 log 裡。
    /// </para>
    /// <para>🔴 只補 <c>MapData</c> 說有房間、而且不是四角的格子。</para>
    /// </remarks>
    private void FillMissingRoomCenters()
    {
        // 想補的格子先數出來：這個數字與擬合成不成功無關，而且**兩種結局都要印它**。
        var wanted = 0;
        var corners = 0;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            if (RoomCenters[i] != null || (byte)Palace.Rooms[i] == 0)
                continue; // 有座標了，或遊戲自己說這一格不是地圖的一部分
            if (IsGridCorner(i))
                ++corners;
            else
                ++wanted;
        }

        var filled = 0;
        var outcome = Fit();

        // ── 單一出口：上面每一條 return 都在這裡匯流，兩態都帶著實際數字 ──────────
        var signature = $"{FaceName()}|{outcome}";
        if (_gridFitLogged != signature)
        {
            _gridFitLogged = signature;
            Service.Logger.Information(
                $"[DD] 房間中心網格擬合：樓層 {Palace.Floor}、版面 {FaceName()}、想補 {wanted} 格" +
                (corners > 0 ? $"（另有 {corners} 格是四角，永不補）" : string.Empty) +
                $" ⇒ {outcome}");
        }

        string Fit()
        {
            // 兩軸各自的 (自變數, 應變數) 樣本
            var (nx, sumCol, sumColSq, sumX, sumColX) = (0, 0f, 0f, 0f, 0f);
            var (nz, sumRow, sumRowSq, sumZ, sumRowZ) = (0, 0f, 0f, 0f, 0f);
            var distinctCols = 0;
            var distinctRows = 0;
            Span<bool> seenCol = stackalloc bool[5];
            Span<bool> seenRow = stackalloc bool[5];

            for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
            {
                if (RoomCenters[i] is not WPos c)
                    continue;
                var row = (float)(i / 5);
                var col = (float)(i % 5);
                ++nx;
                sumCol += col;
                sumColSq += col * col;
                sumX += c.X;
                sumColX += col * c.X;
                ++nz;
                sumRow += row;
                sumRowSq += row * row;
                sumZ += c.Z;
                sumRowZ += row * c.Z;
                if (!seenCol[i % 5])
                {
                    seenCol[i % 5] = true;
                    ++distinctCols;
                }
                if (!seenRow[i / 5])
                {
                    seenRow[i / 5] = true;
                    ++distinctRows;
                }
            }

            // 少於兩個不同的欄／列就擬不出斜率
            if (distinctCols < 2 || distinctRows < 2)
                return $"擬不出斜率：已知 {nx} 格、只有 {distinctCols} 個不同欄／{distinctRows} 個不同列（兩者都要 ≥2）";

            var denX = nx * sumColSq - sumCol * sumCol;
            var denZ = nz * sumRowSq - sumRow * sumRow;
            if (Math.Abs(denX) < 1e-3f || Math.Abs(denZ) < 1e-3f)
                return $"擬不出斜率：已知 {nx} 格、分母太接近 0（X={denX:f3}、Z={denZ:f3}）";

            var bx = (nx * sumColX - sumCol * sumX) / denX;
            var ax = (sumX - bx * sumCol) / nx;
            var bz = (nz * sumRowZ - sumRow * sumZ) / denZ;
            var az = (sumZ - bz * sumRow) / nz;

            // 殘差檢查：擬合對不上已知的格子，就不要拿它去補未知的格子
            var maxResidual = 0f;
            for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
            {
                if (RoomCenters[i] is not WPos c)
                    continue;
                var dx = Math.Abs(ax + bx * (i % 5) - c.X);
                var dz = Math.Abs(az + bz * (i / 5) - c.Z);
                maxResidual = Math.Max(maxResidual, Math.Max(dx, dz));
            }
            if (maxResidual > GridFitMaxResidual)
                return $"放棄：已知 {nx} 格、最大殘差 {maxResidual:f1}y 超過門檻 {GridFitMaxResidual:f0}y ⇒ 缺格維持未知";

            for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
            {
                if (RoomCenters[i] != null)
                    continue;
                if ((byte)Palace.Rooms[i] == 0)
                    continue; // 遊戲自己說這一格不是地圖的一部分——不補
                if (IsGridCorner(i))
                    continue; // 🔴 四角永遠不是 5×5 網格上的房間，補了就是幽靈房心（見 IsGridCorner）
                RoomCenters[i] = new WPos(ax + bx * (i % 5), az + bz * (i / 5));
                _centerFitted[i] = true;
                ++filled;
            }

            return $"採用：補了 {filled} 格（已知 {nx} 格、最大殘差 {maxResidual:f1}y 未超過門檻 " +
                $"{GridFitMaxResidual:f0}y、X={ax:f1}+{bx:f1}·col、Z={az:f1}+{bz:f1}·row）";
        }
    }

    /// <summary>
    /// 逐版面實算房間歸屬的容許誤差：取所有房間裡「最近鄰房距」最大的那一個的一半，再加餘裕。
    /// </summary>
    /// <remarks>
    /// 為什麼取最大而不是最小：這個值是 <see cref="NearestRoom"/> 的<b>截斷距離</b>，
    /// 而不是分辨兩間房的門檻——歸屬本來就是「離誰最近算誰的」，截斷只決定
    /// 「離所有房都太遠就當不在任何房裡」。取最小會讓格子大的那幾間房內側的東西
    /// 被靜默丟掉（實測厄運迷宮有 25.2% 的格子踩到這個）。
    /// </remarks>
    private float ComputeRoomTolerance()
    {
        var maxNearest = 0f;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            if (RoomCenters[i] is not WPos a)
                continue;
            var bestSq = float.MaxValue;
            for (var j = 0; j < DeepDungeonState.NumRooms; ++j)
            {
                if (i == j || RoomCenters[j] is not WPos b)
                    continue;
                var dsq = (b - a).LengthSq();
                if (dsq < bestSq)
                    bestSq = dsq;
            }
            if (bestSq < float.MaxValue)
                maxNearest = Math.Max(maxNearest, MathF.Sqrt(bestSq));
        }

        // 半幅 + 8y 餘裕；下限維持原本的 35y（只會變寬，不可能讓既有行為退步）
        var tol = Math.Clamp(maxNearest * (_hallMode ? HallToleranceFactor : 0.5f) + 8f, RoomCenterToleranceFloor, RoomCenterToleranceCap);
        Service.Logger.Information(
            $"[DD] 房間歸屬容許誤差：樓層 {Palace.Floor}、版面 {FaceName()}、" +
            $"最大最近鄰房距 {maxNearest:f1}y ⇒ 採用 {tol:f1}y");
        return tol;
    }

    #endregion

    #region 大廳層（無內牆的稠密 12 格版面）

    /// <summary>大廳層的格數：3 列 × 4 欄。</summary>
    private const int HallCellCount = 12;

    /// <summary>大廳格心用的 <see cref="Wall.Depth"/>，沿用 <see cref="LoadedFloors"/> 對中心點的慣例值。</summary>
    private const float HallCenterDepth = 0.25f;

    /// <summary>
    /// 大廳層的房間歸屬容許誤差係數（取代一般層的 0.5）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>大廳非用大一點的係數不可，而理由不是「怕算不準」。</b>一般層的格子被牆隔開，
    /// 角色能站的位置離房中心有限；大廳 12 格完全互通，角色可以站到格子的**對角**上，
    /// 而 52×51 格的半對角線是 36.3y —— 已經超過 <c>0.5×間距+8</c> 算出來的 35.0y。
    /// 2026-08-14 拿實機 log 的 75 筆房間交界樣本實算：0.5 的係數有 7 筆超出容許（最大 38.2y），
    /// 0.75 則是 75/75 全部涵蓋。超出的後果是閘門在角落站久了就翻成不通過，
    /// 小地圖上已定位的標記連環閃爍。
    /// <para>
    /// 📌 放寬距離門檻**不會**放寬正確性：<see cref="ScoreFace"/> 另外要求
    /// 「離本人最近的房間就是遊戲回報的那一間」，辨別力來自那一條。距離門檻要抓的是
    /// 「整份座標表根本在別的地方」（實測差 665~723y），48y 與 35y 對那件事沒有差別。
    /// </para>
    /// </remarks>
    private const float HallToleranceFactor = 0.75f;

    /// <summary>本層是不是大廳層。</summary>
    private bool _hallMode;

    /// <summary>大廳格心是哪來的。</summary>
    private HallCenterSource _hallCenterSource;

    private enum HallCenterSource
    {
        /// <summary>還沒有格心：剛偵測到大廳，或即時擬合還沒通過自我校準。</summary>
        None,
        /// <summary>離線量測寫死的固定表。</summary>
        FixedTable,
        /// <summary>本層即時擬合出來的。</summary>
        LiveFit
    }

    #region 天之御柱大廳的固定格心表

    /// <summary>
    /// 天之御柱大廳層的格心線性模型：<c>X = 原點 + 間距 × col</c>、<c>Z = 原點 + 間距 × row</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 📌 <b>資料來源</b>：2026-08-14 天之御柱 1~40 層實機 log 的「房間位置普查」
    /// （<c>WorldStateGameSync</c> 那份 2Hz 定時取樣），四個大廳層（15／17／28／34）
    /// 共 1580 筆房內樣本，加權合併後兩軸各做一次最小平方直線擬合。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼一張表就夠</b>：那四層橫跨三組樓層（11-20／21-30／31-40），
    /// 逐層各自擬合出來的網格互判是滿分——把任一層的模型拿去判其他三層的房間質心，
    /// 42 個樣本裡 41~42 個判回自己（row/col 對調的負對照只有 0.238）。
    /// 也就是說<b>大廳是天之御柱裡一塊固定的實體區域</b>，與樓層組無關；
    /// 這與一般層正好相反（一般層的 A／B 表逐樓層組都不同）。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼用線性模型而不是逐格質心</b>：兩者的留一層交叉驗證幾乎同分
    /// （質心 42/42、線性 41/42），但那個測試是<b>偏心的</b>——它拿「玩家走位平均」去預測
    /// 「玩家走位平均」，而我們真正要的是房間中心。改用獨立估計量（房間交界穿越樣本的
    /// 外接框中點）比對，線性模型平均偏差 7.7y、逐格質心 8.7y，線性勝；
    /// 在交界樣本上判對房間的比率也是線性較高（0.653 對 0.573）。
    /// 線性模型只有 4 個參數（逐格質心有 24 個），對只有四層的樣本比較不會過擬合。
    /// </para>
    /// <para>
    /// ⚠️ <b>只有這個迷宮有表。</b>死者宮殿與厄運迷宮的大廳沒有任何量測資料，
    /// 拿這組數字去套會是「差幾百碼」等級的錯誤 —— 所以
    /// <see cref="BuildHallCentersFromTable"/> 硬性檢查迷宮種類，其他迷宮走即時擬合。
    /// </para>
    /// </remarks>
    private const float HohHallOriginX = 222.62f;
    private const float HohHallSpacingX = 52.81f;
    private const float HohHallOriginZ = 199.52f;
    private const float HohHallSpacingZ = 51.35f;

    /// <summary>
    /// 固定表實測涵蓋的格位範圍（列 1..3、欄 0..3）。
    /// </summary>
    /// <remarks>
    /// 🔴 四個大廳層的 MapData 佔用格位<b>一模一樣</b>（房號 5~8／10~13／15~18，
    /// 連通位元逐格逐位元相同）。超出這個矩形的格位從來沒被量到過，
    /// 硬把線性模型外推過去就是在猜 —— 寧可退回即時擬合。
    /// </remarks>
    private const int HohHallMinRow = 1;
    private const int HohHallMaxRow = 3;
    private const int HohHallMinCol = 0;
    private const int HohHallMaxCol = 3;

    /// <summary>照固定表算出這一層的格心；沒有可用的表時回 <c>null</c>。</summary>
    private WPos?[]? BuildHallCentersFromTable()
    {
        if (Palace.DungeonId != DeepDungeonState.DungeonType.HOH)
            return null;

        var centers = new WPos?[DeepDungeonState.NumRooms];
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            if ((byte)Palace.Rooms[i] == 0)
                continue;
            var row = i / 5;
            var col = i % 5;
            if (row < HohHallMinRow || row > HohHallMaxRow || col < HohHallMinCol || col > HohHallMaxCol)
                return null;
            centers[i] = new WPos(HohHallOriginX + HohHallSpacingX * col, HohHallOriginZ + HohHallSpacingZ * row);
        }
        return centers;
    }

    #endregion

    /// <summary>
    /// 本層是不是大廳層：<b>12 格、稠密矩形、內部每一對相鄰格都互通</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>刻意不用 <c>Progress.Tileset == 2</c></b> —— 那在台服是死條件，實測大廳層回報 0。
    /// 改成問 MapData 自己的形狀，因為「無內牆」本來就是大廳的定義，而 MapData 的低四位元
    /// 就是四向連通旗標，這是遊戲自己送過來的、不是我們猜的。
    /// </para>
    /// <para>
    /// 📌 <b>MapData 是靜態版面而不是逐步揭露的。</b>這一點必須成立，否則進場那一刻判不出來。
    /// 2026-08-14 對 41 個樓層實算：玩家實際待過的每一間房，在<b>進場那一幀</b>的 MapData 就已經
    /// 是非零了（反例 0 個）。<c>Revealed</c> 是另一個位元（0x80），與「這一格是不是房間」無關。
    /// </para>
    /// <para>
    /// 🔑 <b>判準的誤判率是量出來的</b>：同一份 log 的 41 個樓層，命中 4/4 個大廳、0/37 誤判
    /// （一般層最多 7 格，離 12 格很遠）。判準刻意嚴格到「剛好 12 格」——
    /// 別的形狀的大廳我們沒有任何資料，寧可維持今天的行為也不要誤判成大廳，
    /// 因為誤判的代價是把一般層那份**本來是對的**座標表整個關掉。
    /// </para>
    /// </remarks>
    private bool DetectHall()
    {
        var count = 0;
        int minRow = 5, maxRow = -1, minCol = 5, maxCol = -1;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            if ((byte)Palace.Rooms[i] == 0)
                continue;
            ++count;
            var row = i / 5;
            var col = i % 5;
            if (row < minRow)
                minRow = row;
            if (row > maxRow)
                maxRow = row;
            if (col < minCol)
                minCol = col;
            if (col > maxCol)
                maxCol = col;
        }

        if (count != HallCellCount)
            return false;
        // 稠密矩形：外接框的面積要剛好等於格數
        if ((maxRow - minRow + 1) * (maxCol - minCol + 1) != count)
            return false;

        // 內部無牆：每一對相鄰格的連通旗標**兩側都要有**。
        // ⚠️ 兩側都檢查是刻意的——旗標理論上對稱，但「理論上」不是查證。
        for (var row = minRow; row <= maxRow; ++row)
        {
            for (var col = minCol; col <= maxCol; ++col)
            {
                var cur = Palace.Rooms[row * 5 + col];
                if (row < maxRow)
                {
                    var south = Palace.Rooms[(row + 1) * 5 + col];
                    if (!cur.HasFlag(RoomFlags.ConnectionS) || !south.HasFlag(RoomFlags.ConnectionN))
                        return false;
                }
                if (col < maxCol)
                {
                    var east = Palace.Rooms[row * 5 + col + 1];
                    if (!cur.HasFlag(RoomFlags.ConnectionE) || !east.HasFlag(RoomFlags.ConnectionW))
                        return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// 用一份格心座標組出「只有中心、沒有門」的版面表。
    /// </summary>
    /// <remarks>
    /// 🔴 四個方向欄位刻意留 <c>default</c>：<see cref="ApplyFace"/> 只在方向欄位<b>不是</b>
    /// default 時才把它當成關著的門加進 <see cref="Walls"/> —— 所以大廳層一道牆都不會注入。
    /// 大廳 12 格互通，注入錯誤的牆會把走廊封死，而尋路失敗的外顯是「角色站著不動」，
    /// 完全看不出跟牆有關。同理 <see cref="TryGetDoor"/> 會回 false，門口路點自動退回方向場。
    /// </remarks>
    private static Tileset<Wall> BuildCenterOnlyFace(WPos?[] centers)
    {
        var rooms = new RoomData<Wall>[DeepDungeonState.NumRooms];
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
            rooms[i] = new RoomData<Wall>(
                centers[i] is WPos c ? new Wall(c, HallCenterDepth) : default,
                default, default, default, default);
        return new Tileset<Wall>(rooms);
    }

    /// <summary>把一份大廳格心套進去當作唯一的版面。</summary>
    /// <remarks>
    /// 🔴 <b>B 面留成 null 是刻意的</b>：鏡像自我校準比的是相距約 845y 的兩份版面，
    /// 大廳只有一份座標，那道機制在這裡沒有意義（而且它會拿「另一面」去搶，只會搗亂）。
    /// <see cref="ComputeRoomCoords"/> 的換面分支要求 <c>_faceA != null &amp;&amp; _faceB != null</c>，
    /// 所以留 null 就自然停用，不需要多一個旗標。
    /// </remarks>
    private void ApplyHallFace(WPos?[] centers, HallCenterSource source, string sourceText)
    {
        _hallCenterSource = source;
        _faceA = BuildCenterOnlyFace(centers);
        _faceB = null;
        ApplyFace(0);
        Service.Logger.Information(
            $"[DD] 大廳層格心啟用：樓層 {Palace.Floor}、迷宮 {Palace.DungeonId}、" +
            $"來源 {sourceText}、容許誤差 {RoomTolerance:f1}y");
    }

    #region 大廳格心的即時擬合（沒有固定表的迷宮才走這條）

    /// <summary>取樣間隔（秒）。與普查用的 2Hz 一致。</summary>
    private const double HallSampleIntervalSeconds = 0.5d;

    /// <summary>
    /// 換層後要靜置這麼久才開始取樣（秒）。
    /// </summary>
    /// <remarks>
    /// 🔴 剛進場那一瞬間角色還在傳送中，回報的是一個與房號完全無關的固定位置
    /// （實機 log 裡的 (0, -290) 那一組，離房中心 600~740y）。那種樣本會把平均值拉歪，
    /// 而且外顯只是「離群值大了點」，不像壞掉。
    /// </remarks>
    private const double HallSettleSeconds = 2d;

    /// <summary>每隔這麼久試擬合一次（秒）。</summary>
    private const double HallFitIntervalSeconds = 5d;

    /// <summary>至少要有這麼多格有足夠樣本才准擬合（大廳共 12 格）。</summary>
    private const int HallFitMinCells = 8;

    /// <summary>一格至少要這麼多筆樣本才算數。</summary>
    private const int HallFitMinSamplesPerCell = 10;

    /// <summary>
    /// 擬合的最大容許殘差（碼）。
    /// </summary>
    /// <remarks>
    /// 實機四個大廳層各自擬合的最大殘差是 17.8~26.7y（樣本是玩家走位平均，本來就有走位偏差），
    /// 所以門檻取 30y：真的擬得出來的過得了，離譜的擋得住。
    /// </remarks>
    private const float HallFitMaxResidual = 30f;

    private readonly int[] _hallSampleN = new int[DeepDungeonState.NumRooms];
    private readonly double[] _hallSampleX = new double[DeepDungeonState.NumRooms];
    private readonly double[] _hallSampleZ = new double[DeepDungeonState.NumRooms];
    private DateTime _hallFloorSince;
    private DateTime _hallNextSample;
    private DateTime _hallNextFit;

    /// <summary>目前這份即時擬合用了幾格；0＝還沒擬合成功過。</summary>
    private int _hallFitCells;

    /// <summary>收一筆「本人在遊戲說的那間房裡」的位置樣本，夠了就試擬合。</summary>
    private void AccumulateHallSample(int reportedRoom, WPos pos)
    {
        // 有固定表就不必擬合——固定表在進場那一幀就生效，即時擬合要走過八間房才有結果。
        if (!_hallMode || _hallCenterSource == HallCenterSource.FixedTable)
            return;
        if ((uint)reportedRoom >= DeepDungeonState.NumRooms || (byte)Palace.Rooms[reportedRoom] == 0)
            return;

        var now = World.CurrentTime;
        if ((now - _hallFloorSince).TotalSeconds < HallSettleSeconds || now < _hallNextSample)
            return;
        _hallNextSample = now.AddSeconds(HallSampleIntervalSeconds);
        ++_hallSampleN[reportedRoom];
        _hallSampleX[reportedRoom] += pos.X;
        _hallSampleZ[reportedRoom] += pos.Z;

        if (now < _hallNextFit)
            return;
        _hallNextFit = now.AddSeconds(HallFitIntervalSeconds);
        TryFitHallCenters();
    }

    /// <summary>
    /// 拿累積到的房內位置擬合大廳格心；通過自我校準閘門才真的套用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 模型與 <see cref="FillMissingRoomCenters"/> 同一套：X 只跟 col 有關、Z 只跟 row 有關，
    /// 房號＝5×row+col。差別是自變數的樣本來自本層實測而不是寫死的表。
    /// </para>
    /// <para>
    /// 🔴 <b>閘門有三道，任一沒過就整個不套用（＝維持今天的行為）</b>：
    /// ①覆蓋度——大廳的每一列、每一欄都要有取樣過的格子，否則等於在外推；
    /// ②自我判定——擬出來的格心要把每一格的樣本質心判回<b>它自己</b>，這正是
    /// <see cref="ScoreFace"/> 真正依賴的性質；③殘差不得超過 <see cref="HallFitMaxResidual"/>。
    /// 沒有②就分不出「擬合成功」與「這段程式自己算錯」——離線工具的校準閘門是同一個形狀。
    /// </para>
    /// <para>📌 只在「用得比上次多格」時才重新套用，免得每 5 秒抖一次。</para>
    /// </remarks>
    private void TryFitHallCenters()
    {
        var n = 0;
        double sumCol = 0d, sumColSq = 0d, sumX = 0d, sumColX = 0d;
        double sumRow = 0d, sumRowSq = 0d, sumZ = 0d, sumRowZ = 0d;
        Span<bool> seenRow = stackalloc bool[5];
        Span<bool> seenCol = stackalloc bool[5];

        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            if (_hallSampleN[i] < HallFitMinSamplesPerCell)
                continue;
            var x = _hallSampleX[i] / _hallSampleN[i];
            var z = _hallSampleZ[i] / _hallSampleN[i];
            double row = i / 5, col = i % 5;
            ++n;
            sumCol += col;
            sumColSq += col * col;
            sumX += x;
            sumColX += col * x;
            sumRow += row;
            sumRowSq += row * row;
            sumZ += z;
            sumRowZ += row * z;
            seenRow[i / 5] = true;
            seenCol[i % 5] = true;
        }

        if (n < HallFitMinCells || n <= _hallFitCells)
            return;

        // ① 覆蓋度：大廳用到的每一列與每一欄都要有樣本
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
            if ((byte)Palace.Rooms[i] != 0 && (!seenRow[i / 5] || !seenCol[i % 5]))
                return;

        var denCol = n * sumColSq - sumCol * sumCol;
        var denRow = n * sumRowSq - sumRow * sumRow;
        if (Math.Abs(denCol) < 1e-6d || Math.Abs(denRow) < 1e-6d)
            return;

        var bx = (n * sumColX - sumCol * sumX) / denCol;
        var ax = (sumX - bx * sumCol) / n;
        var bz = (n * sumRowZ - sumRow * sumZ) / denRow;
        var az = (sumZ - bz * sumRow) / n;

        var centers = new WPos?[DeepDungeonState.NumRooms];
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
            if ((byte)Palace.Rooms[i] != 0)
                centers[i] = new WPos((float)(ax + bx * (i % 5)), (float)(az + bz * (i / 5)));

        // ②③ 自我判定與殘差
        var maxResidual = 0f;
        var total = 0;
        for (var i = 0; i < DeepDungeonState.NumRooms; ++i)
        {
            total += _hallSampleN[i];
            if (_hallSampleN[i] < HallFitMinSamplesPerCell)
                continue;
            var mean = new WPos((float)(_hallSampleX[i] / _hallSampleN[i]), (float)(_hallSampleZ[i] / _hallSampleN[i]));
            var best = -1;
            var bestSq = float.MaxValue;
            for (var j = 0; j < DeepDungeonState.NumRooms; ++j)
            {
                if (centers[j] is not WPos cj)
                    continue;
                var dsq = (cj - mean).LengthSq();
                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    best = j;
                }
            }
            if (best != i)
                return;
            if (centers[i] is not WPos ci)
                return;
            maxResidual = Math.Max(maxResidual, Math.Max(Math.Abs(ci.X - mean.X), Math.Abs(ci.Z - mean.Z)));
        }
        if (maxResidual > HallFitMaxResidual)
            return;

        _hallFitCells = n;
        ApplyHallFace(centers, HallCenterSource.LiveFit,
            $"即時擬合（{n} 格、{total} 筆樣本、最大殘差 {maxResidual:f1}y、" +
            $"X={ax:f1}+{bx:f1}·col、Z={az:f1}+{bz:f1}·row）");
    }

    #endregion

    /// <summary>log 用的版面名稱。大廳層沒有 A／B 之分。</summary>
    private string FaceName() => _hallMode ? "大廳" : _activeFace == 0 ? "A" : _activeFace == 1 ? "B" : "未載入";

    #endregion

    protected void AddLOSFromTerrain(Actor Source, float Range)
    {
        var (entry, data) = _obstacles.Find(Source.PosRot.XYZ());
        if (entry == null || data == null)
        {
            Service.Log($"no bitmap found for {Source}, not adding LOS hints");
            return;
        }

        var pixelRange = (int)(Range / data.PixelSize);
        var casterOff = Source.Position - entry.Origin;
        var casterCell = casterOff / data.PixelSize;
        var casterX = (int)casterCell.X;
        var casterZ = (int)casterCell.Z;

        var bm = new Bitmap(data.Width, data.Height, data.Color0, data.Color1, data.Resolution);
        for (var i = Math.Max(0, casterX - pixelRange); i <= Math.Min(data.Width, casterX + pixelRange); ++i)
        {
            for (var j = Math.Max(0, casterZ - pixelRange); j <= Math.Min(data.Height, casterZ + pixelRange); ++j)
            {
                var pt = new Vector2(i, j);
                var cc = new Vector2(casterX, casterZ);
                if (!IsBlocked(data, pt, cc, pixelRange))
                    bm[i, j] = true;
            }
        }

        _losCache[Source.InstanceID] = (entry.Origin, bm);
        LOS.Add(Source);
    }

    private static bool IsBlocked(Bitmap map, Vector2 point, Vector2 origin, float maxRange)
    {
        var dir = origin - point;
        var dist = dir.Length();
        if (dist >= maxRange)
            return true;

        dir /= dist;

        var ox = point.X;
        var oy = point.Y;
        var vx = dir.X;
        var vy = dir.Y;

        for (var i = 0; i < (int)dist; ++i)
        {
            if (map[(int)ox, (int)oy])
                return true;
            ox += vx;
            oy += vy;
        }

        return false;
    }
}
