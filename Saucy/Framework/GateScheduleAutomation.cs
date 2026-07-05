using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using Saucy.IPC;
using System;
using System.Collections.Generic;
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
    // Delayed by 1 minute off the raw :10/:30/:50 mark per user feedback, presumably to give the
    // previous GATE's wrap-up/reward window time to actually finish before wandering off.
    private static bool IsCoordinatorWindow => DateTime.Now.Minute is 11 or 31 or 51;

    // The GATE registration NPC isn't necessarily interactable/present the instant the minute
    // ticks over (the coordinator-teleport/area transition may still be settling) — wait 30s into
    // the window before searching for it, per user feedback.
    private static bool IsJoinWindow => DateTime.Now.Minute is 0 or 20 or 40 && DateTime.Now.Second >= 30;

    // A manually-triggered "立即移動" target is remembered here and re-checked every tick (not
    // just on the click) until it either interacts or the player leaves the Saucer/stage — a
    // single one-shot call can start the walk but can't catch the moment the player actually
    // arrives, since arrival happens several frames later.
    private static GateNpcSpot? manualCoordinatorTarget;

    // "我會在非活動期間類嘗試進入活動區 能讓我手動執行 開始導航嗎" — lets the join search run right
    // now regardless of the :00/:20/:40 clock window, for testing/early entry. Auto-expires so a
    // forgotten manual trigger doesn't run forever.
    private static readonly TimeSpan ManualJoinDuration = TimeSpan.FromSeconds(60);
    private static DateTime? manualJoinUntilUtc;

    // Once the coordinator's actually been talked to for this window, the teleport it triggers
    // may drop the player far from every OTHER recorded spot — re-running "nearest" every
    // remaining tick of the same ~1-minute window would then immediately walk back toward
    // whichever spot is nearest (often the one just left), per user feedback ("避免觸發下一個
    // 活動時又往回跑"). Latch once handled and don't try again until the window itself resets.
    //
    // Persisted to Configuration (not a plain static bool) — a plugin reload mid-window used to
    // forget "already handled" and immediately repeat the search/walk, since a fresh in-memory
    // flag defaults back to false ("我已參加過 重載後記錄消失 又回去找NPC"). The window itself is
    // only ~1 minute wide and windows are 20 minutes apart, so "handled within the last 10
    // minutes" reliably means "already handled THIS window" without needing exact window-id
    // bookkeeping.
    private static readonly TimeSpan HandledLatchWindow = TimeSpan.FromMinutes(10);

    private static bool HasHandledRecently(long utcTicks) =>
        utcTicks != 0 && DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc) < HandledLatchWindow;

    private static void MarkCoordinatorHandled()
    {
        C.GoldSaucerGates.LastCoordinatorHandledUtcTicks = DateTime.UtcNow.Ticks;
        C.Save();
    }

    private static void MarkJoinHandled()
    {
        C.GoldSaucerGates.LastJoinHandledUtcTicks = DateTime.UtcNow.Ticks;
        C.Save();
    }

    public static void Tick()
    {
        if (!GateDirector.InSaucer || GateDirector.IsPlayerOnStage())
        {
            ReleaseJoinNavigation();
            manualCoordinatorTarget = null;
            manualJoinUntilUtc = null;

            // Actually being on stage means the join succeeded (interacting alone doesn't always
            // report success back if the ride/duty pulls the player in immediately after) — latch
            // this so that leaving the GATE while still inside the same window doesn't restart the
            // NPC search from scratch ("退出遊戲後 會一直找同一個NPC").
            if (GateDirector.IsPlayerOnStage())
            {
                MarkJoinHandled();
            }

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
            if (C.GoldSaucerGates.EventCoordinatorAutoNavigate &&
                !HasHandledRecently(C.GoldSaucerGates.LastCoordinatorHandledUtcTicks) &&
                NavigateToNearestCoordinator())
            {
                MarkCoordinatorHandled();
            }
        }

        // Per-GATE toggles (AirForceAutoJoin/WindBlowsAutoJoin/etc.) are applied inside
        // SupportedSpots itself, so the overall search just needs to run whenever ANY of them
        // might be enabled — no single shared "auto join" flag to check here anymore.
        var manualJoinActive = manualJoinUntilUtc is { } until && DateTime.UtcNow < until;
        if (manualJoinActive || (IsJoinWindow && !HasHandledRecently(C.GoldSaucerGates.LastJoinHandledUtcTicks)))
        {
            TryJoinNearestSupportedNpc();
        }
        else
        {
            ReleaseJoinNavigation();
            manualJoinUntilUtc = null;
        }
    }

    /// <summary>Manual "開始導航" trigger — runs the same GATE-join search/walk/interact flow as
    /// the :00/:20/:40 window, right now, ignoring the clock and the "already handled" latch. Use
    /// when testing or entering the area early/outside the normal window.</summary>
    public static void TriggerManualJoin() => manualJoinUntilUtc = DateTime.UtcNow + ManualJoinDuration;

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

            if (GateNpcNavigation.IsInteractOnCooldown)
            {
                return false;
            }

            var interacted = ObjectHelper.TryInteractWithBaseId(spot.DataId, GateNpcNavigation.CloseRange, "Saucy.GateSchedule.Coordinator");
            if (interacted)
            {
                GateNpcNavigation.MarkInteracted();
            }

            return interacted;
        }

        if (!Vnavmesh.IsMoving())
        {
            Vnavmesh.TryMoveTo(destination, false, GateNpcNavigation.CloseRange);
        }

        return false;
    }

    // The GATE NPCs the user has actually recorded via the per-GATE panels — "支援的NPC" per the
    // user's request, i.e. GATEs this plugin can already fully play once joined. Air Force One's
    // spot also covers Leap of Faith (confirmed shared NPC — "報名登高跳跳樂 和 報名空軍裝甲 共用
    // NPC"), and Cliffhanger contributes every spot in its list (it has two, confirmed by user).
    //
    // Paired with the GateType each spot actually belongs to (not just a flat position list) so a
    // successful join can record WHICH gate was just registered for — needed by
    // IsWithinPostJoinSettle below.
    // "為每個GATE單獨加上自動報名開關" — each GATE's spot(s) are only offered up to the
    // nearest-NPC search when that GATE's own toggle is on, instead of one shared switch
    // controlling every supported GATE at once.
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

    // "報名後 還沒傳送 路徑就已經是錯的了" — the moment the registration NPC is interacted with,
    // the actual teleport onto the arena hasn't happened yet, so any movement logic that starts
    // immediately (whether gated on IsInGate or not) can aim at coordinates that only make sense
    // AFTER that teleport, walking off toward nonsense from wherever the player was still standing
    // to register. Record when each GATE was actually joined so callers (WindBlows/SliceIsRight)
    // can hold off starting movement until a real settle delay has passed.
    private static readonly Dictionary<Module.GateType, DateTime> lastJoinedUtc = [];

    /// <summary>True while still within `seconds` of that GATE's last successful registration —
    /// callers should hold off starting any movement toward an in-arena spot until this goes
    /// false.</summary>
    public static bool IsWithinPostJoinSettle(Module.GateType gate, double seconds) =>
        lastJoinedUtc.TryGetValue(gate, out var joinedUtc) && (DateTime.UtcNow - joinedUtc).TotalSeconds < seconds;

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

    // "不要連續報名 CD30秒" — dedicated to just the registration interact itself, separate from
    // GateNpcNavigation's shared 30s interact cooldown (which also covers coordinator interacts and
    // the manual "立即互動" button, so an unrelated interact elsewhere could otherwise reset the
    // same shared timer and let registration fire again sooner than intended).
    private const double RegisterCooldownSeconds = 30;
    private static DateTime lastRegisterUtc = DateTime.MinValue;
    private static bool IsRegisterOnCooldown => (DateTime.UtcNow - lastRegisterUtc).TotalSeconds < RegisterCooldownSeconds;

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
            if (!IsRegisterOnCooldown && !GateNpcNavigation.IsInteractOnCooldown &&
                ObjectHelper.TryInteractWithObject(nearestNpc, "Saucy.GateSchedule.Join"))
            {
                GateNpcNavigation.MarkInteracted();
                MarkJoinHandled();
                lastJoinedUtc[nearestGate] = DateTime.UtcNow;
                lastRegisterUtc = DateTime.UtcNow;
            }

            ReleaseJoinNavigation();
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
