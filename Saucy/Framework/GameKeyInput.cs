using System;
using System.Runtime.InteropServices;
namespace Saucy.Framework;

/// <summary>
/// Confirmed live: jumping (Space) worked via SendMessage(WM_KEYDOWN/UP) to the FFXIVGAME window,
/// but held movement (W/A/D) never actually moved the character — only spun/jumped in place. This
/// points to FFXIV reading discrete actions (jump) through the window message queue but continuous
/// movement through real keyboard hardware state (GetAsyncKeyState / raw input), which
/// SendMessage/PostMessage never touches since they only enqueue a message rather than updating
/// the OS's actual key-state table. SendInput does update that global state (it's the same API
/// real input-injection tools use), so it's used here instead — for both movement and jump, since
/// jump additionally needs to work while a movement key is already "held" via SendInput. As a
/// safety measure (this simulates real global keyboard state, not a message to a specific window),
/// key sends are skipped unless the FFXIVGAME window is currently the OS foreground window.
/// </summary>
internal static class GameKeyInput
{
    [DllImport("user32.dll")]
    private static extern nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint MAPVK_VK_TO_VSC = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
        // KEYBDINPUT is the largest union member we use; padding to match MOUSEINPUT/HARDWAREINPUT
        // size (both 24 bytes on x64) isn't needed since we never populate those variants, but the
        // struct must still report the OS-expected total size for SendInput's cbSize parameter.
        private readonly long padding;
    }

    public const int VK_W = 0x57;
    public const int VK_A = 0x41;
    public const int VK_D = 0x44;
    public const int VK_SPACE = 0x20;

    private static int? heldKey;

    /// <summary>Call every tick with the desired held key (or null for none). Handles releasing the
    /// previous key when the direction changes and re-pressing every tick while held.</summary>
    public static void SetHeldKey(int? key)
    {
        if (heldKey == key)
        {
            if (key is { } k)
            {
                SendKey(k, keyUp: false);
            }
            return;
        }

        if (heldKey is { } previous)
        {
            SendKey(previous, keyUp: true);
        }

        heldKey = key;
        if (key is { } newKey)
        {
            SendKey(newKey, keyUp: false);
        }
    }

    public static void ReleaseHeldKey() => SetHeldKey(null);

    public static void TapKey(int vk)
    {
        SendKey(vk, keyUp: false);
        SendKey(vk, keyUp: true);
    }

    private static void SendKey(int vk, bool keyUp)
    {
        if (!IsGameForeground())
        {
            return;
        }

        var scanCode = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        var flags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = 0, wScan = scanCode, dwFlags = flags, time = 0, dwExtraInfo = nint.Zero }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static bool IsGameForeground()
    {
        var fg = GetForegroundWindow();
        return fg != nint.Zero && TryFindGameWindow(out var gameHwnd) && fg == gameHwnd;
    }

    private static bool TryFindGameWindow(out nint hwnd)
    {
        hwnd = nint.Zero;
        var current = nint.Zero;
        while ((current = FindWindowEx(nint.Zero, current, "FFXIVGAME", null)) != nint.Zero)
        {
            GetWindowThreadProcessId(current, out var pid);
            if (pid == Environment.ProcessId)
            {
                hwnd = current;
                return true;
            }
        }

        return false;
    }
}
