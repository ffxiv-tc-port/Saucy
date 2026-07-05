using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Linq;
using System.Numerics;
namespace Saucy.LeapOfFaith;

internal static unsafe class LeapOfFaithDetection
{
    // Confirmed via live diagnostic: Leap of Faith does NOT create a GoldSaucerManager
    // GFateDirector like Wind Blows/Any Way the Wind Blows does — "目前沒有作用中的 GATE 導演"
    // stayed true throughout an actual run with visible cactuar-collection progress. Instead it
    // sets ConditionFlag 56 (BoundByDuty56), found by dumping every active ConditionFlag while
    // mid-run. Distinct from Air Force One's BoundByDuty95.
    //
    // BoundByDuty56 turned out NOT unique to Leap of Faith — confirmed live via screenshot showing
    // the platform-marker blue trail overlay drawn during an unrelated duty/FATE fight (亞瑟羅王)
    // far outside the Gold Saucer ("登高跳跳樂的畫路徑 在其他副本中也畫了"). First attempt gated
    // this on GateDirector.InSaucer (hardcoded TerritoryType 144, the main Gold Saucer square) —
    // that turned out WRONG too: it broke detection during a real, late-stage Leap of Faith run
    // ("這是已經快結束了" — platform points/GateType stayed at 0/None despite being deep into an
    // actual attempt), meaning Leap of Faith's jump course must run in a different, non-144
    // instanced territory. Switched to GoldSaucerManager's existence instead — but that ALSO turned
    // out not to be zone-scoped: it stayed non-null even in Eureka, so the trail overlay showed up
    // there too ("登高的繪至路徑 會在優雷卡顯示"). Neither BoundByDuty56 nor GoldSaucerManager
    // presence actually distinguishes this specific minigame from unrelated content.
    //
    // Use the real, minigame-specific objects instead — the finish marker (FinishDataId) and the
    // cactuar trophies (CactuarDataIds) only ever exist while an actual Leap of Faith run is live,
    // so their presence in the object table is a far more reliable signal than any zone/manager/
    // condition-flag check.
    private static bool HasLeapOfFaithObjects()
    {
        foreach (var obj in Svc.Objects)
        {
            if (obj != null && (obj.DataId == LeapOfFaithAutomation.FinishDataId ||
                                 Array.IndexOf(LeapOfFaithAutomation.CactuarDataIds, obj.DataId) >= 0))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsActive => Svc.Condition[ConditionFlag.BoundByDuty56] && HasLeapOfFaithObjects();
}

/// <summary>
/// Best-effort auto-movement for Leap of Faith. Platform layout and cactuar-trophy positions are
/// randomized per run and there's no navmesh over the floating platforms, so this can't do real
/// pathfinding or obstacle-aware jumping — it only steers toward whichever known object (the
/// finish line or a cactuar trophy) is currently visible in the object table, and jumps on a
/// fixed timer as a rough heuristic. Movement uses simulated key taps (same mechanism already
/// used for Air Force One's Space-to-shoot), never direct memory writes to the character.
/// Expect this to fall off course sometimes — there is no floor/collision detection.
/// </summary>
internal static unsafe class LeapOfFaithAutomation
{
    // Identified from real recordings (see LeapOfFaithRecorder): 6 bronze cactuar instances all
    // share DataId 2009590, silver share 2009589, the single gold is 2009588, and the finish
    // marker is named "終點" under DataId 2009601. Internal (not private) since
    // LeapOfFaithPlatformObserver also needs these to recognize a deliberate "safe detour back to
    // grab a cactuar" loop, as opposed to a failed run resetting back to an earlier point.
    internal const uint FinishDataId = 2009601;
    internal static readonly uint[] CactuarDataIds = [2009588, 2009589, 2009590];

    private const float TurnThresholdRadians = 0.14f; // ~8 degrees

    public static Vector3? CurrentTargetPosition { get; private set; }
    public static bool CurrentTargetIsFinish { get; private set; }
    public static bool CurrentTargetIsCactuar { get; private set; }
    public static global::Saucy.Framework.Module.GateType LastObservedGateType { get; private set; } =
        global::Saucy.Framework.Module.GateType.None;

    private static Vector3? startPosition;
    private static bool wasInGate;

    // Tracks whether THIS module currently owns the shared GameKeyInput held-key state, so the
    // top-level "not in our gate" exit only releases keys it actually set.
    private static bool weAreHoldingKeys;

    private static void HoldKeys(System.Collections.Generic.IEnumerable<int> keys)
    {
        GameKeyInput.SetHeldKeys(keys);
        weAreHoldingKeys = true;
    }

    private static void ReleaseKeys()
    {
        GameKeyInput.ReleaseHeldKey();
        weAreHoldingKeys = false;
    }

    // Standing on a cactuar/finish for ~1s picks it up — once that's happened, stop offering it
    // as a target (both for steering and the on-screen pointer) for the rest of this run, per
    // user feedback ("目標 踩在上面一秒後消失 該場遊戲不再顯示 避免干擾").
    //
    // Keyed by (GameObjectId, Position) rather than just the id — FFXIV's client recycles
    // GameObjectId slots as objects despawn/spawn, so a BRAND NEW cactuar elsewhere in the run can
    // end up reusing an id already in this set, making it get silently excluded from targeting the
    // instant it appears, before the player ever got near it ("目標不見了 還沒接近他"). Requiring
    // the position to also roughly match means only the actual consumed object (which doesn't
    // move) gets excluded, not an unrelated new spawn that happens to inherit its old id.
    private const float StandingOnRadius = 1.5f;
    private const double StandOnSeconds = 1.0;
    private const float ConsumedPositionTolerance = 2f;
    private static readonly System.Collections.Generic.List<(ulong Id, Vector3 Position)> consumedTargets = [];
    private static ulong currentTargetObjectId;
    private static DateTime? standingOnTargetSinceUtc;

    private static bool IsConsumed(ulong id, Vector3 position) =>
        consumedTargets.Any(c => c.Id == id && Vector3.Distance(c.Position, position) < ConsumedPositionTolerance);

    // Live trail of the player's own path this run, for the "玩家路徑" line overlay in
    // LeapOfFaithModule — separate from the manual export-to-JSON recorder, this is automatic and
    // just for on-screen reference while playing. Capped so a very long run doesn't grow unbounded.
    private const int MaxTrailPoints = 400;
    private const int TrailSampleIntervalMs = 200;
    private static readonly System.Collections.Generic.List<Vector3> ownTrail = [];
    private static DateTime lastTrailSampleUtc;

    public static System.Collections.Generic.IReadOnlyList<Vector3> OwnTrail => ownTrail;

    // "進入Gate後 太早開始移動了 一直跑出舞台" — IsActive flips true as soon as the finish/cactuar
    // objects load into the object table, which can happen right as the teleport-in is still
    // settling (player position/camera/floor not fully stable yet). Starting to steer immediately
    // off whatever target happens to be found at that instant sent the character running off the
    // spawn platform before it had even properly landed. Wait briefly after entering before letting
    // any movement logic run — same fix already applied to Cliffhanger's GateEntrySettleSeconds.
    private const double GateEntrySettleSeconds = 5;
    private static DateTime gateEnteredUtc;

    public static void OnUpdate()
    {
        if (GateDirector.InSaucer && GateDirector.IsPlayerOnStage())
        {
            LastObservedGateType = GateDirector.GetCurrentGate();
        }

        var inGate = LeapOfFaithDetection.IsActive;
        if (inGate && !wasInGate)
        {
            startPosition = Player.Available ? Player.Position : null;
            ownTrail.Clear();
            consumedTargets.Clear();
            stickyWaypoint = null;
            stickyWaypointFinalTarget = null;
            stuckCheckPos = null;
            currentTargetObjectId = 0;
            standingOnTargetSinceUtc = null;
            gateEnteredUtc = DateTime.UtcNow;
        }
        else if (!inGate && wasInGate)
        {
            // Left the GATE (finished, fell out, or gave up) — persist whatever platform
            // positions were observed from other players this run for next time.
            LeapOfFaithPlatformObserver.Save();
            startPosition = null;
        }
        wasInGate = inGate;

        if (!inGate)
        {
            CurrentTargetPosition = null;

            // Only release if THIS module actually holds keys right now — this branch runs every
            // tick whenever Leap of Faith just isn't active, which is most of the time if another
            // GATE module (e.g. Cliffhanger) is also enabled and currently in ITS gate instead.
            // Calling GameKeyInput.ReleaseHeldKey() unconditionally here stomped over whatever that
            // other module was actively holding that same tick, cancelling its movement every
            // frame — confirmed live: "只有W被阻止移動...但自動移動還是在原地" (the held key kept
            // getting yanked back up the instant it was pressed). Only touch the shared key state
            // when we're the one who set it.
            if (weAreHoldingKeys)
            {
                GameKeyInput.ReleaseHeldKey();
                weAreHoldingKeys = false;
            }
            return;
        }

        if (!Player.Available)
        {
            CurrentTargetPosition = null;
            return;
        }

        if ((DateTime.UtcNow - gateEnteredUtc).TotalSeconds < GateEntrySettleSeconds)
        {
            if (weAreHoldingKeys)
            {
                GameKeyInput.ReleaseHeldKey();
                weAreHoldingKeys = false;
            }
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

        LeapOfFaithPlatformObserver.Observe();
        FindTarget();
        CheckStandingOnTarget();

        if (!C.GoldSaucerGates.LeapOfFaithAutoMovement)
        {
            ReleaseKeys();
            return;
        }

        if (CurrentTargetPosition is not { } target)
        {
            // No finish/cactuar in range AND no recorded platform data covers wherever we are —
            // standing still is a guaranteed failure, so at least try pushing forward and jumping
            // periodically instead of freezing at an unexplored edge ("這種地方嘗試跳躍").
            BlindExploreForward();
            return;
        }

        if (CheckStuckAndRecover(Player.Position))
        {
            // Held key was just released and the waypoint invalidated so next tick can pick a
            // fresh one — but also try a blind forward jump right now, since standing at an edge
            // the recorded track doesn't cover is exactly the kind of spot a real jump might clear
            // ("這種地方嘗試跳躍").
            BlindExploreForward();
            return;
        }

        var waypoint = SelectSteeringWaypoint(target, out var gapAhead);
        SteerToward(waypoint, gapAhead);
    }

    // If auto-movement holds a direction into a wall/edge it can't actually walk past (e.g. the
    // chosen waypoint is across a gap the character can't reach that way), the character just sits
    // there pushing against geometry with the key stuck held — looked like "不會移動 也不能動"
    // (can't even move manually, since a synthetic key is still being held down every tick).
    // Detect near-zero real-world progress over a few seconds and force a release + re-pick.
    private const float StuckMinProgress = 1f;
    private const double StuckTimeoutSeconds = 3.0;
    private static Vector3? stuckCheckPos;
    private static DateTime stuckCheckSinceUtc;

    private static bool CheckStuckAndRecover(Vector3 playerPos)
    {
        if (stuckCheckPos is not { } lastPos || Vector3.Distance(lastPos, playerPos) > StuckMinProgress)
        {
            stuckCheckPos = playerPos;
            stuckCheckSinceUtc = DateTime.UtcNow;
            return false;
        }

        if ((DateTime.UtcNow - stuckCheckSinceUtc).TotalSeconds < StuckTimeoutSeconds)
        {
            return false;
        }

        stickyWaypoint = null;
        stickyWaypointFinalTarget = null;
        stuckCheckPos = playerPos;
        stuckCheckSinceUtc = DateTime.UtcNow;
        ReleaseKeys();
        return true;
    }

    /// <summary>
    /// No known target/track/guide-trail data to steer by. Tried pushing forward + jumping blindly
    /// first ("standing still guarantees failure") — confirmed live it just as often ran the
    /// character off the platform into the void instead ("會往空處跑"). Tried gating that on a
    /// vnavmesh forward-floor check next, but vnavmesh has already been confirmed (repeatedly, live)
    /// to return no floor data ANYWHERE inside this GATE's dynamic platforms — so that check can
    /// only ever fail here and is dead weight, not a real safety net ("不是說在登高內無論何處
    /// vnavmesh 都查不到地板嗎"). With no reliable floor signal of any kind available, standing
    /// still is the only safe option left when there's truly no recorded data to go on.
    /// </summary>
    private static void BlindExploreForward() => ReleaseKeys();

    // Steering straight at a distant final target can cut across a gap the recorded blue track
    // actually routes around. Per user request ("沿著藍色軌道移動並跳躍"), hop through the nearest
    // recorded platform point that's both within reach and makes real progress toward the target,
    // rather than always beelining for the target itself once it's far away.
    private const float WaypointLookaheadRadius = 15f;
    private const float MinWaypointDistance = 1.5f;

    // A recorded track segment connecting two points that far apart could only have been made by
    // a real jump (walking speed covers far less ground between consecutive 200ms-interval
    // samples) — used below to decide WHEN to jump instead of jumping on a blind fixed timer.
    private const float JumpSegmentLengthThreshold = 3.5f;

    // Re-scanning ALL observed points fresh every single frame let the "best" candidate flip
    // between two similarly-scored points from one frame to the next (floating point ties, a
    // slightly-closer point becoming available as the player moves a few cm) — each flip yanks the
    // steering angle in a different direction, which reads as constantly running left/right
    // instead of smoothly following the track ("自動移動 沒有跟著軌跡 而是往左或往右一直跑"). Stick
    // with the current waypoint until it's actually reached (or genuinely invalidated) instead of
    // re-picking every tick.
    private const float WaypointArrivalRadius = 2f;

    // "5~10秒前 有人安全走過的路線" — only trust a trail as a live safety signal for this long
    // after its last update; older than this and it's no more trustworthy than the static dot
    // cloud, so let it fall through to that instead.
    private static readonly TimeSpan GuideTrailMaxAge = TimeSpan.FromSeconds(10);

    // A stale sticky waypoint computed for a PREVIOUS final target (e.g. the nearest cactuar
    // changed once a closer one came into range) must not keep being reused — it could easily be
    // sitting almost exactly where the player already is relative to the new target, which zeroes
    // out SteerToward's movement vector and looks like "doesn't move at all" ("這次不會動了"). Tie
    // the cached waypoint to the final target it was chosen for and drop it the moment that target
    // changes, on top of the existing arrival/out-of-range invalidation.
    private static Vector3? stickyWaypoint;
    private static Vector3? stickyWaypointFinalTarget;

    private static Vector3 SelectSteeringWaypoint(Vector3 finalTarget, out bool gapAhead)
    {
        var playerPos = Player.Position;

        // Jump exactly when standing near a track segment long enough that it could only have
        // been made by a real jump — same signal regardless of which waypoint gets picked below.
        gapAhead = HasNearbyRecordedSegment(playerPos);

        if (stickyWaypointFinalTarget is { } previousTarget && Vector3.Distance(previousTarget, finalTarget) > 0.1f)
        {
            stickyWaypoint = null;
        }
        stickyWaypointFinalTarget = finalTarget;

        var directDist = Vector3.Distance(playerPos, finalTarget);
        if (directDist <= WaypointLookaheadRadius)
        {
            stickyWaypoint = null;
            return finalTarget;
        }

        // Keep steering toward the already-chosen waypoint until actually reached — only drop it
        // once close enough to arrive, or once it's no longer a sane pick (too far now, e.g. after
        // a fall/reset).
        if (stickyWaypoint is { } current)
        {
            var distToCurrent = Vector3.Distance(playerPos, current);
            if (distToCurrent > WaypointArrivalRadius && distToCurrent <= WaypointLookaheadRadius * 1.5f)
            {
                return current;
            }

            stickyWaypoint = null;
        }

        // Prefer following a route someone is CURRENTLY (within the last few seconds) walking
        // safely, over the aggregated all-time dot cloud below — per user request ("不能沿著5~10秒
        // 前 有人安全走過的路線嗎"). A trail still being actively updated is direct proof that
        // stretch hasn't been fallen off recently, which the static point cloud can't promise (it
        // mixes in data from any time, including routes that may no longer be safe/relevant).
        if (LeapOfFaithPlatformObserver.TryGetGuideWaypoint(playerPos, finalTarget, GuideTrailMaxAge, out var guideIsLongHop) is { } guideWaypoint)
        {
            stickyWaypoint = guideWaypoint;
            gapAhead = guideIsLongHop;
            return guideWaypoint;
        }

        Vector3? best = null;
        var bestDistToTarget = directDist;
        foreach (var point in LeapOfFaithPlatformObserver.ObservedPlatforms)
        {
            var pos = point.Position;
            var distFromPlayer = Vector3.Distance(playerPos, pos);
            if (distFromPlayer > WaypointLookaheadRadius || distFromPlayer < MinWaypointDistance)
            {
                continue;
            }

            var distToTargetFromPoint = Vector3.Distance(pos, finalTarget);
            if (distToTargetFromPoint >= bestDistToTarget)
            {
                continue;
            }

            bestDistToTarget = distToTargetFromPoint;
            best = pos;
        }

        if (best is { } waypoint)
        {
            stickyWaypoint = waypoint;
            return waypoint;
        }

        // No recorded waypoint at all covers this direction — nothing but a blind guess ahead, so
        // treat it the same as a known jump gap rather than walking straight off the edge.
        gapAhead = true;
        return finalTarget;
    }

    /// <summary>True if the player is currently standing near one end of a recorded track segment
    /// long enough that it could only represent a real jump (see JumpSegmentLengthThreshold) — the
    /// signal used to decide when to actually press jump, instead of a blind fixed interval.</summary>
    private static bool HasNearbyRecordedSegment(Vector3 playerPos)
    {
        foreach (var segment in LeapOfFaithPlatformObserver.ComputeLinearSegments())
        {
            var length = Vector3.Distance(segment.A, segment.B);
            if (length < JumpSegmentLengthThreshold)
            {
                continue;
            }

            if (Vector3.Distance(playerPos, segment.A) < MinWaypointDistance ||
                Vector3.Distance(playerPos, segment.B) < MinWaypointDistance)
            {
                return true;
            }
        }

        return false;
    }

    private static void FindTarget()
    {
        var playerPos = Player.Position;

        IGameObject? finish = null;
        IGameObject? nearestCactuar = null;
        var nearestCactuarDist = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventObj || IsConsumed(obj.GameObjectId, obj.Position))
            {
                continue;
            }

            var dist = Vector3.Distance(obj.Position, playerPos);

            if (obj.DataId == FinishDataId)
            {
                finish = obj;
                continue;
            }

            if (CactuarDataIds.Contains(obj.DataId) && dist < nearestCactuarDist)
            {
                nearestCactuar = obj;
                nearestCactuarDist = dist;
            }
        }

        // Prefer the finish once it's in range — reaching it completes the GATE. Otherwise steer
        // toward whichever cactuar trophy is currently visible/nearby.
        if (finish != null)
        {
            CurrentTargetPosition = finish.Position;
            CurrentTargetIsFinish = true;
            CurrentTargetIsCactuar = false;
            currentTargetObjectId = finish.GameObjectId;
        }
        else if (nearestCactuar != null)
        {
            CurrentTargetPosition = nearestCactuar.Position;
            CurrentTargetIsFinish = false;
            CurrentTargetIsCactuar = true;
            currentTargetObjectId = nearestCactuar.GameObjectId;
        }
        else
        {
            CurrentTargetPosition = FindPlatformFallbackTarget(playerPos);
            CurrentTargetIsFinish = false;
            CurrentTargetIsCactuar = false;
            currentTargetObjectId = 0;
        }
    }

    /// <summary>Marks the current finish/cactuar target as picked-up once the player has stood
    /// within StandingOnRadius of it continuously for StandOnSeconds, so it stops being offered as
    /// a target (steering or the on-screen pointer) for the rest of this run.</summary>
    private static void CheckStandingOnTarget()
    {
        if (currentTargetObjectId == 0 || CurrentTargetPosition is not { } target ||
            Vector3.Distance(Player.Position, target) > StandingOnRadius)
        {
            standingOnTargetSinceUtc = null;
            return;
        }

        standingOnTargetSinceUtc ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - standingOnTargetSinceUtc.Value).TotalSeconds < StandOnSeconds)
        {
            return;
        }

        consumedTargets.Add((currentTargetObjectId, target));
        currentTargetObjectId = 0;
        standingOnTargetSinceUtc = null;
        CurrentTargetPosition = null;
    }

    /// <summary>
    /// No finish/cactuar currently in range — fall back to the nearest platform position inferred
    /// from other players standing still (see LeapOfFaithPlatformObserver), preferring ones that
    /// represent forward progress (farther from the start than the player already is) so this
    /// doesn't just walk back and forth toward an already-passed platform.
    /// </summary>
    private static Vector3? FindPlatformFallbackTarget(Vector3 playerPos)
    {
        if (startPosition is not { } start)
        {
            return null;
        }

        var playerProgress = Vector3.Distance(start, playerPos);

        Vector3? best = null;
        var bestDist = float.MaxValue;
        foreach (var candidate in LeapOfFaithPlatformObserver.ObservedPlatforms)
        {
            var pos = candidate.Position;
            if (Vector3.Distance(start, pos) <= playerProgress)
            {
                continue;
            }

            var dist = Vector3.Distance(playerPos, pos);
            if (dist < bestDist)
            {
                best = pos;
                bestDist = dist;
            }
        }

        return best;
    }

    private static void SteerToward(Vector3 target, bool gapAhead)
    {
        var toTarget = target - Player.Position;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() < 0.01f)
        {
            ReleaseKeys();
            return;
        }
        toTarget = Vector3.Normalize(toTarget);

        // Standard FFXIV facing-vector convention: forward = (sin(rotation), 0, cos(rotation)).
        var rotation = Player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation));

        var cross = (forward.X * toTarget.Z) - (forward.Z * toTarget.X);
        var dot = Math.Clamp(Vector3.Dot(forward, toTarget), -1f, 1f);
        var angleDiff = MathF.Acos(dot) * MathF.Sign(cross == 0 ? 1 : cross);

        // This never got the fix Cliffhanger's steering did — pure "hold A/D alone to turn" never
        // actually rotates the character in this client's control scheme (A/D strafe instead), so
        // it just held a synthetic strafe key indefinitely: no real forward progress ("自動移動不
        // 會動"), and since GameKeyInput uses real OS-level SendInput key state, that stuck key kept
        // fighting the player's own manual WASD input the whole time too ("手動移動時他又會阻止我
        // 動"). Hold W together with A/D to curve the run direction instead — same combined-
        // movement pattern already proven on Cliffhanger, self-correcting every frame against the
        // real updated position regardless of rotation-reading ambiguity. The now-unused "反轉轉
        // 向" checkbox is left in Configuration but no longer read here; A/D direction was never the
        // actual problem.
        var needsCorrection = MathF.Abs(angleDiff) > TurnThresholdRadians;
        HoldKeys(needsCorrection
            ? (System.Collections.Generic.IEnumerable<int>)[GameKeyInput.VK_W, angleDiff > 0 ? GameKeyInput.VK_A : GameKeyInput.VK_D]
            : [GameKeyInput.VK_W]);

        // Jump based on observing the recorded blue track (gapAhead — see SelectSteeringWaypoint)
        // rather than a blind fixed interval, per user feedback ("跳躍不能定時跳 觀察藍色軌跡跳
        // 躍"). The throttle here only guards against re-tapping jump every single frame while
        // gapAhead stays true across a multi-frame approach, not a periodic timer.
        if (gapAhead && EzThrottler.Throttle("Saucy.LeapOfFaith.Jump", 800))
        {
            GameKeyInput.TapKey(GameKeyInput.VK_SPACE);
        }
    }
}
