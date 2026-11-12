using ECommons.EzIpcManager;
using Saucy.Framework;
using System;
namespace Saucy.IPC;

[IPC(IPCNames.Lifestream)]
internal static class Lifestream
{
    [EzIPC]
    public static Func<uint, byte, bool> Teleport = null!;

    [EzIPC]
    public static Func<bool> IsBusy = null!;

    [EzIPC]
    public static Action Abort = null!;

    [EzIPC]
    public static Action<string> ExecuteCommand = null!;

    public static bool IsInstalled => SubscriptionManager.IsInitialized(IPCNames.Lifestream);

    public static bool IsBusyNow() => IsInstalled && IsBusy();

    /// <summary>送請求時帶的發起者名（中止時要用同一個名字）。</summary>
    public const string OwnerGoldSaucerNavigator = "GoldSaucerNavigator";

    public const string OwnerTriadNavigation = "TriadMapNavigation";

    private static readonly LifestreamOwnership ownership = new();

    /// <summary>每幀呼叫（framework 執行緒）：追蹤 Saucy 自己送的那一趟結束了沒。沒有記錄時不做 IPC。</summary>
    public static void TickOwnership()
    {
        if (ownership.Owner != null)
        {
            ownership.Observe(IsBusyNow(), Environment.TickCount64);
        }
    }

    /// <summary>只中止 <paramref name="owner"/> 自己送出、還沒結束的那一趟；別的插件或使用者開的 Lifestream 流程不碰。回傳有沒有送出中止。</summary>
    public static bool TryAbortOwned(string owner)
    {
        if (!ownership.TryTakeForAbort(owner))
        {
            return false;
        }

        TryAbort();
        return true;
    }

    private static void TryAbort()
    {
        if (!IsInstalled)
        {
            return;
        }

        try
        {
            Abort();
        }
        catch
        {
            // Lifestream not loaded or IPC unavailable.
        }
    }

    public static bool TryTeleport(string owner, uint aetheryteId, byte subIndex = 0)
    {
        if (!IsInstalled || aetheryteId == 0)
        {
            return false;
        }

        var busyBefore = IsBusyNow();
        if (!Teleport.TryInvoke(aetheryteId, subIndex, out var started) || !started)
        {
            return false;
        }

        ownership.NoteRequested(owner, busyBefore, Environment.TickCount64);
        return true;
    }

    public static bool TryAethernetViaLiCommand(string owner, string? destinationName)
    {
        if (!IsInstalled || string.IsNullOrWhiteSpace(destinationName) || IsBusyNow())
        {
            return false;
        }

        ExecuteCommand(destinationName.Trim());
        ownership.NoteRequested(owner, busyBefore: false, Environment.TickCount64);
        return true;
    }
}
