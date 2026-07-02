using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Saucy.Framework;
using System;
using System.Linq;
using System.Numerics;
namespace Saucy.LeapOfFaith;

internal static class LeapOfFaithDetection
{
    // Confirmed via live diagnostic: Leap of Faith does NOT create a GoldSaucerManager
    // GFateDirector like Wind Blows/Any Way the Wind Blows does — "目前沒有作用中的 GATE 導演"
    // stayed true throughout an actual run with visible cactuar-collection progress. Instead it
    // sets ConditionFlag 56 (BoundByDuty56), found by dumping every active ConditionFlag while
    // mid-run. Distinct from Air Force One's BoundByDuty95.
    public static bool IsActive => Svc.Condition[ConditionFlag.BoundByDuty56];
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

    // Live trail of the player's own path this run, for the "玩家路徑" line overlay in
    // LeapOfFaithModule — separate from the manual export-to-JSON recorder, this is automatic and
    // just for on-screen reference while playing. Capped so a very long run doesn't grow unbounded.
    private const int MaxTrailPoints = 400;
    private const int TrailSampleIntervalMs = 200;
    private static readonly System.Collections.Generic.List<Vector3> ownTrail = [];
    private static DateTime lastTrailSampleUtc;

    public static System.Collections.Generic.IReadOnlyList<Vector3> OwnTrail => ownTrail;

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
            GameKeyInput.ReleaseHeldKey();
            return;
        }

        if (!Player.Available)
        {
            CurrentTargetPosition = null;
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

        if (!C.GoldSaucerGates.LeapOfFaithAutoMovement || CurrentTargetPosition is not { } target)
        {
            GameKeyInput.ReleaseHeldKey();
            return;
        }

        SteerToward(target);
    }

    private static void FindTarget()
    {
        var playerPos = Player.Position;

        IGameObject? finish = null;
        IGameObject? nearestCactuar = null;
        var nearestCactuarDist = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventObj)
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
        }
        else if (nearestCactuar != null)
        {
            CurrentTargetPosition = nearestCactuar.Position;
            CurrentTargetIsFinish = false;
            CurrentTargetIsCactuar = true;
        }
        else
        {
            CurrentTargetPosition = FindPlatformFallbackTarget(playerPos);
            CurrentTargetIsFinish = false;
            CurrentTargetIsCactuar = false;
        }
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

    private static void SteerToward(Vector3 target)
    {
        var toTarget = target - Player.Position;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() < 0.01f)
        {
            GameKeyInput.ReleaseHeldKey();
            return;
        }
        toTarget = Vector3.Normalize(toTarget);

        // Standard FFXIV facing-vector convention: forward = (sin(rotation), 0, cos(rotation)).
        // Not verified against this build in-game yet — if auto-movement turns the wrong way,
        // flip the "反轉轉向" option in the panel rather than editing this.
        var rotation = C.GoldSaucerGates.LeapOfFaithInvertTurn ? -Player.Rotation : Player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation));

        var cross = (forward.X * toTarget.Z) - (forward.Z * toTarget.X);
        var dot = Math.Clamp(Vector3.Dot(forward, toTarget), -1f, 1f);
        var angleDiff = MathF.Acos(dot) * MathF.Sign(cross == 0 ? 1 : cross);

        GameKeyInput.SetHeldKey(MathF.Abs(angleDiff) > TurnThresholdRadians
            ? (angleDiff > 0 ? GameKeyInput.VK_A : GameKeyInput.VK_D)
            : GameKeyInput.VK_W);

        if (EzThrottler.Throttle("Saucy.LeapOfFaith.Jump", (int)(C.GoldSaucerGates.LeapOfFaithJumpIntervalSeconds * 1000)))
        {
            GameKeyInput.TapKey(GameKeyInput.VK_SPACE);
        }
    }
}
