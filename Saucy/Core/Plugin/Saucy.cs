using Dalamud.Plugin;
using ECommons;
using ECommons.Configuration;
using ECommons.LanguageHelpers;
using ECommons.SimpleGui;
using NAudio.Wave;
using PunishLib;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Collections.Specialized;
using Module = ECommons.Module;

namespace Saucy;

public sealed partial class Saucy : IDalamudPlugin
{
    private const string commandName = "/saucy";
    public static Saucy P = null!;

    public static TriadSession TriadRun = new();

    public static UIReaderTriadGame uiReaderGame = null!;
    public static UIReaderTriadPrep uiReaderPrep = null!;
    public static UIReaderTriadResults uiReaderMatchResults = null!;
    public static UIReaderScheduler uiReaderScheduler = null!;
    public static UIReaderGamesResults uiReaderGamesResults = null!;
    public static GameDataLoader dataLoader = null!;
    public static ModuleManager ModuleManager = null!;

    private readonly object _lockObj = new();
    private readonly PluginUI _pluginUi = new();
    private bool _autoOpenedForTriadFlow;
    private Mp3FileReader? _currentReader;
    private WaveOutEvent? _currentWaveOut;
    private TriadCollectionHost? _triadCollectionHost;
    private StopAllIpc? _stopAllIpc;

    public Saucy(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this, Module.All);

        // 確認框防重按閘門的幀計數器：在掛上任何其他 Framework.Update 處理常式之前先掛，
        // 讓它排在本外掛多播委派的最前面（本 pin 是整條委派共用一個 try／catch，
        // 前面的人擲例外會讓後面的人整個 tick 不被呼叫）。拆除在 ForceTeardown()。
        AddonPressGuard.EnsureClock();

        ECommons.LanguageHelpers.Localization.Init("ChineseTraditional");
        PunishLibMain.Init(pluginInterface, "Saucy", new AboutPlugin() { IconPath = LocalIconPath(pluginInterface) });
        EzConfig.Migrate<Configuration>();
        C = EzConfig.Init<Configuration>();
        C.MigrateToBackgroundCpuCores();
        var renamedNpcKeys = C.NormalizeNpcNameKeys();
        if (renamedNpcKeys > 0)
        {
            Svc.Log.Information($"[Saucy] 對戰次數統計有 {renamedNpcKeys} 個 NPC 名是 7.25 前的舊中間點寫法，已改成新寫法（同一個 NPC 兩種寫法的次數已相加）");
        }

        if (C.GoldSaucerGates.MigrateAirForceCircleRadii())
        {
            Svc.Log.Information($"[Saucy] 空軍裝甲駕駛員：原本共用的半徑 {C.GoldSaucerGates.AirForceBombAvoidRadius:0} 拆成炸彈避讓圈 {C.GoldSaucerGates.BombCircleRadius():0}、目標標靶圈 {C.GoldSaucerGates.TargetCircleRadius():0}");
        }

        TriadRunSession.ModuleEnabled = false;
        TriadCardFarmSession.DeactivateSession(clearProgress: true);
        TriadRunSession.ResetRunModeForPluginLoad();
        C.Save();
        PrepareTriadSessionForPluginLoad();
        P = this;

        EzConfigGui.Init(_pluginUi);
        Svc.PluginInterface.UiBuilder.OpenMainUi += EzConfigGui.Open;

        Svc.Commands.AddHandler(commandName, new(OnCommand)
        {
            HelpMessage = "Opens the Saucy menu. Use /saucy d for debug, /saucy stop to halt navigation and automation.".Loc()
        });

        dataLoader = new();
        dataLoader.StartAsyncWork();

        TriadRun.profileGS = new();

        uiReaderGame = new();
#pragma warning disable CS8622
        uiReaderGame.OnUIStateChanged += TriadRun.UpdateGame;
#pragma warning restore CS8622

        uiReaderPrep = new()
        {
            shouldScanDeckData = (TriadRun.profileGS == null) || TriadRun.profileGS.HasErrors
        };
        uiReaderPrep.OnUIStateChanged += TriadRun.UpdateDecks;
        uiReaderPrep.OnMatchRequestChanged += OnTriadPrepUiChanged;
        uiReaderPrep.OnDeckSelectionChanged += OnTriadPrepUiChanged;

        uiReaderMatchResults = new();
        uiReaderMatchResults.OnUpdated += CheckResults;

        uiReaderGamesResults = new();
        uiReaderGamesResults.OnAirForceUpdated += CheckAirForceResults;

        uiReaderScheduler = new(Svc.GameGui);
        uiReaderScheduler.AddObservedAddon(uiReaderGame);
        uiReaderScheduler.AddObservedAddon(uiReaderPrep.uiReaderMatchRequest);
        uiReaderScheduler.AddObservedAddon(uiReaderPrep.uiReaderDeckSelect);
        uiReaderScheduler.AddObservedAddon(uiReaderMatchResults);
        uiReaderScheduler.AddObservedAddon(uiReaderGamesResults);

        ModuleManager = new();
        C.EnabledModules.CollectionChanged += OnChange;

        _triadCollectionHost = new(pluginInterface);

        SubscriptionManager.Prepare();
        SubscriptionManager.Subscribe();
        Svc.Framework.Update += RunBot;
        TriadDialogueSkip.RegisterTalkSetupClaim();
        Svc.PluginInterface.UiBuilder.Draw += ObjectDebugOverlay.Draw;
        PreciseMovement.Init();
        _stopAllIpc = new();
    }

    /// <summary>
    /// 出貨包裡與外掛庫圖示相同的 images/about-icon.png；不存在時回 null（關於分頁沿用 manifest 的 IconUrl）。
    /// 不用 csproj 既有的 images/icon.png：DalamudPackager 打包時會把那個檔名從 latest.zip 剔除。
    /// </summary>
    private static string? LocalIconPath(IDalamudPluginInterface pluginInterface)
    {
        var dir = pluginInterface.AssemblyLocation.DirectoryName;
        if (dir is null) return null;
        var path = System.IO.Path.Combine(dir, "images/about-icon.png");
        return System.IO.File.Exists(path) ? path : null;
    }

    public string Name => "Saucy";
    public static Configuration C { get; private set; } = null!;

    public void Dispose()
    {
        _stopAllIpc?.Dispose();
        Svc.Commands.RemoveHandler(commandName);
        Svc.PluginInterface.UiBuilder.OpenMainUi -= EzConfigGui.Open;
        Svc.Framework.Update -= RunBot;
        Svc.PluginInterface.UiBuilder.Draw -= ObjectDebugOverlay.Draw;
        // 確認框防重按閘門掛著 AddonLifecycle 監聽器：在 ECommonsMain.Dispose() 之前拆乾淨，
        // 不留任何指向本組件的委派。
        AddonPressGuard.ForceTeardown();
        PreciseMovement.Shutdown();
        TriadDialogueSkip.RemoveTalkListeners();

        // 先收自己發起的移動、停模組，再釋放 IPC 訂閱：釋放之後 vnavmesh／Lifestream 包裝全變 no-op，停不到自己的路徑。
        // 卸載期（遊戲關閉）Dispose 可能就地跑在別的執行緒：那時不呼叫別的插件，維持先釋放、模組停用時的 IPC 全是 no-op。
        var canReachOtherPlugins = !Svc.Framework.IsFrameworkUnloading && Svc.Framework.IsInFrameworkUpdateThread;
        if (!canReachOtherPlugins)
        {
            SubscriptionManager.DisposeAll();
        }

        PrepareTriadSessionForPluginUnload();
        StopOwnMovementForUnload();
        ModuleManager.Dispose();
        _triadCollectionHost?.Dispose();
        YesAlready.ResumeIfPausedBySaucy();
        if (canReachOtherPlugins)
        {
            SubscriptionManager.DisposeAll();
        }

        _triadCollectionHost = null;
        lock (_lockObj) { DisposeAudio(); }
        ECommonsMain.Dispose();
        P = null!;
    }

    /// <summary>卸載時收回自己發起、還在跑的移動與流程（只停自己的；每個停止點各自 try，一個失敗不擋其他）。</summary>
    private static void StopOwnMovementForUnload()
    {
        ECommons.GenericHelpers.Safe(TriadMapNavigation.CancelActiveNavigation);
        ECommons.GenericHelpers.Safe(() => global::Saucy.Framework.GoldSaucer.GoldSaucerNavigator.Cancel(announce: false, reason: "插件卸載"));
        ECommons.GenericHelpers.Safe(() => global::Saucy.MiniCactpot.MiniCactpotAutoPlay.Cancel(announce: false));
        ECommons.GenericHelpers.Safe(() => global::Saucy.JumboCactpot.JumboCactpotAutoTalk.Cancel(announce: false));
        ECommons.GenericHelpers.Safe(GateScheduleAutomation.ReleaseOwnNavigation);
    }

    private void OnChange(object? sender, NotifyCollectionChangedEventArgs e)
    {
        GateEmergencyHold.OnModulesChanging();
        foreach (var m in ModuleManager.Modules)
        {
            if (C.EnabledModules.Contains(m.InternalName) && !m.IsEnabled)
            {
                m.EnableInternal();
            }

            if (!C.EnabledModules.Contains(m.InternalName) && m.IsEnabled)
            {
                m.DisableInternal();
            }
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Length == 0)
        {
            EzConfigGui.Toggle();
            return;
        }

        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length >= 1 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            // 與 IPC Saucy.StopAll 同一份停止清單，也同樣壓住目前的 GATE 時段：不壓的話，定時流程下一幀就把剛停的路又走起來。
            var holdUntil = StopAllIpc.StopNow("/saucy stop");
            Svc.Chat.Print("[Saucy] " + "Stopped navigation, travel, and triad automation.".Loc());
            if (holdUntil is { } until)
            {
                Svc.Chat.Print("[Saucy] " + "GATE schedule and GATE modules are paused until ??. Press a start button to resume earlier.".Loc(until.ToString("HH:mm")));
            }

            return;
        }

        if (args.Length >= 1 && args[0].Equals("d", StringComparison.OrdinalIgnoreCase))
        {
            _pluginUi.OpenForDebug();
            EzConfigGui.Open();
            return;
        }

        if (args.Length < 2 || !args[0].Equals("tt", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var subCommand = args[1];

        if (subCommand.Equals("go", StringComparison.OrdinalIgnoreCase))
        {
            TriadRunSession.ModuleEnabled = true;
            TriadRunSession.BeginAutomationSession();
            Svc.Chat.Print("[Saucy] " + "Triad Module Enabled!".Loc());
            return;
        }

        if (subCommand.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            TriadRunSession.StopAllAutomation();
            return;
        }

        if (subCommand.Equals("play", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
        {
            if (int.TryParse(args[2], out var val))
            {
                TriadRunSession.ApplyRunMode(TriadRunMode.PlayXTimes, matchCount: val);
                Svc.Chat.Print("[Saucy] " + "Play X Amount of Times Enabled!".Loc());
            }
            else
            {
                Svc.Chat.Print("[Saucy] " + "Incorrect value specified: ??".Loc(args[2]));
            }
            return;
        }

        if (subCommand == "cards" && args.Length >= 3)
        {
            if (args[2].ToLower() == "any")
            {
                TriadRunSession.ApplyRunMode(TriadRunMode.PlayUntilAnyCard);
                Svc.Chat.Print("[Saucy] " + "Play Until Any Cards Drop Enabled!".Loc());
            }

            if (args[2].ToLower() == "all")
            {
                TriadRunSession.ApplyRunMode(TriadRunMode.PlayUntilAllCards);
                Svc.Chat.Print("[Saucy] " + "Play Until All Cards Drop from NPC at Least X Times Enabled!".Loc());
            }

            if (args.Length >= 4 && int.TryParse(args[3], out var val))
            {
                TriadRunSession.NumberOfTimes = Math.Max(1, val);
                if (TriadRunSession.PlayXTimes)
                {
                    C.TriadMatchCount = TriadRunSession.NumberOfTimes;
                    C.Save();
                }
            }
        }
    }
}
