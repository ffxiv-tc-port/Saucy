using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
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

    // Live trail of the player's own path this run, drawn on screen the same way as Leap of
    // Faith's — automatic, separate from the manual export-to-JSON recorder.
    private const int MaxTrailPoints = 400;
    private const int TrailSampleIntervalMs = 200;
    private static readonly List<Vector3> ownTrail = [];
    private static DateTime lastTrailSampleUtc;
    private static bool wasInGate;

    public static IReadOnlyList<Vector3> OwnTrail => ownTrail;

    // Show a bomb's marker/blast circle only for a short window after it first appears, rather
    // than for its whole (possibly long) lifetime — the actual danger moment is right as it
    // spawns/telegraphs, per feedback ("炸彈出現時標示 3秒後移除"). Tunable since the real
    // telegraph-to-explosion timing is unconfirmed.
    private static readonly Dictionary<ulong, DateTime> bombFirstSeenUtc = [];

    public static void OnUpdate()
    {
        if (GateDirector.InSaucer && GateDirector.IsPlayerOnStage())
        {
            LastObservedGateType = GateDirector.GetCurrentGate();
        }

        var inGate = GateDirector.IsInGate(global::Saucy.Framework.Module.GateType.Cliffhanger);
        if (inGate && !wasInGate)
        {
            ownTrail.Clear();
        }
        wasInGate = inGate;

        if (!inGate || !Player.Available)
        {
            CurrentTargetPosition = null;
            NearestBombPosition = null;
            GameKeyInput.ReleaseHeldKey();
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

        if (!C.GoldSaucerGates.CliffhangerAutoMovement || CurrentTargetPosition is not { } target)
        {
            GameKeyInput.ReleaseHeldKey();
            return;
        }

        SteerToward(target);
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

            if (obj.DataId == ChickDataId)
            {
                var dist = Vector3.Distance(obj.Position, playerPos);
                if (dist < nearestChickDist)
                {
                    nearestChick = obj;
                    nearestChickDist = dist;
                }
            }
            else if (obj.DataId == BombDataId)
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
                }
            }
        }

        CurrentTargetPosition = nearestChick?.Position;
        NearestBombPosition = nearestBomb?.Position;
        AllBombPositions = bombPositions;
    }

    private static void SteerToward(Vector3 target)
    {
        // If a bomb is close, steer away from it instead of toward the chick — surviving takes
        // priority over rescue speed since a bomb hit likely knocks the player off the stage.
        var steerTarget = target;
        if (NearestBombPosition is { } bomb && Vector3.Distance(Player.Position, bomb) < BombAvoidRadius)
        {
            var awayFromBomb = Player.Position - bomb;
            awayFromBomb.Y = 0;
            if (awayFromBomb.LengthSquared() > 0.01f)
            {
                steerTarget = Player.Position + Vector3.Normalize(awayFromBomb) * 5f;
            }
        }

        var toTarget = steerTarget - Player.Position;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() < 0.01f)
        {
            GameKeyInput.ReleaseHeldKey();
            return;
        }
        toTarget = Vector3.Normalize(toTarget);

        var rotation = C.GoldSaucerGates.CliffhangerInvertTurn ? -Player.Rotation : Player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation));

        var cross = (forward.X * toTarget.Z) - (forward.Z * toTarget.X);
        var dot = Math.Clamp(Vector3.Dot(forward, toTarget), -1f, 1f);
        var angleDiff = MathF.Acos(dot) * MathF.Sign(cross == 0 ? 1 : cross);

        // No real floor/collision detection here — walking straight toward a target or straight
        // away from a bomb can walk the player off a ledge (confirmed live: "他跳樓了"). If
        // vnavmesh is installed, refuse to press forward when there's no landable floor a couple
        // meters ahead in the direction we're about to move; otherwise fall back to the old
        // (unsafe) behavior since we have no other way to know where the edges are.
        var isTurning = MathF.Abs(angleDiff) > TurnThresholdRadians;
        if (!isTurning && Vnavmesh.IsInstalled)
        {
            var aheadPoint = Player.Position + (forward * 2.5f);
            if (Vnavmesh.TryGetPointOnFloor(aheadPoint, allowUnlandable: false, halfExtentXz: 1.5f) is not { } floorPoint ||
                MathF.Abs(floorPoint.Y - Player.Position.Y) > 2f)
            {
                GameKeyInput.ReleaseHeldKey();
                return;
            }
        }

        GameKeyInput.SetHeldKey(isTurning
            ? (angleDiff > 0 ? GameKeyInput.VK_A : GameKeyInput.VK_D)
            : GameKeyInput.VK_W);
    }
}
