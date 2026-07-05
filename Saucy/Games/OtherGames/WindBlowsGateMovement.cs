using ECommons.GameHelpers;
using Saucy.Framework;
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

    // "按鈕有效 但沒有移動到精準點" — vnavmesh's own path-follow considers itself "arrived" at a
    // coarser tolerance than the sub-meter precision SafeSpot.On actually needs (distance <
    // 0.00025), so it stops well short and never gets asked to close the last stretch. Once within
    // this radius, stop relying on vnavmesh (whose resolved/floor-snapped destination can itself
    // already be off by up to a meter — see ResolveDestination) and hand off to manual key-steering
    // aimed at the exact raw destination instead.
    private const float ManualApproachRadius = 3f;

    public static bool TryMoveTo(Vector3 destination, float closeRange = 0.25f)
    {
        if (!Vnavmesh.IsInstalled)
        {
            return false;
        }

        if (Vnavmesh.IsWithinHorizontalRange(destination, closeRange))
        {
            PreciseMovement.SetDesiredDirection(null);
            ReleaseIfOwned();
            return true;
        }

        if (Vnavmesh.IsWithinHorizontalRange(destination, ManualApproachRadius))
        {
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }
            ReleaseIfOwned();
            SteerPrecisely(destination);
            return true;
        }

        var pathDestination = ResolveDestination(destination);
        if (pathDestination == null)
        {
            ReleaseIfOwned();
            return false;
        }

        // IsOnPlatform used to gate every call here (require the player already be within
        // PlatformRadius of PlatformCenter before allowing any move toward the safe spot) — but
        // PlatformRadius (3.5 units) is barely bigger than the safe spot itself, while the player
        // actually starts the storm scattered anywhere across the much larger arena. That made this
        // silently refuse to ever start moving for basically the whole approach — a chicken-and-egg
        // deadlock (confirmed by ForceMoveTo, which bypasses this exact check, working where this
        // didn't — "沒有自動移動到安全點"). Drop the gate; ResolveDestination's own known-good
        // fallback destination is enough of a safety net on its own.
        if (Vnavmesh.IsWithinHorizontalRange(pathDestination.Value, closeRange))
        {
            ReleaseIfOwned();
            return true;
        }

        if (_ownsPath)
        {
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

    /// <summary>Bypasses IsOnPlatform/ResolveDestination entirely and calls vnavmesh directly —
    /// for the "強制移動測試" button, since the normal TryMoveTo silently no-ops (never even
    /// attempts a move) whenever IsOnPlatform reads false, which could itself be the very thing
    /// broken and needs isolating from ("還是沒移動" after every other fix still applied).</summary>
    public static bool ForceMoveTo(Vector3 destination, float closeRange)
    {
        if (!Vnavmesh.IsInstalled)
        {
            return false;
        }

        if (Vnavmesh.IsWithinHorizontalRange(destination, closeRange))
        {
            PreciseMovement.SetDesiredDirection(null);
            return true;
        }

        if (Vnavmesh.IsWithinHorizontalRange(destination, ManualApproachRadius))
        {
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }
            SteerPrecisely(destination);
            return true;
        }

        return Vnavmesh.TryMoveTo(destination, false, closeRange);
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
