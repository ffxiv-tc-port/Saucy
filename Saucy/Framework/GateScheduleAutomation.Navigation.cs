using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Saucy.Framework;

internal static unsafe partial class GateScheduleAutomation
{
    /// <summary>Public so the Event Coordinator panel's "立即移動至最近的" button can trigger the
    /// same nearest-spot navigation immediately, without waiting for the :55/:15/:35 window.
    /// Returns true once interacted (or nothing usable to navigate to).</summary>
    public static bool NavigateToNearestCoordinator()
    {
        if (!Player.Available || C.GoldSaucerGates.EventCoordinatorSpots.Count == 0)
        {
            return false;
        }

        var playerPos = Player.Position;
        var nearest = C.GoldSaucerGates.EventCoordinatorSpots
            .Where(s => s.Recorded)
            .OrderBy(s => Vector3.Distance(playerPos, new Vector3(s.X, s.Y, s.Z)))
            .FirstOrDefault();

        return nearest != null && NavigateAndInteractCoordinator(nearest);
    }

    /// <summary>Walks toward the spot and interacts once in range. Returns true once interacted
    /// (or the spot/vnavmesh isn't usable), false while still en route.</summary>
    private static bool NavigateAndInteractCoordinator(GateNpcSpot spot)
    {
        if (!Vnavmesh.IsInstalled || !spot.Recorded)
        {
            return true;
        }

        var destination = new Vector3(spot.X, spot.Y, spot.Z);
        if (Vnavmesh.IsWithinHorizontalRange(destination, GateNpcNavigation.CloseRange))
        {
            // Close enough to talk — interact rather than just standing next to them. Only ever interacts, never auto-picks a menu option afterward (unlike the GATE-join flow, which is scoped to a known-safe "start minigame" confirm) — a coordinator's menu is an area-select list, not a single yes/no, so choosing where to teleport stays manual.
            // TryInteractWithBaseId needs to be called across MULTIPLE ticks: the first call only sets the target, a later (throttled) call actually fires InteractWithObject. Only report "done" once it reports the interact itself actually fired.
            if (spot.DataId == 0)
            {
                return true;
            }

            // Auto-advance any plain narration text the interact triggers — some of these NPCs teleport right off a
            // Talk dialogue with no SelectString menu at all, so requiring a menu to be visible before reporting "done"
            // would never mark those handled and keep walking back to re-interact after the teleport already happened.
            // Report done as soon as the interact itself actually fires.
            AdvanceTalkWithYesAlreadyPaused("Saucy.GateSchedule.CoordinatorTalk");

            if (GateNpcNavigation.IsInteractOnCooldown)
            {
                return false;
            }

            var interacted = ObjectHelper.TryInteractWithBaseId(spot.DataId, GateNpcNavigation.CloseRange, "Saucy.GateSchedule.Coordinator");
            if (interacted)
            {
                GateNpcNavigation.MarkInteracted();
                coordinatorTalkPosition = Player.Position;
            }

            return interacted;
        }

        switch (coordinatorSteer.Tick(coordinatorNavOwned, busy: false, out _))
        {
            case ManualSteerYield.Result.Hold:
                return false;
            case ManualSteerYield.Result.GaveUp:
                // 回 true＝這一趟結束：手動目標清掉、排程視窗記成已處理，不會馬上又走回去。
                coordinatorSteer.Reset();
                Svc.Chat.Print("[Saucy] " + "Stopped walking to the Event Coordinator: you kept moving on your own for ?? seconds.".Loc(ManualSteerWatch.MaxYieldMs / 1000));
                return true;
        }

        if (!Vnavmesh.IsMoving())
        {
            Lifestream.TryAbortForeignBeforeOwnMove("活動解說員自動靠近");
            if (Vnavmesh.TryMoveTo(destination, false, GateNpcNavigation.CloseRange))
            {
                coordinatorNavOwned = true;
            }
        }

        return false;
    }

    // The GATE NPCs the user has actually recorded via the per-GATE panels: GATEs this plugin can already fully play once joined. Air Force One's spot also covers Leap of Faith, and Cliffhanger contributes every spot in its list (it has two, confirmed by user).
    // Paired with the GateType each spot actually belongs to (not just a flat position list) so a successful join can record WHICH gate was just registered for — needed by IsWithinPostJoinSettle below.
    // each GATE's spot(s) are only offered up to the nearest-NPC search when that GATE's own toggle is on, instead of one shared switch controlling every supported GATE at once.
    private static IEnumerable<(Module.GateType Gate, GateNpcSpot Spot)> SupportedSpots
    {
        get
        {
            if (C.GoldSaucerGates.AirForceAutoJoin)
            {
                yield return (Module.GateType.AirForceOne, C.GoldSaucerGates.AirForceNpcSpot);
            }

            if (C.GoldSaucerGates.WindBlowsAutoJoin)
            {
                yield return (Module.GateType.AnyWayTheWindBlows, C.GoldSaucerGates.WindBlowsNpcSpot);
            }

            if (C.GoldSaucerGates.SliceIsRightAutoJoin)
            {
                yield return (Module.GateType.SliceIsRight, C.GoldSaucerGates.SliceIsRightNpcSpot);
            }

            if (C.GoldSaucerGates.CliffhangerAutoJoin)
            {
                foreach (var spot in C.GoldSaucerGates.CliffhangerNpcSpots)
                {
                    yield return (Module.GateType.Cliffhanger, spot);
                }
            }
        }
    }

    private static void TryJoinNearestSupportedNpc()
    {
        // Only walks up and interacts — the resulting confirmation dialogue is left for another
        // plugin (e.g. YesAlready) to handle. Auto-pressing
        // it here would just race/conflict with whatever else is watching for it.
        if (!Player.Available || !Vnavmesh.IsInstalled)
        {
            return;
        }

        joinSearchStartUtc ??= DateTime.UtcNow;

        var playerPos = Player.Position;
        IGameObject? nearestNpc = null;
        var nearestDist = float.MaxValue;
        var nearestGate = Module.GateType.None;
        foreach (var (gate, spot) in SupportedSpots)
        {
            if (!spot.Recorded || spot.DataId == 0)
            {
                continue;
            }

            var candidate = ObjectHelper.FindNearestByBaseId(spot.DataId, NpcSearchRadius);
            if (candidate == null)
            {
                continue;
            }

            var dist = Vector3.Distance(playerPos, candidate.Position);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestNpc = candidate;
                nearestGate = gate;
            }
        }

        if (nearestNpc == null)
        {
            if ((DateTime.UtcNow - joinSearchStartUtc.Value).TotalSeconds >= SearchTimeoutSeconds)
            {
                ReleaseJoinNavigation();
            }

            return;
        }

        if (nearestDist <= JoinInteractRange)
        {
            // a plain narration Talk window (before the eventual yes/no confirm another plugin like YesAlready handles) doesn't advance itself. Click through it every tick we're standing right at our own registration target — safe to do broadly here since we only reach this branch while actively trying to register for a GATE we're right next to, not some unrelated conversation.
            // Report "done" as soon as the interact itself actually fires (not just target-lock): some of these NPCs teleport right off a Talk window with no SelectString at all.
            AdvanceTalkWithYesAlreadyPaused("Saucy.GateSchedule.JoinTalk");

            if (!IsRegisterOnCooldown && !GateNpcNavigation.IsInteractOnCooldown &&
                ObjectHelper.TryInteractWithObject(nearestNpc, "Saucy.GateSchedule.Join"))
            {
                GateNpcNavigation.MarkInteracted();
                MarkJoinHandled();
                MarkJoined(nearestGate);
                lastRegisterUtc = DateTime.UtcNow;
            }

            ReleaseJoinNavigation();
            return;
        }

        switch (joinSteer.Tick(joinNavOwned, busy: false, out _))
        {
            case ManualSteerYield.Result.Hold:
                return;
            case ManualSteerYield.Result.GaveUp:
                joinNavOwned = false;
                ReleaseJoinNavigation();
                MarkJoinHandled();
                manualJoinUntilUtc = null;
                Svc.Chat.Print("[Saucy] " + "Stopped walking to the GATE registration NPC: you kept moving on your own for ?? seconds.".Loc(ManualSteerWatch.MaxYieldMs / 1000));
                return;
        }

        if (!Vnavmesh.IsMoving() && Vnavmesh.TryMoveTo(nearestNpc.Position, false, JoinInteractRange))
        {
            joinNavOwned = true;
        }
    }

    private static void ReleaseJoinNavigation()
    {
        joinSearchStartUtc = null;
        joinSteer.Reset();
        if (joinNavOwned)
        {
            joinNavOwned = false;
            if (Vnavmesh.IsInstalled)
            {
                Vnavmesh.StopOwnPath();
            }
        }
    }
}
