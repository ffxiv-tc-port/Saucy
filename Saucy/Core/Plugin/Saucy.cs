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

    public Saucy(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this, Module.All);

        // 確認框防重按閘門的幀計數器：在掛上任何其他 Framework.Update 處理常式之前先掛，
        // 讓它排在本外掛多播委派的最前面（本 pin 是整條委派共用一個 try／catch，
        // 前面的人擲例外會讓後面的人整個 tick 不被呼叫）。拆除在 ForceTeardown()。
        AddonPressGuard.EnsureClock();

        ECommons.LanguageHelpers.Localization.Init("ChineseTraditional");
        PunishLibMain.Init(pluginInterface, "Saucy", new AboutPlugin());
        EzConfig.Migrate<Configuration>();
        C = EzConfig.Init<Configuration>();
        C.MigrateToBackgroundCpuCores();
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
        Svc.PluginInterface.UiBuilder.Draw += ObjectDebugOverlay.Draw;
        PreciseMovement.Init();
    }
    public string Name => "Saucy";
    public static Configuration C { get; private set; } = null!;

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(commandName);
        Svc.PluginInterface.UiBuilder.OpenMainUi -= EzConfigGui.Open;
        Svc.Framework.Update -= RunBot;
        Svc.PluginInterface.UiBuilder.Draw -= ObjectDebugOverlay.Draw;
        // 確認框防重按閘門掛著 AddonLifecycle 監聽器：在 ECommonsMain.Dispose() 之前拆乾淨，
        // 不留任何指向本組件的委派。
        AddonPressGuard.ForceTeardown();
        PreciseMovement.Shutdown();
        PrepareTriadSessionForPluginUnload();
        _triadCollectionHost?.Dispose();
        YesAlready.ResumeIfPausedBySaucy();
        SubscriptionManager.DisposeAll();
        TriadMapNavigation.CancelActiveNavigation();
        _triadCollectionHost = null;
        lock (_lockObj) { DisposeAudio(); }
        ModuleManager.Dispose();
        ECommonsMain.Dispose();
        P = null!;
    }

    private void OnChange(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
            TriadRunSession.StopAllAutomation();
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
