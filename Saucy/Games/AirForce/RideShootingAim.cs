using System;
using System.Numerics;
using System.Runtime.InteropServices;
namespace Saucy.AirForce;

internal static unsafe class RideShootingAim
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    /// <summary>
    /// Aiming in this minigame follows the real OS mouse cursor, not the internal
    /// Handler.AimScreenX/Y struct field (confirmed: that field reads live, plausible values but
    /// writing it has no effect on where shots land — user confirmed aim tracks actual mouse
    /// movement). Move the real cursor instead of writing game memory.
    /// </summary>
    public static bool TrySetScreenAim(Vector2 screen) =>
        SetCursorPos((int)screen.X, (int)screen.Y);

    public static bool VerifyLayoutParity(out string detail)
    {
        var agent = AgentRideShooting.TryGet();
        if (agent == null)
        {
            detail = "RideShooting agent is null (not in duty?)";
            return true;
        }

        var agentPtr = (nint)agent;
        var legacyHandler = *(nint*)(agentPtr + 0x30);
        var typedHandler = (nint)agent->Handler;
        if (legacyHandler != typedHandler)
        {
            detail = $"Handler pointer mismatch: legacy=0x{legacyHandler:X}, typed=0x{typedHandler:X}";
            return false;
        }

        if (legacyHandler == 0)
        {
            detail = "Handler is null";
            return true;
        }

        var legacyX = *(float*)(legacyHandler + 0xC70);
        var legacyY = *(float*)(legacyHandler + 0xC74);
        var typedX = agent->Handler->AimScreenX;
        var typedY = agent->Handler->AimScreenY;
        if (Math.Abs(legacyX - typedX) > 0.001f || Math.Abs(legacyY - typedY) > 0.001f)
        {
            detail = $"Aim mismatch: legacy=({legacyX:F1},{legacyY:F1}) typed=({typedX:F1},{typedY:F1})";
            return false;
        }

        detail = $"OK — aim ({typedX:F1}, {typedY:F1})";
        return true;
    }

    public static bool TryReadAim(out Vector2 aim)
    {
        aim = default;
        var agent = AgentRideShooting.TryGet();
        var handler = agent != null ? agent->Handler : null;
        if (handler == null)
        {
            return false;
        }

        aim = new(handler->AimScreenX, handler->AimScreenY);
        return true;
    }
}
