using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BossMod.Global.DeepDungeon;

[ConfigDisplay(Name = "Auto-DeepDungeon (Experimental)", Parent = typeof(ModuleConfig))]
public sealed class AutoDDConfig : ConfigNode
{
    private static readonly Vector4 PrereqOkColor = new(0.45f, 0.80f, 0.45f, 1f);
    private static readonly Vector4 PrereqBadColor = new(0.95f, 0.70f, 0.30f, 1f);
    private static readonly Vector4 PrereqNoteColor = new(0.72f, 0.72f, 0.72f, 1f);

    /// <summary>
    /// 這一整頁的兩個前置條件，以及它們<b>現在</b>的狀態。
    /// </summary>
    /// <remarks>
    /// 🔑 為什麼需要：兩個閘門都在別的地方，而且失敗形式都是
    /// 「設定頁看起來一切正常、選項全部勾好，但什麼都不會發生」。
    /// <list type="number">
    /// <item>
    /// <b>模組成熟度</b>：深牢的區域模組全部標成 <c>Maturity.WIP</c>，而
    /// <c>ZoneModuleConfig.MinMaturity</c> 的預設值是 <c>Contributed</c>。
    /// <c>ZoneModuleRegistry.CreateModule</c> 的條件是「模組成熟度 &gt;= 要求的最低成熟度」，
    /// 而 WIP 比 Contributed <b>低</b> ⇒ <b>預設值下整個深牢模組根本不會被建立</b>，
    /// 連小地圖都不會出現。設定頁卻照樣完整渲染 —— 設定節點與模組的生命週期是分開的。
    /// </item>
    /// <item>
    /// <b>BMR 的 AI</b>：這一頁大部分選項最後都是寫進 <c>AIHints</c> 的
    /// <c>GoalZones</c>／<c>ForbiddenZones</c>，而那兩者只有 AI 的導航在讀，
    /// AI 沒開就是把提示算出來丟掉。（例外是自動選怪，它走
    /// <c>Hints.ForcedTarget</c>，由 <c>Plugin.ExecuteHints</c> 無條件執行。）
    /// </item>
    /// </list>
    /// ⚠️ 這裡讓設定節點去讀 <c>AIManager</c> 的執行期狀態，是刻意接受的分層破壞：
    /// 前置條件寫成靜態文字沒有用，使用者要知道的是「我現在缺的是哪一個」。
    /// </remarks>
    public override void DrawHeader(UITree tree, WorldState ws)
    {
        var minMaturity = ZoneModuleManager.Config.MinMaturity;
        // 深牢模組全是 WIP，而 WIP 是最低的一級 ⇒ 只有把要求也放到 WIP 才載入得起來
        var maturityOk = minMaturity <= BossModuleInfo.Maturity.WIP;
        var aiOn = AI.AIManager.Instance?.Beh != null;
        var (maturityKey, maturityFallback) = MaturityName(minMaturity);
        var maturityName = Loc.T(maturityKey, maturityFallback);

        ImGui.TextColored(PrereqNoteColor, Loc.T("DD_PrereqTitle", "Preconditions for everything on this page:"));

        ImGui.TextColored(maturityOk ? PrereqOkColor : PrereqBadColor, string.Format(
            maturityOk
                ? Loc.T("DD_PrereqMaturityOk", "1. Zone module maturity is set to \"{0}\" - the deep dungeon module can load.")
                : Loc.T("DD_PrereqMaturityBad", "1. Zone module maturity is set to \"{0}\", but the deep dungeon module is work-in-progress and will NOT load. Lower it in the \"Full duty automation\" tab; until then nothing on this page does anything and the minimap will not appear either."),
            maturityName));

        ImGui.TextColored(aiOn ? PrereqOkColor : PrereqBadColor,
            aiOn
                ? Loc.T("DD_PrereqAIOn", "2. BMR's AI is running.")
                : Loc.T("DD_PrereqAIOff", "2. BMR's AI is off - options that move your character will not do anything until you turn it on."));

        ImGui.TextColored(PrereqNoteColor, Loc.T("DD_PrereqNote", "Automatic mob targeting works without the AI. Trap avoidance, walking to coffers and walking to the Cairn of Passage all need it."));

        // 🔑 第三個閘門，而且是最容易讓人以為「設定壞了」的那一個：五個設定其實是被
        //    autorotation 模組讀走的，模組沒進 preset 就完全不會執行，而設定頁上看不出來。
        //    （模組本身還有一層：AIBehaviour 的 `Preset = target.Target != null ? … : null`
        //    ——沒有目標時整條管線是關的。）
        ImGui.TextColored(PrereqNoteColor, Loc.T("DD_PrereqRotationModule",
            "Note: the four \"Kite\" settings and \"also drink the deep dungeon's own potions\" are consumed by the \"Deep Dungeon AI\" autorotation module, not by this module. They do nothing unless that module is part of your active preset with the matching track switched on - and that module only runs while you have a target."));

        ImGui.Separator();
    }

    /// <summary>
    /// 成熟度的短名稱（loc key ＋ 英文 fallback）。
    /// </summary>
    /// <remarks>
    /// 列舉本身的 <c>PropertyDisplay</c> 是整句說明，塞進這一行太長，所以另外給短名稱。
    /// ⚠️ fallback 必須是真的英文字，<b>不能拿 key 自己當 fallback</b> ——
    /// 那樣譯文一旦缺漏，使用者看到的會是 <c>DD_MaturityWIP</c> 這種原始鍵名。
    /// </remarks>
    private static (string Key, string Fallback) MaturityName(BossModuleInfo.Maturity m) => m switch
    {
        BossModuleInfo.Maturity.WIP => ("DD_MaturityWIP", "work in progress"),
        BossModuleInfo.Maturity.Contributed => ("DD_MaturityContributed", "contributed"),
        _ => ("DD_MaturityVerified", "verified"),
    };

    public enum ClearBehavior
    {
        [PropertyDisplay("Do not auto target")]
        None,
        [PropertyDisplay("Stop when passage opens")]
        Passage,
        [PropertyDisplay("Target everything if not at level cap, otherwise stop when passage opens")]
        Leveling,
        [PropertyDisplay("Target everything")]
        All,
    }

    [PropertyDisplay("Enable module", tooltip: "WARNING: This feature is very experimental and most likely will contain bugs or unintended behavior.\nTo enable this feature in its current state, you must activate 'Work-in-Progress' maturity modules in the `Full Duty Automation` tab.")]
    public bool Enable = true;
    [PropertyDisplay("Enable minimap")]
    public bool EnableMinimap = true;
    [PropertyDisplay("Player marker size", tooltip: "Size of the arrow marking your own position on the minimap, relative to its original size. The arrow is 64px wide inside an 88px cell, which makes it spill over the room you are standing in; shrink it if it hides the coffer icons.")]
    [PropertySlider(0.4f, 1.5f, Speed = 0.01f)]
    public float PlayerMarkerScale = 0.7f;

    // 📌 預設開：純顯示、零自動化，而且只在「偵測到怪」的格子寫一個數字——
    //    沒偵測到的格子完全不動，所以不會製造持續性的視覺噪音；
    //    通道石開啟後還會自動淡化（那時候找剩怪已經沒有價值了）。
    [PropertyDisplay("Show how many enemies are in each room on the minimap",
        tooltip: "Writes a small count in the corner of every room where enemies are currently detected. Mainly for finding the last few mobs while the Cairn of Passage has not unlocked yet - the count is highlighted while it is still locked, and fades once it opens.\n\nIMPORTANT: only enemies loaded around you exist as far as any plugin is concerned, so a room with no number is NOT necessarily clear - it may simply be too far away. Rooms are therefore never marked as empty.")]
    public bool ShowRoomEnemies = true;

    [PropertyDisplay("Try to avoid traps", tooltip: "Avoid known trap locations sourced from PalacePal data. Does not need PalacePal installed since data is included in BMR. (Traps revealed by a Pomander of Sight will always be avoided regardless of this setting.)")]
    public bool TrapHints = true;

    // 🔴 預設 true。純資料增益：拿不到就退回 BMR 內建的那份表，也就是今天的行為，
    //    所以預設開不可能讓任何人變差。合約版本對不上一樣整條停用。
    // 🔴 2026-08-10 使用者裁決：拿掉「PalacePal 記載過的埋藏寶藏」顯示之後，這個開關只剩下
    //    陷阱資料一個作用，文案必須跟著改成準確描述——留著「與埋藏寶藏資料」會變成謊話，
    //    而且使用者是靠關掉這個開關去消寶藏噪音的，語意對不上會讓他找不到正確的開關。
    //    ⚠️ BMR 的 Loc key 就是英文原文：改英文＝loc/tw.json 的鍵要一起搬。
    [PropertyDisplay("Also use PalacePal's own trap data when it is installed",
        tooltip: "BMR ships a snapshot of PalacePal's trap data, but PalacePal itself keeps learning new locations. With this on, whatever PalacePal currently knows for this deep dungeon is merged in on top of the built-in table (duplicates within 1y are dropped).\n\nIf PalacePal is not installed, is disabled, or speaks a version of the interface this build does not know, everything silently falls back to the built-in table - nothing breaks and nothing changes from before.\n\nTraps only. PalacePal's Accursed Hoard records are deliberately not drawn: that list covers all ten floors of the deep dungeon at once, so on a single floor's 5x5 map it lights up almost every room - that is noise, not information. PalacePal also draws its own hoard markers in the world already. The hoards BMR does show come from the objects the game itself places, and are unaffected by this setting.\n\nRead-only - BMR never writes anything back to PalacePal.")]
    public bool UsePalacePal = true;

    // 🔴 預設關（opt-in）。使用者已裁決過「不畫 PalacePal 來源的埋藏寶藏標記」；這裡接的是
    //    語意不同的新端點（只回當下看得到的實體），但要不要重新亮起來屬於新的裁決，不自己做。
    //    關著的時候 _palVisibleHoards 恆為空，行為與加這個開關之前逐字相同。
    [PropertyDisplay("Also show Accursed Hoard that only PalacePal can currently see",
        tooltip: "BMR already finds the Accursed Hoard itself, straight from the object the game places at the spot, and that is not affected by this setting. This only adds entities that PalacePal can see right now and BMR's own scan did not pick up; anything within 5y of something BMR already found is dropped, so while everything works normally you will see no difference at all.\n\nWhy it exists: BMR skips game objects without a valid network id, while PalacePal reads the object table unfiltered. Whether the unnamed buried-hoard object always carries one cannot be checked outside the game, so this is the fallback for the case where it does not.\n\nThis is NOT PalacePal's location database. That list covers all ten floors of the deep dungeon at once and lights up almost every room, and is still deliberately not drawn. Only what PalacePal can see on this floor, this run, is used, and only while its snapshot is less than a second old.\n\nNeeds 'show the buried Accursed Hoard' above, and does nothing unless PalacePal is installed and speaks its newer 'visible locations' interface. Off by default.\n\nRead-only - BMR never writes anything back to PalacePal.")]
    public bool UsePalacePalVisibleHoard = false;
    [PropertyDisplay("Automatically navigate to Cairn of Passage")]
    public bool AutoPassage = true;

    // 🔴 預設 false（opt-in），而且這是刻意的裁決而不是保守：使用者要的原話只有「走到通道石旁邊」，
    //    「自動下一層」是多出來的一步，由他自己勾。關著的時候行為與拆分前逐字相同
    //    ——在此之前這條路徑<b>從來沒有</b>設過 InteractWithTarget（TryGetRoomDestination 的註解
    //    「只是走過去，絕不自動互動」講的就是這件事）。
    // 📌 只加「最後那一下」，不加任何移動來源：走過去仍然是 AutoPassage 那兩個 GoalZone 的事，
    //    這個開關只在人已經站到互動距離內時才把傳送裝置設成互動目標。
    // ⚠️ 遊戲自己的「要前往下一層嗎」確認視窗<b>不</b>由 BMR 回答：BMR 全庫沒有任何
    //    SelectYesno／addon callback 的處理管線（實測：GetAddonByName 只有讀詠唱條與 debug 視窗兩處），
    //    不為了這個功能新增一條盲按。所以最終那一下「是」仍然是使用者按的，tooltip 必須講清楚。
    [PropertyDisplay("Also interact with the Cairn of Passage once you have reached it",
        tooltip: "Needs \"automatically navigate to Cairn of Passage\" above - this setting only adds the final click, it never walks you anywhere by itself.\n\nOff by default: navigating there walks you next to it and stops, leaving the decision to descend to you. With this ticked, once you are standing in interact range, out of combat and not transformed, the interact is sent for you.\n\nThe game then asks you to confirm in its own dialog, and BMR does NOT answer that dialog - you still press Yes yourself, so a floor is never left by accident.\n\nOnly fires while the Cairn is actually unlocked, and never while \"reveal all rooms before proceeding to next floor\" still has rooms left to visit. Coffers keep exactly the priority configured above.\n\nLike \"automatically open coffers you have reached\", this needs BMR's AI (or the Normal Movement autorotation module) to be running - it is what actually sends the interact.")]
    public bool AutoUsePassage = false;

    [PropertyDisplay("Automatic mob targeting behavior")]
    public ClearBehavior AutoClear = ClearBehavior.Leveling;

    // 🔴 預設 false（opt-in）。這是新行為，不是把既有行為拆開：在此之前「自動選怪」真的就只有
    //    選怪——被動怪的優先度是 -3，AI 的走向目標那條路徑選不到它，所以角色從不主動靠過去。
    // 📌 2026-08-17 語意放寬成全職業（原本硬性限定坦克）。當初限定坦克的顧慮是
    //    「非坦克走過去是送死」，但那個顧慮的來源是**距離**不是職業：現在接近距離看職能
    //    （AutoClear.PullApproachRange），遠程／法系／治療停在 15y 開火而不是走到臉上。
    //    ⚠️ 欄位名維持 TankPull 不改：改名會讓既有使用者的設定值靜默歸零（EzConfig 是照
    //    欄位名反序列化的），而這個開關他已經勾起來了。顯示文字改掉就夠了。
    [PropertyDisplay("Also walk up to the mob that was picked, to start the pull",
        tooltip: "\"Automatic mob targeting\" above only picks a target - it never moves you, so passive mobs are targeted and then ignored. With this on, your character also moves into range of that target and fires an opener to actually start the pull.\n\nTanks and melee walk into melee range; ranged, casters and healers stop about 15y away and open from there, so they are not sent into melee. Monk is the only job with no ranged opener, so it walks in.\n\nOnly while out of combat: it stops as soon as anything has aggro (see the pull limit below), while transformed, and while your HP is under the \"stop travelling below\" threshold. It never overrides trap avoidance or AOE dodging.\n\nOff by default; with it off nothing about targeting changes.")]
    public bool TankPull = false;

    // ⚠️ 舊文案是「暫停導航前可拉取的最大怪物數」，讀起來像是「戰鬥中會不會走位」，
    //    但它其實只管「要不要繼續趕往目標房間」——閃避與戰鬥走位永遠是開著的。
    [PropertyDisplay("Keep travelling until this many mobs have aggro (0 = stop travelling as soon as you are in combat)",
        tooltip: "Only controls travelling towards the destination room. Dodging and combat positioning are always active regardless of this value.\n\n0 means you stop heading for the room the moment anything aggros you. Higher values let you keep moving while that many mobs are already on you - useful for pulling several packs at once.")]
    [PropertySlider(0, 15)]
    public int MaxPull = 0;

    [PropertyDisplay("Stop travelling below this much HP (%)",
        tooltip: "Pauses travelling to the destination room while your HP is below this percentage, so you do not walk into the next pack at low health. Dodging and combat positioning are unaffected.\n\n0 disables this.")]
    [PropertySlider(0, 90)]
    public int StopTravelBelowHPPercent = 0;
    // ⚠️ 標籤上的「僅厄運迷宮」不是保守說法，是實測結果：這個旗標唯一的讀取點是
    //    AutoClear.AddLOS()，而 AddLOS() 全庫只有 EOFloorModule 呼叫（五處）。
    //    PalaceFloorModule 與 HoHFloorModule 零呼叫 ⇒ 在死者宮殿與天之逆焰是死鍵。
    [PropertyDisplay("Try to use terrain to LOS attacks (Eureka Orthos only)",
        tooltip: "Only has any effect in Eureka Orthos: it is the only deep dungeon whose floor module marks casts as line-of-sight-able. In Palace of the Dead and Heaven-on-High this setting does nothing at all.\n\nWhen it does apply, it replaces the simple \"stay out of a circle around the caster\" hint with one computed from the actual terrain, so you can break line of sight instead of just running away. Falls back to the simple circle if no obstacle map is available for the floor.")]
    public bool AutoLOS = false;

    // 🔴 預設 None＝這個功能整個關著，既有行為一個位元都不變。
    // 📌 使用者裁決的原話：「不然做成按著指定按鍵時 一直觸發?」——語意是一個明確的
    //    「現在交給你走」開關：按住期間強制趕路，放開立刻停、控制權回到使用者手上。
    //    所以它刻意是**按住**而不是切換：切換會留下「我以為關了其實還開著」的狀態，
    //    而這個功能的重點正是「使用者隨時知道現在是誰在走」。
    // ⚠️ 只解除**趕路**的三道閘門（戰鬥中／拉怪數上限／低血量），不碰任何「互動」開關：
    //    自動開箱仍然看「Automatically open coffers you have reached」、
    //    自動下一層仍然看「Also interact with the Cairn of Passage once you have reached it」。
    //    按住這顆鍵不會讓你意外掉到下一層。
    // ⚠️ 閃避與戰鬥走位完全不經過這裡（它們住在 AIHints 的 ForbiddenZones／AIBehaviour），
    //    這顆鍵一個字都碰不到它們。
    [PropertyDisplay("Hold this key to force travelling",
        tooltip: "While the key is held, travelling to the destination room, to coffers and to the Cairn of Passage runs regardless of combat, the pull limit and the low-HP threshold above. Let go and it stops immediately.\n\nThis only forces the walking. Whether a coffer is opened, or the Cairn is actually used, still follows their own settings - holding the key can never drop you to the next floor by accident.\n\nDodging and combat positioning are unaffected either way. Off by default.")]
    public ActionTweaksConfig.ModifierKey ForceTravelKey = ActionTweaksConfig.ModifierKey.None;

    [PropertyDisplay("Automatically navigate to coffers",
        tooltip: "Walking only. Whether a coffer you have reached is actually opened is the separate setting below.")]
    public bool AutoMoveTreasure = true;

    // 🔴 預設 true＝維持既有行為。這個開關是把原本綁在 AutoMoveTreasure 上的「開箱」語意拆出來的，
    //    不是新功能：拆分前 `AutoMoveTreasure` 關掉之後，只要人走到寶箱 3.5y 內仍然會自動開，
    //    因為判斷式是 `(AutoMoveTreasure && canNavigate) || 距離 < 3.5f`（&& 比 || 優先），
    //    與「自動移動至寶箱」這個標籤的字面意思不符。
    [PropertyDisplay("Automatically open coffers you have reached",
        tooltip: "Opens a coffer once you are next to it. This is separate from walking to it: with this on and \"automatically navigate to coffers\" off, nothing drags you anywhere, but a coffer you walked up to yourself still gets opened.\n\nNote this needs BMR's AI (or the Normal Movement autorotation module) to be running - it is what actually sends the interact.")]
    public bool AutoOpenTreasure = true;
    [PropertyDisplay("Prioritize opening coffers over Cairn of Passage")]
    public bool OpenChestsFirst = false;
    [PropertyDisplay("Open gold coffers")]
    public bool GoldCoffer = true;
    // ⚠️ tooltip 寫的是 OpenSilver（AutoClear.cs）真正的判斷式，不是概略描述：
    //    HP <= 70% 直接 false（爆炸銀箱打 70% 最大 HP）；武器+防具 < 198 才無條件 true；
    //    到 198 之後死者宮殿一律 false，天之逆焰／厄運迷宮則放寬成「樓層 >= 7」。
    [PropertyDisplay("Open silver coffers",
        tooltip: "Ticking this is necessary but not sufficient - silver coffers can explode for 70% of your max HP, so they are also skipped when:\n- your current HP is at or below 70% of maximum;\n- your weapon + armour levels add up to 198 or more, AND you are in Palace of the Dead (there is nothing left to gain there).\n\nIn Heaven-on-High and Eureka Orthos, once you are at 198 they are only opened from floor 7 onwards, where magicite/demiclones start dropping.")]
    public bool SilverCoffer = true;
    [PropertyDisplay("Open bronze coffers")]
    public bool BronzeCoffer = true;

    // 🔴 預設 true＝維持既有行為。埋藏的寶藏以前完全沒有閘門：候選判斷式裡它是
    //    `oid == (uint)OID.BandedCoffer`，後面沒有接任何 `&& Config.X`，
    //    所以銅銀金三個框全部關掉它照樣會去開。
    [PropertyDisplay("Open Accursed Hoard coffers",
        tooltip: "The banded coffers dug up from the Accursed Hoard. Until now these had no setting at all and were always handled, even with all three coffer types above unticked.\n\nAlso controls whether the glowing spot revealed by a Pomander of Intuition is walked to.")]
    public bool BandedCoffer = true;

    // 🔴 純顯示，零自動化：這個開關<b>不</b>改變任何走位或開箱行為，只決定畫不畫標記。
    //    要不要自動走過去仍然是上面 BandedCoffer + AutoMoveTreasure 的事。
    // 📌 預設 true 是刻意的。這不是回退既有行為 —— 埋藏的寶藏在此之前<b>完全沒有任何顯示</b>，
    //    小地圖與世界疊加層都沒有，所以預設開不會蓋掉任何人原本看得到的東西。
    //    （BMR 的設定反序列化是遍歷 JSON 鍵，既有使用者的檔案裡沒有這個鍵就會吃到新預設。）
    [PropertyDisplay("Show the buried Accursed Hoard",
        tooltip: "Marks the Accursed Hoard on the minimap and on the ground in the world.\n\nThe buried hoard is completely invisible in game until a Pomander of Intuition reveals it, so without this there is nothing to walk to. The marker comes straight from the object the game itself places at the spot - nothing is guessed and no location database is used, so if the game does not place the object, nothing is drawn rather than something wrong.\n\nDisplay only: it never moves you and never opens anything. The marker disappears once the hoard has been dug up.")]
    public bool ShowAccursedHoard = true;

    [PropertyDisplay("Manual \"walk to room\" button on the minimap (requires vnavmesh)",
        tooltip: "Adds a button under the minimap that walks you to the room you picked, in one go. You press it, it walks, and it stops on arrival - it never opens coffers, never uses the Cairn of Passage, and never starts the next leg by itself. The route does not avoid mobs.\n\nBefore anything moves, the whole route is checked against known trap locations - every stretch between corners, not just the corners themselves - and refused outright, with the reason shown under the minimap, if any part of it passes too close. It is never re-routed around a trap. Off by default.")]
    public bool ManualRoomWalk = false;

    // ── 風箏 ──────────────────────────────────────────────────────────
    // 📌 這幾個是「Deep Dungeon AI」自動循環模組的風箏參數。放在這一頁而不是模組的 track 選項，
    //    是因為 track 選項只吃列舉、給不了數值滑桿，而使用者要調的正是數值。
    //    預設值就是拆出來之前寫死的 9 / 25 / 0.05，拆分前後行為完全相同。
    [PropertyDisplay("Kite: stay at least this far from the target",
        tooltip: "The \"Deep Dungeon AI\" autorotation module's \"kite enemies\" option keeps ranged jobs and healers inside a ring around the target; this is the inner edge of that ring.\n\nMeasured hitbox to hitbox, so 0 would be touching.")]
    [PropertySlider(3f, 20f, Speed = 0.1f)]
    public float KiteMinDistance = 9f;

    [PropertyDisplay("Kite: but no further away than this",
        tooltip: "Outer edge of the kiting ring. Keep it inside your attack range, or you will kite yourself out of the fight.")]
    [PropertySlider(10f, 30f, Speed = 0.1f)]
    public float KiteMaxDistance = 25f;

    [PropertyDisplay("Kite: how strongly to prefer that ring",
        tooltip: "How much weight kiting gets in the AI's positioning. It competes with everything else the AI wants - dodging, positionals, following - and the default is deliberately small so that dodging always wins.\n\nRaise it if the character ignores kiting, but a large value will start fighting AOE avoidance.")]
    [PropertySlider(0.01f, 1f, Speed = 0.01f)]
    public float KiteWeight = 0.05f;

    // ── 仇恨迴避 ──────────────────────────────────────────────────────
    // 🔴 預設 0（opt-in），理由跟 TankPull 一模一樣：這是新的**移動**行為，
    //    而 BMR 的設定反序列化是遍歷 JSON 鍵（跟 EzConfig 相反）⇒ 新預設**既有使用者拿得到**。
    //    沒實機驗過的移動改動不該自己開起來，尤其是疊在剛驗證過的陷阱迴避／風箏／開拉之上。
    [PropertyDisplay("Route around mobs that have not noticed you yet",
        tooltip: "While travelling to the destination room and out of combat, mobs that have not noticed you yet get their detection range marked as a no-go area for pathfinding, so the route actually bends around it instead of walking straight through.\n\nSight mobs use a 90-degree cone along the direction they are facing, so hugging a wall or slipping behind them is a valid route. Sound and proximity mobs use a full circle, and so does any mob not in the data table.\n\nIt never traps you: shapes you are already standing in are skipped, and if you stop making progress for a couple of seconds the whole thing switches off for 6 seconds so you walk through anyway. Off by default.")]
    public bool AggroAvoid = false;

    // 📌 這條滑條是**半徑**，不是權重。0 ＝整個功能關閉（AddAggroAvoidance 的第一道閘門就檢查它）。
    //    ⚠️ 這裡原本掛著一段「滑條上限 9 必須 < 非戰鬥趕路權重 10」的註解，那是負權重版
    //       （AggroAvoidWeight，已於改用禁區時刪除）留下來的，對半徑完全不適用：
    //       禁區走的是 PixelMaxG／PathLeeway 那條路，**路徑成本根本不看目標區權重**，
    //       所以那條不等式已經沒有對應的東西。上限 20 純粹是「想繞得更保守」的餘裕。
    [PropertyDisplay("Assumed aggro radius (yalms, added to the mob's hitbox)",
        tooltip: "How far a mob is assumed to notice you - the radius of the cone or circle above, measured from the mob plus its hitbox.\n\n10 is the value NecroLens uses for deep dungeon mobs. 0 disables the whole feature.")]
    [PropertySlider(0f, 20f, Speed = 0.5f)]
    public float AggroAvoidRadius = 10f;

    // 🔴 預設 true。這個開關修的是一個靜默失效，完整說明見 AIHints.WantKiting。
    [PropertyDisplay("Kite: allow backing away while dodging",
        tooltip: "Whenever anything dangerous is telegraphed, the AI normally penalises any spot further from your target than where you stand now, so it does not drift away. That penalty is far stronger than the kiting preference, so without this, kiting silently does nothing while an AOE is up - which in a deep dungeon is most of the time.\n\nTicked, that one penalty is skipped while kiting is actually active. Dodging itself is completely unaffected - no dangerous spot ever becomes acceptable.\n\nUntick for the old behaviour.")]
    public bool KiteAllowRetreatWhileDodging = true;

    // 🔴 預設 false（opt-in）。BMR 的新設定欄位預設值會直接生效在既有使用者身上，
    //    而不是每個人都裝了 WrathCombo、也不是每個人都希望我們去動它。
    [PropertyDisplay("Pause WrathCombo's auto-rotation while in a deep dungeon",
        tooltip: "Uses WrathCombo's own lease mechanism to ask it to stop auto-rotating while you are inside a deep dungeon, so it does not fight BMR's rotation, and hands control back when you leave.\n\nReleasing the lease makes WrathCombo restore your settings itself, so nothing is left changed if the game or a plugin crashes.\n\nDoes nothing if WrathCombo is not installed.")]
    public bool SuspendWrathCombo = false;

    /// <summary>
    /// 深牢裡改用的自動循環 preset 名稱；<b>空字串＝不切換</b>（預設）。
    /// </summary>
    /// <remarks>
    /// 📌 刻意<b>沒有</b> <c>[PropertyDisplay]</c>：設定頁的通用繪製對 string 只會給一個文字輸入框，
    /// 而要使用者手打 preset 名字必然會打錯，打錯的表現又是「靜默不切換」。
    /// 改由 <see cref="DrawCustom"/> 畫成從 preset 資料庫枚舉出來的下拉選單。
    /// ⚠️ 少了 <c>PropertyDisplay</c> 不影響存檔：序列化的條件只有「非 static 且沒有 JsonIgnore」。
    /// </remarks>
    public string DeepDungeonPreset = "";

    /// <summary>
    /// 深牢專用 preset 的下拉選單。
    /// </summary>
    /// <remarks>
    /// ⚠️ preset 清單只有執行期才拿得到，而且只能透過 <c>AIManager.Instance</c>
    /// （與 <see cref="DrawHeader"/> 讀 AI 狀態是同一個已被接受的分層破壞）。
    /// AI 尚未建立時清單是空的 —— 這時<b>絕對不能</b>把設定值清成空字串，
    /// 否則使用者只是打開設定頁看一眼就把設定弄丟了。
    /// </remarks>
    public override void DrawCustom(UITree tree, WorldState ws)
    {
        var presets = AI.AIManager.Instance?.Autorot.Database.Presets.AllPresets;
        var current = DeepDungeonPreset;
        var label = string.IsNullOrEmpty(current) ? Loc.T("DD_PresetNone", "(do not switch)") : current;

        ImGui.SetNextItemWidth(Math.Min(ImGui.GetWindowWidth() * 0.4f, 260f));
        using (var combo = ImRaii.Combo(Loc.T("DD_PresetSwitch", "Autorotation preset to use inside deep dungeons"), label))
        {
            if (combo)
            {
                if (ImGui.Selectable(Loc.T("DD_PresetNone", "(do not switch)"), string.IsNullOrEmpty(current)))
                {
                    DeepDungeonPreset = "";
                    Modified.Fire();
                }

                if (presets != null)
                {
                    var count = presets.Count;
                    for (var i = 0; i < count; ++i)
                    {
                        var name = presets[i].Name;
                        if (ImGui.Selectable(name, name == current))
                        {
                            DeepDungeonPreset = name;
                            Modified.Fire();
                        }
                    }
                }
            }
        }

        // 設定裡有名字、但清單裡找不到（改名或刪除）—— 要看得見，不然表現只是「沒有切換」
        if (!string.IsNullOrEmpty(current) && presets != null && !presets.Any(p => string.Equals(p.Name, current, BossMod.Autorotation.PresetDatabase.NameComparison)))
            ImGui.TextColored(PrereqBadColor, string.Format(
                Loc.T("DD_PresetMissing", "Preset \"{0}\" no longer exists - nothing will be switched. Pick another one."), current));
        else if (presets == null)
            ImGui.TextColored(PrereqNoteColor, Loc.T("DD_PresetListUnavailable", "The preset list is only available once the AI has been initialised; the stored setting is kept as-is."));

        ImGui.TextColored(PrereqNoteColor, Loc.T("DD_PresetHint", "Switching happens in memory only - your saved AI preset setting is never rewritten, so a crash cannot leave you stuck on the deep dungeon preset."));

        ImGui.Separator();
        DrawPomanderAutoUseList();
    }

    [PropertyDisplay("Reveal all rooms before proceeding to next floor")]
    public bool FullClear = false;
    [PropertyDisplay("Allow automatic pomander use")]
    public bool AllowPomander = false;

    // 🔴 預設 true 是使用者明確要求的（「當開啟金寶箱 身上對應的魔陶器已滿 要可以自動使用 重新開箱」
    //    的姊妹需求）。這不是回退既有行為：以前這裡是一行 TODO，銀寶箱在魔石滿的時候
    //    會被**永久跳過**，那顆寶箱就此不開。
    [PropertyDisplay("Use up a magicite / demiclone when a silver coffer is skipped because you are at cap",
        tooltip: "Silver coffers in Heaven-on-High and Eureka Orthos can contain magicite / demiclones. If you already hold three, the game refuses the coffer and it used to be skipped forever.\n\nWith this on, one of the stones you are holding is used so the coffer can be opened.\n\nWhich one is picked is not arbitrary: on a boss floor Ifrit / Titan / Garuda only deal damage, while Odin defeats the boss outright, so Odin is kept until it is the only thing left. In Eureka Orthos, Unei and Doga are spent first and the Onion Knight is kept the same way.\n\nOnly ever fires for a coffer the game has already told us contains one (via the \"you cannot carry any more\" message).")]
    public bool AutoUseMagiciteWhenCapped = true;

    #region 逐魔陶器的自動使用開關

    /// <summary>
    /// 金寶箱裡的魔陶器已經滿三個時，允許自動用掉哪一種來騰位置。索引＝<see cref="PomanderID"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>預設值就是原本那份寫死的 15 種白名單，再加上「用了無害或有益」的那幾種</b>，
    /// 所以既有使用者不會少掉任何原本會自動使用的魔陶器。
    /// <para>
    /// 預設<b>關</b>的分兩類：①變身類（蠍尾獅化／魅魔化／基路伯化／恐慌裝甲化）——變身期間
    /// 整個模組都被 <c>IsPlayerTransformed</c> 擋住；②會改變樓層狀態而且可能不是你要的
    /// （改變敵人＝下一層某間房變成模仿怪、形態變化、魔法效果解除＝連自己的增益一起消掉）。
    /// </para>
    /// <para>
    /// ⚠️ 索引 0（<see cref="PomanderID.None"/>）永遠不用。陣列長度會在
    /// <see cref="Deserialize"/> 正規化，舊設定檔比列舉短時補上新項目的預設值，
    /// 而不是讓 UI 或消費端讀到索引外例外。
    /// </para>
    /// </remarks>
    public bool[] PomanderAutoUse = BuildDefaultPomanderAutoUse();

    /// <summary>
    /// 預設允許自動使用的魔陶器。效果描述逐條讀 <c>DeepDungeonItem</c> 表判定
    /// （該表第 1..35 列與 <see cref="PomanderID"/> 1..35 逐項對齊，已查表確認）。
    /// </summary>
    private static readonly PomanderID[] DefaultAutoUsable = [
        // ── 原本寫死的 15 種（維持既有行為）──────────────────────────
        PomanderID.Steel,            // 強化防禦：自身受到的傷害減輕 40%
        PomanderID.Strength,         // 強化自身：傷害與治療量 +30%
        PomanderID.Sight,            // 全景：點亮本層地圖與陷阱
        PomanderID.Raising,          // 復生：復活第一個倒下的隊員
        PomanderID.Fortune,          // 運氣上升：本層擊敗敵人時寶箱出現率上升
        PomanderID.Concealment,      // 隱形：自身與小隊成員隱形
        PomanderID.Affluence,        // 寶箱增加：增加下一層的寶箱
        PomanderID.Frailty,          // 弱化敵人：本層敵人攻防低下
        PomanderID.ProtoSteel,
        PomanderID.ProtoStrength,
        PomanderID.ProtoSight,
        PomanderID.ProtoRaising,
        PomanderID.ProtoLethargy,    // 緩速：本層敵人緩速
        PomanderID.ProtoFortune,
        PomanderID.ProtoAffluence,
        // ── 新增：用了無害或有益 ─────────────────────────────────────
        PomanderID.Safety,           // 咒印解除：清除本層所有陷阱
        PomanderID.Flight,           // 減少敵人：減少下一層的敵人（與「寶箱增加」同形狀，原本就預設開）
        PomanderID.Purity,           // 解咒：解除自身的詛咒狀態
        PomanderID.Intuition,        // 感知寶藏：顯示埋藏的寶藏位置
        PomanderID.Petrification,    // 石化敵人：本層敵人石化（模組本來就會優先打石化目標）
        PomanderID.ProtoSafety,
        PomanderID.ProtoStorms,      // 大漩渦：本層敵人 HP 降至個位數
        PomanderID.ProtoFlight,
        PomanderID.ProtoPurity,
        PomanderID.ProtoIntuition,
        // ── revision 1（2026-08-13 使用者指定）───────────────────────
        // 魔法效果解除：本層沒有魔法效果時遊戲會拒絕——那條由 AutoClear 的
        // 重試上限接手（3 次沒生效就本層放棄），不會卡佇列。
        PomanderID.Serenity,
        PomanderID.ProtoSerenity,
    ];

    private static bool[] BuildDefaultPomanderAutoUse()
    {
        var res = new bool[(int)PomanderID.Count];
        foreach (var p in DefaultAutoUsable)
            res[(int)p] = true;
        return res;
    }

    /// <summary>某一種魔陶器可不可以被自動用掉。索引超出存檔範圍時退回預設值。</summary>
    public bool CanAutoUsePomander(PomanderID p)
    {
        var i = (int)p;
        return (uint)i < (uint)PomanderAutoUse.Length && PomanderAutoUse[i];
    }

    /// <summary>
    /// 反序列化之後把陣列長度補回列舉長度。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這一步的失敗形式是：以後 <see cref="PomanderID"/> 加了新項目，
    /// 舊設定檔存的陣列比列舉短，UI 一畫就 <c>IndexOutOfRangeException</c>，
    /// 而且只有真的打開深牢設定頁的人才會踩到。
    /// 缺的那幾格補<b>新的預設值</b>，不是補 false ——「沒存過」等於「還沒表態」。
    /// </remarks>
    /// <summary>
    /// 白名單預設值的一次性遷移版本。
    /// </summary>
    /// <remarks>
    /// 🔴 既有使用者的 <see cref="PomanderAutoUse"/> 已整條陣列存進設定檔，
    /// 改 <see cref="DefaultAutoUsable"/> 只有全新安裝的人吃得到。要把新預設帶給
    /// 既有使用者只能做一次性遷移（本欄位記到哪一版）。使用者遷移後手動關掉的話，
    /// 下次存檔會連本欄位一起存，不會被重複翻回來。
    /// </remarks>
    public int PomanderAutoUseRevision;

    public override void Deserialize(System.Text.Json.JsonElement j, System.Text.Json.JsonSerializerOptions ser)
    {
        base.Deserialize(j, ser);

        var want = (int)PomanderID.Count;
        if (PomanderAutoUse.Length != want)
        {
            var fixedUp = BuildDefaultPomanderAutoUse();
            var n = Math.Min(PomanderAutoUse.Length, want);
            Array.Copy(PomanderAutoUse, fixedUp, n);
            PomanderAutoUse = fixedUp;
        }

        // revision 1：魔法效果解除加入自動使用（2026-08-13 使用者裁決「不會使用」是缺陷）
        if (PomanderAutoUseRevision < 1)
        {
            PomanderAutoUseRevision = 1;
            if ((int)PomanderID.Serenity < PomanderAutoUse.Length)
                PomanderAutoUse[(int)PomanderID.Serenity] = true;
            if ((int)PomanderID.ProtoSerenity < PomanderAutoUse.Length)
                PomanderAutoUse[(int)PomanderID.ProtoSerenity] = true;
        }
    }

    /// <summary>
    /// 魔陶器的顯示名稱。
    /// </summary>
    /// <remarks>
    /// 🔑 直接讀客戶端的 <c>DeepDungeonItem</c> 表，<b>不走 loc 檔</b>——
    /// 這樣拿到的一定是該語系客戶端裡的官方名稱，不必維護 35 條翻譯，
    /// 也不會有譯名與遊戲內對不上的問題。查不到才退回列舉名。
    /// </remarks>
    private static string PomanderName(PomanderID p)
    {
        var name = Service.LuminaRow<Lumina.Excel.Sheets.DeepDungeonItem>((uint)p)?.Name.ToString();
        return string.IsNullOrEmpty(name) ? p.ToString() : name;
    }

    private static string PomanderTooltip(PomanderID p) => Service.LuminaRow<Lumina.Excel.Sheets.DeepDungeonItem>((uint)p)?.Tooltip.ToString() ?? "";

    /// <summary>
    /// 逐魔陶器的勾選清單。
    /// </summary>
    /// <remarks>
    /// 📌 分兩段是照遊戲自己的分類：1..19 是死者宮殿／天之逆焰的「魔陶器」，
    /// 20..35 是厄運迷宮的「魔科學器」。兩段都摺疊起來，因為 35 個核取方塊
    /// 攤開會把這一頁其他設定推到看不見的地方。
    /// </remarks>
    private void DrawPomanderAutoUseList()
    {
        if (!ImGui.CollapsingHeader(Loc.T("DD_PomanderAutoUseHeader", "Which pomanders may be used automatically to make room")))
            return;

        ImGui.TextColored(PrereqNoteColor, Loc.T("DD_PomanderAutoUseNote",
            "Only used when a gold coffer cannot be opened because you already hold three of what is inside. Names and descriptions come straight from the game's own data."));

        DrawPomanderRange(Loc.T("DD_PomanderGroupClassic", "Palace of the Dead / Heaven-on-High"), PomanderID.Safety, PomanderID.Petrification);
        DrawPomanderRange(Loc.T("DD_PomanderGroupProto", "Eureka Orthos"), PomanderID.ProtoLethargy, PomanderID.ProtoRaising);
    }

    private void DrawPomanderRange(string label, PomanderID first, PomanderID last)
    {
        ImGui.Separator();
        ImGui.TextColored(PrereqNoteColor, label);
        var col = 0;
        for (var p = first; p <= last; ++p)
        {
            var i = (int)p;
            if ((uint)i >= (uint)PomanderAutoUse.Length)
                continue;

            if (col++ % 2 == 1)
                ImGui.SameLine(ImGui.GetWindowWidth() * 0.5f);

            var v = PomanderAutoUse[i];
            if (ImGui.Checkbox($"{PomanderName(p)}###pomanderAuto{i}", ref v))
            {
                PomanderAutoUse[i] = v;
                Modified.Fire();
            }
            if (ImGui.IsItemHovered())
            {
                var tip = PomanderTooltip(p);
                if (tip.Length > 0)
                    ImGui.SetTooltip(tip);
            }
        }
    }

    #endregion

    // 🔴 預設 false，而且這是**刻意回退上游的既有行為**（上游會在 HP 40%／60% 以下自動喝掉）。
    //    深牢專屬秘藥是特殊商店購入、不可出售的昂貴資源，使用者明確表示要自己決定何時用。
    //    「替使用者自動花掉他刻意保留的資源」屬於不該預設開啟的那一類。
    //    ⚠️ 不影響低血量的保命藥水——那條只用一般治療劑（頂級／聖級／上級），買得到、無爭議。
    [PropertyDisplay("Also drink the deep dungeon's own potions automatically",
        tooltip: "The deep-dungeon-only potions (Sustaining / Empyrean / Orthos) come from a special vendor, cannot be sold, and are usually saved for a moment you pick yourself - so they are NOT used automatically by default.\n\nTick this to let the module drink them below 40%/60% HP, which is what it used to do.\n\nSeparate from the emergency potion below 30% HP: that one only ever uses ordinary Max / Hyper / Super Potions.")]
    public bool AutoUseDeepDungeonPotion = false;
}
