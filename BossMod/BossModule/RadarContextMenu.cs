using Dalamud.Bindings.ImGui;

namespace BossMod;

/// <summary>雷達／提示視窗的右鍵選單。</summary>
/// <remarks>
/// 預設設定下雷達視窗帶 NoDecoration，Dalamud 不畫標題列按鈕 ⇒ 齒輪（本場模組設定）與關閉鈕
/// （停用模組）在畫面上完全沒有入口，這個選單是它們的替代入口。
/// <para>契約：選單開著會跨多幀，所以每一幀都重新讀 ActiveModule、不保存任何模組參考；
/// 模組在選單開著時消失，項目只會變灰而不會擲例外。</para>
/// </remarks>
public static class RadarContextMenu
{
    private const string PopupID = "bmr-radar-ctx";
    private static readonly BossModuleConfig _config = BossModuleManager.Config;

    public static void Draw(BossModuleManager mgr, Action openConfig)
    {
        if (!ImGui.BeginPopupContextWindow(PopupID, ImGuiPopupFlags.MouseButtonRight))
            return;

        // 每幀重讀：選單是跨幀存在的，模組隨時可能在兩幀之間被卸載。
        var module = mgr.ActiveModule;
        var haveModule = module != null;
        var haveInfo = module?.Info != null;
        var modified = false;

        if (ImGui.MenuItem(Loc.T("RADAR_ModuleSettings", "Encounter module settings..."), false, haveInfo) && module?.Info is { } info)
            _ = new BossModuleConfigWindow(info, mgr.WorldState);
        if (!haveInfo)
            HoverTooltip(Loc.T("RADAR_NoModuleTip", "No encounter module is active right now, so there is nothing encounter-specific to configure."));

        if (ImGui.MenuItem(Loc.T("RADAR_Deactivate", "Deactivate current module"), false, haveModule))
            mgr.ActiveModule = null;
        if (!haveModule)
            HoverTooltip(Loc.T("RADAR_NoModuleTip", "No encounter module is active right now, so there is nothing encounter-specific to configure."));

        ImGui.Separator();

        ImGui.SetNextItemWidth(175f);
        if (ImGui.DragFloat(Loc.T("Radar arena scale factor"), ref _config.ArenaScale, 0.1f, 0.1f, 10f, "%.2f", ImGuiSliderFlags.Logarithmic))
            modified = true;

        // 子選單裡刻意用 Checkbox 而不是 MenuItem：MenuItem 一點就把整個選單關掉，
        // 而這一組開關常常要連續調好幾個。
        if (ImGui.BeginMenu(Loc.T("RADAR_Display", "Radar display")))
        {
            modified |= ImGui.Checkbox(Loc.T("Rotate radar to match camera orientation"), ref _config.RotateArena);
            modified |= ImGui.Checkbox(Loc.T("Transparent radar window background"), ref _config.TrishaMode);
            modified |= ImGui.Checkbox(Loc.T("Show arena border in radar"), ref _config.ShowBorder);
            modified |= ImGui.Checkbox(Loc.T("Show cardinal direction names on radar"), ref _config.ShowCardinals);
            modified |= ImGui.Checkbox(Loc.T("Show waymarks on radar"), ref _config.ShowWaymarks);
            modified |= ImGui.Checkbox(Loc.T("Show text hints in separate window"), ref _config.HintsInSeparateWindow);
            ImGui.EndMenu();
        }

        ImGui.Separator();

        modified |= Toggle(Loc.T("Lock radar and hint window movement and mouse interaction"), ref _config.Lock);
        HoverTooltip(Loc.T("RADAR_LockTip", "While locked the radar ignores all mouse input, so this menu cannot be opened either - unlock it from the settings window."));

        if (ImGui.MenuItem(Loc.T("RADAR_PluginSettings", "BossModReborn settings...")))
            openConfig();

        if (modified)
            _config.Modified.Fire();
        ImGui.EndPopup();
    }

    private static bool Toggle(string label, ref bool value)
    {
        if (!ImGui.MenuItem(label, value))
            return false;
        value = !value;
        return true;
    }

    // AllowWhenDisabled：變灰的項目預設不算 hover，少了這個旗標「為什麼不能點」就沒地方說。
    private static void HoverTooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }
}
