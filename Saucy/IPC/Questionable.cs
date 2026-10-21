using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Globalization;
namespace Saucy.IPC;

[IPC(IPCNames.Questionable)]
internal static class Questionable
{
    [EzIPC]
    public static Func<string, bool> StartSingleQuest = null!;

    [EzIPC]
    public static Func<string, bool> IsQuestLocked = null!;

    [EzIPC]
    public static Func<string, bool> IsReadyToAcceptQuest = null!;

    [EzIPC]
    public static Func<string, bool> IsQuestComplete = null!;

    [EzIPC]
    public static Func<string, bool> IsQuestAccepted = null!;

    [EzIPC]
    public static Func<string, bool> IsQuestUnobtainable = null!;

    [EzIPC]
    public static Func<bool> IsRunning = null!;

    /// <summary>true＝已開始；false＝不開始（原因由 Questionable 自己寫 log）。舊版沒有這個端點。</summary>
    [EzIPC]
    public static Func<bool> StartAlliedSocietyDailies = null!;

    public static bool IsInstalled => SubscriptionManager.IsInitialized(IPCNames.Questionable);

    public enum AlliedDailiesStart : byte
    {
        Started,
        Declined,
        Unavailable
    }

    /// <summary>請 Questionable 開始友好部族日常。只在 framework 執行緒呼叫。</summary>
    /// <param name="detail">呼叫不到時的原因；其他情況是空字串。</param>
    public static AlliedDailiesStart TryStartAlliedSocietyDailies(out string detail)
    {
        detail = string.Empty;
        if (!IsInstalled || StartAlliedSocietyDailies is null)
        {
            detail = "沒有載入 Questionable";
            return AlliedDailiesStart.Unavailable;
        }

        // SafeWrapper.None：型別對不上（IpcTypeMismatchError）與提供端自己擲的例外都會原樣冒上來，全部在這裡攔。
        try
        {
            return StartAlliedSocietyDailies() ? AlliedDailiesStart.Started : AlliedDailiesStart.Declined;
        }
        catch (IpcNotReadyError)
        {
            detail = "這版 Questionable 沒有提供這個端點（需要更新 Questionable）";
        }
        catch (IpcError ex)
        {
            detail = $"IPC 錯誤 {ex.GetType().Name}：{ex.Message}";
        }
        catch (Exception ex)
        {
            detail = $"Questionable 擲出例外 {ex.GetType().Name}：{ex.Message}";
        }

        return AlliedDailiesStart.Unavailable;
    }

    public static bool IsQuestingNow()
    {
        if (!IsInstalled || IsRunning is null)
        {
            return false;
        }

        try
        {
            return IsRunning();
        }
        catch
        {
            return false;
        }
    }
}

internal static class QuestionableTriad
{
    private static readonly HashSet<uint> QuestsWithoutPath = [];

    public static void ClearUnsupportedCache() => QuestsWithoutPath.Clear();

    public static bool HasAutomationPath(uint luminaQuestRowId)
    {
        if (!Questionable.IsInstalled || luminaQuestRowId == 0)
        {
            return true;
        }

        if (QuestsWithoutPath.Contains(luminaQuestRowId))
        {
            return false;
        }

        return !(IsQuestLocked(luminaQuestRowId) && IsReadyToAccept(luminaQuestRowId));
    }

    public static bool IsQuestComplete(uint luminaQuestRowId) =>
        InvokeBool(Questionable.IsQuestComplete, luminaQuestRowId);

    public static bool IsQuestAccepted(uint luminaQuestRowId) =>
        InvokeBool(Questionable.IsQuestAccepted, luminaQuestRowId);

    public static bool IsReadyToAccept(uint luminaQuestRowId) =>
        InvokeBool(Questionable.IsReadyToAcceptQuest, luminaQuestRowId);

    public static bool IsQuestLocked(uint luminaQuestRowId) =>
        InvokeBool(Questionable.IsQuestLocked, luminaQuestRowId);

    public static bool IsQuestUnobtainable(uint luminaQuestRowId) =>
        InvokeBool(Questionable.IsQuestUnobtainable, luminaQuestRowId);

    public static bool TryStartSingleQuest(uint luminaQuestRowId)
    {
        if (!Questionable.IsInstalled || luminaQuestRowId == 0)
        {
            return false;
        }

        if (!Questionable.StartSingleQuest.TryInvoke(FormatQuestId(luminaQuestRowId), out var started))
        {
            return false;
        }

        if (!started)
        {
            QuestsWithoutPath.Add(luminaQuestRowId);
        }

        return started;
    }

    private static string FormatQuestId(uint luminaQuestRowId) =>
        ((ushort)(luminaQuestRowId & 0xFFFF)).ToString(CultureInfo.InvariantCulture);

    private static bool InvokeBool(Func<string, bool> ipc, uint luminaQuestRowId)
    {
        if (!Questionable.IsInstalled || luminaQuestRowId == 0)
        {
            return false;
        }

        return ipc.TryInvoke(FormatQuestId(luminaQuestRowId), out var ret) && ret;
    }
}
