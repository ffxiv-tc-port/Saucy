using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using Saucy.IPC;
using System;
using System.Linq;
using System.Numerics;
namespace Saucy.Framework;

/// <summary>
/// Gold Saucer GATEs run on a fixed real-world-clock cycle: Event Coordinator NPCs ("活動解說員")
/// teleport the player to the next GATE's area during the :10/:30/:50 prep window, and the GATE
/// itself is joinable at its registration NPC during the :00/:20/:40 window. Both windows are only
/// acted on if their respective toggle is on and the user has personally recorded at least one
/// relevant NPC spot — never guessed, per the DataId-misidentification lessons elsewhere in this
/// codebase.
/// </summary>
internal static unsafe class GateScheduleAutomation
{
    private static bool IsCoordinatorWindow => DateTime.Now.Minute is 10 or 30 or 50;

    // The GATE registration NPC isn't necessarily interactable/present the instant the minute
    // ticks over (the coordinator-teleport/area transition may still be settling) — wait 30s into
    // the window before searching for it, per user feedback.
    private static bool IsJoinWindow => DateTime.Now.Minute is 0 or 20 or 40 && DateTime.Now.Second >= 30;

    // A manually-triggered "立即移動" target is remembered here and re-checked every tick (not
    // just on the click) until it either interacts or the player leaves the Saucer/stage — a
    // single one-shot call can start the walk but can't catch the moment the player actually
    // arrives, since arrival happens several frames later.
    private static GateNpcSpot? manualCoordinatorTarget;

    // Once the coordinator's actually been talked to for this window, the teleport it triggers
    // may drop the player far from every OTHER recorded spot — re-running "nearest" every
    // remaining tick of the same ~1-minute window would then immediately walk back toward
    // whichever spot is nearest (often the one just left), per user feedback ("避免觸發下一個
    // 活動時又往回跑"). Latch once handled and don't try again until the window itself resets.
    private static bool handledCoordinatorThisWindow;

    public static void Tick()
    {
        if (!GateDirector.InSaucer || GateDirector.IsPlayerOnStage())
        {
            ReleaseJoinNavigation();
            manualCoordinatorTarget = null;
            return;
        }

        if (manualCoordinatorTarget is { } manualTarget)
        {
            if (NavigateAndInteractCoordinator(manualTarget))
            {
                manualCoordinatorTarget = null;
            }
        }
        else if (IsCoordinatorWindow)
        {
            if (C.GoldSaucerGates.EventCoordinatorAutoNavigate && !handledCoordinatorThisWindow &&
                NavigateToNearestCoordinator())
            {
                handledCoordinatorThisWindow = true;
            }
        }
        else
        {
            handledCoordinatorThisWindow = false;
        }

        if (C.GoldSaucerGates.AutoJoinNearSupportedNpc && IsJoinWindow)
        {
            TryJoinNearestSupportedNpc();
        }
        else
        {
            ReleaseJoinNavigation();
        }
    }

    /// <summary>"立即移動" button — walks toward this specific spot and interacts on arrival,
    /// re-checked every tick regardless of the :10/:30/:50 window.</summary>
    public static void TriggerManualCoordinatorMove(GateNpcSpot spot) => manualCoordinatorTarget = spot;

    /// <summary>Public so the Event Coordinator panel's "立即移動至最近的" button can trigger the
    /// same nearest-spot navigation immediately, without waiting for the :10/:30/:50 window.
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
            // Close enough to talk — interact rather than just standing next to them. Only ever
            // interacts, never auto-picks a menu option afterward (unlike the GATE-join flow,
            // which is scoped to a known-safe "start minigame" confirm) — a coordinator's menu is
            // an area-select list, not a single yes/no, so choosing where to teleport stays manual.
            //
            // TryInteractWithBaseId needs to be called across MULTIPLE ticks: the first call only
            // sets the target, a later (throttled) call actually fires InteractWithObject. Only
            // report "done" once it reports the interact itself actually fired — returning true
            // right after the first (targeting-only) attempt stopped this from ever being called
            // again, so it silently got stuck after locking the target ("有鎖定 但沒有執行互動").
            if (spot.DataId == 0)
            {
                return true;
            }

            return ObjectHelper.TryInteractWithBaseId(spot.DataId, GateNpcNavigation.CloseRange, "Saucy.GateSchedule.Coordinator");
        }

        if (!Vnavmesh.IsMoving())
        {
            Vnavmesh.TryMoveTo(destination, false, GateNpcNavigation.CloseRange);
        }

        return false;
    }

    // Only the 3 GATE NPCs the user has actually recorded via the per-GATE panels — "支援的NPC"
    // per the user's request, i.e. GATEs this plugin can already fully play once joined.
    private static GateNpcSpot[] SupportedSpots => [
        C.GoldSaucerGates.AirForceNpcSpot,
        C.GoldSaucerGates.WindBlowsNpcSpot,
        C.GoldSaucerGates.SliceIsRightNpcSpot
    ];

    private const float JoinInteractRange = 5f;

    // A coordinator teleport drops the player right at the venue for the next GATE, so the real
    // registration NPC should always be nearby — a wide search radius risks picking up some OTHER
    // supported NPC that's technically visible/loaded but actually in an unrelated area, and then
    // walking a long way toward the wrong one ("觸發傳送後 不要去找距離太遠的其他報名NPC").
    private const float NpcSearchRadius = 5f;

    // If the actual NPC object can't be found in the world within 10s of starting to look (wrong
    // area, coordinator teleport didn't land where expected, NPC despawned, etc.), give up and
    // release any in-progress navigation instead of leaving the player walking indefinitely toward
    // a spot with nothing there — per user feedback ("延遲後 10秒內找不到NPC 取消導航").
    private const float SearchTimeoutSeconds = 10f;

    private static DateTime? joinSearchStartUtc;
    private static bool joinNavOwned;

    private static void TryJoinNearestSupportedNpc()
    {
        // Only walks up and interacts — the resulting confirmation dialogue is left for another
        // plugin (e.g. YesAlready) to handle, per user: "介面確認會由其他插件接管". Auto-pressing
        // it here would just race/conflict with whatever else is watching for it.
        if (!Player.Available || !Vnavmesh.IsInstalled)
        {
            return;
        }

        joinSearchStartUtc ??= DateTime.UtcNow;

        var playerPos = Player.Position;
        IGameObject? nearestNpc = null;
        var nearestDist = float.MaxValue;
        foreach (var spot in SupportedSpots)
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
            ReleaseJoinNavigation();
            ObjectHelper.TryInteractWithObject(nearestNpc, "Saucy.GateSchedule.Join");
            return;
        }

        if (!Vnavmesh.IsMoving())
        {
            Vnavmesh.TryMoveTo(nearestNpc.Position, false, JoinInteractRange);
        }
        joinNavOwned = true;
    }

    private static void ReleaseJoinNavigation()
    {
        joinSearchStartUtc = null;
        if (joinNavOwned)
        {
            joinNavOwned = false;
            if (Vnavmesh.IsInstalled)
            {
                Vnavmesh.StopPath();
            }
        }
    }
}
