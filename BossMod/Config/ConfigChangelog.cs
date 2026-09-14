using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using System.Reflection;

namespace BossMod;

/// <summary>一則變更紀錄的種類。</summary>
public enum ChangelogKind
{
    /// <summary>這一版新增的設定項。</summary>
    NewOption,
    /// <summary>沒有新增設定，但既有行為變了（門檻重新校準、判定改寫之類）。</summary>
    ChangedBehaviour,
}

/// <summary>
/// 變更紀錄的一筆登記。<paramref name="NodeType"/> 與 <paramref name="FieldName"/> 兩個都給的時候，
/// 顯示用的標籤與 tooltip 直接從那個欄位的 <see cref="PropertyDisplayAttribute"/> 取，
/// 不另外抄一份 —— 兩份文案遲早會漂移，而漂移的失敗形式是靜默的。
/// </summary>
public sealed record class ConfigChangelogEntry(
    string Version,
    ChangelogKind Kind,
    Type? NodeType,
    string? FieldName,
    string DescriptionKey,
    string DescriptionFallback);

/// <summary>
/// 版本升級後第一次開遊戲時，列出「這一版多了哪些選項、哪些行為變了」。
/// </summary>
/// <remarks>
/// 🔴 <b>這個視窗一個使用者設定都不會動。</b>它只讀 <see cref="PropertyDisplayAttribute"/> 拿文案，
/// 唯一寫回設定檔的是 <see cref="MiscConfig.LastSeenVersion"/>（＝「這份清單我看過了」）。
/// 上游 awgil 的版本在每一列旁邊放了 Enable／Disable 按鈕，本實作刻意不做：
/// 一個「告訴你發生什麼事」的視窗不該同時是一個會改你設定的視窗。
/// <para>
/// ⚠️ 版本號取自組件版本。發版流程（.github/workflows/release.yml）會用 git tag 覆寫
/// <c>-p:Version=</c>，所以正式版拿到的是 <c>7.20.0.NN</c>；本機開發建置則是 csproj 裡的
/// <c>7.15.0.&lt;BuildNumber&gt;</c>，比對結果會把全部條目都列出來 —— 那只影響開發機。
/// </para>
/// </remarks>
public sealed class ConfigChangelogWindow : UIWindow
{
    /// <summary>
    /// 條目登記處。新增功能時在這裡補一列，版本號寫「這個改動第一次出貨的那個 tag」。
    /// </summary>
    /// <remarks>
    /// 📌 只登記寫得出處的：找不到對應 commit／說不清楚改了什麼的就不要湊數，
    /// 一份摻了猜測的變更紀錄比沒有更糟。
    /// </remarks>
    private static readonly ConfigChangelogEntry[] Registry =
    [
        // 🔴 這一則刻意登記,因為它是少數「新選項的預設值不是停用」的改動:
        //    既有使用者升上來就直接拿到 Alt,不講的話會變成「BMR 突然在我按 Alt 時不動了」。
        // ⚠️ 版本號寫的是「這個改動第一次出貨的那個 tag」。撰寫時 feed 上的是 7.20.0.93,
        //    所以填下一號;比對是 `resolved.Version > prev`,實際出貨號更大也照樣顯示得到。
        // 🔴 .94 只涵蓋「BMR 自己注入移動輸入」那條路。深牢的「走到房間」是 vnavmesh 在走的,
        //    完全不經過 BMR 的 detour ⇒ 當時按住 Alt 對它沒有任何效果,而 .94 的提示也是這樣寫的。
        //    這一則就是把那句話推翻掉,所以必須登記:使用者讀過舊提示,不講他不會知道行為變了。

        // 沒有新增任何設定，但「右鍵雷達什麼都不會發生」變成了「會開選單」——
        // 而且它是唯一的入口：預設的透明雷達沒有標題列，齒輪與關閉鈕根本畫不出來。
        new("7.20.0.110", ChangelogKind.ChangedBehaviour, null, null,
            "CHANGELOG_RadarContextMenu",
            "Right-clicking the radar (or the separate hints window) now opens a menu. " +
            "It holds this encounter's own settings, \"deactivate current module\", the arena scale, " +
            "the display toggles you change most often, and the lock. " +
            "This matters because the default transparent radar has no title bar at all, " +
            "so the cog and close buttons that used to sit there were never drawn - " +
            "encounter-specific settings could only be reached through the \"Supported bosses\" tab. " +
            "Nothing changed for you if you right-click elsewhere; locking the radar still disables " +
            "all of its mouse input, this menu included."),

        new("7.20.0.95", ChangelogKind.ChangedBehaviour, typeof(ActionTweaksConfig), nameof(ActionTweaksConfig.PauseAutoMoveKey),
            "CHANGELOG_DeepDungeonWalkPause",
            "The pause-automatic-movement key now covers the deep dungeon \"Walk to room\" button as well. That route is walked by vnavmesh rather than by BossMod, so it used to ignore the key entirely - the option's own tooltip said so. BossMod now asks vnavmesh to hold still while the key is held and releases it the moment you let go. The computed route is kept, so you carry on from wherever you are standing instead of re-pathing or backtracking."),

        new("7.20.0.94", ChangelogKind.NewOption, typeof(ActionTweaksConfig), nameof(ActionTweaksConfig.PauseAutoMoveKey),
            "CHANGELOG_PauseAutoMoveKey",
            "Holding a key now pauses BossMod's automatic movement. Unlike almost every other new option this one is ON by default, bound to Alt: while Alt is held nothing in the plugin steers your character and you move manually, and it lets go on the frame you release. Only movement is affected - actions, dodging hints, positional hints and mitigation are untouched. Set it to \"None\" in Action tweaks if you do not want it."),

        new("7.20.0.63", ChangelogKind.NewOption, typeof(MiscConfig), nameof(MiscConfig.UnlockMultibox),
            "CHANGELOG_UnlockMultibox",
            "Unlocking multiboxing is now a setting, and it is off by default. Up to and including 7.20.0.62 the plugin closed the game's single-instance mutex on every load, unconditionally and without telling you. Tick the option if you want that behaviour back; read its tooltip first."),

        new("7.20.0.62", ChangelogKind.ChangedBehaviour, null, null,
            "CHANGELOG_DeepDungeonGridFit",
            "Deep dungeon auto-clear: the room-centre grid fit was recalibrated against live measurements (the acceptance threshold went from 15y to 30y), and phantom room centres that the map data synthesises on boss floors are now discarded. Room positions on the minimap and while travelling should line up more often. No new settings, and nothing you have configured changed."),

        new("7.20.0.61", ChangelogKind.NewOption, typeof(ActionTweaksConfig), nameof(ActionTweaksConfig.DashSafetyBlockExternal),
            "CHANGELOG_DashSafetyBlockExternal",
            "The existing dash safety checks only ever saw actions that went through BossMod's own action queue. Plugins that call the game's UseAction directly - WrathCombo's auto-rotation, for instance - bypassed them entirely, so a gap closer they fired could still throw you into an AOE. The same landing-spot check now also runs at the UseAction hook, which every source passes through. On by default, and only while BossMod's AI is enabled."),

        new("7.20.0.61", ChangelogKind.NewOption, typeof(ActionTweaksConfig), nameof(ActionTweaksConfig.DashSafetyActivationThreshold),
            "CHANGELOG_DashSafetyActivationThreshold",
            "Companion slider for the option above: only danger zones that are already active, or that go off within this many seconds, are allowed to block a dash. A zone that will not resolve for a long time is not a reason to refuse - the AI would simply walk back out of it."),

        new("7.20.0.59", ChangelogKind.NewOption, typeof(Global.DeepDungeon.AutoDDConfig), nameof(Global.DeepDungeon.AutoDDConfig.ForceTravelKey),
            "CHANGELOG_ForceTravelKey",
            "Deep dungeon auto-clear can now be told \"just go, I am watching\": while the chosen key is held, travelling ignores the in-combat, pull-limit and low-HP gates, and stops the moment you let go. It only forces the walking - whether a coffer is opened, or the Cairn of Passage is actually used, still follows their own settings. Defaults to no key, i.e. the whole feature is off."),
    ];

    /// <summary>一筆已經解析成功、可以畫出來的條目。</summary>
    private sealed record class ResolvedEntry(Version Version, ChangelogKind Kind, string PageName, string OptionLabel, string OptionTooltip, string Description);

    private readonly List<ResolvedEntry> _entries = [];
    private readonly string _previousText;
    private readonly string _currentText;

    /// <param name="hadExistingConfig">
    /// 載入設定檔<b>之前</b>那個檔案存不存在。用來分辨兩件外表一樣、意義相反的事：
    /// 全新安裝（不該用一面「更新內容」牆迎接他）與既有使用者第一次升到有變更紀錄的版本
    /// （他正是要看這面牆的人，而他的設定檔裡當然還沒有 LastSeenVersion 這個鍵）。
    /// </param>
    public ConfigChangelogWindow(bool hadExistingConfig)
        : base($"{Loc.T("CHANGELOG_Title", "BossMod Reborn - what's new")}###bmr_changelog", false, new(560f, 420f))
    {
        var cfg = Service.Config.Get<MiscConfig>();
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        _currentText = current.ToString();

        Version? previous = null;
        if (Version.TryParse(cfg.LastSeenVersion, out var stored))
        {
            previous = stored;
            _previousText = stored.ToString();
        }
        else if (hadExistingConfig)
        {
            // 既有使用者，但設定檔還沒有這個鍵 ⇒ 從頭列給他看
            previous = new Version(0, 0, 0, 0);
            _previousText = Loc.T("CHANGELOG_UnknownPrevious", "an earlier version");
        }
        else
        {
            _previousText = "";
        }

        if (cfg.LastSeenVersion != _currentText)
        {
            cfg.LastSeenVersion = _currentText;
            // 只戳根節點的 Modified（＝排一次存檔），不戳 MiscConfig 自己的 Modified：
            // 節點事件上掛著多開解鎖的監聽者，而這裡並沒有任何使用者可見的設定變動。
            Service.Config.Modified.Fire();
        }

        if (previous is Version prev)
        {
            foreach (var e in Registry)
            {
                var resolved = Resolve(e);
                if (resolved != null && resolved.Version > prev)
                    _entries.Add(resolved);
            }
            _entries.Sort((a, b) => b.Version.CompareTo(a.Version));
        }

        IsOpen = _entries.Count > 0;
        if (IsOpen)
            Service.Logger.Information($"[Changelog] 版本由 {_previousText} 變為 {_currentText}，列出 {_entries.Count} 則變更（不會改動任何既有設定值）。");
    }

    /// <summary>
    /// 把一筆登記解析成可畫的內容。解析不了就回 <c>null</c> 並記一行 —— 欄位被改名時
    /// 應該是「這一則不見了」而不是「設定視窗炸掉」。
    /// </summary>
    private static ResolvedEntry? Resolve(ConfigChangelogEntry e)
    {
        if (!Version.TryParse(e.Version, out var ver))
        {
            Service.Logger.Information($"[Changelog] 條目 {e.DescriptionKey} 的版本字串 '{e.Version}' 解析失敗，略過。");
            return null;
        }

        var description = Loc.T(e.DescriptionKey, e.DescriptionFallback);
        if (e.NodeType == null || e.FieldName == null)
            return new(ver, e.Kind, "", "", "", description);

        var field = e.NodeType.GetField(e.FieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var display = field?.GetCustomAttribute<PropertyDisplayAttribute>();
        if (display == null)
        {
            Service.Logger.Information($"[Changelog] 條目 {e.DescriptionKey} 指向的 {e.NodeType.Name}.{e.FieldName} 找不到（或沒有 PropertyDisplay），只顯示說明文字。");
            return new(ver, e.Kind, "", "", "", description);
        }

        var pageAttr = e.NodeType.GetCustomAttribute<ConfigDisplayAttribute>();
        var pageName = pageAttr?.Name ?? (e.NodeType.Name.EndsWith("Config", StringComparison.Ordinal) ? e.NodeType.Name[..^"Config".Length] : e.NodeType.Name);
        return new(ver, e.Kind, Loc.T(pageName, pageName), Loc.T(display.Label, display.Label), Loc.T(display.Tooltip, display.Tooltip), description);
    }

    public override void Draw()
    {
        if (_previousText.Length > 0)
            ImGui.TextWrapped($"{Loc.T("CHANGELOG_Since", "Changes since the version you last used")}: {_previousText} → {_currentText}");
        else
            ImGui.TextWrapped($"{Loc.T("CHANGELOG_Current", "Current version")}: {_currentText}");

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextWrapped(Loc.T("CHANGELOG_NoValuesChanged", "This window is a notice only - none of your existing settings were changed."));

        ImGui.Separator();

        using (var child = ImRaii.Child("changelogbody", new Vector2(0f, -ImGui.GetFrameHeightWithSpacing()), false))
        {
            if (child)
            {
                var lastVersion = "";
                for (var i = 0; i < _entries.Count; ++i)
                {
                    var e = _entries[i];
                    var verText = e.Version.ToString();
                    if (verText != lastVersion)
                    {
                        lastVersion = verText;
                        ImGui.Spacing();
                        using var c = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet);
                        ImGui.TextUnformatted(verText);
                    }

                    using var id = ImRaii.PushId(i);
                    DrawEntry(e);
                }
            }
        }

        if (ImGui.Button(Loc.T("CHANGELOG_OK", "Got it")))
            IsOpen = false;
    }

    private static void DrawEntry(ResolvedEntry e)
    {
        var kindText = e.Kind switch
        {
            ChangelogKind.NewOption => Loc.T("CHANGELOG_KindNewOption", "New option"),
            _ => Loc.T("CHANGELOG_KindChangedBehaviour", "Behaviour change"),
        };

        ImGui.Bullet();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
            ImGui.TextUnformatted($"[{kindText}]");

        if (e.OptionLabel.Length > 0)
        {
            ImGui.SameLine();
            if (e.OptionTooltip.Length > 0)
            {
                UIMisc.HelpMarker(e.OptionTooltip);
                ImGui.SameLine();
            }
            ImGui.TextUnformatted(e.PageName.Length > 0 ? $"{e.PageName} → {e.OptionLabel}" : e.OptionLabel);
        }

        using var indent = ImRaii.PushIndent();
        ImGui.TextWrapped(e.Description);
    }
}
