using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.WindowsFormsReflector;
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
    // marker is named "終點" under DataId 2009601.
    private const uint FinishDataId = 2009601;
    private static readonly uint[] CactuarDataIds = [2009588, 2009589, 2009590];

    private const float ScanRadius = 80f;
    private const float TurnThresholdRadians = 0.14f; // ~8 degrees
    private const int KeyTapIntervalMs = 90;

    public static Vector3? CurrentTargetPosition { get; private set; }
    public static bool CurrentTargetIsFinish { get; private set; }
    public static bool CurrentTargetIsCactuar { get; private set; }
    public static global::Saucy.Framework.Module.GateType LastObservedGateType { get; private set; } =
        global::Saucy.Framework.Module.GateType.None;

    private static Vector3? startPosition;
    private static bool wasInGate;

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
            return;
        }

        if (!Player.Available)
        {
            CurrentTargetPosition = null;
            return;
        }

        LeapOfFaithPlatformObserver.Observe();
        FindTarget();

        if (!C.GoldSaucerGates.LeapOfFaithAutoMovement || CurrentTargetPosition is not { } target)
        {
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
            if (dist > ScanRadius)
            {
                continue;
            }

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
            if (Vector3.Distance(start, candidate) <= playerProgress)
            {
                continue;
            }

            var dist = Vector3.Distance(playerPos, candidate);
            if (dist < bestDist)
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return best;
    }

    private static void SteerToward(Vector3 target)
    {
        if (!EzThrottler.Throttle("Saucy.LeapOfFaith.Steer", KeyTapIntervalMs))
        {
            return;
        }

        var toTarget = target - Player.Position;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() < 0.01f)
        {
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

        if (MathF.Abs(angleDiff) > TurnThresholdRadians)
        {
            _ = WindowsKeypress.SendKeypress(angleDiff > 0 ? Keys.A : Keys.D);
        }
        else
        {
            _ = WindowsKeypress.SendKeypress(Keys.W);
        }

        if (EzThrottler.Throttle("Saucy.LeapOfFaith.Jump", (int)(C.GoldSaucerGates.LeapOfFaithJumpIntervalSeconds * 1000)))
        {
            _ = WindowsKeypress.SendKeypress(Keys.Space);
        }
    }
}
