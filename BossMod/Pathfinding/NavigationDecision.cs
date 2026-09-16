using System.Threading;

namespace BossMod.Pathfinding;

// utility for selecting player's navigation target
// there are several goals that navigation has to meet, in following rough priority
// 1. stay away from aoes; tricky thing is that sometimes it is ok to temporarily enter aoe, if we're sure we'll exit it in time
// 2. maintain uptime - this is represented by being in specified range of specified target, and not moving to interrupt casts unless needed
// 3. execute positionals - this is strictly less important than points above, we only do that if we can meet other conditions
// 4. be in range of healers - even less important, but still nice to do
public struct NavigationDecision
{
    // context that allows reusing large memory allocations
    public sealed class Context
    {
        public float[] Scratch = [];
        public Map Map = new();
        public ThetaStar ThetaStar = new();
    }

    public WPos? Destination;
    public WPos? NextWaypoint;
    public float LeewaySeconds; // can be used for finishing casts / slidecasting etc.
    public float TimeToGoal;

    #region 診斷（純輸出，不參與任何決策）

    /// <summary>這一次真正拿去 rasterize 的目標區數量（<b>快照</b>長度，不是還活著的那份 List）。</summary>
    public int DiagGoalZones;

    /// <summary>目標區有沒有真的被畫上權重場。詠唱中、玩家格出視窗、玩家腳下本來就危險時都<b>不會</b>畫。</summary>
    public bool DiagGoalsRasterized;

    /// <summary>玩家所在的格子在不在尋路視窗內。</summary>
    public bool DiagPlayerInWindow;

    /// <summary>玩家格的危險度（<c>float.MaxValue</c>＝安全、0＝現在就危險、負＝不可通行）。</summary>
    public float DiagPlayerMaxG;

    /// <summary>玩家格的目標權重。</summary>
    public float DiagPlayerPriority;

    /// <summary>整張權重場的最高權重（<b>含走不到的格子</b>）。</summary>
    public float DiagMaxPriority;

    /// <summary>這一次搜尋真的走得到的格子裡最高的權重。見 <see cref="ThetaStar.MaxReachedPriority"/>。</summary>
    public float DiagReachablePriority;

    /// <summary>整個可達區域都探過了（開放清單掃空）。</summary>
    public bool DiagSearchExhausted;

    /// <summary>這一次尋路展開了幾格。</summary>
    public int DiagSearchSteps;

    /// <summary>呼叫端傳進來的<b>原始</b>移動速度（碼／秒），沒有被夾限前的值。</summary>
    public float DiagRawSpeed;

    /// <summary>原始速度不在合理範圍，這一次改用 <see cref="NominalPlayerSpeed"/> 算。見那裡的說明。</summary>
    public bool DiagSpeedSubstituted;

    /// <summary>
    /// 把「尋路這一次到底看到了什麼」寫成一行。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只在狀態翻轉時呼叫</b>：這支會組字串，每幀叫等於每幀配置記憶體，而且會把 log 洗掉。
    /// <para>
    /// 🔑 存在的理由是「角色不走」有<b>四種互斥成因</b>，而它們的外觀完全相同（回 null、不報錯）：
    /// <list type="number">
    /// <item>玩家格落在尋路視窗外 —— 目標區對玩家的位置完全沒有作用。</item>
    /// <item>目標區整段沒被畫上權重場（詠唱中，或玩家腳下已經在危險區裡 ⇒ 安全優先）。</item>
    /// <item><b>權重場在玩家腳下是平的</b> —— 玩家格的權重已經等於場上最高，
    /// <c>ThetaStar.PrefillH</c> 因此給它 <c>HScore == 0</c>，<c>Execute</c> 一步都不跑就回傳起點，
    /// <c>GetFirstWaypoints</c> 回 <c>(null, null)</c>。<b>平台型的目標區</b>
    /// （<c>GoalSingleTarget(pos, 大半徑, w)</c> 這種半徑內同分的）只要把玩家包在裡面就必然踩到這條。</item>
    /// <item>有高低差但更好的格子被禁區／障礙物隔開。</item>
    /// <item><b>移動速度讀到 0</b> —— 見 <see cref="NominalPlayerSpeed"/>。這一條在 .59 之前
    /// 會偽裝成第 ④ 條的相反面（「走得到更好的格子卻沒採用」），因為它壞的是<b>評分</b>不是搜尋。</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <summary>這一次算不出目的地的原因是「場上根本沒有目標區」＝<b>沒有人要求移動</b>（閒置常態）。</summary>
    /// <remarks>🔴 條件與 <see cref="DiagSummary"/> 裡「場上沒有任何目標區」那一支<b>逐字相同</b>，改一邊要改另一邊。</remarks>
    public readonly bool DiagIdleNoGoals => DiagPlayerInWindow && !DiagGoalsRasterized && DiagGoalZones == 0;

    public readonly string DiagSummary()
    {
        var why = !DiagPlayerInWindow
            ? "玩家格落在尋路視窗外 ⇒ 目標區對玩家目前的位置完全沒有作用"
            : !DiagGoalsRasterized
                ? DiagGoalZones == 0
                    ? "場上沒有任何目標區 ⇒ 沒有人要求移動"
                    : "目標區沒有被畫上權重場（詠唱中，或玩家腳下已經在危險區裡，安全優先）"
                : DiagMaxPriority <= DiagPlayerPriority
                    ? "權重場在玩家腳下是平的（玩家格的權重已經等於場上最高）⇒ 尋路認定「已經在最佳位置」"
                    // 🔑 這裡以前只有一句「更好的格子到不了」，而那句把兩種完全不同的病混在一起。
                    //    分辨它們的唯一數字就是「走得到的最高權重」——見 ThetaStar.MaxReachedPriority。
                    : DiagReachablePriority <= DiagPlayerPriority + 1e-4f
                        ? (DiagSearchExhausted
                            ? "整個可達區域都探過了，走得到的最高權重就是玩家腳下這一格 ⇒ 玩家卡在局部最高點（更高的權重被禁區／障礙物隔在外面）"
                            : "走得到的最高權重就是玩家腳下這一格，但搜尋沒有掃完可達區域 ⇒ 提早收斂")
                        : "走得到更好的格子，尋路卻沒有採用 ⇒ 這是尋路自己的問題，不是目標區的問題";
        return $"目標區 {DiagGoalZones} 個、已畫上權重場={(DiagGoalsRasterized ? "是" : "否")}、玩家格在視窗內={(DiagPlayerInWindow ? "是" : "否")}、" +
            $"玩家格危險度={FormatMaxG(DiagPlayerMaxG)}、玩家格權重={DiagPlayerPriority:f2}、場上最高權重={DiagMaxPriority:f2}、" +
            $"走得到的最高權重={DiagReachablePriority:f2}、展開 {DiagSearchSteps} 格{(DiagSearchExhausted ? "（已掃完）" : "")}、" +
            $"移動速度={DiagRawSpeed:f2}{(DiagSpeedSubstituted ? $"（不合理，改用 {NominalPlayerSpeed:f0}）" : "")} ⇒ {why}";
    }

    private static string FormatMaxG(float g) => g == float.MaxValue ? "安全" : g < 0f ? "不可通行" : $"{g:f2}s";

    #endregion

    public const float ActivationTimeCushion = 1f; // reduce time between now and activation by this value in seconds; increase for more conservativeness

    /// <summary>
    /// 移動速度讀不出合理值時拿來代打的名目速度（碼／秒）。與 <see cref="Build"/> 的
    /// <c>playerSpeed</c> 預設值相同，也就是未改造的角色跑步速度。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>speed 讀到 0 會讓整個尋路靜默失效，而且症狀完全不像「速度」的問題。</b>
    /// 這是 2026-08-14 深牢「趕路站著不動」的真因，機制是純算術的：
    /// <list type="number">
    /// <item><c>ThetaStar.Start(map, pos, 1f / speed)</c> ⇒ <c>gMultiplier = 1/0 = +∞</c>
    /// ⇒ <c>_deltaGSide = Resolution * ∞ = +∞</c>。</item>
    /// <item>於是<b>第一步</b>就有 <c>candidateG = 0 + ∞ = ∞</c>。</item>
    /// <item><c>VisitNeighbour</c> 算餘裕：<c>candidateLeeway = min(destPixG, parentPixG) - candidateG</c>
    /// ＝ <c>float.MaxValue - ∞</c> ＝ <b><c>-∞</c></b>。</item>
    /// <item><c>CalculateScore</c> 因此 <c>pathSafe == false</c>，跳過 <c>destSafe &amp;&amp; pathSafe</c> 那一支；
    /// 而全程都是安全格 ⇒ <c>pathMinG == _startMaxG == float.MaxValue</c> ⇒ 落到
    /// <c>destBetter ? UnsafeImprove : UnsafeAsStart</c>，而 <c>destBetter</c>＝
    /// <c>pixMaxG &gt; _startMaxG</c>＝<c>MaxValue &gt; MaxValue</c>＝false
    /// ⇒ <b>每一格都是 <c>Score.UnsafeAsStart</c>(3)</b>。</item>
    /// <item>起點自己不受影響（它的餘裕是 <c>_startMaxG</c> 不是 <c>MaxValue-g</c>）⇒ <c>_startScore == Safe</c>(7)。
    /// <c>ExecuteStep</c> 的 <c>_bestIndex</c> 只在<b>分數更高</b>時才換，3 永遠不高於 7
    /// ⇒ <b>最佳格從頭到尾都是起點</b>；<c>_fallbackIndex</c> 要 <c>UltimatelySafe</c> 才會設，也永遠不會設
    /// ⇒ <c>Execute()</c> 的迴圈兩個提早結束條件都不成立 ⇒ <b>把整個可達區域掃完</b>
    /// ⇒ <c>BestIndex()</c> 回起點 ⇒ <c>GetFirstWaypoints</c> 的
    /// 「<c>GScore==0 &amp;&amp; PathMinG==MaxValue</c>」命中 ⇒ 回 <c>(null, null)</c>
    /// ⇒ <b><c>Destination == null</c>：角色不走、不畫標線、不報錯。</b></item>
    /// </list>
    /// <para>
    /// 🔑 <b>這是從實機 log 的數字反推出來的，不是猜的。</b>2026-08-14 01:55~01:57 那一段的九行
    /// 診斷全部是「玩家格安全、走得到的最高權重＝場上最高權重、展開 6392~6630 格<b>已掃完</b>」。
    /// 反過來推：既然那些格子<b>被展開了</b>就代表沒有被 <c>JustBad</c> 守衛擋掉，
    /// 而安全格只要 <c>pathSafe</c> 成立就必然拿到 <c>Safe</c> 以上、必然贏過起點；
    /// 既然沒贏，<c>pathSafe</c> 就必須對<b>每一格</b>都是 false；
    /// 又因為 <c>pathMinG</c> 仍是 <c>MaxValue</c>（否則會變 <c>UltimatelySafe</c> 而設下 fallback、
    /// 迴圈會提早結束、就不會「已掃完」），唯一的可能就是 <c>MaxValue - g &lt;= 0</c>
    /// ⇒ <c>g &gt;= float.MaxValue</c> ⇒ <c>_deltaGSide</c> 是 ∞ ⇒ <b>speed 是 0</b>。
    /// 沒有第二組輸入能同時滿足那九行的每一個欄位。
    /// </para>
    /// <para>
    /// ⚠️ <b>為什麼夾限不會動到戰鬥閃避</b>（這條是出貨前提，不是順帶）：
    /// <list type="bullet">
    /// <item>對任何<b>物理上可能</b>的速度（走路 ~2、跑步 6、衝刺 ~10、減速 3、坐騎 ~20），
    /// <c>speed is &gt; MinValidSpeed and &lt; MaxValidSpeed</c> 恆為真 ⇒ 這段是<b>恆等式</b>，
    /// 一個位元都不會變。閃避的權重場、評分、路徑全部逐字相同。</item>
    /// <item>唯一會改變輸出的輸入是 0／負數／NaN／∞／荒謬值，而<b>那些情況今天的輸出是「完全不閃避」</b>
    /// （上面推導的 <c>Destination == null</c>）。沒有任何閃避行為需要被保留，只有被恢復。</item>
    /// <item>g 值在這套演算法裡的語意只有「時間」（餘裕與 F 值的排序）。速度估錯只會讓 ETA 不準；
    /// 速度是 ∞ 則會摧毀<b>排序本身</b>（把每一格壓成同一個分數）。有限值嚴格較好。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 📌 刻意<b>不</b>去動 <c>ClientState.MoveSpeed</c> 本身：
    /// 讓 <c>MainDebugWindow</c> 的「Player move speed」繼續顯示<b>真值</b>，
    /// 這裡只負責讓尋路不被它拖垮，並把原始值寫進 <see cref="DiagRawSpeed"/> 供離線判讀。
    /// </para>
    /// </remarks>
    public const float NominalPlayerSpeed = 6f;

    /// <summary>速度低於這個值就當作「沒讀到」。比任何實際的減速都低。</summary>
    private const float MinValidPlayerSpeed = 0.1f;

    /// <summary>速度高於這個值就當作「沒讀到」。比任何坐騎都高，同時擋掉 ∞。</summary>
    private const float MaxValidPlayerSpeed = 200f;

    public static NavigationDecision Build(Context ctx, WorldState ws, AIHints hints, Actor player, float playerSpeed = 6f, float forbiddenZoneCushion = default, bool avoidFutureAOEs = false, float activationTimeCushion = ActivationTimeCushion)
    {
        // build a pathfinding map: rasterize all forbidden zones and goals
        hints.InitPathfindMap(ctx.Map);
        // local copies of forbidden zones and goals to ensure no race conditions during async pathfinding
        (Func<WPos, float>, DateTime, ulong)[] localForbiddenZones = [.. hints.ForbiddenZones];
        Func<WPos, float>[] localGoalZones = [.. hints.GoalZones];
        // 🔴 閘門必須看**快照**，不能看還活著的那份 List。
        //    這支會被 AIBehaviour 丟進 Task.Run 在執行緒集區跑，而主執行緒每幀都會
        //    AIHintsBuilder.Update → hints.Clear() 把 ForbiddenZones/GoalZones 清空。
        //    快照拿到之後、閘門判斷之前若剛好被清掉，就會出現「快照裡明明有目標區、卻整段不 rasterize」
        //    ⇒ 權重場全平 ⇒ ThetaStar 的最佳格就是玩家腳下 ⇒ GetFirstWaypoints 回 (null, null)
        //    ⇒ Destination 為 null ⇒ **角色不走、也不畫任何標線，而且完全不報錯**。
        //    上面兩行取本地副本的註解本來就是為了這個 race，閘門漏改是單純的疏漏。
        if (localForbiddenZones.Length != 0)
        {
            if (avoidFutureAOEs)
            {
                // treat all zones as immediately active: the AI will never enter any AOE even briefly
                var now = ws.CurrentTime;
                for (var i = 0; i < localForbiddenZones.Length; i++)
                    localForbiddenZones[i] = (localForbiddenZones[i].Item1, now, localForbiddenZones[i].Item3);
            }
            RasterizeForbiddenZones(ctx.Map, localForbiddenZones, ws.CurrentTime, ref ctx.Scratch, activationTimeCushion);
        }
        // WorldToGrid 不做夾限，玩家在格線外時 x/y 會是負數，GridToIndex 因此算出負的索引。
        // 原本的守衛只檢查上界（Length > index），任何負索引都會通過 → IndexOutOfRangeException。
        // 實際發生條件：AIHints.Clear() 把 PathfindMapCenter 歸零，而只有 CalculateAutoHints 會重設它；
        // 有 active boss module 且該模組沒設定中心時，中心就停在 (0,0)，於是遠離原點的區域必爆。
        // 用 InBounds 同時驗兩個軸——只檢查 index >= 0 是不夠的：x = -1、y = 1 會算出「看似合法」
        // 但屬於前一列的索引。這也是本專案其他地方的既有寫法（AIBehaviour.cs:350、NormalMovement.cs:264）。
        // 📌 這幾個值同時也是下面診斷欄位的來源，所以刻意提到迴圈外算一次（值與原本逐案相同）。
        var (playerGridX, playerGridY) = ctx.Map.WorldToGrid(player.Position);
        var playerInWindow = ctx.Map.InBounds(playerGridX, playerGridY);
        var playerCell = playerInWindow ? ctx.Map.GridToIndex(playerGridX, playerGridY) : -1;
        var goalsRasterized = false;
        if (player.CastInfo == null) // don't rasterize goal zones if casting or if inside a very dangerous pixel
        {
            if (playerInWindow && ctx.Map.PixelMaxG[playerCell] is >= 1f or < 0f) // prioritize safety over uptime, still needs to be active for below 0 MaxG to go back inside arena bounds if needed
            {
                if (localGoalZones.Length != 0) // 同上：看快照，不要看還在被主執行緒清空的那份 List
                {
                    RasterizeGoalZones(ctx.Map, localGoalZones);
                    goalsRasterized = true;
                }
                if (forbiddenZoneCushion > 0)
                {
                    AvoidForbiddenZone(ctx.Map, forbiddenZoneCushion);
                }
            }
        }
        // execute pathfinding
        // 🔴 speed 不合理時一律用名目速度 —— 完整推導與「為什麼閃避不受影響」見 NominalPlayerSpeed。
        //    ⚠️ 寫成 `is > x and < y` 而不是 `!(...)`：NaN 對這個模式回 false，會正確落到代打那邊。
        var speedValid = playerSpeed is > MinValidPlayerSpeed and < MaxValidPlayerSpeed;
        var effectiveSpeed = speedValid ? playerSpeed : NominalPlayerSpeed;
        ctx.ThetaStar.Start(ctx.Map, player.Position, 1.0f / effectiveSpeed);
        var bestNodeIndex = ctx.ThetaStar.Execute();
        ref var bestNode = ref ctx.ThetaStar.NodeByIndex(bestNodeIndex);
        var waypoints = GetFirstWaypoints(ctx.ThetaStar, ctx.Map, bestNodeIndex, player.Position);
        return new()
        {
            Destination = waypoints.first,
            NextWaypoint = waypoints.second,
            LeewaySeconds = bestNode.PathLeeway,
            TimeToGoal = bestNode.GScore,
            // 診斷：全部取自本次真正用到的地圖，不重算、不猜。玩家格出視窗時不去讀陣列（那正是上面守衛擋的東西）。
            DiagGoalZones = localGoalZones.Length,
            DiagGoalsRasterized = goalsRasterized,
            DiagPlayerInWindow = playerInWindow,
            DiagPlayerMaxG = playerInWindow ? ctx.Map.PixelMaxG[playerCell] : default,
            DiagPlayerPriority = playerInWindow ? ctx.Map.PixelPriority[playerCell] : default,
            DiagMaxPriority = ctx.Map.MaxPriority,
            DiagReachablePriority = ctx.ThetaStar.MaxReachedPriority,
            DiagSearchExhausted = ctx.ThetaStar.OpenListExhausted,
            DiagSearchSteps = ctx.ThetaStar.NumSteps,
            DiagRawSpeed = playerSpeed,
            DiagSpeedSubstituted = !speedValid
        };
    }

    private static void AvoidForbiddenZone(Map map, float forbiddenZoneCushion)
    {
        var d = (int)(forbiddenZoneCushion / map.Resolution);
        map.MaxPriority = -1;
        var pixels = map.EnumeratePixels();
        var len = pixels.Length;
        for (var i = 0; i < len; ++i)
        {
            ref readonly var p = ref pixels[i];
            ref readonly var px = ref p.x;
            ref readonly var py = ref p.y;
            var cellIndex = map.GridToIndex(px, py);
            if (map.PixelMaxG[cellIndex] == float.MaxValue)
            {
                var hasDangerousNeighbour = false;
                for (var ox = -1; ox <= 1 && !hasDangerousNeighbour; ++ox)
                {
                    for (var oy = -1; oy <= 1; ++oy)
                    {
                        if (ox == 0 && oy == 0)
                        {
                            continue;
                        }
                        var (nx, ny) = map.ClampToGrid((px + ox * d, py + oy * d));
                        if (map.PixelMaxG[map.GridToIndex(nx, ny)] != float.MaxValue)
                        {
                            hasDangerousNeighbour = true;
                            break;
                        }
                    }
                }

                if (hasDangerousNeighbour)
                {
                    map.PixelPriority[cellIndex] -= 0.125f;
                }
            }
            map.MaxPriority = Math.Max(map.MaxPriority, map.PixelPriority[cellIndex]);
        }
    }

    public static void RasterizeForbiddenZones(Map map, (Func<WPos, float> shapeDistance, DateTime activation, ulong source)[] zones, DateTime current, ref float[] scratch, float activationTimeCushion = ActivationTimeCushion)
    {
        // 1) Cluster activation times
        // very slight difference in activation times cause issues for pathfinding - cluster them together
        var zonesFixed = new (Func<WPos, float> shapeDistance, float g)[zones.Length];
        DateTime clusterEnd = default, globalStart = current, globalEnd = current.AddSeconds(120d);
        float clusterG = 0;
        var lenZonesFixed = zonesFixed.Length;
        for (var i = 0; i < lenZonesFixed; ++i)
        {
            ref var zone = ref zones[i];
            var activation = zone.activation.Clamp(globalStart, globalEnd);
            if (activation > clusterEnd)
            {
                clusterG = ActivationToG(activation, current, activationTimeCushion);
                clusterEnd = activation.AddSeconds(0.5d);
            }
            zonesFixed[i] = (zone.shapeDistance, clusterG);
        }

        var width = map.Width;
        var height = map.Height;
        var lenPixelMaxG = map.PixelMaxG.Length;

        var resolution = map.Resolution;
        var cushion = resolution * 0.5f;
        map.MaxG = clusterG;

        // 🔴 `ref` 不能省。原本是傳值，下面的 `scratch = new float[...]` 只改到本地參數，
        //    呼叫端的 ctx.Scratch 永遠停在 `[]`（Context 的初值）⇒ 這個條件**每次都成立**，
        //    等於每幀白配一個 lenPixelMaxG 大小的 float 陣列（120x120 的地圖＝14400 float／57.6KB）
        //    再整個丟掉。改成 ref 之後緩衝區長在 Context 上，只在地圖變大時重配一次。
        // ⚠️ 這**不會製造新的資料競態**：同一個 Context 的 map.PixelMaxG／PixelPriority
        //    本來就是同樣「只增不減、跨呼叫重複使用」的緩衝區（Map.Init 就是這個寫法），
        //    所以若真有兩次 Build 在同一個 Context 上重疊，權重場早就先被寫壞了，
        //    scratch 變成常駐並沒有新增任何共用面。
        //    單次呼叫之內也安全：Parallel.For 會等全部工作完成才返回，
        //    pass #1 寫、pass #2 才讀，兩者之間有隱含的屏障。
        if (scratch.Length < lenPixelMaxG)
            scratch = new float[lenPixelMaxG];
        // ref 參數不能被 lambda 捕捉（CS1628），所以取一個本地別名給下面的 Parallel.For 用。
        // 它與 scratch 指向**同一個陣列**，而成長過的那份在上一行已經回寫給呼叫端了。
        var buf = scratch;
        // 只填這次真的會用到的前段（與 Map.Init 對 PixelMaxG 的做法一致）：
        // 緩衝區可能比這次的地圖大，尾段不會被任何一個 pass 讀到。
        Array.Fill(buf, float.MaxValue, 0, lenPixelMaxG);

        var dy = map.LocalZDivRes * resolution * resolution;
        var dx = dy.OrthoL();
        var topLeft = map.Center - (width >> 1) * dx - (height >> 1) * dy;

        // note that a zone can partially intersect a pixel; so what we do is check each corner and set the maxg value of a pixel equal to the minimum of 4 corners
        // to avoid 4x calculations, we do a slightly tricky loop:
        // - outer loop fills row i to with g values corresponding to the 'upper edge' of cell i
        // - inner loop calculates the g value at the left border, then iterates over all right corners and fills minimums of two g values to the cells
        // - second outer loop calculates values at 'bottom' edge and then updates the values of all cells to correspond to the cells rather than edges
        // - third loops checks center and surrounding circle until cell edge to counter small cones not intersecting corners of a cell

        // --------------------------------------------------------------
        // PASS #1 (Parallel over rows): compute min corner G in scratch
        // --------------------------------------------------------------

        // This pass sets: scratch[iCell] = min(G-of-left-corner, G-of-right-corner)
        // for each pixel in row y, from x=0..width-1.
        Parallel.For(0, height, y =>
        {
            var rowStart = y * width;
            var rowCorner = topLeft + y * dy;

            var leftPos = rowCorner;
            var leftG = CalculateMaxG(ref zonesFixed, leftPos);

            for (var x = 0; x < width; ++x)
            {
                var rightPos = leftPos + dx;
                var rightG = CalculateMaxG(ref zonesFixed, rightPos);
                buf[rowStart + x] = Math.Min(leftG, rightG);
                leftPos = rightPos;
                leftG = rightG;
            }
        });

        // --------------------------------------------------------------
        // PASS #2 (Parallel over columns): combine top corners with bottom
        // --------------------------------------------------------------
        //
        // This takes the 'top' corners from scratch[] and merges them with
        // the 'bottom' corners for each column. We can parallelize
        // by letting each thread handle one column of pixels. Since each
        // column is independent of others, there's no write collision.
        //
        // We'll track how many cells become blocked in a thread-local counter
        // and aggregate it with Interlocked.Add.

        var numBlockedCells = 0;

        Parallel.For(0, width, x =>
        {
            // Each column starts from the same 'bottom corner' approach:
            // But we can compute "bottom corners" for this column now.
            // The bottom row's corner is topLeft + height*dy + x*dx
            // because at the end of pass #1, 'cy' was top-left + (height)*dy.
            var cyBottom = topLeft + height * dy + x * dx;
            var bleftG = CalculateMaxG(ref zonesFixed, cyBottom);

            var columnStart = x;
            var localBlocked = 0; // local aggregator

            var bottomG = bleftG;
            for (var y = height - 1; y >= 0; y--)
            {
                var jCell = columnStart + y * width;
                // top corner from pass #1
                var topG = buf[jCell];
                ref var pixelMaxG = ref map.PixelMaxG[jCell];
                var cellG = Math.Min(Math.Min(topG, bottomG), pixelMaxG);

                pixelMaxG = cellG;
                if (cellG != float.MaxValue)
                {
                    map.PixelPriority[jCell] = float.MinValue;
                    localBlocked++;
                }
                bottomG = topG;
            }

            // Merge local count
            Interlocked.Add(ref numBlockedCells, localBlocked);
        });

        // --------------------------------------------------------------
        // PASS #3 (Parallel): check each pixel center to catch partial overlaps, this is needed because small cones might not intersect corners
        // with a cushion of cellsize / 2 this ensures the entire inner circle until the edge will be safe
        // --------------------------------------------------------------
        Parallel.For(0, lenPixelMaxG, idx =>
        {
            var (px, py) = map.IndexToGrid(idx);
            var centerPos = map.GridToWorld(px, py, 0.5f, 0.5f);

            var centerG = CalculateMaxG(ref zonesFixed, centerPos, cushion);
            var oldVal = map.PixelMaxG[idx];
            if (centerG < oldVal)
            {
                map.PixelMaxG[idx] = centerG;
                if (oldVal == float.MaxValue)
                {
                    map.PixelPriority[idx] = float.MinValue;
                    Interlocked.Increment(ref numBlockedCells);
                }
            }
        });

        // --------------------------------------------------------------
        // PASS #4: if absolutely everything is blocked, free the "least dangerous"
        // --------------------------------------------------------------
        //  - We need the actual max of map.PixelMaxG to know which ones to free
        //  - First parallel pass: find max
        //  - Second parallel pass: free cells with that max

        if (numBlockedCells == width * height)
        {
            // 4a) find the real max
            var realMaxG = float.MinValue;
            // parallel reduction
            Parallel.For(0, lenPixelMaxG, () => float.MinValue,
                (i, loopState, localMax) =>
                {
                    ref var val = ref map.PixelMaxG[i];
                    return (val > localMax) ? val : localMax;
                },
                localMax =>
                {
                    // Merge local maxima with an atomic
                    float initVal, computedVal;
                    do
                    {
                        initVal = realMaxG;
                        computedVal = Math.Max(initVal, localMax);
                    }
                    while (initVal != Interlocked.CompareExchange(
                        ref realMaxG, computedVal, initVal));
                }
            );

            // 4b) free pixels that match that max
            Parallel.For(0, lenPixelMaxG, i =>
            {
                ref var pixelMaxG = ref map.PixelMaxG[i];
                if (pixelMaxG == realMaxG)
                {
                    pixelMaxG = float.MaxValue;
                    map.PixelPriority[i] = 0f;
                }
            });
        }
    }

    public static void RasterizeGoalZones(Map map, Func<WPos, float>[] goals)
    {
        var resolution = map.Resolution;
        var width = map.Width;
        var height = map.Height;
        var dy = map.LocalZDivRes * resolution * resolution;
        var dx = dy.OrthoL();
        var topLeft = map.Center - (width >> 1) * dx - (height >> 1) * dy;
        var len = goals.Length;

        // We'll do two passes:
        //    Pass #1: row-by-row (parallel over y)
        //    Pass #2: column-by-column (parallel over x)

        //------------------------------------------------------------------------
        // PASS #1 (row-based) - fill in partial priorities in map.PixelPriority
        //------------------------------------------------------------------------
        Parallel.For(0, height, y =>
        {
            // For row y, compute the position of the 'left corner' in world coords
            var cy = topLeft + y * dy;

            // Sum up all goals at the left corner (x=0)
            float leftP = 0;
            for (var i = 0; i < len; ++i)
            {
                leftP += goals[i](cy);
            }

            // Now walk across the row from x=0..(width-1), computing right corner
            var rowStart = y * width;
            var cx = cy;
            for (var x = 0; x < width; ++x)
            {
                // Right corner for this pixel is cx = cy + x*dx
                cx += dx;
                float rightP = 0;
                for (var i = 0; i < len; ++i)
                {
                    rightP += goals[i](cx);
                }

                // Store the min in PixelPriority
                map.PixelPriority[rowStart + x] = Math.Min(leftP, rightP);

                // Shift left -> right
                leftP = rightP;
            }
        });

        //------------------------------------------------------------------------
        // PASS #2 (column-based) - combine top (in PixelPriority) with bottom corners
        //------------------------------------------------------------------------
        // We also update map.MaxPriority here. Each thread will keep a local maximum
        // and we'll merge them in a thread-safe way.
        var globalMaxPriority = float.MinValue;

        // We'll compute the bottom-left corner *once* per column. The bottom row is
        // topLeft + height*dy. Then we move right by x*dx for each column.
        var bottomRowLeft = topLeft + height * dy;  // world coords for left corner of the *bottom* row

        Parallel.For(0, width, () => float.MinValue,
        (x, loopState, localMax) =>
        {
            // For column x, compute the bottom-left corner
            var cyBottom = bottomRowLeft + x * dx;

            // The 'left' bottom corner's priority
            float bleftP = 0;
            for (var i = 0; i < len; ++i)
            {
                bleftP += goals[i](cyBottom);
            }

            var bottomP = bleftP;
            var iCell = (height - 1) * width + x;

            for (var y = height - 1; y >= 0; --y, iCell -= width)
            {
                var topP = map.PixelPriority[iCell];

                // If this pixel is not blocked (PixelMaxG == float.MaxValue),
                // we keep the min of topP and bottomP. Otherwise, we set it to float.MinValue.
                if (map.PixelMaxG[iCell] == float.MaxValue)
                {
                    var cellP = Math.Min(topP, bottomP);
                    map.PixelPriority[iCell] = cellP;

                    // Update local max
                    if (cellP > localMax)
                        localMax = cellP;
                }
                else
                {
                    // Mark blocked areas
                    map.PixelPriority[iCell] = float.MinValue;
                }

                // Shift bottom -> top for next iteration
                bottomP = topP;
            }

            // Return thread-local max for final merge
            return localMax;
        },
        // Final merge across threads:
        localMax =>
        {
            float initVal, newVal;
            do
            {
                initVal = globalMaxPriority;
                newVal = Math.Max(initVal, localMax);
            }
            while (initVal != Interlocked.CompareExchange(
                ref globalMaxPriority, newVal, initVal));
        });

        // Finally store the global maximum in map.MaxPriority
        map.MaxPriority = globalMaxPriority;
    }
    private static float ActivationToG(DateTime activation, DateTime current, float activationTimeCushion) => Math.Max(0f, (float)(activation - current).TotalSeconds - activationTimeCushion);

    private static float CalculateMaxG(ref (Func<WPos, float> shapeDistance, float g)[] zones, WPos p, float cushion = 0f)
    {
        var len = zones.Length;
        var threshold = cushion;
        for (var i = 0; i < len; ++i)
        {
            ref var z = ref zones[i];
            if (z.shapeDistance(p) < threshold)
                return z.g;
        }
        return float.MaxValue;
    }

    private static (WPos? first, WPos? second) GetFirstWaypoints(ThetaStar pf, Map map, int cell, WPos startingPos)
    {
        ref var startingNode = ref pf.NodeByIndex(cell);
        var iterations = 0; // iteration counter to prevent rare cases of infinite loops
        var maxIterations = map.Width * map.Height;

        if (startingNode.GScore == 0f && startingNode.PathMinG == float.MaxValue)
            return (null, null); // we're already in safe zone

        var nextCell = cell;
        do
        {
            ref var node = ref pf.NodeByIndex(cell);
            if (pf.NodeByIndex(node.ParentIndex).GScore == 0f || ++iterations == maxIterations)
            {
                //var dest = pf.CellCenter(cell);
                // if destination coord matches player coord, do not move along that coordinate, this is used for precise positioning
                var destCoord = map.IndexToGrid(cell);
                var playerCoordFrac = map.WorldToGridFrac(startingPos);
                var playerCoord = Map.FracToGrid(playerCoordFrac);
                var dest = map.GridToWorld(destCoord.x, destCoord.y, destCoord.x == playerCoord.x ? playerCoordFrac.X - playerCoord.x : 0.5f, destCoord.y == playerCoord.y ? playerCoordFrac.Y - playerCoord.y : 0.5f);

                var next = pf.CellCenter(nextCell);
                return (dest, next);
            }
            nextCell = cell;
            cell = node.ParentIndex;
        }
        while (true);
    }
}
