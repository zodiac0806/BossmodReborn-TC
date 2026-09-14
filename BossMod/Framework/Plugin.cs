using BossMod.Autorotation;
using Dalamud.Common;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace BossMod;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "BossMod Reborn";

    private readonly ICommandManager CommandManager;

    private readonly RotationDatabase _rotationDB;
    private readonly WorldState _ws;
    private readonly AIHints _hints;
    private readonly BossModuleManager _bossmod;
    private readonly ZoneModuleManager _zonemod;
    private readonly AIHintsBuilder _hintsBuilder;
    private readonly MovementOverride _movementOverride;
    private readonly ActionManagerEx _amex;
    private readonly WorldStateGameSync _wsSync;
    private readonly RotationModuleManager _rotation;
    private readonly AI.AIManager _ai;
    private readonly AI.Broadcast _broadcast;
    private readonly IPCProvider _ipc;
    private readonly DTRProvider _dtr;
    // 「不需掛 preset 的方位提示」的遲滯狀態。與 GoToPositional 模組各持一份、互不干擾。
    private readonly Autorotation.MiscAI.AutoPositional.Hysteresis _positionalHintAuto = new();
    // 「不需掛 preset 的預測減傷」用的模組實例與它的預設策略值,見 UpdatePredictiveMitigationWithoutPreset。
    // 🔴 實例綁在某一個 Actor 上(RotationModule.Player 是 readonly),所以玩家換人就要重建 —— 不是每幀 new。
    private Autorotation.MiscAI.PredictiveMitigation? _predictiveMitAuto;
    private Autorotation.StrategyValues? _predictiveMitAutoStrategy;
    private TimeSpan _prevUpdateTime;
    private DateTime _throttleJump;
    private DateTime _throttleInteract;

    // 多開解鎖:目前實際「已經解鎖過了嗎」,用來只在翻轉時動作(見建構式)
    private readonly ConfigListener<MiscConfig> _multibox;
    private bool _multiboxUnlocked;

    // 設定存檔去抖動用的狀態(見 RequestConfigSave)
    private static readonly TimeSpan ConfigSaveDebounce = TimeSpan.FromSeconds(1d);
    private readonly FileInfo _configFile;
    private readonly object _configSaveLock = new();
    private DateTime _configSaveDeadline = DateTime.MaxValue; // MaxValue 表示目前沒有待寫入的改動
    private Task? _configSaveTask;

    // windows
    private readonly ConfigUI _configUI; // TODO: should be a proper window!
    private readonly BossModuleMainWindow _wndBossmod;
    private readonly BossModuleHintsWindow _wndBossmodHints;
    private readonly ZoneModuleWindow _wndZone;
    private readonly ReplayManagementWindow _wndReplay;
    private readonly UIRotationWindow _wndRotation;
    private readonly MainDebugWindow _wndDebug;
    private readonly ConfigChangelogWindow _wndChangelog;
    private readonly RotationSolverRebornModule _rsr;

    public unsafe Plugin(IDalamudPluginInterface dalamud, ICommandManager commandManager, ISigScanner sigScanner, IDataManager dataManager)
    {
        if (!dalamud.ConfigDirectory.Exists)
            dalamud.ConfigDirectory.Create();
        var dalamudRoot = dalamud.GetType().Assembly.
                GetType("Dalamud.Service`1", true)!.MakeGenericType(dalamud.GetType().Assembly.GetType("Dalamud.Dalamud", true)!).
                GetMethod("Get")!.Invoke(null, BindingFlags.Default, null, [], null);
        var dalamudStartInfo = dalamudRoot?.GetType().GetProperty("StartInfo", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dalamudRoot) as DalamudStartInfo;
        var gameVersion = dalamudStartInfo?.GameVersion?.ToString() ?? "unknown";
#if CUSTOMCS
        // 🔴 只有自帶 CS 副本(CustomCS=true)時才由我們初始化 Resolver。
        // 預設吃 Dalamud/lib 那份 FFXIVClientStructs.dll,Dalamud 本體在載入外掛之前
        // 就已經 Setup + Resolve 過同一個單例了;在這裡再跑一次等於對「已解析的單例」
        // 重跑解析,不是無害的重入。
        InteropGenerator.Runtime.Resolver.GetInstance.Setup(sigScanner.SearchBase, gameVersion, new(dalamud.ConfigDirectory.FullName + "/cs.json"));
        FFXIVClientStructs.Interop.Generated.Addresses.Register();
        InteropGenerator.Runtime.Resolver.GetInstance.Resolve();
#endif

        dalamud.Create<Service>();
        Loc.Load("tw");
        HintText.Load("tw");
        Service.LogHandlerDebug = msg => Service.Logger.Debug(msg);
        Service.LogHandlerVerbose = msg => Service.Logger.Verbose(msg);
        Service.LuminaGameData = dataManager.GameData;
        Service.WindowSystem = new("bmr");
        //Service.Device = pluginInterface.UiBuilder.Device;
        Service.Condition.ConditionChange += OnConditionChanged;
        Camera.Instance = new();

        // 「設定檔在載入之前存不存在」必須在 LoadFromFile 之前問,而且要立刻存成 bool
        // (FileInfo.Exists 第一次讀之後就快取住了)。ConfigChangelogWindow 靠它分辨
        // 「全新安裝」與「既有使用者第一次升上來」——這兩者的設定檔裡都沒有 LastSeenVersion。
        var hadExistingConfig = dalamud.ConfigFile.Exists;
        Service.Config.Initialize();
        Service.Config.LoadFromFile(dalamud.ConfigFile);
        _configFile = dalamud.ConfigFile;
        Service.Config.Modified.Subscribe(RequestConfigSave);

        // 🔴 多開解鎖刻意搬到設定載入「之後」才跑。它原本就在 Service.Config.Initialize() 之前,
        //    那個時間點設定根本還讀不到,掛不上開關。它做的是關掉本行程的單一實例互斥鎖
        //    (handle 一關就是整個遊戲行程活著的期間都有效),晚幾行執行對結果沒有任何差別。
        // 📌 預設關,而且只在「翻轉成開」時才動作、才印診斷。
        _multibox = Service.Config.GetAndSubscribe<MiscConfig>(cfg =>
        {
            if (cfg.UnlockMultibox == _multiboxUnlocked)
                return;
            _multiboxUnlocked = cfg.UnlockMultibox;
            if (_multiboxUnlocked)
            {
                Service.Logger.Information("[Multibox] 多開解鎖已開啟:開始列舉本行程的控制代碼,關閉遊戲的單一實例互斥鎖(名稱以 _ffxiv_game0 結尾)。");
                MultiboxUnlock.Exec();
            }
            else
            {
                Service.Logger.Information("[Multibox] 多開解鎖已關閉:這一刻起不再做任何事。已經被關掉的互斥鎖控制代碼要重開遊戲才會回來。");
            }
        });

        CommandManager = commandManager;
        CommandManager.AddHandler("/bmr", new CommandInfo(OnCommand) { HelpMessage = "Show boss mod settings UI" });

        ActionDefinitions.Instance.UnlockCheck = QuestUnlocked; // ensure action definitions are initialized and set unlock check functor (we don't really store the quest progress in clientstate, for now at least)

        // 🔴 Framework.Instance() 是 [StaticAddress(…, isPointer: true)]，回傳全域指標槽的**內容**，合法可為 null。
        // 📌 判定：這裡是外掛建構子（載入路徑），不是每幀路徑。Dalamud 自己得先有 Framework 才載得動外掛，
        //    這一刻為 null 幾乎不可能；而 qpf 是世界狀態所有時間戳的**除數**，沒有中性值可退
        //    （退 0 會讓每個時間戳變成 Infinity／NaN，是靜默的錯誤資料）。
        //    ⇒ 選擇擲明確的受管理例外：Dalamud 記成「外掛載入失敗」並顯示原因，遊戲照常跑；
        //    原本的裸鏈解參考則是 AccessViolationException＝當場把遊戲帶走，且完全沒有訊息。
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (framework == null)
            throw new InvalidOperationException("Client::System::Framework::Framework 尚未建立，無法取得效能計數器頻率，BossModReborn 無法初始化。");
        var qpf = (ulong)framework->PerformanceCounterFrequency;
        _rotationDB = new(new(dalamud.ConfigDirectory.FullName + "/autorot"), new(dalamud.AssemblyLocation.DirectoryName! + "/DefaultRotationPresets.json"));
        _ws = new(qpf, gameVersion);
        _rsr = new(dalamud);
        _hints = new();
        _bossmod = new(_ws);
        _zonemod = new(_ws);
        _hintsBuilder = new(_ws, _bossmod, _zonemod, _rsr);
        _movementOverride = new(dalamud);
        _amex = new(_ws, _hints, _movementOverride);
        _wsSync = new(_ws, _amex);
        _rotation = new(_rotationDB, _bossmod, _hints);
        _ai = new(_rotation, _amex, _movementOverride);
        _broadcast = new();
        _ipc = new(_bossmod, _hints, _rotation, _amex, _movementOverride, _ai);
        _dtr = new(_rotation, _ai, () => OpenConfigUI());
        _wndBossmod = new(_bossmod, _zonemod, () => OpenConfigUI());
        _wndBossmodHints = new(_bossmod, _zonemod, () => OpenConfigUI());
        _wndZone = new(_zonemod);
        var config = Service.Config.Get<ReplayManagementConfig>();
        var replayDir = string.IsNullOrEmpty(config.ReplayFolder) ? dalamud.ConfigDirectory.FullName + "/replays" : config.ReplayFolder;
        _wndReplay = new ReplayManagementWindow(_ws, _bossmod, _rotationDB, new DirectoryInfo(replayDir));
        _configUI = new(Service.Config, _ws, new DirectoryInfo(replayDir), _rotationDB);
        config.Modified.ExecuteAndSubscribe(() => _wndReplay.UpdateLogDirectory());
        _wndRotation = new(_rotation, _amex, () => OpenConfigUI("Autorotation presets"));
        _wndDebug = new(_ws, _rotation, _zonemod, _amex, _movementOverride, _hintsBuilder, dalamud);
        // 版本升級後第一次載入時自己開起來;沒有可列的內容就整個不開(見 ConfigChangelogWindow 建構式)
        _wndChangelog = new(hadExistingConfig);

        dalamud.UiBuilder.DisableAutomaticUiHide = true;
        dalamud.UiBuilder.Draw += DrawUI;
        dalamud.UiBuilder.OpenMainUi += () => OpenConfigUI();
        dalamud.UiBuilder.OpenConfigUi += () => OpenConfigUI();
    }

    public void Dispose()
    {
        // 逐一隔離每一步拆除:任何一步擲出受管理例外時只記一行,然後繼續下一步。
        // 原本是 24 個毫無防護的呼叫排成一串 —— 第一個擲例外的那個會讓它後面的全部不執行,
        // 而後面那些包含解除原生 hook、退訂 Condition 事件、交回 vnavmesh 的移動租約,
        // 以及把還沒寫出去的設定改動沖到磁碟。
        // 📌 代價不只是「漏掉」:Dispose 只要擲一次例外,Dalamud 就把外掛標成 UnloadError
        //    (LocalPlugin.UnloadAsync),此後這個遊戲行程裡就再也不能卸載/重載它;
        //    而 loader 照樣會被釋放 —— 沒解除的 hook 就留在原生碼上指向已卸載的組件。
        // 🔴 這**不是** AccessViolationException 的防護。AVE 在 .NET Core 是
        //    corrupted-state exception,catch(Exception) 與這裡的隔離對它完全無效;
        //    這裡處理的只有受管理例外。
        // 🔴 順序與原本逐字相同,不要重排 —— 後面的子系統可能還在讀前面的狀態。
        SafeTeardown("Condition.ConditionChange", () => { Service.Condition.ConditionChange -= OnConditionChanged; });
        SafeTeardown("_multibox", () => _multibox.Dispose());
        SafeTeardown("_wndChangelog", () => _wndChangelog.Dispose());
        SafeTeardown("_wndDebug", () => _wndDebug.Dispose());
        SafeTeardown("_wndRotation", () => _wndRotation.Dispose());
        SafeTeardown("_wndReplay", () => _wndReplay.Dispose());
        SafeTeardown("_wndZone", () => _wndZone.Dispose());
        SafeTeardown("_wndBossmodHints", () => _wndBossmodHints.Dispose());
        SafeTeardown("_wndBossmod", () => _wndBossmod.Dispose());
        SafeTeardown("_configUI", () => _configUI.Dispose());
        SafeTeardown("_dtr", () => _dtr.Dispose());
        SafeTeardown("_ipc", () => _ipc.Dispose());
        SafeTeardown("_ai", () => _ai.Dispose());
        SafeTeardown("_rotation", () => _rotation.Dispose());
        SafeTeardown("_wsSync", () => _wsSync.Dispose());
        SafeTeardown("_amex", () => _amex.Dispose());
        SafeTeardown("_movementOverride", () => _movementOverride.Dispose());
        SafeTeardown("_hintsBuilder", () => _hintsBuilder.Dispose());
        SafeTeardown("_zonemod", () => _zonemod.Dispose());
        SafeTeardown("_bossmod", () => _bossmod.Dispose());
        SafeTeardown("ActionDefinitions.Instance", () => ActionDefinitions.Instance.Dispose());
        SafeTeardown("CommandManager /bmr", () => CommandManager.RemoveHandler("/bmr"));
        SafeTeardown("FlushPendingConfigSave", FlushPendingConfigSave); // 放在最後,連拆除過程中(例如回放清單)產生的改動也一併寫出去
        SafeTeardown("GarbageCollection", GarbageCollection);
    }

    // 拆除用的逐步隔離:一步失敗就記一行並繼續下一步,絕不讓例外傳出 Dispose。
    // 🔴 只對受管理例外有效 —— AccessViolationException 是 corrupted-state exception,
    //    catch(Exception) 攔不到,不要把這個 helper 當成原生層的防護。
    // 診斷寫 Error:拆除失敗是真的故障,而且要能在使用者那份幾十萬行 Debug 的 log 裡看得見。
    private static void SafeTeardown(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            try
            {
                Service.Logger.Error(e, $"[Dispose] 拆除「{what}」時擲出例外，已略過這一步、繼續釋放其餘子系統。");
            }
            catch
            {
                // 連寫 log 都失敗時也不能中斷拆除 —— 這裡已經沒有別的地方可以回報了。
            }
        }
    }

    // 設定存檔去抖動:設定 UI 用的是 DragFloat/DragInt/ColorEdit,這類控制項在「拖曳期間每一幀」都會回傳 true
    // 並觸發 Modified。原本每次都直接排一個背景存檔,拖 3 秒 slider 就會排出上百個並行 Task,每個都要用
    // Parallel.ForEach 序列化全部設定節點,又互搶同一個 FileShare.None 的檔案 handle(多數直接丟 IOException
    // 被吞掉):落地順序沒有保證,最後成功寫入的可能是較舊的快照,而且 thread pool 被佔滿後會回頭拖慢繪製執行緒。
    // 改成合流:只記下「最後一次改動的時間」,等安靜 ConfigSaveDebounce 之後才真的寫一次。
    private void RequestConfigSave()
    {
        lock (_configSaveLock)
            _configSaveDeadline = DateTime.UtcNow + ConfigSaveDebounce;
    }

    // 每幀檢查一次:待寫入的改動安靜夠久了就真的存檔,且同一時間只允許一個存檔在跑
    private void UpdatePendingConfigSave()
    {
        lock (_configSaveLock)
        {
            if (_configSaveDeadline == DateTime.MaxValue || DateTime.UtcNow < _configSaveDeadline)
                return;
            if (_configSaveTask is { IsCompleted: false })
                return; // 上一次存檔還沒寫完,下一幀再試,避免兩個寫入互搶檔案 handle

            _configSaveDeadline = DateTime.MaxValue;
            _configSaveTask = Task.Run(() => Service.Config.SaveToFile(_configFile));
        }
    }

    // 卸載外掛/關遊戲時同步沖掉待寫入的改動,避免使用者最後一次調整因為去抖延遲而遺失
    private void FlushPendingConfigSave()
    {
        Task? inflight;
        bool pending;
        lock (_configSaveLock)
        {
            inflight = _configSaveTask;
            pending = _configSaveDeadline != DateTime.MaxValue;
            _configSaveDeadline = DateTime.MaxValue;
            _configSaveTask = null;
        }

        try
        {
            inflight?.Wait(TimeSpan.FromSeconds(5d));
        }
        catch (Exception e)
        {
            Service.Log($"Failed to wait for pending config save: {e}");
        }

        if (pending)
            Service.Config.SaveToFile(_configFile);
    }

    private void OnCommand(string cmd, string args)
    {
        Service.Log($"OnCommand: {cmd} {args}");
        var split = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0)
        {
            OpenConfigUI();
            return;
        }

        switch (split[0].ToUpperInvariant())
        {
            case "D":
                _wndDebug.IsOpen = true;
                _wndDebug.BringToFront();
                break;
            case "CFG":
                var output = Service.Config.ConsoleCommand(new ArraySegment<string>(split, 1, split.Length - 1));
                foreach (var msg in output)
                    Service.ChatGui.Print(msg);
                break;
            case "GC":
                GarbageCollection();
                break;
            case "R":
                HandleReplayCommand(split);
                break;
            case "AR":
                ParseAutorotationCommands(split);
                break;
            case "RESETCOLORS":
                ResetColors();
                break;
            case "RESTOREROTATION":
                ToggleRestoreRotation();
                break;
            case "TOGGLEANTICHEAT":
                ToggleAnticheat();
                break;
            case "RADAR":
                ToggleRadar(split);
                break;
        }
    }

    private bool HandleReplayCommand(string[] messageData)
    {
        if (messageData.Length == 1)
            _wndReplay.SetVisible(!_wndReplay.IsOpen);
        else
        {
            switch (messageData[1].ToUpperInvariant())
            {
                case "ON":
                    _wndReplay.StartRecording("");
                    break;
                case "OFF":
                    _wndReplay.StopRecording();
                    break;
                default:
                    Service.ChatGui.Print($"[BMR] Unknown replay command: {messageData[1]}");
                    break;
            }
        }
        return false;
    }

    private static void ResetColors()
    {
        var defaultConfig = ColorConfig.DefaultConfig;
        var currentConfig = Service.Config.Get<ColorConfig>();
        var fields = typeof(ColorConfig).GetFields(BindingFlags.Public | BindingFlags.Instance);

        for (var i = 0; i < fields.Length; ++i)
        {
            ref var field = ref fields[i];
            var value = field.GetValue(defaultConfig);
            if (value is Color or Color[])
                field.SetValue(currentConfig, value);
        }

        currentConfig.Modified.Fire();
        Service.Log("Colors have been reset to default values.");
    }

    private static bool ToggleAnticheat()
    {
        var config = Service.Config.Get<ActionTweaksConfig>();
        config.ActivateAnticheat = !config.ActivateAnticheat;
        config.Modified.Fire();
        Service.Log($"The animation lock anticheat is now {(config.ActivateAnticheat ? "enabled" : "disabled")}");
        return true;
    }

    private static bool ToggleRestoreRotation()
    {
        var config = Service.Config.Get<ActionTweaksConfig>();
        config.RestoreRotation = !config.RestoreRotation;
        config.Modified.Fire();
        Service.Log($"Restore character orientation after action use is now {(config.RestoreRotation ? "enabled" : "disabled")}");
        return true;
    }

    private const string ConfigWindowName = "BossModReborn";

    private void OpenConfigUI(string showTab = "")
    {
        // ⚠️ 不能只 new 一個 UISimpleWindow 當成「開關」：UIWindow 的建構式在同名視窗
        // 已存在時只會做 IsOpen = true 與 BringToFront()（見 UIWindow.cs 的 detached 分支），
        // 永遠不會關閉，所以 DTR 右鍵按第二次沒有任何反應。這裡自己找既有視窗來開關。
        var existing = Service.WindowSystem?.Windows.FirstOrDefault(w => w.WindowName == ConfigWindowName);
        if (existing != null)
        {
            existing.IsOpen = !existing.IsOpen;
            if (existing.IsOpen)
            {
                _configUI.ShowTab(showTab);
                existing.BringToFront();
            }
            return;
        }

        _configUI.ShowTab(showTab);
        _ = new UISimpleWindow(ConfigWindowName, _configUI.Draw, true, new(300, 300));
    }

    private void DrawUI()
    {
        var tsStart = DateTime.Now;
        // 🔴 必須在這裡拍(Draw 回呼裡),因為它會讀 ImGui 的按鍵狀態;真正的讀取者是
        //    MovementOverride 的兩支移動 detour,那邊不在 Draw 回呼裡、只能讀這一幀的快照。
        // 🔴 這一步起到 _bossmod.Update() 為止,原本是六個裸敘述串在一起:任何一個擲出受管理
        //    例外,同一幀後面的**全部**處理就整段不執行 —— 包含 _hintsBuilder.Update(危險區)、
        //    _amex 的技能佇列、_rotation、_ai、WindowSystem.Draw() 與 ExecuteHints()。
        //    而 Dalamud 那一側不會把 BMR 關掉:UiBuilder 的 Draw 走的是 InvokeSafely
        //    (Dalamud/Utility/EventHandlerExtensions.cs:66 —— 逐個訂閱者 try/catch 之後只寫
        //    一行 Log.Error,而且**每一幀**都寫),所以外面看到的只是「log 一直在噴、BMR 半死不活」。
        //    ⇒ 逐一隔離:壞掉的那一步跳過,同一幀後面的照常跑;失敗訊息自己節流。
        // 🔴 這不是 AccessViolationException 的防護 —— AVE 在 .NET Core 是 corrupted-state
        //    exception,catch(Exception) 攔不到;這裡處理的只有受管理例外。
        try
        {
            _movementOverride.UpdateAutoMovementPause();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("MovementOverride.UpdateAutoMovementPause", ex);
        }
        // 🔴 擲例外時退回 false ＝「這一幀沒有移動意圖」,那是保守值:唯一的消費端
        //    AIHintsBuilder.Update(:62)只在它為 true 時把 hints.MaxCastTime 壓成 0
        //    (＝這一幀不建議起長詠唱),回 false 只是少壓一次,不會多下任何指令。
        //    表達式本身一字未動,只是把宣告與賦值拆開。
        var moveImminent = false;
        try
        {
            moveImminent = _movementOverride.IsMoveRequested() && (!ActionManagerEx.Config.PreventMovingWhileCasting || _movementOverride.IsForceUnblocked());
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("MovementOverride.IsMoveRequested/IsForceUnblocked", ex);
        }

        try
        {
            _dtr.Update();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("DTRProvider.Update", ex);
        }
        try
        {
            Camera.Instance?.Update();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("Camera.Update", ex);
        }
        try
        {
            _wsSync.Update(_prevUpdateTime);
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("WorldStateGameSync.Update", ex);
        }
        try
        {
            _bossmod.Update();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("BossModuleManager.Update", ex);
        }
        // 🔴 這裡起到方法結尾,原本同樣是一串裸敘述。區域模組(深牢 AutoClear)的 Update 會
        //    呼叫 vnavmesh 的 IPC,而 IPC 端點跑在對方的碼裡 —— 對方擲什麼我們控制不了。
        //    這裡只隔離、只記 log:不吞掉任何語意差異(IpcNotReadyError 與其他例外一樣
        //    都會被記下來、都會讓這一步跳過),也不替它決定要不要重試。
        try
        {
            _zonemod.ActiveModule?.Update();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("ZoneModule.Update", ex);
        }
        // 🔴 這一步是本方法唯一的**資料生產者**:它先 _hints.Clear() 再把危險區、敵人清單、
        //    要打的技能填進 _hints,而下面**每一個**消費者都讀同一份 —— 位移攔截、循環模組、
        //    AI、FinishActionGather、疊加層。擲例外時 _hints 會停在「清乾淨了但只填到一半」。
        // 🔴 所以失敗時退回明確的保守值:再 Clear() 一次 ＝「BMR 這一幀完全沒有意見」。
        //    為什麼那是保守的 —— 空的 _hints 讓每個消費者都變成不動作:沒有危險區(位移攔截
        //    不攔、AI 沒有禁區可躲)、沒有敵人(不選目標、不移動)、ActionsToExecute 是空的
        //    (FinishActionGather 不會送出任何技能)。半套的 _hints 才危險:那會拿「只填了一半的
        //    危險區」去下真實的走位與技能決策,而且外面完全看不出來。
        //    ⚠️ 這一幀的疊加層會什麼都不畫 —— 那是刻意的:畫一半的危險區比不畫更會害人。
        // 📌 AIHints.Clear() 本身只是欄位歸零與集合 Clear(AIHints.cs:161),不做配置、不呼叫
        //    外部;仍然包一層,是為了讓「連退回都失敗」也留下紀錄而不是把例外再擲出去。
        try
        {
            _hintsBuilder.Update(_hints, PartyState.PlayerSlot, moveImminent);
        }
        catch (Exception ex)
        {
            try
            {
                _hints.Clear();
            }
            catch (Exception clearEx)
            {
                LogDrawStepFailure("AIHints.Clear(退回保守值)", clearEx);
            }
            LogDrawStepFailure("AIHintsBuilder.Update", ex);
        }
        // 危險區這時候才剛建好（hints.Clear -> 模組填 -> Normalize 都在上面那一行裡跑完）。
        // 位移攔截的快照必須在這裡拍，而且必須在 Draw 回呼裡 —— IsForceUnblocked 會讀 ImGui IO。
        // ⚠️ 這一步失敗時**不**做任何退回:DashInterceptTweak 的 _snapshot 是它的私有欄位,
        //    跳過這一步等於沿用上一幀的危險區快照 ＝ 依上一幀的資訊繼續攔位移技(fail-closed)。
        //    那個方向是安全的:多攔到的位移技下一幀就自然放行,而且使用者按著逃生鍵隨時能強制
        //    放行;反過來把快照清掉才危險(fail-open ＝ 放行衝進真的危險區)。
        //    Plugin 這一側也沒有清掉它的公開途徑,為此開一個介面是不成比例的。
        try
        {
            _amex.UpdateDashIntercept(_movementOverride.IsForceUnblocked());
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("ActionManagerEx.UpdateDashIntercept", ex);
        }
        try
        {
            _amex.QueueManualActions();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("ActionManagerEx.QueueManualActions", ex);
        }
        try
        {
            _rotation.Update(_amex.AnimationLockDelayEstimate, _movementOverride.IsMoving());
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("RotationModuleManager.Update", ex);
        }
        // 🔴 位置有兩個硬條件:必須在上面 _hintsBuilder.Update(它會 AIHints.Clear())**之後**,
        //    否則寫進去的東西當幀就被清掉;必須在下面 WindowSystem.Draw()**之前**,
        //    否則疊加層讀到的是上一幀的值。放在 _rotation.Update 之後還多一個好處:
        //    循環模組已經跑完,能直接看出它有沒有自己給方位建議(有就讓給它)。
        try
        {
            UpdatePositionalHintDisplay();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("UpdatePositionalHintDisplay", ex);
        }
        // 🔴 位置的硬條件與上面那行相同,再加一條:必須在下面 _amex.FinishActionGather() **之前** ——
        //    這一支會往 Hints.ActionsToExecute 推技能,而那個佇列就是 FinishActionGather 消費的。
        try
        {
            UpdatePredictiveMitigationWithoutPreset();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("UpdatePredictiveMitigationWithoutPreset", ex);
        }
        try
        {
            _ai.Update();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("AIManager.Update", ex);
        }
        try
        {
            _broadcast.Update();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("Broadcast.Update", ex);
        }
        // 📌 這一步不需要額外的退回值:FinishActionGather 的第一行就是 AutoQueue = default
        //    (ActionManagerEx.cs:176),所以中途擲例外留下的是「這一幀沒有要送的技能」,
        //    本身就是保守值 —— 不會把上一幀選好的技能誤送出去。
        try
        {
            _amex.FinishActionGather();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("ActionManagerEx.FinishActionGather", ex);
        }

        // 🔴 讀不到就退回 false ＝「遊戲的 HUD 沒有被隱藏」。為什麼那是保守的 —— 這個旗標只決定
        //    「要不要畫」:退回 false 保住改動前的可見行為(視窗照畫),最壞的後果是過場動畫時
        //    多看到一層疊加層,純美觀而且一眼看得出來;退回 true 則是把 BMR 的**全部**視窗與
        //    疊加層靜默關掉,那正好長得像「外掛掛了」—— 也就是這一整串隔離要消滅的那種表象。
        //    表達式本身一字未動,只是把宣告與賦值拆開。
        var uiHidden = false;
        try
        {
            uiHidden = Service.GameGui.GameUiHidden || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Service.Condition[ConditionFlag.WatchingCutscene78] || Service.Condition[ConditionFlag.WatchingCutscene];
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("GameUiHidden/Condition", ex);
        }
        // 🔴 Service.WindowSystem?.Draw() 是這個方法裡唯一刻意沒有隔離的一步。
        //    ① 它最主要的失敗面已經被 Dalamud 用更好的機制包住了:Window.DrawInternal 對
        //       this.Draw()(視窗內容)有專屬的 try/catch(本 pin 的 Window.cs:522-538)——
        //       第一次擲例外顯示紅字並在 1 秒後自動重試,10 秒內第二次才改留手動按鈕。
        //       那是**使用者看得見**的回報,在外面再包一層只會把它換成一行節流過的 log。
        //    ② 在這裡 catch 反而有風險:WindowSystem.Draw 一開頭做 ImGui.PushID(Namespace),
        //       而 Window.DrawInternal 內部有 ImGui.Begin/End 對。從中途擲出去的話 ImGui 的
        //       ID 堆疊與視窗堆疊是不平衡的,而我們無從得知該補幾次 Pop/End ——
        //       接住之後繼續送 ImGui 指令(下面的疊加層)是在一個已知壞掉的狀態上加東西。
        // ⚠️ 代價要講清楚:Dalamud 的保護**只**蓋 Window.Draw() 的內容。PreOpenCheck()、
        //    Update()、DrawConditions()、PreDraw()、PostDraw()、OnOpen()/OnClose() 這些覆寫點
        //    都沒有被蓋到 —— 那裡擲例外仍然會讓這一幀後面的每一步(疊加層、ExecuteHints()、
        //    UpdatePendingConfigSave())整批跳掉。要不要連它也包,是行為取捨,留給呼叫端裁決。
        if (!uiHidden)
        {
            Service.WindowSystem?.Draw();
            // 🔴 Service.WindowSystem?.Draw() 刻意**不包**,但它後面這一支是
            //    BMR 自己的疊加層、沒有任何保護,獨立隔離。
            try
            {
                _amex.DrawSlidecastMarker(); // overlay anchored to the game's cast bar, so it has to follow the same hidden-UI rule as the rest of the HUD
            }
            catch (Exception ex)
            {
                LogDrawStepFailure("ActionManagerEx.DrawSlidecastMarker", ex);
            }
        }

        // 📌 這一支是真的會按下按鍵/送出技能的地方(跳躍、互動、AutoQueue)。它自己內部就有
        //    節流(_throttleJump/_throttleInteract),隔離不會讓它變成連發。
        try
        {
            ExecuteHints();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("ExecuteHints", ex);
        }

        try
        {
            Camera.Instance?.DrawWorldPrimitives();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("Camera.DrawWorldPrimitives", ex);
        }
        // 🔴 這一步值得單獨隔離的理由與別的不同:它是設定存檔的去抖動寫入器。前面任何一步
        //    每幀擲例外的話,舊碼會讓它**永遠輪不到**,使用者剛改好的設定就一直不落地 ——
        //    那是靜默的資料遺失,不是畫面問題。
        try
        {
            UpdatePendingConfigSave();
        }
        catch (Exception ex)
        {
            LogDrawStepFailure("UpdatePendingConfigSave", ex);
        }
        _prevUpdateTime = DateTime.Now - tsStart;
    }

    /// <summary>DrawUI 每一步各自隔離之後,失敗訊息的節流表(鍵＝隔離點名稱)。</summary>
    /// <remarks>
    /// 📌 鍵是原始碼裡寫死的字面值(目前 21 個),所以這張表不會長大,不需要淘汰。
    /// 🔴 刻意<b>不用</b> <c>ECommons.Throttlers.EzThrottler</c>:那是整個外掛共用的靜態實例、
    /// 內部是零同步的 <c>Dictionary</c>,而且首次必放行、key 全域持久。
    /// </remarks>
    private static readonly Dictionary<string, DateTime> _drawStepLastLog = [];

    /// <summary>只保護 <see cref="_drawStepLastLog"/>。</summary>
    /// <remarks>
    /// 🔴 <b>鎖內只查表與寫回:不寫 log、不做 I/O、不碰 ImGui、不呼叫 IPC。</b>
    /// 📌 現況這張表只有繪製執行緒會碰,上鎖是廉價的保險 —— 這個「字典＋時間戳」的形狀
    /// 在艦隊裡已經以裸字典的形式壞過兩次(失敗形式是字典本身壞掉,不是拿到舊值)。
    /// </remarks>
    private static readonly object _drawStepLogGate = new();

    /// <summary>同一個隔離點的失敗訊息最快每 10 秒一則。</summary>
    /// <remarks>
    /// ⚠️ 沒有節流的話,一個穩定重現的例外會以畫面更新率(每秒數十次)寫 log —— 那正是
    /// 隔離之前 Dalamud 自己在做的事,把它原封不動搬過來就白隔離了。
    /// </remarks>
    private static readonly TimeSpan DrawStepLogThrottle = TimeSpan.FromSeconds(10d);

    /// <summary>把 DrawUI 某一步的失敗記一行 Error(節流),然後回去繼續跑同一幀後面的處理。</summary>
    /// <remarks>
    /// 🔴 時間軸用 <see cref="DateTime.UtcNow"/>(牆鐘)而不是 <c>World.CurrentTime</c>:後者是
    /// frame 時間戳,回放視窗裡會跳來跳去,拿它當節流基準會在倒帶時整批放行或整批卡死。
    /// 🔴 最外層再包一次 catch:走到這裡的時候呼叫端正在收拾殘局,連「寫 log 本身失敗」
    /// 也必須吞掉,否則例外會從 catch 區塊裡再飛出去,等於沒隔離。
    /// </remarks>
    private static void LogDrawStepFailure(string step, Exception ex)
    {
        try
        {
            var now = DateTime.UtcNow;
            bool shouldLog;
            lock (_drawStepLogGate)
            {
                shouldLog = !_drawStepLastLog.TryGetValue(step, out var last) || now - last >= DrawStepLogThrottle;
                if (shouldLog)
                {
                    _drawStepLastLog[step] = now;
                }
            }
            // 🔴 寫 log 一定在鎖外。
            if (shouldLog)
            {
                Service.Logger.Error(ex, $"[Draw] 「{step}」擲出例外,這一幀只跳過它一步,DrawUI 後面的處理照常執行。相同的失敗最多每 {DrawStepLogThrottle.TotalSeconds:f0} 秒記一次。");
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 不需啟用 preset 也能顯示的方位提示。**純顯示**,寫的是 <see cref="AIHints.PositionalHintDisplayOnly"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 這裡**絕不**寫 <see cref="AIHints.RecommendedPositional"/>:
    /// <c>AI.AIBehaviour.SelectPrimaryTarget</c> 會把那個欄位讀去設 <c>Targeting.PreferredPosition</c>,
    /// 也就是「AI 請繞到目標側背」。使用者要的是看得到提示,不是角色自己跑起來 ——
    /// 顯示與走位在這裡必須維持解耦,所以另立了一個沒有任何 AI 消費端的欄位。
    /// <para>
    /// 目標一律沿用玩家當前的硬目標,**不自己選怪**(選怪是行為不是顯示)。
    /// 推不出方位就什麼都不寫,那一幀維持 <c>default</c> ⇒ 疊加層不畫。
    /// </para>
    /// </remarks>
    private void UpdatePositionalHintDisplay()
    {
        var config = Autorotation.RotationModuleManager.Config;
        // 🔴 旗標先判:關閉時連一次推導都不跑。這就是「預設 false ＝ 對既有使用者零開銷」的來源。
        //    也一併看 ShowPositionals —— 疊加層總開關關著的話算了也沒人畫。
        if (!config.ShowPositionalsWithoutPreset || !config.ShowPositionals)
            return;

        // 循環模組自己已經給了方位建議(使用者有掛提供方位的 preset)就整段讓開:
        // 既有使用者看到的東西逐位元組不變,兩邊也不會互相打架。
        if (_hints.RecommendedPositional.Target != null)
        {
            _positionalHintAuto.Reset();
            return;
        }

        var player = _rotation.Player;
        // 戰鬥外不畫:方位推導看的是連段/量表,不在戰鬥時那些值多半是殘留的,畫出來只會誤導。
        if (player == null || !player.InCombat)
        {
            _positionalHintAuto.Reset();
            return;
        }

        // 真北期間所有方位需求自動滿足,再畫錐純粹是噪音
        if (player.FindStatus((uint)ClassShared.SID.TrueNorth) != null)
        {
            _positionalHintAuto.Reset();
            return;
        }

        var target = _rotation.WorldState.Actors.Find(player.TargetID);
        // Omnidirectional(無方位判定的敵人)無條件過濾 —— 與 UIRotationWindow.DrawPositional 同一條規則
        if (target == null || target.IsAlly || target.IsDeadOrDestroyed || !target.IsTargetable || target.Omnidirectional)
        {
            _positionalHintAuto.Reset();
            return;
        }

        var positional = _positionalHintAuto.Update(_rotation.WorldState, player, _hints, target);
        if (positional is not (Positional.Flank or Positional.Rear))
            return; // 判不出來 ⇒ 這一幀什麼都不寫

        // correct 的算式與 GoToPositional.Execute 逐字相同(那裡抄自 Basexan.UpdatePositionals)
        var toPlayer = (player.Position - target.Position).Normalized();
        var facing = target.Rotation.ToDirection();
        var correct = positional == Positional.Flank
            ? MathF.Abs(facing.Dot(toPlayer)) < 0.7071067f
            : facing.Dot(toPlayer) < -0.7071068f;

        // Imminent 固定 true,與 GoToPositional 寫 RecommendedPositional 時的做法一致
        //(我們沒有循環規劃,無從得知方位技「還有幾個 GCD」)。
        _hints.PositionalHintDisplayOnly = (target, positional, true, correct);
    }

    /// <summary>
    /// 不需掛 preset 也執行 <see cref="Autorotation.MiscAI.PredictiveMitigation"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>這一條會真的按技能</b>（模組往 <c>Hints.ActionsToExecute</c> 推減傷，
    /// 而 <c>_amex.FinishActionGather()</c> 每幀無條件消費那個佇列、不看 AI 的 ForbidActions）。
    /// 所以預設 false，UI 標籤本身就寫明會按技能，細節放 tooltip。
    /// </para>
    /// <para>
    /// <b>為什麼需要這個</b>：那個模組是「BMR 只按減傷、輸出交給外部循環外掛」的完成品，
    /// 但它只在掛了 preset 時才會被 <c>RotationModuleManager</c> 執行，而 preset 不持久化 ——
    /// 實務上等於整份程式碼在休眠。這裡把它接到與方位提示同一條「無 preset」路徑上。
    /// </para>
    /// <para>
    /// 🔑 <b>刻意不動模組本身一個字元。</b>不抽方法、不改簽名 —— 直接用它公開的
    /// <c>Execute</c> 進入點，配上「模組自己宣告的預設策略值」。
    /// 這樣「原 module 路徑行為不變」不是靠比對得出的結論，而是<b>建構上就成立</b>：
    /// <c>PredictiveMitigation.cs</c> 在這次改動裡完全沒有被修改。
    /// </para>
    /// <para>
    /// ⚠️ 掛著 preset 或有計畫在跑時整段讓開，由原本的 <c>RotationModuleManager</c> 路徑負責 ——
    /// 否則同一幀會有兩個地方推同一批減傷。強制停用（<c>ForceDisable</c>）也是 <c>Preset != null</c>，
    /// 所以一併被這條擋掉，符合「強制停用就該全部停」的直覺。
    /// </para>
    /// </remarks>
    private void UpdatePredictiveMitigationWithoutPreset()
    {
        // 🔴 旗標先判:關閉時連查詢都不做。這就是「預設 false ＝ 對既有使用者零開銷」的來源。
        if (!Autorotation.RotationModuleManager.Config.RunPredictiveMitigationWithoutPreset)
        {
            _predictiveMitAuto = null;
            _predictiveMitAutoStrategy = null;
            return;
        }

        // 掛了 preset／有計畫在跑 ⇒ 讓給 RotationModuleManager，避免同一幀推兩次。
        if (_rotation.Preset != null || _rotation.Planner?.Plan != null)
            return;

        var player = _rotation.Player;
        if (player == null)
        {
            _predictiveMitAuto = null;
            return;
        }

        // RotationModule.Player 是 readonly 且綁定建構當下那個 Actor 物件，
        // 所以玩家換人（換區、重登、換角）時必須重建，不能沿用舊實例。
        if (_predictiveMitAuto == null || _predictiveMitAuto.Player != player)
        {
            if (!Autorotation.RotationModuleRegistry.Modules.TryGetValue(typeof(Autorotation.MiscAI.PredictiveMitigation), out var entry))
                return; // 模組沒註冊成功（理論上不會發生）⇒ 什麼都不做，不擲例外
            _predictiveMitAuto = new(_rotation, player);
            // 策略值＝模組自己宣告的預設：StrategyValues 的每一格是 StrategyConfig.CreateEmpty()，
            // 也就是軌道的第 0 個選項與 DefineFloat 的 defaultValue（RaidwideLead 5s／TankbusterLead 4s／
            // EmergencyHP 30%／UnknownSchool=Skip）。與使用者在 preset 裡「沒動過任何一條軌」拿到的完全相同。
            _predictiveMitAutoStrategy = new(entry.Definition.Configs);
        }

        // 目標解析與 RotationModuleManager.Update 裡那一行逐字相同。
        var target = _hints.ForcedTarget ?? _rotation.WorldState.Actors.Find(player.TargetID);
        _predictiveMitAuto.Execute(_predictiveMitAutoStrategy!.Value, target, _amex.AnimationLockDelayEstimate, _movementOverride.IsMoving());
    }

    private unsafe bool QuestUnlocked(uint link)
    {
        // see ActionManager.IsActionUnlocked
        var gameMain = FFXIVClientStructs.FFXIV.Client.Game.GameMain.Instance();
        return link == 0
            || Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(gameMain->CurrentTerritoryTypeId)?.TerritoryIntendedUse.RowId == 31 // deep dungeons check is hardcoded in game
            || FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(link);
    }

    private unsafe void ExecuteHints()
    {
        _movementOverride.DesiredDirection = _hints.ForcedMovement;
        _movementOverride.MisdirectionThreshold = _hints.MisdirectionThreshold;
        // update forced target, if needed (TODO: move outside maybe?)
        if (_hints.ForcedTarget != null && _hints.ForcedTarget.IsTargetable)
        {
            var obj = _hints.ForcedTarget.SpawnIndex >= 0 ? FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance()->Objects.IndexSorted[_hints.ForcedTarget.SpawnIndex].Value : null;
            if (obj != null && obj->EntityId != _hints.ForcedTarget.InstanceID)
                Service.Log($"[ExecHints] Unexpected new target: expected {_hints.ForcedTarget.InstanceID:X} at #{_hints.ForcedTarget.SpawnIndex}, but found {obj->EntityId:X}");
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->Target = obj;
        }
        foreach (var s in _hints.StatusesToCancel)
        {
            var res = FFXIVClientStructs.FFXIV.Client.Game.StatusManager.ExecuteStatusOff(s.statusId, s.sourceId != 0 ? (uint)s.sourceId : 0xE0000000);
            Service.Log($"[ExecHints] Canceling status {s.statusId} from {s.sourceId:X} -> {res}");
        }
        if (_hints.WantJump && _ws.CurrentTime > _throttleJump)
        {
            //Service.Log($"[ExecHints] Jumping...");
            FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2);
            _throttleJump = _ws.FutureTime(0.1d);
        }

        if ((AI.AIManager.Instance?.Beh != null || Autorotation.MiscAI.NormalMovement.Instance != null) && CheckInteractRange(_ws.Party.Player(), _hints.InteractWithTarget))
        {
            // many eventobj interactions "immediately" start some cast animation (delayed by server roundtrip), and if we keep trying to move toward the target after sending the interact request, it will be canceled and force us to start over
            _movementOverride.DesiredDirection = default;

            if (_amex.EffectiveAnimationLock == 0 && _ws.CurrentTime >= _throttleInteract)
            {
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(GetActorObject(_hints.InteractWithTarget), false);
                _throttleInteract = _ws.FutureTime(1.1d);
            }
        }
    }

    private unsafe bool CheckInteractRange(Actor? player, Actor? target)
    {
        var playerObj = GetActorObject(player);
        var targetObj = GetActorObject(target);
        if (playerObj == null || targetObj == null)
            return false;

        // treasure chests have no client-side interact range check at all; just assume they use the standard "small" range, seems to be accurate from testing
        if (targetObj->ObjectKind is FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Treasure)
            return player?.DistanceToHitbox(target) <= 2.09f;

        // 🔴 EventFramework.Instance() 是 [StaticAddress(…, isPointer: true)]，合法可為 null。
        //    fail-closed：拿不到就回 false＝「不在互動範圍內」，於是這幀不會送出互動請求
        //    （回 true 才危險：那會讓自動互動在不該互動時送封包）。
        var eventFramework = EventFramework.Instance();
        return eventFramework != null && eventFramework->CheckInteractRange(playerObj, targetObj, 1, false);
    }

    private unsafe FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* GetActorObject(Actor? actor)
    {
        if (actor == null)
            return null;

        var obj = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance()->Objects.IndexSorted[actor.SpawnIndex].Value;
        if (obj == null || obj->GetGameObjectId() != actor.InstanceID)
            return null;

        return obj;
    }

    private void ParseAutorotationCommands(string[] cmd)
    {
        switch (cmd.Length > 1 ? cmd[1].ToUpperInvariant() : "")
        {
            case "CLEAR":
                Service.Log($"Console: clearing autorotation preset '{_rotation.Preset?.Name ?? "<n/a>"}'");
                _rotation.Preset = null;
                break;
            case "DISABLE":
                Service.Log($"Console: force-disabling from preset '{_rotation.Preset?.Name ?? "<n/a>"}'");
                _rotation.Preset = RotationModuleManager.ForceDisable;
                break;
            case "SET":
                if (cmd.Length <= 2)
                    Service.Log("Specify an autorotation preset name.");
                else
                    ParseAutorotationSetCommand([.. cmd.Skip(1)], false);
                break;
            case "TOGGLE":
                ParseAutorotationSetCommand(cmd.Length > 2 ? [.. cmd.Skip(1)] : [""], true);
                break;
            case "UI":
                _wndRotation.SetVisible(!_wndRotation.IsOpen);
                break;
        }
    }

    private void ParseAutorotationSetCommand(string[] presetName, bool toggle)
    {
        if (presetName.Length < 2)
        {
            Service.Log("No valid preset name provided.");
            return;
        }

        var userInput = string.Join(" ", presetName.Skip(1)).Trim();
        if (userInput == "null" || string.IsNullOrWhiteSpace(userInput))
        {
            _rotation.Preset = null;
            Service.Log("Disabled AI autorotation preset.");
            return;
        }
        var normalizedInput = userInput.ToUpperInvariant();
        // 🔑 preset 名查找統一走 PresetDatabase.NameComparison（單一真值來源）。.Trim() 是本呼叫點的
        //    區域輸入正規化，與大小寫敏感度是兩件事——保留在這裡，不提進 canonical 比較器。
        var preset = _rotation.Database.Presets.AllPresets
            .FirstOrDefault(p => p.Name.Trim().Equals(normalizedInput, PresetDatabase.NameComparison))
            ?? RotationModuleManager.ForceDisable;
        if (preset != null)
        {
            var newPreset = toggle && _rotation.Preset == preset ? null : preset;
            Service.Log($"Console: {(toggle ? "toggle" : "set")} changes preset from '{_rotation.Preset?.Name ?? "<n/a>"}' to '{newPreset?.Name ?? "<n/a>"}'");
            _rotation.Preset = newPreset;
        }
        else
        {
            Service.ChatGui.PrintError($"Failed to find preset '{presetName}'");
        }
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        Service.Log($"Condition change: {flag}={value}");
    }

    public static void GarbageCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // 給繪製執行緒用的版本:GC.WaitForPendingFinalizers() 是無界等待,載了幾個大 replay 之後
    // 在 Draw 裡直接呼叫會讓那一幀卡上數百毫秒(畫面明顯頓一下)。回收本身還是要做——replay 的
    // 緩衝區確實是靠 finalizer 才真正釋放——只是不能卡在 ImGui 的 frame 裡做。
    public static void GarbageCollectionAsync() => Task.Run(GarbageCollection);

    private static bool ToggleRadar(string[] messageData)
    {
        var config = Service.Config.Get<BossModuleConfig>();

        if (messageData.Length == 1)
            config.Enable = !config.Enable;
        else
        {
            switch (messageData[1].ToUpperInvariant())
            {
                case "ON":
                    config.Enable = true;
                    break;
                case "OFF":
                    config.Enable = false;
                    break;
                default:
                    Service.ChatGui.Print($"[BMR] Unknown radar command: {messageData[1]}");
                    return false;
            }
        }

        config.Modified.Fire();
        Service.Log($"Radar is now {(config.Enable ? "enabled" : "disabled")}");
        return true;
    }
}
