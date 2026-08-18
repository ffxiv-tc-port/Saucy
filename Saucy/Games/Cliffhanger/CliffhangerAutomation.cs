using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Numerics;
namespace Saucy.Cliffhanger;

/// <summary>
/// Cliffhanger (搶救小鳥大作戰) has a real GFateDirector like most other GATEs (confirmed live:
/// GateType 1 / "Cliffhanger" shows up in the GoldSaucerGates debug panel, and a recorded run
/// showed InGate true for the majority of samples once actually inside — unlike Leap of Faith
/// which needed a ConditionFlag workaround). Identified from a real recording
/// (CliffhangerObjects_20260702_130044.json): the rescue target is EventNpc DataId 1010469
/// ("迷路的陸行鳥雛鳥"), and the main hazard is BattleNpc DataId 3782 ("炸彈", sampled thousands of
/// times in a single ~38s run — clearly a continuously-moving active threat). Steers toward the
/// nearest chick while trying to keep distance from any nearby bomb, using the same simulated-key
/// movement mechanism as Leap of Faith.
/// </summary>
internal static unsafe class CliffhangerAutomation
{
    private const uint ChickDataId = 1010469;
    private const uint BombDataId = 3782;
    private const float BombAvoidRadius = 8f;
    private const float TurnThresholdRadians = 0.14f; // ~8 degrees

    // Actual blast radius is unknown (never confirmed against a real explosion hitbox) — 6 units
    // is a rough guess based on the bomb's visible ring size relative to BombAvoidRadius (which was
    // tuned live against real avoidance behavior). Exposed as a live-tunable slider so it can be
    // corrected without a rebuild once observed against a real explosion.
    public static float BombBlastRadiusGuess => C.GoldSaucerGates.CliffhangerBombBlastRadiusGuess;

    public static global::Saucy.Framework.Module.GateType LastObservedGateType { get; private set; } =
        global::Saucy.Framework.Module.GateType.None;

    public static Vector3? CurrentTargetPosition { get; private set; }
    public static Vector3? NearestBombPosition { get; private set; }
    public static IReadOnlyList<Vector3> AllBombPositions { get; private set; } = [];

    // Diagnostic-only — measured real-world speed (m/s) each tick, for comparing against
    // PreciseMovement.LastCommandedMagnitude in the debug panel: if the commanded magnitude stays
    // at 1 (full speed) while this measured speed visibly drops, the deceleration is coming from
    // the game engine itself (e.g. resolving our command as more strafe than forward run), not from
    // our own speed-scaling code.
    public static float MeasuredSpeed { get; private set; }
    private static Vector3? lastMeasuredPosition;
    private static DateTime lastMeasuredUtc;

    private static void UpdateMeasuredSpeed()
    {
        var now = DateTime.UtcNow;
        if (lastMeasuredPosition is { } lastPos)
        {
            var dt = (now - lastMeasuredUtc).TotalSeconds;
            if (dt > 0.001)
            {
                MeasuredSpeed = Vector3.Distance(Player.Position, lastPos) / (float)dt;
            }
        }

        lastMeasuredPosition = Player.Position;
        lastMeasuredUtc = now;
    }

    // How long the nearest bomb has existed — a freshly-spawned bomb doesn't explode immediately,
    // so it's safe to just run past; only one that's "been around a while" is actually close to
    // going off and worth avoiding ("正前方的已經出現一段時間炸彈需要避讓 剛出現的炸彈則快速通
    // 過"). Reuses the same tunable "炸彈標示顯示時間" slider as the age threshold — once a bomb
    // outlives that window it's treated as old enough to matter.
    public static double? NearestBombAgeSeconds { get; private set; }

    // Live trail of the player's own path this run, drawn on screen the same way as Leap of
    // Faith's — automatic, separate from the manual export-to-JSON recorder.
    private const int MaxTrailPoints = 400;
    private const int TrailSampleIntervalMs = 200;
    private static readonly List<Vector3> ownTrail = [];
    private static DateTime lastTrailSampleUtc;
    private static bool wasInGate;

    public static IReadOnlyList<Vector3> OwnTrail => ownTrail;

    // Replaying a real manually-recorded run (position + where a jump happened) instead of guessing
    // live — per user request ("我手動跑一次(包含跳躍) 你照路徑試試"). Built once per GATE entry
    // from whatever CliffhangerRecorder captured most recently; falls back to the live
    // target-chase/vnavmesh behavior if nothing's been recorded yet. Walked via TickReplayRoute,
    // which mirrors TickSparseRoute's vnavmesh-for-distance/precise-for-close-approach/
    // advance-only-on-actual-jump logic.
    private const float ReplayWaypointArrivalRadius = 1f;
    private static List<CliffhangerRecorder.ReplayWaypoint>? replayRoute;
    private static int replayIndex;

    // Sparse, user-marked route (start / jump takeoff points with a separately-recorded facing
    // direction / end) — takes priority over the dense auto-recorded replay above when the user
    // has set one up, per request ("整段路只要跳三次 由我記錄點...跳躍點 方向 另外錄"). vnavmesh
    // handles the actual walking between marked points; a jump point switches to manual key
    // control using the recorded direction only for the jump itself.
    private const float RouteArrivalRadius = 1f;

    // "往非跳躍點移動時 可先用vnavmesh走長距離路徑 接近時 用新方法校正位置" — ordinary segments
    // hand off from vnavmesh to precise key-steering once within this range of the waypoint.
    //
    // "中間有障礙" — precise key-steering walks a straight line with no obstacle awareness at all
    // (unlike vnavmesh's real pathfinding), so a wide 3m handoff (borrowed from WindBlows/SliceIsRight's
    // open, obstacle-free arenas) let it try to plow straight through terrain/props between two
    // waypoints that are only a few meters apart. Keep vnavmesh responsible for pathfinding around
    // obstacles for as much of the distance as possible; only the final short stretch — where
    // vnavmesh's own coarser arrival tolerance stops being precise enough — switches to straight-line
    // steering.
    private const float RouteManualApproachRadius = 1.2f;

    // Steer directly at a jump waypoint's actual recorded position the whole approach (not
    // vnavmesh, not a synthetic far-away aim point) and start tapping jump once within this
    // radius — jumping mid-approach rather than after a separate stop/re-aim phase gives more
    // accurate direction correction, per user request ("接近時按跳躍 這樣修正方向應該比較精準").
    //
    // 2m turned out too tight to reliably trigger at all under the old key-simulation steering
    // ("放寬好了 沒抓到") — widened up to 3.5m as a result. Now that PreciseMovement gives much more
    // reliable alignment, 3.5m turned out to commit to the jump too early/far out, launching before
    // actually lined up with the far platform and missing it ("太早跳...沒跳上台階"). Tightened back
    // down closer to the original value.
    //
    // Still too loose once full-speed/run-up was made to apply for the whole approach (not just
    // once inside this radius) — many recorded jump-point segments are themselves only a couple
    // meters long, so the character would already be "close enough" from tick one, spend its whole
    // MinJumpRunUpMs timer standing almost still, and launch with barely any real momentum ("很小
    // 的距離跳 加速不夠"). Shrunk down to essentially "actually touching the takeoff point" instead
    // of a wide commit radius — this forces the character to run the ENTIRE recorded segment at
    // full speed before the jump is even allowed to fire, matching the real desired behavior
    // ("往二號跑 碰到時跳躍").
    //
    // Widened slightly (0.5→1.2) for chained jumps specifically ("有成功跳過第一次，但落地後又往回
    // 跑") — a real recorded run's consecutive jump takeoffs can be as little as 0.6-0.8m apart, but
    // the AUTOMATED jump's landing spot doesn't perfectly reproduce the original recording's exact
    // landing (different run-up speed/alignment/timing), so it can land slightly past the next
    // recorded jump waypoint. Since "close enough" is checked as a plain distance (not direction-
    // aware), overshooting a 0.5m radius meant literally turning around and walking backward to
    // hit that exact point before being allowed to jump again. 1.2m comfortably absorbs realistic
    // landing variance from a genuinely nearby next takeoff without meaningfully loosening the
    // "run the whole segment, jump on contact" behavior for normal, longer segments.
    private const float JumpApproachRadius = 1.2f;
    private static int routeIndex;
    public static int RouteIndex => routeIndex;

    /// <summary>"測試模式 改成啟動移動 點一下照開始路徑跑" — one-shot trigger button instead of a
    /// persistent checkbox: resets the route back to its first waypoint and turns on test-run
    /// movement, so a single click always (re)starts a full run from the beginning rather than
    /// requiring a separate "turn test mode on" step first.</summary>
    public static void StartTestRun()
    {
        routeIndex = 0;
        manualMoveDestination = null;
        TestRunActive = true;
    }

    // Show a bomb's marker/blast circle only for a short window after it first appears, rather
    // than for its whole (possibly long) lifetime — the actual danger moment is right as it
    // spawns/telegraphs, per feedback ("炸彈出現時標示 3秒後移除"). Tunable since the real
    // telegraph-to-explosion timing is unconfirmed.
    private static readonly Dictionary<ulong, DateTime> bombFirstSeenUtc = [];

    // "活動外GateType None 時 可以試跑測試嗎" — lets the replay-route movement logic run outside
    // the actual GATE (e.g. standing in the same area between windows), so the recorded
    // path/jump-timing can be verified without waiting for the real :00/:20/:40 window. Only
    // affects movement gating here — FindTargetAndThreat still only finds real chick/bomb objects
    // if they actually happen to be loaded, which they normally won't be outside the instance.
    public static bool TestRunActive { get; set; }

    // Tracks whether THIS module currently owns PreciseMovement's shared desired-direction state,
    // so the top-level "not in our gate" exit only releases it if it actually set it — see the
    // comment at that call site for why an unconditional release there was actively harmful.
    private static bool weAreHoldingKeys;

    // Switched from GameKeyInput's SendInput-simulated WASD (never reliable enough — "鍵盤模擬 現
    // 在完全不能用...vnavmesh 接近後逼近也是基於鍵盤模擬 反而會亂跑") to PreciseMovement, which
    // hooks the game's own movement-input read function directly (same technique BossModReborn
    // uses) instead of simulating keypresses. Jump is a discrete action (not continuous hold) and
    // stays on GameKeyInput.TapKey, which was never the unreliable part.
    private static void HoldDirection(Vector3 direction)
    {
        PreciseMovement.SetDesiredDirection(direction);
        weAreHoldingKeys = true;
    }

    private static void ReleaseKeys()
    {
        PreciseMovement.SetDesiredDirection(null);
        weAreHoldingKeys = false;
    }

    // "小雛鳥的立即移動只出現1 tick 就消失" — TryMoveNowTo only ever issued the vnavmesh move ONCE
    // on the click; the very next tick, OnUpdate's normal TickSparseRoute (still running whenever
    // a route exists and auto-movement is on) issued its own vnavmesh command for the CURRENT route
    // waypoint and stomped over the just-started manual move, cancelling it after a single step.
    // Track the manual destination so OnUpdate can keep re-asserting it (and skip the route/replay/
    // target logic entirely) until the player actually arrives or a timeout passes, instead of a
    // fire-and-forget call that the next tick immediately overwrites.
    private static Vector3? manualMoveDestination;
    private static bool manualMoveIsJumpPoint;
    private static DateTime manualMoveExpiresUtc;
    private const double ManualMoveTimeoutSeconds = 20;

    /// <summary>"立即移動" button for a non-jump route waypoint — one-shot manual trigger to walk
    /// straight there via vnavmesh, bypassing the whole route/gate-state machine, for testing a
    /// single point directly. Takes priority over the normal automation loop in OnUpdate until
    /// arrival or timeout, so it isn't immediately overridden by the route's own per-tick vnavmesh
    /// command.</summary>
    public static bool TryMoveNowTo(Vector3 destination)
    {
        if (!Vnavmesh.IsInstalled)
        {
            return false;
        }

        // Used to kick off a real vnavmesh path immediately on click, even when the destination
        // was already within ManualApproachRadius (where TickManualMove wants precise steering
        // instead) — meaning the very first tick always started a vnavmesh path regardless of
        // distance ("看起來還是用vnavmesh"). Just set the state and let TickManualMove decide next
        // tick, same as the jump variant already does.
        manualMoveDestination = destination;
        manualMoveIsJumpPoint = false;
        manualMoveExpiresUtc = DateTime.UtcNow.AddSeconds(ManualMoveTimeoutSeconds);
        return true;
    }

    /// <summary>"移動到跳躍點並跳躍" button — same one-shot manual override as TryMoveNowTo, but for
    /// a jump waypoint: steers there with the same precise key-control TickSparseRoute uses for
    /// real jump points (never vnavmesh, since vnavmesh isn't trustworthy right at a cliff edge),
    /// and taps jump once close enough and aligned.</summary>
    public static bool TryMoveNowToAndJump(Vector3 destination)
    {
        manualMoveDestination = destination;
        manualMoveIsJumpPoint = true;
        manualMoveExpiresUtc = DateTime.UtcNow.AddSeconds(ManualMoveTimeoutSeconds);
        return true;
    }

    // "加一個按鈕 三點連續測試" — chains the 3 recorded points (start -> jump point+jump -> next
    // start) into a single click instead of pressing each of the 3 test buttons in order by hand.
    // 0 = not running; 1/2/3 = currently walking to/attempting the corresponding step.
    private static int sequentialTestStep;
    private static Vector3 sequentialStart, sequentialJump, sequentialNextStart;

    public static void StartSequentialJumpTest(Vector3 start, Vector3 jump, Vector3 nextStart)
    {
        sequentialStart = start;
        sequentialJump = jump;
        sequentialNextStart = nextStart;
        sequentialTestStep = 1;
        TryMoveNowTo(start);
    }

    public static bool IsSequentialTestRunning => sequentialTestStep > 0;

    /// <summary>Called whenever a manual move/jump step just completed — moves on to the next of
    /// the 3 recorded points if a sequential test is running.</summary>
    private static void AdvanceSequentialTest()
    {
        switch (sequentialTestStep)
        {
            case 1:
                sequentialTestStep = 2;
                TryMoveNowToAndJump(sequentialJump);
                break;
            case 2:
                sequentialTestStep = 3;
                TryMoveNowTo(sequentialNextStart);
                break;
            case 3:
                sequentialTestStep = 0;
                break;
        }
    }

    public static string SequentialTestStatus => sequentialTestStep switch
    {
        1 => "前往跳躍起點...",
        2 => "前往跳躍點並跳躍...",
        3 => "前往跳躍後起點...",
        _ => "未執行"
    };

    /// <summary>Re-asserts the manual move destination every tick (rather than a single
    /// fire-and-forget call) until the player arrives or it times out. Returns true while a manual
    /// move is active, so OnUpdate can skip the rest of the automation loop for this tick.</summary>
    private static bool TickManualMove()
    {
        if (manualMoveDestination is not { } dest)
        {
            return false;
        }

        // A jump point's whole purpose is to move AWAY from dest (jump across a gap) — the instant
        // the player is standing right at the takeoff spot (which is exactly when this button gets
        // clicked mid-testing, one point at a time) treating "already within arrival radius" as
        // "done, nothing to do" meant the jump itself never actually got triggered at all
        // ("移動到跳躍點並跳躍 沒有反應"). Jump points ignore the distance-based arrival check
        // entirely and just keep steering/attempting the jump every tick until the timeout expires.
        //
        // Non-jump manual moves used the same loose RouteArrivalRadius (1m) as the real route's
        // waypoint-advance check — fine for "close enough to continue along a route", but this
        // button exists specifically to test PRECISE positioning, so 1m looked like "no reaction"
        // whenever clicked from just inside that radius ("接近時 按鈕無反應"). Use a much tighter
        // radius here instead.
        const float ManualMoveArrivalRadius = 0.15f;
        if (!manualMoveIsJumpPoint && DateTime.UtcNow >= manualMoveExpiresUtc)
        {
            manualMoveDestination = null;
            ReleaseKeys();
            sequentialTestStep = 0;
            // "點選立即移動會啟動測試模式 變成往第一點跑" — returning false here let OnUpdate fall
            // through to the auto-route logic THIS SAME TICK (TestRunActive turning on to make this
            // button work at all also resets routeIndex back to the route's first point), so a
            // manual move that just finished/expired could immediately start walking toward whatever
            // routeIndex happened to point at instead of just stopping. Return true so OnUpdate exits
            // this tick; the route logic (if any) only takes over starting next tick.
            return true;
        }

        if (!manualMoveIsJumpPoint && Vector3.Distance(Player.Position, dest) < ManualMoveArrivalRadius)
        {
            manualMoveDestination = null;
            ReleaseKeys();
            AdvanceSequentialTest();
            return true;
        }

        if (manualMoveIsJumpPoint)
        {
            if (DateTime.UtcNow >= manualMoveExpiresUtc)
            {
                manualMoveDestination = null;
                ReleaseKeys();
                sequentialTestStep = 0;
                return true;
            }

            // "三點測試中的第二點 不會跳...也沒有移動" — this test button can be clicked from
            // anywhere, including standing at the recorded "next start" point on the FAR side of
            // the very gap this jump point is meant to cross (exactly what happens testing jump
            // points one at a time out of order). allowJumpAcrossGap:false (used for the real
            // route's approach segment, where a gap genuinely means "wrong path") would just
            // refuse to move at all here instead — allow jumping across a gap during the approach
            // for this manual test specifically, since we can't assume anything about what's on the
            // other side when testing a single point in isolation.
            //
            // isJumpApproach:true for the WHOLE approach (not just once inside JumpApproachRadius)
            // — see SteerByKeysToward's own comment for why gating full-speed/run-up purely on
            // radius starved short segments of any real acceleration distance at all ("很小的距離跳
            // 加速不夠 連跳躍起點都跳不到").
            var jumped = SteerByKeysToward(dest, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: true, isJumpApproach: true);
            if (jumped)
            {
                // "跳躍的瞬間是降速的 跳不遠" — returning false here (as if no manual move were
                // active anymore) let OnUpdate fall through to its other branches THIS SAME TICK,
                // which (finding no route/replay/target active) called ReleaseKeys() — zeroing the
                // movement command for exactly the one frame the jump itself just fired on. Return
                // true instead so OnUpdate exits immediately after this tick's jump, leaving the
                // full-speed command (already set by SteerByKeysToward's HoldDirection call above)
                // intact through the actual liftoff frame; AdvanceSequentialTest already queued the
                // next move, which TickManualMove will pick up starting next tick.
                manualMoveDestination = null;
                AdvanceSequentialTest();
                return true;
            }
            return true;
        }

        // These test segments are always short (a few meters between recorded points) — no need
        // to hand off to vnavmesh for the first stretch at all. Confirmed live that mixing vnavmesh
        // (for the "far" portion) with PreciseMovement (for the close approach) caused the two to
        // fight each other right at the handoff distance ("移動似乎還是用vnavmesh 接近後會亂跑"),
        // while PreciseMovement alone (already proven via the jump point auto-jumping correctly)
        // has no such issue. Just steer precisely the whole way.
        SteerByKeysToward(dest, allowVnavmeshFloorCheck: false, allowJumpAcrossGap: false);
        return true;
    }

    /// <summary>Diagnostic mirror of TickSparseRoute's bomb-blocking check, for the Debug/route
    /// panel to show whether a bomb is the reason the route currently isn't advancing.</summary>
    public static bool IsBlockedByBomb => TryGetBlockingBomb(out _);

    // "報名後 還沒傳送 路徑就已經是錯的了" — same shared settle window used by WindBlows/
    // SliceIsRight: right after registering, the actual teleport onto the course hasn't necessarily
    // landed yet, so IsInGate can flip true a moment before the character has actually settled at
    // the real starting position. Unified onto GateScheduleAutomation's join timer (populated by
    // GateNpcNavigation.TickList's registration interact) instead of Cliffhanger's own
    // entry-timestamp guard, so every GATE module holds off movement on the same signal
    // ("傳送到指定位置後 才開始自動移動").
    public const double PostJoinSettleSeconds = 30;

    public static void OnUpdate()
    {
        if (GateDirector.InSaucer && GateDirector.IsPlayerOnStage())
        {
            LastObservedGateType = GateDirector.GetCurrentGate();
        }

        var inGate = GateDirector.IsInGate(Module.GateType.Cliffhanger) || TestRunActive;
        if (inGate && !wasInGate)
        {
            ownTrail.Clear();
            replayRoute = CliffhangerRecorder.BuildReplayRoute();
            replayIndex = 0;
            routeIndex = 0;
        }
        wasInGate = inGate;

        if (!inGate || !Player.Available)
        {
            CurrentTargetPosition = null;
            NearestBombPosition = null;

            // Only release if THIS module actually holds movement right now — this branch runs
            // every single tick whenever Cliffhanger just isn't in its own gate, which is most of
            // the time if the module is enabled at all. Unconditionally clearing shared state here
            // would stomp over whatever another concurrently-enabled GATE module (e.g. Leap of
            // Faith) was actively driving that same tick.
            if (weAreHoldingKeys)
            {
                PreciseMovement.SetDesiredDirection(null);
                weAreHoldingKeys = false;
            }
            return;
        }

        UpdateMeasuredSpeed();

        // "地圖還沒完全載入 他就開始計算新的路線了 這是造成摔落的主因" — a fixed time delay alone
        // doesn't guarantee the navmesh has actually finished (re)building for the new area; if
        // vnavmesh reports itself not ready yet, any path it computes (or floor query the precise
        // steering does) can be based on incomplete/stale mesh data, sending the character off an
        // edge that isn't really there yet. Wait for the timer AND, if vnavmesh is installed, its
        // own readiness signal — whichever takes longer.
        if (GateScheduleAutomation.IsWithinPostJoinSettle(Module.GateType.Cliffhanger, PostJoinSettleSeconds) ||
            (Vnavmesh.IsInstalled && !Vnavmesh.IsNavReady()))
        {
            ReleaseKeys();
            return;
        }

        if ((DateTime.UtcNow - lastTrailSampleUtc).TotalMilliseconds >= TrailSampleIntervalMs)
        {
            lastTrailSampleUtc = DateTime.UtcNow;
            if (ownTrail.Count == 0 || Vector3.Distance(ownTrail[^1], Player.Position) > 0.5f)
            {
                ownTrail.Add(Player.Position);
                if (ownTrail.Count > MaxTrailPoints)
                {
                    ownTrail.RemoveAt(0);
                }
            }
        }

        FindTargetAndThreat();

        if (TickManualMove())
        {
            return;
        }

        if (!C.GoldSaucerGates.CliffhangerAutoMovement)
        {
            ReleaseKeys();
            return;
        }

        // Force the manually-marked route whenever one exists — the separate toggle requirement
        // meant "自動移動" alone silently fell back to the old auto-recorded replay if the second
        // checkbox wasn't also on, even though a real manual route existed ("自動移動 強制使用手
        // 動標點 之前錄得路線並沒有用"). A manually-marked route is always more trustworthy than
        // the auto-derived one, so just use it whenever it's there — no extra toggle needed.
        if (C.GoldSaucerGates.CliffhangerRoute.Count > 0)
        {
            TickSparseRoute();
            return;
        }

        if (replayRoute is { Count: > 0 })
        {
            TickReplayRoute();
            return;
        }

        if (CurrentTargetPosition is not { } target)
        {
            ReleaseKeys();
            return;
        }

        SteerToward(target);
    }

    /// <summary>Walks the sparse user-marked route in order: vnavmesh handles normal segments,
    /// and a jump waypoint switches to manual key control (facing the recorded direction) once
    /// reached, resuming vnavmesh after clearing the gap. A nearby bomb pauses everything in
    /// place rather than trying to react/dodge mid-route ("你偵測危險 停下 或繼續跑").</summary>
    private static void TickSparseRoute()
    {
        var route = C.GoldSaucerGates.CliffhangerRoute;
        if (routeIndex >= route.Count)
        {
            ReleaseKeys();
            return;
        }

        // Only react to a bomb that's BOTH old enough to actually be close to exploding (>=1s)
        // AND roughly in the way ahead — a freshly-spawned bomb (even right next to the path) is
        // safe to just run past, and one off to the side/behind isn't blocking anything ("正前方
        // 的已經出現一段時間炸彈需要避讓 剛出現的炸彈則快速通過"). No retreating either — just
        // stop dead once within 3m of it (keep closing the distance right up until then), rather
        // than backing away ("閃避不用後退...可接近到3公尺才停下").
        if (TryGetBlockingBomb(out _))
        {
            if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            ReleaseKeys();
            return;
        }

        var wp = route[routeIndex];
        var dest = new Vector3(wp.X, wp.Y, wp.Z);

        if (wp.IsJumpPoint)
        {
            // Steer continuously and precisely toward the actual recorded takeoff point itself
            // (not vnavmesh, not a synthetic far-away aim point derived from a separately-recorded
            // facing) — jump gets tapped as soon as close enough AND heading is aligned, so the
            // jump happens mid-approach rather than after a separate stop/turn/re-aim phase. A
            // real, nearby position target is inherently more precise to steer against than an
            // extrapolated point 8m out ("接近時按跳躍 這樣修正方向應該比較精準"). No longer needs
            // a separately-recorded jump direction/rotation at all — heading is corrected purely by
            // steering toward the real position (SteerByKeysToward's W+A/D approach), same as every
            // other segment, per "不要記錄方向了 靠移動時自動修正".
            if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            var jumped = SteerByKeysToward(dest, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: false, isJumpApproach: true);

            // Advancing as soon as merely "close enough to attempt" (closeEnoughToJump) turned out
            // too early — the jump itself is still gated on alignment/throttle inside
            // SteerByKeysToward, so that tick could advance the index toward the NEXT waypoint
            // before the actual spacebar tap ever fired, aiming movement at the wrong target and
            // never actually jumping at all ("跳躍判定很奇怪 有時跳完會走回去"). Advance only once
            // SteerByKeysToward reports the jump keypress genuinely fired this tick — that's the
            // moment we're actually committed and airborne, not merely "in range to try".
            if (jumped)
            {
                routeIndex++;
            }
            return;
        }

        if (Vector3.Distance(Player.Position, dest) < RouteArrivalRadius)
        {
            routeIndex++;
            return;
        }

        // Whether the NEXT waypoint (after this one) is a jump point only matters for the FINAL
        // short stretch right up to this waypoint (near a cliff edge, vnavmesh's pathfinding isn't
        // guaranteed to take the expected route — the short polygon right at the edge may not be
        // mesh-connected the way a human would walk it, sending the character down some entirely
        // different, longer path: "沒有從跳躍點前一個點 移動到跳躍點 就直接沿路走下來了"). It does
        // NOT mean the ENTIRE distance back to wherever the player currently is should skip vnavmesh
        // — a route point can easily be many meters away across obstacle-filled terrain, and blind
        // straight-line steering the whole way just walks into whatever's in between
        // ("走到1號點後 會改用精準模式往第二點衝 但中間有障礙"). Let vnavmesh handle distance same as
        // any ordinary segment; only skip it once already within the close-approach radius below.
        var nextIsJumpPoint = routeIndex + 1 < route.Count && route[routeIndex + 1].IsJumpPoint;

        // Ordinary segment (or the final approach into a pre-jump waypoint) — now that the 3-point
        // jump test proved the precise steering itself works, hand long-distance travel back to
        // vnavmesh (fast, handles real terrain/obstacles) and only switch to precise key-steering
        // once close, for the exact final positioning vnavmesh's own coarser arrival tolerance can't
        // give ("往非跳躍點移動時 可先用vnavmesh走長距離路徑 接近時 用新方法校正位置").
        if (Vnavmesh.IsInstalled && !Vnavmesh.IsWithinHorizontalRange(dest, RouteManualApproachRadius))
        {
            if (!Vnavmesh.IsMoving())
            {
                Vnavmesh.TryMoveTo(dest, false, RouteArrivalRadius);
            }
            return;
        }

        if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopPath();
        }

        // Once inside the close-approach radius, a pre-jump waypoint still gets full-speed/no-decel
        // steering (isJumpApproach:true, canPressJump:false — it isn't the real takeoff point, so it
        // must not fire the jump itself) so momentum carries through into the actual jump waypoint
        // right after it.
        SteerByKeysToward(dest, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: false, isJumpApproach: nextIsJumpPoint, canPressJump: false);
    }

    /// <summary>Advances through the recorded replay route as each waypoint is reached. Returns
    /// false once the route runs out (or none was ever recorded) so the caller falls back to live
    /// target-chasing.</summary>
    /// <summary>Walks the dense auto-recorded replay route the same way TickSparseRoute walks a
    /// manually-marked one — vnavmesh for distance, precise steering close-up, and (critically)
    /// only advancing past a jump waypoint once SteerByKeysToward reports the jump actually fired.
    ///
    /// The old version (TryGetReplayWaypoint + SteerToward) advanced purely on distance-to-waypoint,
    /// which broke down specifically for jump waypoints: a jump waypoint's recorded position is the
    /// last-ground-contact TAKEOFF spot, and after actually jumping the character lands meters past
    /// it — often still farther than the (deliberately tight) arrival radius, so the index never
    /// advanced and the character then steered BACKWARD toward the takeoff point it had just left,
    /// blocking every jump after the first ("第一個跳躍點有點偏 並往回跑 造成第二次無法跳").</summary>
    private static void TickReplayRoute()
    {
        if (replayRoute is not { Count: > 0 } route || replayIndex >= route.Count)
        {
            ReleaseKeys();
            return;
        }

        if (TryGetBlockingBomb(out _))
        {
            if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            ReleaseKeys();
            return;
        }

        var wp = route[replayIndex];
        var dest = wp.Position;

        if (wp.JumpHere)
        {
            if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            var jumped = SteerByKeysToward(dest, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: false, isJumpApproach: true);
            if (jumped)
            {
                replayIndex++;
            }
            return;
        }

        if (Vector3.Distance(Player.Position, dest) < ReplayWaypointArrivalRadius)
        {
            replayIndex++;
            return;
        }

        var nextIsJumpPoint = replayIndex + 1 < route.Count && route[replayIndex + 1].JumpHere;

        if (Vnavmesh.IsInstalled && !Vnavmesh.IsWithinHorizontalRange(dest, RouteManualApproachRadius))
        {
            if (!Vnavmesh.IsMoving())
            {
                Vnavmesh.TryMoveTo(dest, false, ReplayWaypointArrivalRadius);
            }
            return;
        }

        if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopPath();
        }

        SteerByKeysToward(dest, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: false, isJumpApproach: nextIsJumpPoint, canPressJump: false);
    }

    private static void FindTargetAndThreat()
    {
        var playerPos = Player.Position;

        IGameObject? nearestChick = null;
        var nearestChickDist = float.MaxValue;
        IGameObject? nearestBomb = null;
        var nearestBombDist = float.MaxValue;
        var bombPositions = new List<Vector3>();

        foreach (var obj in Svc.Objects)
        {
            if (obj == null)
            {
                continue;
            }

            if (obj.BaseId == ChickDataId)
            {
                var dist = Vector3.Distance(obj.Position, playerPos);
                if (dist < nearestChickDist)
                {
                    nearestChick = obj;
                    nearestChickDist = dist;
                }
            }
            else if (obj.BaseId == BombDataId)
            {
                // A bomb that already exploded/died shouldn't keep showing an avoid marker or
                // blast circle — IsDead is on the base IGameObject interface so this works
                // regardless of the object's exact runtime type.
                if (obj.IsDead)
                {
                    bombFirstSeenUtc.Remove(obj.GameObjectId);
                    continue;
                }

                if (!bombFirstSeenUtc.TryGetValue(obj.GameObjectId, out var firstSeen))
                {
                    firstSeen = DateTime.UtcNow;
                    bombFirstSeenUtc[obj.GameObjectId] = firstSeen;
                }

                var displayExpired = (DateTime.UtcNow - firstSeen).TotalSeconds > C.GoldSaucerGates.CliffhangerBombDisplaySeconds;
                if (!displayExpired)
                {
                    bombPositions.Add(obj.Position);
                }

                var dist = Vector3.Distance(obj.Position, playerPos);
                if (dist < nearestBombDist)
                {
                    nearestBomb = obj;
                    nearestBombDist = dist;
                    NearestBombAgeSeconds = (DateTime.UtcNow - firstSeen).TotalSeconds;
                }
            }
        }

        if (nearestBomb == null)
        {
            NearestBombAgeSeconds = null;
        }

        CurrentTargetPosition = nearestChick?.Position;
        NearestBombPosition = nearestBomb?.Position;
        AllBombPositions = bombPositions;
    }

    // Unlike Leap of Faith's dynamic floating platforms (confirmed to have NO vnavmesh floor
    // coverage anywhere), Cliffhanger's course is static level geometry — a live vnavmesh debug
    // overlay screenshot confirmed real, solid mesh coverage across the whole course. That means
    // vnavmesh's own pathfinding can be trusted here instead of the guess-based manual steering
    // Leap of Faith is stuck with, so route the main "walk to chick" movement through actual
    // navmesh pathfinding rather than screenshot-driven route building — the real mesh IS already
    // the route data.
    //
    // MUST stay <= ReplayWaypointArrivalRadius above. It used to be
    // 1.5m while the replay-index advance check needed 1m — vnavmesh would consider itself
    // "arrived" and stop 1-1.5m short, but the replay logic never saw that as close enough to
    // advance to the NEXT waypoint, so the character just stood there until the player manually
    // stepped the remaining gap ("現在要我走一步 他才會偵測到下一步"). Since jump waypoints only
    // get selected once the index reaches them, this also silently ate every jump ("還是沒有跳").
    private const float ArrivalRange = 0.8f;

    private static void SteerToward(Vector3 target, bool forceJump = false)
    {
        // A moving bomb needs an immediate reactive dodge — vnavmesh pathfinding recomputes
        // asynchronously and can't react fast enough to something that moves every frame, so this
        // case keeps the old manual key-steering instead of handing off to vnavmesh.
        if (NearestBombPosition is { } bomb && Vector3.Distance(Player.Position, bomb) < BombAvoidRadius)
        {
            // Cancel any in-progress vnavmesh path first — it doesn't know about the bomb and
            // would otherwise keep pulling the player back onto its own route while the manual
            // dodge keys are also being pressed.
            if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            SteerByKeysToward(Player.Position + AwayFromBombDirection(bomb) * 5f, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: false);
            return;
        }

        // A replay waypoint flagged as a known jump point (see CliffhangerRecorder.BuildReplayRoute)
        // is ground truth from a real successful manual run — go straight to manual key-steering
        // with jump forced on, skipping vnavmesh entirely rather than waiting for it to fail first.
        if (forceJump)
        {
            if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            SteerByKeysToward(target, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: true, isJumpApproach: true);
            return;
        }

        // vnavmesh's own pathfinding can't cross a gap in the mesh at all — it just never finds a
        // path and sits there doing nothing, which is indistinguishable from "broken" until you
        // notice the course has an actual break requiring a real jump ("路徑中間有斷點 需要跳躍").
        // Detect "not making progress despite a vnavmesh goal being active" and fall back to manual
        // key-steering (which CAN jump) instead of just standing there forever.
        if (Vnavmesh.IsInstalled && Vnavmesh.IsNavReady() && !IsVnavStuck(target))
        {
            // A held key from a previous bomb-dodge/fallback frame must be released here —
            // otherwise it stays stuck down while vnavmesh's own movement takes over, fighting
            // with it.
            ReleaseKeys();

            if (Vnavmesh.TickArrival(target, ArrivalRange))
            {
                return;
            }

            if (!Vnavmesh.IsMoving())
            {
                Vnavmesh.TryMoveTo(target, false, ArrivalRange);
            }
            return;
        }

        if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopPath();
        }

        // vnavmesh unavailable, or stuck on an unconnected gap — fall back to manual steering,
        // which (unlike vnavmesh) can actually jump across a break in the path.
        SteerByKeysToward(target, allowVnavmeshFloorCheck: true, allowJumpAcrossGap: true);
    }

    // If the player hasn't actually moved in the last few seconds despite vnavmesh having an
    // active goal, the path is blocked (most likely an unconnected gap in the mesh) rather than
    // just slow — same "not making progress" signal used for Leap of Faith's stuck detector.
    private const float VnavStuckMinProgress = 1f;
    private const double VnavStuckTimeoutSeconds = 3.0;
    private static Vector3? vnavStuckCheckPos;
    private static DateTime vnavStuckCheckSinceUtc;

    private static bool IsVnavStuck(Vector3 target)
    {
        var playerPos = Player.Position;
        if (Vnavmesh.IsWithinHorizontalRange(target, ArrivalRange))
        {
            vnavStuckCheckPos = null;
            return false;
        }

        if (vnavStuckCheckPos is not { } lastPos || Vector3.Distance(lastPos, playerPos) > VnavStuckMinProgress)
        {
            vnavStuckCheckPos = playerPos;
            vnavStuckCheckSinceUtc = DateTime.UtcNow;
            return false;
        }

        return (DateTime.UtcNow - vnavStuckCheckSinceUtc).TotalSeconds >= VnavStuckTimeoutSeconds;
    }

    private static Vector3 AwayFromBombDirection(Vector3 bomb)
    {
        var away = Player.Position - bomb;
        away.Y = 0;
        return away.LengthSquared() > 0.01f ? Vector3.Normalize(away) : Vector3.Zero;
    }

    // "Ahead" is a fairly wide cone (not a narrow line) since heading is only approximate anyway.
    // Per user: only bombs that have existed >=1s (not the display-duration slider — a fixed,
    // short "has it actually been here a moment" check) AND sit within the near 3-5m band ahead
    // are relevant; keep closing the distance right up to 3m before actually stopping, don't react
    // any earlier ("只避讓 前方3~5公尺內 出現一秒以上的炸彈 可接近到3公尺才停下").
    private const float BombAheadDotThreshold = 0.3f; // ~72° half-angle cone in front
    private const double BombMinAgeSeconds = 1.0;
    private const float BombStopDistance = 3f;

    private static bool TryGetBlockingBomb(out Vector3 bomb)
    {
        bomb = default;
        if (NearestBombPosition is not { } candidate || NearestBombAgeSeconds is not { } age || age < BombMinAgeSeconds)
        {
            return false;
        }

        var toBomb = candidate - Player.Position;
        toBomb.Y = 0;
        var dist = toBomb.Length();
        if (dist < 0.1f || dist > BombStopDistance)
        {
            return false;
        }

        var forward = new Vector3(MathF.Sin(Player.Rotation), 0, MathF.Cos(Player.Rotation));
        var dot = Vector3.Dot(forward, Vector3.Normalize(toBomb));
        if (dot < BombAheadDotThreshold)
        {
            return false;
        }

        bomb = candidate;
        return true;
    }

    // PreciseMovement scales speed by the magnitude of the direction vector it's given (capped at
    // 1 = full speed) — always passing a full-length unit vector meant running at full speed all
    // the way to the target with zero slow-down, which reliably overshot the arrival radius before
    // the next tick's distance check caught it ("移動位置不精準 跑過頭"). Linearly ramp speed down
    // once within this distance of the target instead.
    private const float DecelerationDistance = 2f;
    private const float MinApproachSpeed = 0.15f;

    // "往第二點移動並跳躍 不該有限制" — no run-up timer, no alignment requirement: full speed the
    // entire approach (already handled by isJumpApproach forcing angle/distance speed scale to 1
    // below), and the jump itself fires purely on actually reaching the point — nothing else gates
    // it. Direction stays continuously aimed at the live target position the whole way (no locked/
    // frozen bearing), per explicit request.

    /// <summary>Returns true only on the tick the jump keypress actually fires (not merely "close
    /// enough to attempt") — callers that need to advance a route index exactly when the jump
    /// really happens (not before) should key off this, not off distance/angle alone.
    ///
    /// isJumpApproach means "steerTarget is a jump takeoff point" — this now drives full-speed/
    /// no-deceleration movement (and starts the run-up timer) for the WHOLE approach, not just once
    /// within JumpApproachRadius. Previously the caller pre-computed "close enough" and only passed
    /// forceJump=true at that point, meaning short segments (a few meters, common in the 3-point
    /// test tool) spent almost their entire approach already "close enough" — so the run-up timer
    /// and full-speed movement only got a few hundred ms to actually build real momentum before the
    /// jump fired, launching at a near-standstill ("很小的距離跳 加速不夠 連跳躍起點都跳不到").
    /// Now the whole run toward a jump point is always full speed with the run-up clock running the
    /// entire time; only the actual space-bar press stays gated on distance/alignment below.</summary>
    // "跳躍點減速了" — the route segment leading right up to a jump waypoint (TickSparseRoute's
    // "nextIsJumpPoint" branch) still called this with isJumpApproach:false (only the jump waypoint
    // itself got full speed), so the character decelerated approaching THAT prior waypoint, then had
    // to rebuild speed almost from scratch once the route advanced to the actual jump point —
    // killing momentum right before the run-up that matters most. That segment now also passes
    // isJumpApproach:true for full speed/run-up-timer purposes, but must NOT be allowed to actually
    // fire the jump itself (it isn't standing at the real takeoff point) — canPressJump decouples
    // "give this segment full speed and start the run-up clock early" from "this specific call may
    // press space", so passing isJumpApproach:true without canPressJump:true can't spuriously jump
    // just because the character happened to pass within JumpApproachRadius of a waypoint that isn't
    // the real jump point.
    private static bool SteerByKeysToward(Vector3 steerTarget, bool allowVnavmeshFloorCheck, bool allowJumpAcrossGap, bool isJumpApproach = false, bool canPressJump = true)
    {
        var toTargetRaw = steerTarget - Player.Position;
        toTargetRaw.Y = 0;
        var distToTarget = toTargetRaw.Length();
        var rotation = Player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation));
        var closeEnoughToJump = isJumpApproach && canPressJump && distToTarget < JumpApproachRadius;

        // For a jump approach, once basically on top of the target, keep holding the current
        // heading (still running) instead of stopping dead, right up until the jump itself fires.
        if (distToTarget < 0.1f)
        {
            if (!isJumpApproach)
            {
                ReleaseKeys();
                return false;
            }

            distToTarget = 0.1f;
            toTargetRaw = forward * distToTarget;
        }
        var toTarget = toTargetRaw / distToTarget;

        var angleDiff = MathF.Acos(Math.Clamp(Vector3.Dot(forward, toTarget), -1f, 1f));

        // "回到三點測試 似乎是他不會急轉向 只能慢慢轉" / "立即移動會亂跑 繞地圖繞一圈後慢慢修正回
        // 原點" — the character's body can only turn so fast per frame; commanding full-speed
        // movement toward a target that's sharply off to the side (or behind) makes it run a wide
        // curving loop while its facing slowly catches up, instead of turning in place first. Scale
        // speed down the more the target is off to the side/behind, so a big heading mismatch turns
        // into "pivot mostly in place" rather than "sprint in the wrong direction while turning".
        //
        // Both the distance AND angle deceleration are skipped entirely when about to jump — a
        // jump's horizontal distance comes from however fast the character was already moving at
        // takeoff, so slowing down for a precise stop/turn right where forceJump fires meant
        // jumping with near-zero momentum and barely leaving the ground ("有時會原地跳"/"減速了
        // 導致沒跳上台階"). Alignment is still required to actually PRESS jump (further below) —
        // this only affects how fast the character runs while approaching, not whether it jumps.
        // Only non-jump approaches need to decelerate for a precise stop.
        var angleSpeedScale = isJumpApproach ? 1f : Math.Clamp(1f - (angleDiff / MathF.PI), MinApproachSpeed, 1f);
        var distanceSpeedScale = isJumpApproach ? 1f : Math.Clamp(distToTarget / DecelerationDistance, MinApproachSpeed, 1f);
        var moveVector = toTarget * MathF.Min(angleSpeedScale, distanceSpeedScale);

        // No real floor/collision detection here — walking straight toward a target or straight
        // away from a bomb can walk the player off a ledge (confirmed live: "他跳樓了"). If
        // vnavmesh is installed, refuse to move when there's no landable floor a couple meters
        // ahead in the direction we're about to move; otherwise fall back to the old (unsafe)
        // behavior since we have no other way to know where the edges are.
        //
        // Skipped entirely for a jump approach ("要移動到跳躍點才起跳 不用探測地板") — a jump
        // waypoint's whole purpose is to run straight at a recorded takeoff point and jump once
        // actually there; floor probing ahead of the target direction was meant to catch "walked
        // off a ledge by accident", but for a real jump point there's SUPPOSED to be no floor ahead
        // (that's the gap being jumped), so the probe just added noise and false "gap detected"
        // triggers on the ordinary approach instead of ever being needed. Just steer straight there.
        if (allowVnavmeshFloorCheck && !isJumpApproach && Vnavmesh.IsInstalled)
        {
            var aheadPoint = Player.Position + (toTarget * 2.5f);
            if (Vnavmesh.TryGetPointOnFloor(aheadPoint, allowUnlandable: false, halfExtentXz: 1.5f) is not { } floorPoint ||
                MathF.Abs(floorPoint.Y - Player.Position.Y) > 2f)
            {
                if (allowJumpAcrossGap)
                {
                    // No floor found a couple meters ahead — that's exactly what a real gap in the
                    // path looks like ("路徑中間有斷點 需要跳躍"). Keep moving toward the target AND
                    // jump, instead of just refusing to move, since standing at the edge is a
                    // guaranteed failure while a jump at least has a chance of clearing it.
                    HoldDirection(toTarget);
                    if (EzThrottler.Throttle("Saucy.Cliffhanger.GapJump", 800))
                    {
                        GameKeyInput.TapKey(GameKeyInput.VK_SPACE);
                        return true;
                    }
                    return false;
                }

                ReleaseKeys();
                return false;
            }
        }

        // PreciseMovement (Framework/PreciseMovement.cs) hooks the game's own movement-input read
        // directly instead of simulating WASD keys — it writes the desired world-space direction
        // straight into the game's movement resolution, so the character always moves toward the
        // target with no separate "turn first" phase needed. That whole A/D-strafe workaround only
        // existed because simulated key input couldn't translate and rotate reliably at once
        // ("鍵盤模擬 現在完全不能用...vnavmesh 接近後逼近也是基於鍵盤模擬 反而會亂跑"); this hook
        // has no such limitation.
        HoldDirection(moveVector);

        // "往第二點移動並跳躍 不該有限制" — no angle check, no run-up timer: the jump fires purely
        // on actually reaching the point (closeEnoughToJump), full stop.
        if (closeEnoughToJump && EzThrottler.Throttle("Saucy.Cliffhanger.ReplayJump", 800))
        {
            GameKeyInput.TapKey(GameKeyInput.VK_SPACE);
            return true;
        }

        return false;
    }
}
