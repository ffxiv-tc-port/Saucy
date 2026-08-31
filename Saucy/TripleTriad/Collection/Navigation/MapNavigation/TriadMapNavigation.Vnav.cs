using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.LanguageHelpers;
using Saucy.IPC;
using System;
using System.Numerics;
namespace Saucy.TripleTriad;

internal static partial class TriadMapNavigation
{
    private static bool TryStartVnavImmediate(MapLinkPayload location, Vector3 destination, bool fly, TriadNpc? npc = null)
    {
        StopVnavIfRunning();
        BeginPending(
            location,
            destination,
            fly,
            ResolveTargetTerritoryId(location, npc),
            npc,
            startingPhase: NavigationPhase.WaitingForNavReady);
        _pending!.AttemptMountBeforeNav = true;
        _pending.PhaseStartedUtc = DateTime.UtcNow;

        if (!Vnavmesh.IsNavReady())
        {
            AnnounceNavMeshWait(_pending);
            return true;
        }

        TickWaitingForNavReady(_pending);
        if (_pending?.Phase == NavigationPhase.MovingToNpc)
        {
            return true;
        }

        return true;
    }

    private static void AnnounceNavMeshWait(PendingNavigation pending)
    {
        if (pending.NavMeshWaitAnnounced)
        {
            return;
        }

        pending.NavMeshWaitAnnounced = true;
        Svc.Chat.Print("[Saucy] " + "vnavmesh is not ready for this zone yet. Waiting...".Loc());
        Vnavmesh.TryEnsureNavMeshLoading();
    }

    private static void TickWaitingForNavReady(PendingNavigation pending)
    {
        if (!IsInTargetTerritory(pending.TargetTerritoryId))
        {
            if (TryBeginPendingAethernet(pending))
            {
            }

            return;
        }

        RefreshPendingDestination(pending);
        if (TryBeginMovingToNpcIfAlreadyNearby(pending))
        {
            return;
        }

        if (!pending.ArrivedViaMultiAreaRoute && Lifestream.IsBusyNow())
        {
            return;
        }

        if (!Vnavmesh.IsNavReady())
        {
            pending.NavMeshWasReady = false;
            Vnavmesh.TryEnsureNavMeshLoading();
            AnnounceNavMeshWait(pending);
            AnnounceNavMeshBuildProgress(pending);

            if (!Vnavmesh.IsBuildInProgress() &&
                DateTime.UtcNow - pending.PhaseStartedUtc > NavMeshBuildWaitTimeout)
            {
                Svc.Chat.PrintError("[Saucy] " + "vnavmesh is not ready for this zone yet.".Loc());
                ClearPending();
            }

            return;
        }

        if (Vnavmesh.IsMoving())
        {
            StopVnavIfRunning();
        }

        // Henchman waits for Nav.IsReady && player not busy before pathing.
        if (!CanBeginLocalNavigation())
        {
            return;
        }

        if (!pending.NavMeshWasReady)
        {
            pending.NavMeshWasReady = true;
            pending.PhaseStartedUtc = DateTime.UtcNow;
            pending.AttemptMountBeforeNav = true;
            pending.LastAnnouncedBuildProgress = -1;
        }

        if (pending.AttemptMountBeforeNav && !TryResolveMountForNav(pending))
        {
            if (DateTime.UtcNow - pending.PhaseStartedUtc > MountBeforeNavTimeout)
            {
                pending.AttemptMountBeforeNav = false;
            }
            else
            {
                return;
            }
        }

        pending.AttemptMountBeforeNav = false;

        if (TryBeginMovingToNpcIfAlreadyNearby(pending))
        {
            return;
        }

        if (!TryResolveMountForNav(pending))
        {
            return;
        }

        if (TryStartVnav(pending))
        {
            BeginPostVnavPhase(pending);
            return;
        }

        if (DateTime.UtcNow - pending.PhaseStartedUtc > PathfindStartTimeout)
        {
            Svc.Chat.PrintError("[Saucy] " + "vnavmesh could not start movement.".Loc());
            ClearPending();
        }
    }

    private static void AnnounceNavMeshBuildProgress(PendingNavigation pending)
    {
        var progress = Vnavmesh.GetBuildProgress();
        if (progress < 0f)
        {
            return;
        }
        if (pending.LastAnnouncedBuildProgress >= 0)
        {
            return;
        }

        pending.LastAnnouncedBuildProgress = 0;
        Svc.Chat.Print("[Saucy] " + "navmesh building...".Loc());
    }

    private static void BeginPostVnavPhase(PendingNavigation pending)
    {
        if (pending.Npc == null)
        {
            StopVnavIfRunning();
            ClearPending();
            return;
        }

        pending.Phase = NavigationPhase.MovingToNpc;
        pending.PhaseStartedUtc = DateTime.UtcNow;
        pending.VnavRetryCount = 0;
        Svc.Chat.Print("[Saucy] " + "Moving to ??...".Loc(pending.Npc.Name));
    }

    private static Vector3 ResolvePathDestination(PendingNavigation pending)
    {
        if (pending.Npc != null)
        {
            var livePos = ResolveLiveTriadNpcPosition(pending.Npc);
            if (livePos != null)
            {
                pending.Destination = ResolveNpcPathPoint(livePos.Value);
                return pending.Destination;
            }

            pending.Destination = ResolveNpcPathPoint(pending.Destination);
            return pending.Destination;
        }

        return pending.Destination;
    }

    /// <summary>
    /// 回傳「坐騎階段是否已經定案」——已經在坐騎上、或確定當下騎不上去(那就用走的)都算定案。
    /// 回傳 false 只代表「還在嘗試,下一幀再問」。
    /// ⚠️ 定案不等於「人在坐騎上」:騎不上去時這裡照樣回 true 讓導航往下走,真正擋住飛行的是
    /// <see cref="TravelMountHelper.ResolveUseFlying"/> 裡的未騎乘降級,不要把那層拿掉。
    /// (舊版此處連同 TravelMountHelper.TryMountUp 的 bool 回傳一起在說謊:騎不上去也回 true,
    ///  呼叫端因此把 fly=true 交給 vnavmesh,角色站著不動且沒有訊息。)
    /// </summary>
    private static bool TryResolveMountForNav(PendingNavigation pending)
    {
        if (!TravelMountHelper.CanMountInCurrentTerritory())
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.MountOrOrnamentTransition])
        {
            return false;
        }

        return TravelMountHelper.TryMount() != MountAttemptResult.InProgress;
    }

    private static bool TryStartVnav(PendingNavigation pending)
    {
        if (_pending != null &&
            ReferenceEquals(_pending, pending) &&
            pending.Phase != NavigationPhase.WaitingForNavReady)
        {
            return false;
        }

        if (Lifestream.IsBusyNow() || Vnavmesh.IsMoving())
        {
            return false;
        }

        if (!TryResolveMountForNav(pending))
        {
            return false;
        }

        if (pending.Npc != null)
        {
            var npcPos = ResolveNpcPathPoint(ResolveLiveTriadNpcPosition(pending.Npc) ?? pending.Destination);
            pending.Destination = npcPos;
            if (!TryMoveToNpc(pending, npcPos))
            {
                return false;
            }

            Svc.Chat.Print("[Saucy] " + "Moving to ??.".Loc(pending.Location.PlaceName));
            return true;
        }

        var destination = ResolvePathDestination(pending);
        var fly = TravelMountHelper.ResolveUseFlying(pending.Fly);

        if (Vnavmesh.TryPathfindAndMoveTo(destination, fly))
        {
            Svc.Chat.Print("[Saucy] " + "Moving to ??.".Loc(pending.Location.PlaceName));
            return true;
        }

        if (fly && Vnavmesh.TryPathfindAndMoveTo(destination))
        {
            pending.Fly = false;
            Svc.Chat.Print("[Saucy] " + "Moving to ??.".Loc(pending.Location.PlaceName));
            return true;
        }

        return false;
    }

    private static bool TryMoveToNpc(PendingNavigation pending, Vector3 npcPos)
    {
        if (Vnavmesh.IsMoving())
        {
            return false;
        }

        if (!TryResolveMountForNav(pending))
        {
            return false;
        }

        var fly = TravelMountHelper.ResolveUseFlying(pending.Fly);
        if (Vnavmesh.TryMoveTo(npcPos, fly, NpcPathArrivalRange))
        {
            return true;
        }

        if (fly && Vnavmesh.TryMoveTo(npcPos, false, NpcPathArrivalRange))
        {
            pending.Fly = false;
            return true;
        }

        return false;
    }

    private static bool TryPathToNpcBackoff(Vector3 npcPos, bool fly)
    {
        var dest = ResolveNpcBackoffPoint(npcPos);
        // 這段後退是 NPC 互動前的短距離重新站位,呼叫端的 IsReadyForNpcInteraction() 內含
        // EnsureDismountedForNpcInteraction(),到這裡一定是下坐騎狀態,本來就不該飛。
        // ResolveUseFlying 現在也會在未騎乘時降級,這裡再明確擋一次,避免將來那層被放寬時
        // 又把 fly=true 交給 vnavmesh —— 那會讓角色站著不動而且完全沒有訊息。
        var useFly = TravelMountHelper.ResolveUseFlying(fly) && Svc.Condition[ConditionFlag.Mounted];
        return Vnavmesh.TryPathfindAndMoveTo(dest, useFly) ||
               (useFly && Vnavmesh.TryPathfindAndMoveTo(dest));
    }

    private static void StopVnavIfRunning()
    {
        if (!Vnavmesh.IsMoving())
        {
            return;
        }

        Vnavmesh.StopPath();
    }
}
