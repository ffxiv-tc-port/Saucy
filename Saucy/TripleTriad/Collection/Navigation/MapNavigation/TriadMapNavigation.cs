using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Numerics;
namespace Saucy.TripleTriad;

internal static unsafe partial class TriadMapNavigation
{
    private const float NpcInteractionRange = 6f;

    private const float NpcPathArrivalRange = 3f;

    private const float NpcMinStandoffDistance = 2.75f;
    private const float NpcBackoffDistance = 4.5f;

    // 到目的地（3D 距離）短於此值就不上坐騎、不飛，直接走；與 Questionable 採集／visland／AutoDuty 同值。
    private const float WalkWithoutMountDistance = 20f;
    private static readonly TimeSpan DefaultNavigationTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NavMeshBuildWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PathfindStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MountBeforeNavTimeout = TimeSpan.FromSeconds(15);

    private static PendingNavigation? _pending;
    private static TriadNavigationGoal _activeNavigationGoal = TriadNavigationGoal.FarmCards;
    private static bool _frameworkSubscribed;

    public static bool IsNavigationActive => _pending != null;

    public static bool IsExecutingMultiAreaRoute
        => _pending?.Phase == NavigationPhase.ExecutingRoute;

    public static bool IsInNavigationTargetTerritory() =>
        _pending == null ||
        _pending.TargetTerritoryId == 0 ||
        IsInTargetTerritory(_pending.TargetTerritoryId);

    public static void HandleMapClick(
        MapLinkPayload location,
        TriadNpc? npc = null,
        bool fly = true,
        TriadNavigationGoal goal = TriadNavigationGoal.FarmCards)
    {
        // Pre-flight unlock check — runs BEFORE anything else (Battle Hall block, duplicate-navigation
        // check, deck-optimizer prep, teleport). If the NPC's prerequisite quest isn't complete, refuse
        // to do anything: no teleport, no walk, no interaction spam.
        if (TryRejectLockedNpcOnClick(npc))
        {
            return;
        }

        if (TriadBattleHall.ShouldBlockMapNavigation(npc, location))
        {
            TriadBattleHall.PrintNavigationBlocked();
            return;
        }

        if (npc != null &&
            IsNavigationActive &&
            _pending?.Npc?.Id == npc.Id &&
            _pending?.NavigationGoal == goal)
        {
            return;
        }

        _activeNavigationGoal = goal;

        if (!TryBeginNavigation(location, fly, npc))
        {
            Svc.GameGui.OpenMapWithMapLink(location);
            return;
        }

        if (npc != null)
        {
            TriadRunSession.PrepareNavigationRunMode(npc, goal);
            TriadRun.OnNpcSelected(npc, [], true, true);
        }
    }

    private static bool TryRejectLockedNpcOnClick(TriadNpc? npc) =>
        TriadNpcUnlockHelper.TryRejectForClick(npc, out var _);

    public static bool TryRejectLockedNpcDuringNavigation(TriadNpc? npc)
    {
        if (!TriadNpcUnlockHelper.TryReject(npc, out var reason))
        {
            return false;
        }

        TriadNpcUnlockHelper.Announce(reason);
        CancelActiveNavigation();
        return true;
    }

    public static void CancelActiveNavigation()
    {
        TriadRunSession.ClearNavigationDeckBuildOverride();

        if (_pending?.RouteExecution != null)
        {
            _pending.RouteExecution.Failed = true;
        }

        StopVnavIfRunning();
        Lifestream.TryAbort();
        ClearPending();
    }

    public static void Tick()
    {
        var pending = _pending;
        if (pending == null)
        {
            return;
        }

        if (TriadUiState.IsAutomationFlowActive())
        {
            CancelActiveNavigation();
            return;
        }

        if (TryRejectLockedNpcDuringNavigation(pending.Npc))
        {
            return;
        }

        if (pending.Npc != null)
        {
            TriadRun.EnsureNavigationDeckOptimizerStarted(pending.Npc);
        }

        var yielding = TickManualSteer(pending);
        if (_pending != pending)
        {
            return;
        }

        var timeout = pending.RouteExecution?.Route.Timeout ?? DefaultNavigationTimeout;
        var shouldWaitForDeckOptimizer =
            pending.Phase is NavigationPhase.MovingToNpc or NavigationPhase.StartingTriadMatch;
        var waitingForDeckOptimizer =
            shouldWaitForDeckOptimizer &&
            pending.Npc != null &&
            TriadRun.IsNavigationBlockedWaitingForOptimizer(pending.Npc);
        var waitingForNavMesh =
            pending.Phase == NavigationPhase.WaitingForNavReady && !Vnavmesh.IsNavReady();
        var waitingNearNpc =
            pending.Npc != null &&
            pending.Phase is NavigationPhase.MovingToNpc or NavigationPhase.StartingTriadMatch &&
            IsPlayerWithinNpcInteractionRange(pending);
        // 讓位期間等的是使用者，整趟逾時的時鐘停住（路線裡的移動步驟讓位也一樣）。
        var waitingForUser = yielding || pending.RouteExecution?.Steer?.Yielding == true;
        if (waitingForDeckOptimizer || waitingForNavMesh || waitingNearNpc || waitingForUser)
        {
            pending.StartedUtc = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - pending.StartedUtc > timeout)
        {
            Svc.Chat.PrintError("[Saucy] " + "Navigation timed out.".Loc());
            ClearPending();
            return;
        }

        if (yielding)
        {
            return;
        }

        switch (pending.Phase)
        {
            case NavigationPhase.WaitingForLifestream:
                SuppressVnavDuringLifestream();
                if (!IsLifestreamTravelComplete(pending))
                {
                    return;
                }

                if (pending.RouteExecution != null)
                {
                    pending.Phase = NavigationPhase.ExecutingRoute;
                    pending.PhaseStartedUtc = DateTime.UtcNow;
                    return;
                }

                if (TryBeginPendingAethernet(pending))
                {
                    return;
                }

                if (!IsInTargetTerritory(pending.TargetTerritoryId))
                {
                    return;
                }

                RefreshPendingDestination(pending);
                if (TryBeginMovingToNpcIfAlreadyNearby(pending))
                {
                    return;
                }

                pending.Phase = NavigationPhase.WaitingForNavReady;
                pending.PhaseStartedUtc = DateTime.UtcNow;
                pending.AttemptMountBeforeNav = true;
                return;

            case NavigationPhase.ExecutingRoute:
                if (pending.RouteExecution == null)
                {
                    ClearPending();
                    return;
                }

                if (pending.RouteExecution.Failed)
                {
                    ClearPending();
                    return;
                }

                if (MultiAreaRouteExecutor.Tick(pending.RouteExecution))
                {
                    ContinueAfterZoneArrival(pending);
                }

                return;

            case NavigationPhase.WaitingForAethernet:
                SuppressVnavDuringLifestream();
                if (!IsLifestreamTravelComplete(pending))
                {
                    return;
                }

                pending.PendingAethernetShardId = 0;
                pending.PendingAethernetShardName = null;
                RefreshPendingDestination(pending);
                if (TryBeginMovingToNpcIfAlreadyNearby(pending))
                {
                    return;
                }

                pending.Phase = NavigationPhase.WaitingForNavReady;
                pending.PhaseStartedUtc = DateTime.UtcNow;
                pending.AttemptMountBeforeNav = true;
                return;

            case NavigationPhase.ApproachingAethernetHub:
                TickApproachingAethernetHub(pending);
                return;

            case NavigationPhase.WaitingForNavReady:
                TickWaitingForNavReady(pending);
                return;

            case NavigationPhase.MovingToNpc:
                TickMovingToNpc(pending);
                return;

            case NavigationPhase.StartingTriadMatch:
                TickStartingTriadMatch(pending);
                return;
        }
    }
    private static void BeginPending(
        MapLinkPayload location,
        Vector3 destination,
        bool fly,
        uint targetTerritoryId,
        TriadNpc? npc = null,
        MultiAreaRouteExecutor.RouteExecution? routeExecution = null,
        uint aethernetShardId = 0,
        string? aethernetShardName = null,
        uint activeAethernetShardId = 0,
        uint hubAetheryteId = 0,
        uint expectedPostTeleportTerritoryId = 0,
        NavigationPhase startingPhase = NavigationPhase.WaitingForLifestream)
    {
        if (startingPhase is NavigationPhase.WaitingForLifestream or NavigationPhase.WaitingForAethernet)
        {
            StopVnavIfRunning();
        }

        _pending = new()
        {
            Location = location,
            Destination = destination,
            Fly = fly,
            Npc = npc,
            NavigationGoal = _activeNavigationGoal,
            TargetTerritoryId = targetTerritoryId,
            RouteExecution = routeExecution,
            PendingAethernetShardId = aethernetShardId,
            PendingAethernetShardName = aethernetShardName,
            ActiveAethernetShardId = activeAethernetShardId,
            HubAetheryteId = hubAetheryteId,
            ExpectedPostTeleportTerritoryId = expectedPostTeleportTerritoryId,
            AethernetHop = activeAethernetShardId != 0 ? NewAethernetHop(activeAethernetShardId, npc, location) : null,
            Phase = startingPhase,
            StartedUtc = DateTime.UtcNow,
            PhaseStartedUtc = DateTime.UtcNow
        };

        EnsureFrameworkSubscription();
    }

    public static bool TryGetPendingNpc(out TriadNpc? npc)
    {
        npc = _pending?.Npc;
        return npc != null;
    }

    public static bool IsAwaitingTriadStartDialog() =>
        _pending?.Phase == NavigationPhase.StartingTriadMatch;

    public static bool TryAdvanceTriadStartDialog()
    {
        if (TalkHelper.IsVisible())
        {
            if (!IsStableForNpcCommands())
            {
                return false;
            }

            if (SelectYesnoHelper.TryGetTriadYesno(out var talkYesno) &&
                SelectYesnoHelper.PressYes(talkYesno))
            {
                TriadNpcGate.MarkDialogueFlow();
                return true;
            }

            if (TalkHelper.TryAdvance("SaucyNavTalk"))
            {
                TriadNpcGate.MarkDialogueFlow();
                return true;
            }

            return false;
        }

        if (SelectStringHelper.IsNpcListMenuVisible() &&
            SelectStringHelper.TrySelectTriadEntry())
        {
            TriadNpcGate.MarkDialogueFlow();
            return true;
        }

        if (!IsStableForNpcCommands())
        {
            return false;
        }

        if (SelectYesnoHelper.TryGetTriadYesno(out var yesno) &&
            SelectYesnoHelper.PressYes(yesno))
        {
            TriadNpcGate.MarkDialogueFlow();
            return true;
        }

        return false;
    }

    private static void ClearPending(bool stopVnav = true)
    {
        TriadRun.SyncDeckOptimizerBackgroundPause();
        if (stopVnav)
        {
            StopVnavIfRunning();
        }

        _pending = null;
    }

    /// <summary>自己送路徑的幾個階段裡，使用者接手就讓位：回 true＝這一幀什麼都不送、不算逾時。
    /// 停手後從目前位置接續（重試次數歸零、階段時鐘往後推讓位的時間）；讓位太久就結束這一趟、不停使用者的移動。</summary>
    private static bool TickManualSteer(PendingNavigation pending)
    {
        var sendsPaths = pending.Phase is NavigationPhase.ApproachingAethernetHub or NavigationPhase.MovingToNpc
                             or NavigationPhase.StartingTriadMatch ||
                         (pending.Phase == NavigationPhase.WaitingForNavReady && Vnavmesh.IsNavReady());
        if (!sendsPaths)
        {
            pending.Steer.Reset();
            return false;
        }

        // 自己送的路徑跑完之後，vnavmesh 上若還有路徑就是別人的：讓位時只停自己的，絕不全域取消別人的尋路。
        if (!Vnavmesh.IsMoving())
        {
            pending.NavOwned = false;
        }

        switch (pending.Steer.Tick(ownsPath: pending.NavOwned, busy: false, out var paused))
        {
            case ManualSteerYield.Result.Hold:
                return true;
            case ManualSteerYield.Result.Resumed:
                pending.PhaseStartedUtc += paused;
                pending.VnavRetryCount = 0;
                return false;
            case ManualSteerYield.Result.GaveUp:
                Svc.Chat.PrintError("[Saucy] " + "Stopped navigation: you kept moving on your own for ?? seconds.".Loc(global::Saucy.Framework.GoldSaucer.ManualSteerWatch.MaxYieldMs / 1000));
                ClearPending(stopVnav: false);
                return true;
            default:
                return false;
        }
    }
    private static bool IsBetweenAreas() => Svc.Condition[ConditionFlag.BetweenAreas];

    private static bool CanBeginLocalNavigation() =>
        Player.Interactable &&
        !IsBetweenAreas() &&
        !Player.IsAnimationLocked &&
        !Svc.Condition[ConditionFlag.Jumping] &&
        !Svc.Condition[ConditionFlag.MountOrOrnamentTransition] &&
        !Svc.Condition[ConditionFlag.Casting];

    private static bool IsStableForNpcCommands() =>
        !Svc.Condition[ConditionFlag.Mounted] &&
        CanBeginLocalNavigation();

    private static bool IsReadyForNpcInteraction() =>
        EnsureDismountedForNpcInteraction() && IsStableForNpcCommands();

    private static void EnsureFrameworkSubscription()
    {
        if (_frameworkSubscribed)
        {
            return;
        }

        Svc.Framework.Update += OnFrameworkUpdate;
        _frameworkSubscribed = true;
    }

    private static void OnFrameworkUpdate(IFramework _)
    {
        Tick();

        if (_pending == null && _frameworkSubscribed)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            _frameworkSubscribed = false;
        }
    }

    private enum NavigationPhase
    {
        WaitingForLifestream,
        ExecutingRoute,
        WaitingForAethernet,
        ApproachingAethernetHub,
        WaitingForNavReady,
        MovingToNpc,
        StartingTriadMatch
    }

    private sealed class PendingNavigation
    {
        public uint ActiveAethernetShardId;
        public AethernetHopWatch? AethernetHop;
        public bool AnnouncedTriadStart;
        public bool ArrivedViaMultiAreaRoute;
        public bool AttemptMountBeforeNav;
        public required Vector3 Destination;
        public uint ExpectedPostTeleportTerritoryId;
        public required bool Fly;
        public uint HubAetheryteId;
        public int LastAnnouncedBuildProgress = -1;
        public required MapLinkPayload Location;
        public TriadNavigationGoal NavigationGoal;
        public bool NavMeshWaitAnnounced;
        public bool NavMeshWasReady;
        /// <summary>vnavmesh 正在跑的路徑是這一趟自己送的（送成功時設、路徑停了就清）。</summary>
        public bool NavOwned;
        public TriadNpc? Npc;
        public int NpcInteractionAttempts;
        public uint PendingAethernetShardId;
        public string? PendingAethernetShardName;
        public NavigationPhase Phase;
        public DateTime PhaseStartedUtc;
        public MultiAreaRouteExecutor.RouteExecution? RouteExecution;
        public DateTime StartedUtc;
        public readonly ManualSteerYield Steer = new("幻卡 NPC 導航");
        public required uint TargetTerritoryId;
        public int VnavRetryCount;
    }
}
