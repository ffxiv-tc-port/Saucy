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

/// <summary>
/// Gold Saucer GATEs run on a fixed real-world-clock cycle: Event Coordinator NPCs ("活動解說員")
/// teleport the player to the next GATE's area during the :55/:15/:35 prep window (5 minutes before the start), and the GATE
/// itself is joinable at its registration NPC during the :00/:20/:40 window. Both windows are only
/// acted on if their respective toggle is on and the user has personally recorded at least one
/// relevant NPC spot — never guessed.
/// </summary>
internal static unsafe class GateScheduleAutomation
{
    // The coordinator run happens 5 minutes before each GATE. GATEs start at :00/:20/:40, so 5 minutes before
    // each of those is :55/:15/:35.
    private static bool IsCoordinatorWindow => DateTime.Now.Minute is 55 or 15 or 35;

    // The GATE registration NPC isn't necessarily interactable/present the instant the minute
    // ticks over (the coordinator-teleport/area transition may still be settling) — wait 30s into
    // the window before searching for it.
    private static bool IsJoinWindow => DateTime.Now.Minute is 0 or 20 or 40 && DateTime.Now.Second >= 30;

    // A manually-triggered "立即移動" target is remembered here and re-checked every tick (not
    // just on the click) until it either interacts or the player leaves the Saucer/stage — a
    // single one-shot call can start the walk but can't catch the moment the player actually
    // arrives, since arrival happens several frames later.
    private static GateNpcSpot? manualCoordinatorTarget;

    // Manual trigger: lets the join search run right
    // now regardless of the :00/:20/:40 clock window, for testing/early entry. Auto-expires so a
    // forgotten manual trigger doesn't run forever.
    private static readonly TimeSpan ManualJoinDuration = TimeSpan.FromSeconds(60);
    private static DateTime? manualJoinUntilUtc;

    private static readonly ManualSteerYield coordinatorSteer = new("活動解說員自動靠近");
    private static readonly ManualSteerYield joinSteer = new("GATE 報名自動靠近");

    // Once the coordinator's actually been talked to for this window, the teleport it triggers may drop the player far from every OTHER recorded spot — re-running "nearest" every remaining tick of the same ~1-minute window would then immediately walk back toward whichever spot is nearest. Latch once handled and don't try again until the window itself resets.
    // Persisted to Configuration (not a plain static bool) — a plugin reload mid-window used to forget "already handled" and immediately repeat the search/walk, since a fresh in-memory flag defaults back to false.
    // the actual coordinator/registration teleport only takes ~5-10s. 30s is still comfortably longer than a real teleport takes (so a genuinely successful attempt won't immediately retry and interrupt itself), while letting a failed one recover well within the same ~1-minute window.
    private static readonly TimeSpan HandledLatchWindow = TimeSpan.FromSeconds(30);

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
        ObserveGateEnd();

        if (!GateDirector.InSaucer || GateDirector.IsPlayerOnStage())
        {
            CancelAfterGateReturn(null);
            ReleaseJoinNavigation();
            manualCoordinatorTarget = null;
            manualJoinUntilUtc = null;

            // Actually being on stage means the join succeeded (interacting alone doesn't always
            // report success back if the ride/duty pulls the player in immediately after) — latch
            // this so that leaving the GATE while still inside the same window doesn't restart the
            // NPC search from scratch.
            if (GateDirector.IsPlayerOnStage())
            {
                MarkJoinHandled();
            }

            return;
        }

        if (manualCoordinatorTarget is { } manualTarget)
        {
            CancelAfterGateReturn("你手動要走去活動解說員");
            if (NavigateAndInteractCoordinator(manualTarget))
            {
                manualCoordinatorTarget = null;
            }
        }
        else if (IsCoordinatorWindow)
        {
            CancelAfterGateReturn("活動解說員時段開始，交給排程");
            if (GateEmergencyHold.IsHeld)
            {
                if (C.GoldSaucerGates.EventCoordinatorAutoNavigate)
                {
                    GateEmergencyHold.NoteSkipped("定時走去活動解說員");
                }
            }
            else if (C.GoldSaucerGates.EventCoordinatorAutoNavigate &&
                !HasHandledRecently(C.GoldSaucerGates.LastCoordinatorHandledUtcTicks) &&
                NavigateToNearestCoordinator())
            {
                MarkCoordinatorHandled();
            }
        }
        else
        {
            coordinatorSteer.Reset();
            TickAfterGateReturn();
        }

        // Per-GATE toggles (AirForceAutoJoin/WindBlowsAutoJoin/etc.) are applied inside
        // SupportedSpots itself, so the overall search just needs to run whenever ANY of them
        // might be enabled — there is no single shared "auto join" flag to check here.
        var manualJoinActive = manualJoinUntilUtc is { } until && DateTime.UtcNow < until;
        var scheduledJoin = IsJoinWindow && !HasHandledRecently(C.GoldSaucerGates.LastJoinHandledUtcTicks);
        if (scheduledJoin && GateEmergencyHold.IsHeld)
        {
            if (SupportedSpots.Any())
            {
                GateEmergencyHold.NoteSkipped("定時 GATE 報名");
            }

            scheduledJoin = false;
        }

        if (manualJoinActive || scheduledJoin)
        {
            TryJoinNearestSupportedNpc();
        }
        else
        {
            ReleaseJoinNavigation();
            manualJoinUntilUtc = null;
        }
    }

    /// <summary>活動解說員那一段正在管移動（手動「立即移動」，或 :55／:15／:35 時段且有開自動走去、沒被急停壓住）：
    /// 報名 NPC 的自動靠近這段時間不出發。整個時段都算（處理過 30 秒後它還會再找一次）。</summary>
    public static bool IsCoordinatorRunActive =>
        manualCoordinatorTarget != null ||
        (IsCoordinatorWindow && C.GoldSaucerGates.EventCoordinatorAutoNavigate && !GateEmergencyHold.IsHeld);

    /// <summary>Manual "開始導航" trigger — runs the same GATE-join search/walk/interact flow as
    /// the :00/:20/:40 window, right now, ignoring the clock and the "already handled" latch. Use
    /// when testing or entering the area early/outside the normal window.</summary>
    public static void TriggerManualJoin()
    {
        GateEmergencyHold.Release("手動開始 GATE 報名導航");
        manualJoinUntilUtc = DateTime.UtcNow + ManualJoinDuration;
    }

    /// <summary>"立即移動" button — walks toward this specific spot and interacts on arrival,
    /// re-checked every tick regardless of the :55/:15/:35 window.</summary>
    public static void TriggerManualCoordinatorMove(GateNpcSpot spot)
    {
        GateEmergencyHold.Release("手動走去活動解說員");
        manualCoordinatorTarget = spot;
    }

    /// <summary>急停用：清掉手動觸發的「立即移動」與「開始導航」，並停下它們發起的導航；回傳原本有沒有在跑。</summary>
    /// <remarks>依設定在 :55／:00 等時段自動跑的那一段不在這裡處理，由 <see cref="GateEmergencyHold"/> 只在執行期壓住。只在 framework 執行緒呼叫。</remarks>
    public static bool CancelManualTriggers()
    {
        var coordinatorWalking = manualCoordinatorTarget != null;
        var hadManual = coordinatorWalking || manualJoinUntilUtc != null;
        manualCoordinatorTarget = null;
        manualJoinUntilUtc = null;
        coordinatorSteer.Reset();
        ReleaseJoinNavigation();
        if (coordinatorWalking && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopPath();
        }

        return hadManual;
    }

    // ── GATE 結束後走回活動解說員（暴風倖存者、必中一閃快刀斬魔） ──
    // 「結束」＝看過自己在場上，之後不在場上、而且這場 GATE 本身也停了（導演不見、IsFinished、或換成別的 GATE）；
    // 被淘汰但 GATE 還在跑時先等它結束，再過 AfterGateSettleMs 才動。只走過去、不對話：對話與選目的地照舊交給 :55 時段的排程。

    private const long AfterGateSettleMs = 5_000;

    private const long AfterGateWalkLimitMs = 120_000;

    private const long AfterGateRetryMs = 1_000;

    private static readonly ManualSteerYield afterGateSteer = new("GATE 結束後走回活動解說員");

    /// <summary>有開這個選項、目前（或剛剛）在場上的 GATE；None＝沒在等結束。</summary>
    private static Module.GateType afterGateWatch;

    /// <summary>自己離開場上的時刻（<see cref="Environment.TickCount64"/>）；0＝還在場上。</summary>
    private static long afterGateOffStageSince;

    /// <summary>這場 GATE 本身也停了的時刻；0＝還沒停。</summary>
    private static long afterGateEndedSince;

    private static GateNpcSpot? afterGateTarget;

    private static long afterGateStartedAt;

    private static long afterGateNextTryAt;

    private static bool afterGateNavOwned;

    /// <summary>這一場在場上時被停止過：這一場結束後不走回；下一場重新開始看。</summary>
    private static bool afterGateStoppedThisGate;

    /// <summary>正在等 GATE 結束或正在走回活動解說員：報名 NPC 的自動靠近這段時間讓開，不把人拉回報名處。</summary>
    public static bool IsReturningAfterGate => afterGateTarget != null || afterGateOffStageSince != 0;

    private static bool ReturnsAfterGate(Module.GateType gate) => gate switch
    {
        Module.GateType.AnyWayTheWindBlows =>
            C.GoldSaucerGates.WindBlowsReturnToCoordinatorAfterGate && C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows),
        Module.GateType.SliceIsRight =>
            C.GoldSaucerGates.SliceIsRightReturnToCoordinatorAfterGate && C.IsModuleEnabled(ModuleNames.SliceIsRight),
        _ => false
    };

    private static unsafe void ObserveGateEnd()
    {
        if (GateDirector.InSaucer && GateDirector.IsPlayerOnStage())
        {
            var gate = GateDirector.GetCurrentGate();
            afterGateWatch = !afterGateStoppedThisGate && ReturnsAfterGate(gate) ? gate : Module.GateType.None;
            afterGateOffStageSince = 0;
            afterGateEndedSince = 0;
            return;
        }

        afterGateStoppedThisGate = false;
        if (afterGateWatch == Module.GateType.None)
        {
            return;
        }

        if (!GateDirector.InSaucer)
        {
            ForgetGateEndWatch();
            return;
        }

        var now = Environment.TickCount64;
        var hasDirector = GateDirector.TryGetDirector(out var director);
        var flags = hasDirector ? director->Flags : default;
        var directorGate = hasDirector ? (Module.GateType)director->GateType : Module.GateType.None;
        var gateStopped = !hasDirector || flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer.GFateDirectorFlag.IsFinished) ||
                          directorGate != afterGateWatch;
        if (afterGateOffStageSince == 0)
        {
            afterGateOffStageSince = now;
            Svc.Log.Information($"[Saucy] GATE 結束後走回：{afterGateWatch} 已不在場上（導演{(hasDirector ? $"在，Flags={flags}，GateType={directorGate}" : "不在")}）；" +
                                (gateStopped ? $"GATE 已結束，{AfterGateSettleMs / 1000} 秒後走回最近的活動解說員" : "GATE 還在跑，等它結束"));
        }

        if (!gateStopped)
        {
            afterGateEndedSince = 0;
            return;
        }

        if (afterGateEndedSince == 0)
        {
            afterGateEndedSince = now;
        }

        if (now - afterGateEndedSince < AfterGateSettleMs)
        {
            return;
        }

        var ended = afterGateWatch;
        ForgetGateEndWatch();
        if (!ReturnsAfterGate(ended))
        {
            return;
        }

        if (GateEmergencyHold.IsHeld)
        {
            GateEmergencyHold.NoteSkipped("GATE 結束後走回活動解說員");
            return;
        }

        var nearest = NearestRecordedCoordinator();
        if (nearest == null)
        {
            Svc.Log.Information("[Saucy] GATE 結束後走回：沒有已記錄的活動解說員位置，不移動");
            return;
        }

        afterGateTarget = nearest;
        afterGateStartedAt = now;
        afterGateNextTryAt = 0;
        afterGateNavOwned = false;
        afterGateSteer.Reset();
        Svc.Log.Information($"[Saucy] GATE 結束後走回：{ended} 結束，離開場地走向活動解說員（{nearest.X:F1}, {nearest.Y:F1}, {nearest.Z:F1}）");
    }

    private static void ForgetGateEndWatch()
    {
        afterGateWatch = Module.GateType.None;
        afterGateOffStageSince = 0;
        afterGateEndedSince = 0;
    }

    private static GateNpcSpot? NearestRecordedCoordinator()
    {
        if (!Player.Available)
        {
            return null;
        }

        var playerPos = Player.Position;
        return C.GoldSaucerGates.EventCoordinatorSpots
            .Where(s => s.Recorded)
            .OrderBy(s => Vector3.Distance(playerPos, new Vector3(s.X, s.Y, s.Z)))
            .FirstOrDefault();
    }

    private static void TickAfterGateReturn()
    {
        if (afterGateTarget is not { } spot)
        {
            return;
        }

        if (GateEmergencyHold.IsHeld)
        {
            CancelAfterGateReturn("急停");
            return;
        }

        if (IsJoinWindow || manualJoinUntilUtc != null)
        {
            CancelAfterGateReturn("GATE 報名時段開始，交給排程");
            return;
        }

        if (!Vnavmesh.IsInstalled || !Player.Available)
        {
            return;
        }

        var now = Environment.TickCount64;
        var destination = new Vector3(spot.X, spot.Y, spot.Z);
        if (Vnavmesh.IsWithinHorizontalRange(destination, GateNpcNavigation.CloseRange))
        {
            CancelAfterGateReturn("已走到活動解說員旁，交回排程");
            return;
        }

        switch (afterGateSteer.Tick(afterGateNavOwned, busy: false, out var pausedFor))
        {
            case ManualSteerYield.Result.Hold:
                return;
            case ManualSteerYield.Result.GaveUp:
                // 使用者還在自己走：不停他的移動，只結束這一趟。
                afterGateNavOwned = false;
                CancelAfterGateReturn("你自己移動太久，不再接續");
                Svc.Chat.Print($"[Saucy] 已停止 GATE 結束後走回活動解說員：你自己移動超過 {ManualSteerWatch.MaxYieldMs / 1000} 秒。");
                return;
            case ManualSteerYield.Result.Resumed:
                afterGateStartedAt += (long)pausedFor.TotalMilliseconds;
                break;
        }

        if (now - afterGateStartedAt > AfterGateWalkLimitMs)
        {
            CancelAfterGateReturn($"{AfterGateWalkLimitMs / 1000} 秒內沒走到");
            return;
        }

        if (Vnavmesh.IsMoving() || now < afterGateNextTryAt)
        {
            return;
        }

        afterGateNextTryAt = now + AfterGateRetryMs;
        if (Vnavmesh.TryMoveTo(destination, false, GateNpcNavigation.CloseRange))
        {
            afterGateNavOwned = true;
        }
    }

    /// <summary>取消「GATE 結束後走回」（含還在等結束的那一段），停下自己送的路徑；<paramref name="reason"/> 為 null 時不寫 log。回傳原本有沒有在跑。只在 framework 執行緒呼叫。</summary>
    public static bool CancelAfterGateReturn(string? reason)
    {
        var onStageWatch = afterGateWatch != Module.GateType.None && afterGateOffStageSince == 0;
        var active = IsReturningAfterGate || (reason != null && onStageWatch);
        if (reason != null && onStageWatch)
        {
            afterGateStoppedThisGate = true;
        }

        if (reason != null)
        {
            ForgetGateEndWatch();
        }

        if (afterGateTarget == null)
        {
            if (active && reason != null)
            {
                Svc.Log.Information($"[Saucy] GATE 結束後走回：取消等待（{reason}）");
            }

            return active;
        }

        if (afterGateNavOwned && Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopPath();
        }

        afterGateTarget = null;
        afterGateNavOwned = false;
        afterGateSteer.Reset();
        if (reason != null)
        {
            Svc.Log.Information($"[Saucy] GATE 結束後走回：結束（{reason}）");
        }

        return true;
    }

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
            TalkHelper.TryAdvance("Saucy.GateSchedule.CoordinatorTalk");

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

        switch (coordinatorSteer.Tick(ownsPath: true, busy: false, out _))
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
            Vnavmesh.TryMoveTo(destination, false, GateNpcNavigation.CloseRange);
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

    // The moment the registration NPC is interacted with, the actual teleport onto the arena hasn't happened
    // yet, so any movement logic that starts immediately can aim at coordinates that only make sense AFTER
    // that teleport, walking off toward nonsense from wherever the player was still standing to register.
    // Record when each GATE was actually joined so callers (WindBlows/SliceIsRight) can hold off starting
    // movement until a real settle delay has passed.
    private static readonly Dictionary<Module.GateType, DateTime> lastJoinedUtc = [];

    /// <summary>True while still within `seconds` of that GATE's last successful registration —
    /// callers should hold off starting any movement toward an in-arena spot until this goes
    /// false.</summary>
    public static bool IsWithinPostJoinSettle(Module.GateType gate, double seconds) =>
        lastJoinedUtc.TryGetValue(gate, out var joinedUtc) && (DateTime.UtcNow - joinedUtc).TotalSeconds < seconds;

    // GateNpcNavigation.Tick/TickList perform the actual registration interact for most GATEs (Cliffhanger,
    // WindBlows, LeapOfFaith/AirForce), not just TryJoinNearestSupportedNpc below — so those call sites record
    // the join here too; otherwise IsWithinPostJoinSettle never becomes true for the normal per-GATE
    // auto-navigate path and the settle guard in each module's OnUpdate never engages.
    public static void MarkJoined(Module.GateType gate) => lastJoinedUtc[gate] = DateTime.UtcNow;

    private const float JoinInteractRange = 5f;

    // A coordinator teleport drops the player right at the venue for the next GATE, so the real
    // registration NPC should always be nearby — a wide search radius risks picking up some OTHER
    // supported NPC that's technically visible/loaded but actually in an unrelated area, and then
    // walking a long way toward the wrong one.
    private const float NpcSearchRadius = 5f;

    // If the actual NPC object can't be found in the world within 10s of starting to look (wrong
    // area, coordinator teleport didn't land where expected, NPC despawned, etc.), give up and
    // release any in-progress navigation instead of leaving the player walking indefinitely toward
    // a spot with nothing there.
    private const float SearchTimeoutSeconds = 10f;

    private static DateTime? joinSearchStartUtc;
    private static bool joinNavOwned;

    // Dedicated to just the registration interact itself, separate from
    // GateNpcNavigation's shared 30s interact cooldown (which also covers coordinator interacts and
    // the manual "立即互動" button, so an unrelated interact elsewhere could otherwise reset the
    // same shared timer and let registration fire again sooner than intended).
    private const double RegisterCooldownSeconds = 30;
    private static DateTime lastRegisterUtc = DateTime.MinValue;
    private static bool IsRegisterOnCooldown => (DateTime.UtcNow - lastRegisterUtc).TotalSeconds < RegisterCooldownSeconds;

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
            TalkHelper.TryAdvance("Saucy.GateSchedule.JoinTalk");

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

        if (!Vnavmesh.IsMoving())
        {
            Vnavmesh.TryMoveTo(nearestNpc.Position, false, JoinInteractRange);
        }
        joinNavOwned = true;
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
                Vnavmesh.StopPath();
            }
        }
    }
}
