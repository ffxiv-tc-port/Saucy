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
internal static unsafe partial class GateScheduleAutomation
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

    /// <summary>vnavmesh 現在走的是活動解說員那一段自己送的路徑：送路徑成功才設，vnavmesh 停了就清（<see cref="ForgetFinishedPaths"/>）。</summary>
    private static bool coordinatorNavOwned;

    // Once the coordinator's actually been talked to for this window, the teleport it triggers may drop the player far from every OTHER recorded spot — re-running "nearest" every remaining tick of the same ~1-minute window would then immediately walk back toward whichever spot is nearest. Latch once handled and don't try again until the window itself resets.
    // Persisted to Configuration (not a plain static bool) — a plugin reload mid-window used to forget "already handled" and immediately repeat the search/walk, since a fresh in-memory flag defaults back to false.
    // the actual coordinator/registration teleport only takes ~5-10s. 30s is still comfortably longer than a real teleport takes (so a genuinely successful attempt won't immediately retry and interrupt itself), while letting a failed one recover well within the same ~1-minute window.
    private static readonly TimeSpan HandledLatchWindow = TimeSpan.FromSeconds(30);

    private static bool HasHandledRecently(long utcTicks) =>
        utcTicks != 0 && DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc) < HandledLatchWindow;

    // 對話後離對話位置這麼遠（水平）＝被 NPC 傳送走了；解說員之間、解說員與各會場都遠大於這個值。
    private const float CoordinatorTeleportMeters = 20f;

    /// <summary>最近一次真的跟活動解說員互動到時站的位置；null＝這個時段還沒互動到，或已判定過。</summary>
    private static Vector3? coordinatorTalkPosition;

    private static bool IsCoordinatorWindowLocked() =>
        IsInSameWindowMinute(C.GoldSaucerGates.LastCoordinatorTeleportUtcTicks, DateTime.UtcNow.Ticks);

    /// <summary>時段以整分鐘為界（:55／:15／:35 那一分鐘），UTC 與本地時間的分鐘邊界相同。<paramref name="markUtcTicks"/> 為 0＝沒有標記。</summary>
    internal static bool IsInSameWindowMinute(long markUtcTicks, long nowUtcTicks) =>
        markUtcTicks != 0 && markUtcTicks <= nowUtcTicks && markUtcTicks >= nowUtcTicks - (nowUtcTicks % TimeSpan.TicksPerMinute);

    // 被傳送後鎖到時段結束：30 秒鎖存到期後不再走回最近的活動解說員再傳一次。
    private static void ObserveCoordinatorTeleport()
    {
        if (coordinatorTalkPosition is not { } talkedAt || !Player.Available || IsCoordinatorWindowLocked())
        {
            return;
        }

        var delta = Player.Position - talkedAt;
        var distance = MathF.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z));
        if (distance <= CoordinatorTeleportMeters)
        {
            return;
        }

        coordinatorTalkPosition = null;
        C.GoldSaucerGates.LastCoordinatorTeleportUtcTicks = DateTime.UtcNow.Ticks;
        C.Save();
        Svc.Log.Information($"[Saucy] 活動解說員：對話後已被傳送（離對話位置 {distance:F0} 米），這個時段（到 {DateTime.Now:HH:mm}:59）不再走去活動解說員");
    }

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

    /// <summary>這一段在場上已經存過檔；離開場上時再存一次，存的是最後在場上的時刻。</summary>
    private static bool onStageLatchSaved;

    // 在場上每幀都更新鎖存時間（記憶體），但只在上場與下場各存一次檔：每幀存檔＝每幀序列化整份設定並同步寫檔。
    private static void LatchJoinWhileOnStage()
    {
        C.GoldSaucerGates.LastJoinHandledUtcTicks = DateTime.UtcNow.Ticks;
        if (!onStageLatchSaved)
        {
            onStageLatchSaved = true;
            C.Save();
        }
    }

    private static void SaveLatchWhenLeftStage()
    {
        if (onStageLatchSaved && !GateDirector.IsPlayerOnStage())
        {
            onStageLatchSaved = false;
            C.Save();
        }
    }

    // 自己送的路徑跑完或被停了，之後 vnavmesh 上的路徑就不是自己的：讓位與停止都只看這三個旗標。
    private static void ForgetFinishedPaths()
    {
        if ((coordinatorNavOwned || joinNavOwned || afterGateNavOwned) && !Vnavmesh.IsMoving())
        {
            coordinatorNavOwned = false;
            joinNavOwned = false;
            afterGateNavOwned = false;
        }
    }

    public static void Tick()
    {
        ForgetFinishedPaths();
        ObserveGateEnd();
        SaveLatchWhenLeftStage();

        if (!GateDirector.InSaucer || GateDirector.IsPlayerOnStage())
        {
            var walkOffStage = GateDirector.InSaucer && IsCliffhangerWalkOffStage();
            if (!walkOffStage)
            {
                CancelAfterGateReturn(null);
            }

            ReleaseJoinNavigation();
            manualCoordinatorTarget = null;
            manualJoinUntilUtc = null;

            // Actually being on stage means the join succeeded (interacting alone doesn't always
            // report success back if the ride/duty pulls the player in immediately after) — latch
            // this so that leaving the GATE while still inside the same window doesn't restart the
            // NPC search from scratch.
            if (GateDirector.IsPlayerOnStage())
            {
                LatchJoinWhileOnStage();
                if (!walkOffStage && afterJoinArmedAt != 0)
                {
                    Svc.Log.Information($"[Saucy] {AfterJoinLabel}：報名後已在場上、而且開著自動移動，改成這場 GATE 結束後再走回");
                }
            }

            if (walkOffStage)
            {
                TickAfterJoinReturn();
                TickAfterGateReturn();
                return;
            }

            afterJoinArmedAt = 0;

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
            ObserveCoordinatorTeleport();
            if (GateEmergencyHold.IsHeld)
            {
                if (C.GoldSaucerGates.EventCoordinatorAutoNavigate)
                {
                    GateEmergencyHold.NoteSkipped("定時走去活動解說員");
                }
            }
            else if (C.GoldSaucerGates.EventCoordinatorAutoNavigate &&
                !HasHandledRecently(C.GoldSaucerGates.LastCoordinatorHandledUtcTicks) &&
                !IsCoordinatorWindowLocked() &&
                NavigateToNearestCoordinator())
            {
                MarkCoordinatorHandled();
            }
        }
        else
        {
            coordinatorSteer.Reset();
            coordinatorTalkPosition = null;
            TickAfterJoinReturn();
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
        if (coordinatorWalking && coordinatorNavOwned)
        {
            coordinatorNavOwned = false;
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopOwnPath();
            }
        }

        return hadManual;
    }

    /// <summary>卸載用：停下活動解說員／報名／結束後走回這幾段自己送的路徑（別人的不碰）。只在 framework 執行緒呼叫。</summary>
    public static void ReleaseOwnNavigation()
    {
        CancelManualTriggers();
        CancelAfterGateReturn(null);
        if (coordinatorNavOwned)
        {
            coordinatorNavOwned = false;
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopOwnPath();
            }
        }
    }

    // ── GATE 結束後走回活動解說員（暴風倖存者、必中一閃快刀斬魔、搶救小雛鳥大作戰） ──
    // 「結束」＝看過自己在場上，之後不在場上、而且這場 GATE 本身也停了（導演不見、IsFinished、或換成別的 GATE）；
    // 被淘汰但 GATE 還在跑時先等它結束，再過 AfterGateSettleMs 才動。只走過去、不對話：對話與選目的地照舊交給 :55 時段的排程。

    private const long AfterGateSettleMs = 5_000;

    private const long AfterGateWalkLimitMs = 120_000;

    private const long AfterGateRetryMs = 1_000;

    private static readonly ManualSteerYield afterGateSteer = new("GATE 結束後走回活動解說員");

    /// <summary>這一趟走回是誰起的頭（log 前綴）。</summary>
    private static string returnLabel = "GATE 結束後走回";

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

    // ── 搶救小雛鳥大作戰報名後走回活動解說員等下一場 ──
    // 報名互動送出後等對話結束才走。報名後即使已算在場上也照走（使用者確認報名完就能離開）；
    // 只有開著自動移動、要讓 Saucy 代玩時不走，改由上面的「GATE 結束後走回」在這場結束後接手。

    private const string AfterJoinLabel = "搶救小雛鳥大作戰報名後走回";

    internal enum AfterJoinDecision
    {
        Wait,
        Start,
        GiveUp
    }

    private const long AfterJoinSettleMs = 5_000;

    private const long AfterJoinGiveUpMs = 90_000;

    /// <summary>報名互動送出的時刻（<see cref="Environment.TickCount64"/>）；0＝沒在等。</summary>
    private static long afterJoinArmedAt;

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
    public static void MarkJoined(Module.GateType gate)
    {
        lastJoinedUtc[gate] = DateTime.UtcNow;
        GateSlotLedger.NoteJoinInteract();
        if (ReturnsAfterJoin(gate))
        {
            afterJoinArmedAt = Environment.TickCount64;
            Svc.Log.Information($"[Saucy] {AfterJoinLabel}：已跟報名 NPC 互動，對話結束、至少 {AfterJoinSettleMs / 1000} 秒後走回最近的活動解說員等下一場");
        }
    }

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

    // ── 替你翻 NPC 對話前先請 YesAlready 暫停 Talk（共用 YesAlreadyTalkClaim；站在 NPC 旁才續期） ──

    private static readonly YesAlreadyTalkClaim talkClaim =
        new("[Saucy] GATE：無法確認 YesAlready 不會按 NPC 對話框，Saucy 先不替你翻這段對話（交給 YesAlready 或你自己按）");

    /// <summary>站在 NPC 旁每幀呼叫：YesAlready 沒載入就照舊翻；載入了就先確認它的 Talk 已讓開夠久才翻，問不到就不翻。</summary>
    private static void AdvanceTalkWithYesAlreadyPaused(string throttleKey) => talkClaim.TryAdvance(throttleKey);
}
