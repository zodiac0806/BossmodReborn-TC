using Dalamud.Bindings.ImGui;

namespace BossMod;

public sealed class BossModuleHintsWindow : UIWindow
{
    private readonly BossModuleManager _mgr;
    private readonly ZoneModuleManager _zmm;
    private readonly Action _openConfig;

    public BossModuleHintsWindow(BossModuleManager mgr, ZoneModuleManager zmm, Action openConfig) : base("Boss module hints", false, new(400, 100))
    {
        _mgr = mgr;
        _zmm = zmm;
        _openConfig = openConfig;
        RespectCloseHotkey = false;
    }

    public override void PreOpenCheck()
    {
        IsOpen = BossModuleManager.Config.HintsInSeparateWindow && (_mgr.ActiveModule != null || ShowZoneModule());
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (BossModuleManager.Config.Lock)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs;
        var opacity = Math.Clamp(BossModuleManager.Config.HintsWindowOpacity, 0, 100);
        var fullyTransparent = opacity == 0;
        if (fullyTransparent)
            Flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground;
        ForceMainWindow = fullyTransparent; // NoBackground flag without ForceMainWindow works incorrectly for whatever reason
        // leave BgAlpha alone at the extremes: 0 is already handled by NoBackground, and 100 must keep whatever the default window style is
        BgAlpha = fullyTransparent || opacity >= 100 ? null : opacity * 0.01f;
    }

    public override void Draw()
    {
        if (ShowZoneModule())
        {
            _zmm.ActiveModule?.DrawGlobalHints();
        }
        else
        {
            try
            {
                _mgr.ActiveModule?.Draw(default, PartyState.PlayerSlot, true, false);
            }
            catch (Exception ex)
            {
                Service.Log($"Boss module draw-hints crashed: {ex}");
                _mgr.ActiveModule = null;
            }
        }

        RadarContextMenu.Draw(_mgr, _openConfig);
    }

    private bool ShowZoneModule() => _mgr.ActiveModule?.StateMachine.ActivePhase == null && (_zmm.ActiveModule?.WantDrawHints() ?? false);
}
