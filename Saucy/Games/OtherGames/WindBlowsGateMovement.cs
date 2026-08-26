using ECommons.GameHelpers;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Numerics;
namespace Saucy.OtherGames;

internal static class WindBlowsGateMovement
{
    private const float FloorSnapHalfExtent = 1.5f;
    private const float PlatformYTolerance = 0.35f;

    // "安全點和傳送後位置 是一直線" — the post-teleport spawn and the safe spot always sit on a
    // straight line with nothing in between, so this never needs vnavmesh pathfinding at all. Using
    // Vnavmesh.TryMoveTo previously meant that if it got ticked during the post-join settle window
    // (before the teleport landed), it would plan a path from the stale pre-teleport position and
    // walk the player straight off the arena edge trying to reach it ("報名後傳送還沒結束前 就開始
    //從傳送前位置規劃路徑跳出場外"). Steering directly via PreciseMovement removes that class of
    // bug entirely — there's no path to plan, just a direction to walk.
    public static bool TryMoveTo(Vector3 destination, float closeRange = 0.25f)
    {
        if (Vector3.Distance(Player.Position, destination) <= closeRange)
        {
            PreciseMovement.SetDesiredDirection(null);
            return true;
        }

        SteerPrecisely(destination);
        return true;
    }

    // Switched from GameKeyInput's SendInput-simulated WASD to PreciseMovement, which hooks the
    // game's own movement-input read directly instead of simulating keypresses (same technique
    // BossModReborn uses) — same fix already applied to Cliffhanger after key simulation turned out
    // completely unreliable ("鍵盤模擬 現在完全不能用"). No more manual turn/strafe correction
    // needed; PreciseMovement resolves the world-space direction against facing itself.
    private static void SteerPrecisely(Vector3 destination)
    {
        var toTarget = destination - Player.Position;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() < 0.0001f)
        {
            PreciseMovement.SetDesiredDirection(null);
            return;
        }

        PreciseMovement.SetDesiredDirection(toTarget);
    }

    /// <summary>Same straight-line steering as TryMoveTo — kept as a separate entry point for the
    /// "強制移動測試" button so it stays independent of whatever gating the caller normally
    /// applies (IsInGate, settle window, etc.), for isolating whether movement itself works at
    /// all ("還是沒移動" after every other fix still applied).</summary>
    public static bool ForceMoveTo(Vector3 destination, float closeRange) => TryMoveTo(destination, closeRange);

    public static void ReleaseIfOwned() => PreciseMovement.SetDesiredDirection(null);

    /// <summary>Diagnostic-only mirror of the internal IsOnPlatform gate, for the panel to show
    /// why movement might be refusing to start (e.g. TryMoveTo silently no-ops if this is false).</summary>
    public static bool DebugIsOnPlatform(Vector3 position) => IsOnPlatform(position);

    private static bool IsOnPlatform(Vector3 position)
    {
        if (!IsWithinHorizontalRange(position, AnyWayTheWindBlows.Stage.PlatformCenter, AnyWayTheWindBlows.Stage.PlatformRadius))
        {
            return false;
        }

        // Same vnavmesh-coverage caveat as ResolveDestination above — fall back to the player's
        // own live Y against the known platform floor height instead of requiring a floor query
        // that may never succeed on this platform.
        var snapped = Vnavmesh.TryGetPointOnFloor(position, halfExtentXz: FloorSnapHalfExtent);
        var y = snapped?.Y ?? position.Y;
        return MathF.Abs(y - AnyWayTheWindBlows.Stage.PlatformFloorY) <= PlatformYTolerance;
    }

    private static bool IsWithinHorizontalRange(Vector3 position, Vector3 center, float range)
    {
        var delta = position - center;
        return (delta.X * delta.X) + (delta.Z * delta.Z) <= range * range;
    }
}
