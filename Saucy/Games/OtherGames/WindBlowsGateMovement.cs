using ECommons.GameHelpers;
using Saucy.IPC;
using System;
using System.Numerics;
namespace Saucy.OtherGames;

internal static class WindBlowsGateMovement
{
    private const float FloorSnapHalfExtent = 1.5f;
    private const float MaxSnapDrift = 1f;
    private const float PlatformYTolerance = 0.35f;

    private static bool _ownsPath;
    private static Vector3? _snappedDestination;

    public static bool TryMoveTo(Vector3 destination, float closeRange = 0.25f)
    {
        if (!Vnavmesh.IsInstalled)
        {
            return false;
        }

        var pathDestination = ResolveDestination(destination);
        if (pathDestination == null)
        {
            ReleaseIfOwned();
            return false;
        }

        if (!IsOnPlatform(Player.Position))
        {
            ReleaseIfOwned();
            return false;
        }

        if (Vnavmesh.IsWithinHorizontalRange(pathDestination.Value, closeRange))
        {
            ReleaseIfOwned();
            return true;
        }

        if (_ownsPath)
        {
            if (!IsOnPlatform(Player.Position))
            {
                ReleaseIfOwned();
                return false;
            }

            return Vnavmesh.IsMoving() || Vnavmesh.TryMoveTo(pathDestination.Value, false, closeRange);
        }

        if (Vnavmesh.IsMoving())
        {
            return false;
        }

        if (!Vnavmesh.TryMoveTo(pathDestination.Value, false, closeRange))
        {
            return false;
        }

        _ownsPath = true;
        return true;
    }

    public static void ReleaseIfOwned()
    {
        if (!_ownsPath || !Vnavmesh.IsInstalled)
        {
            _ownsPath = false;
            _snappedDestination = null;
            return;
        }

        _ownsPath = false;
        _snappedDestination = null;
        Vnavmesh.StopPath();
    }

    private static Vector3? ResolveDestination(Vector3 destination)
    {
        if (_snappedDestination is { } cached)
        {
            return cached;
        }

        // vnavmesh's baked navmesh doesn't reliably cover this GATE's platform either (same
        // issue already confirmed on Leap of Faith's dynamic platforms — a live diagnostic there
        // showed a 5.6-unit Y mismatch). Requiring a successful floor snap here meant this never
        // moved at all once vnavmesh had no floor data for the spot ("有顯示安全點標記 但沒有導航"
        // — the marker draw doesn't depend on vnavmesh, so it kept showing while movement silently
        // never started). Trust the known-good hardcoded destination directly when snapping fails,
        // rather than refusing to move.
        var snapped = Vnavmesh.TryGetPointOnFloor(destination, halfExtentXz: FloorSnapHalfExtent);
        if (snapped == null)
        {
            _snappedDestination = destination;
            return destination;
        }

        var drift = snapped.Value - destination;
        if ((drift.X * drift.X) + (drift.Z * drift.Z) > MaxSnapDrift * MaxSnapDrift)
        {
            _snappedDestination = destination;
            return destination;
        }

        _snappedDestination = snapped;
        return snapped;
    }

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
