using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.EzSharedDataManager;
using System;
using System.Collections.Generic;
namespace Saucy.IPC;

internal static class YesAlready
{
    private const string StopRequestsKey = "YesAlready.StopRequests";

    private const string InternalName = "YesAlready";

    /// <summary><c>Func&lt;string, int, bool&gt;</c>：把 YesAlready 的單一個功能暫停幾毫秒，時間到它自己恢復。</summary>
    private const string PauseBotherChannel = "YesAlready.PauseBother";

    /// <summary>YesAlready 選 SelectIconString 清單選項的那個功能（它的類別簡名）。</summary>
    private const string ListSelectionFeature = "SelectIconString";

    /// <summary>YesAlready 選 SelectString 選單選項的那個功能（它的類別簡名）。</summary>
    private const string SelectStringFeature = "SelectString";

    /// <summary>YesAlready 在仙人仙彩購票面板 PostSetup 當下送號碼的那個功能（它的類別簡名）。</summary>
    private const string LotteryWeeklyInputFeature = "LotteryWeeklyInput";

    /// <summary>YesAlready 依文字規則按 SelectYesno「是」的那個功能（它的類別簡名）；它在確認框 PostSetup 當下就按。</summary>
    private const string SelectYesnoFeature = "SelectYesno";

    private static bool pausedBySaucy;

    private static ICallGateSubscriber<string, int, bool>? pauseBotherSubscriber;

    /// <summary>請 YesAlready 在這段時間不要選 SelectIconString 清單選項。只在 framework 執行緒呼叫。</summary>
    /// <returns>true＝確定這段時間它不會選（已暫停、該功能本來就關著、或它根本沒載入）；false＝問不到，不能這樣假設。</returns>
    /// <remarks>對方回 false 的意思是「找不到這個功能或它本來就關著」；功能若被改名，這個判斷會失準。</remarks>
    public static bool TryPauseListSelection(int milliseconds) =>
        TryPauseFeature(ListSelectionFeature, "清單選項", milliseconds);

    /// <summary>同 <see cref="TryPauseListSelection"/>，對象換成 SelectString 選單（例如仙人仙彩發放員的「要做什麼？」）。</summary>
    public static bool TryPauseSelectString(int milliseconds) =>
        TryPauseFeature(SelectStringFeature, " SelectString 選單選項", milliseconds);

    /// <summary>同 <see cref="TryPauseListSelection"/>，對象換成 YesAlready 的仙人仙彩自動填號（開窗當下就送號碼的那個功能）。</summary>
    /// <remarks>只是限時暫停，不改使用者在 YesAlready 的設定；YesAlready 沒有 IPC 讀得到那個設定本身是開是關。</remarks>
    public static bool TryPauseLotteryWeeklyInput(int milliseconds) =>
        TryPauseFeature(LotteryWeeklyInputFeature, "仙人仙彩自動填號", milliseconds);

    /// <summary>同 <see cref="TryPauseListSelection"/>，對象換成 SelectYesno 確認框；要在確認框出現<b>之前</b>呼叫才有用。</summary>
    public static bool TryPauseSelectYesno(int milliseconds) =>
        TryPauseFeature(SelectYesnoFeature, "是／否確認框", milliseconds);

    public static bool IsLoaded =>SubscriptionManager.IsLoaded(InternalName);

    private static bool TryPauseFeature(string feature, string label, int milliseconds)
    {
        try
        {
            pauseBotherSubscriber ??= Svc.PluginInterface.GetIpcSubscriber<string, int, bool>(PauseBotherChannel);
            var paused = pauseBotherSubscriber.InvokeFunc(feature, milliseconds);
            Svc.Log.Information($"[Saucy] 請 YesAlready 暫停{label} {milliseconds} 毫秒：{(paused ? "已暫停" : "該功能本來就沒啟用")}");
            return true;
        }
        catch (IpcNotReadyError)
        {
            // 沒載入＝不會按；載入了卻沒有這個端點＝版本不明，不能假設它不會按。
            return !SubscriptionManager.IsLoaded(InternalName);
        }
        catch (Exception ex)
        {
            Svc.Log.Information(ex, $"[Saucy] 無法請 YesAlready 暫停{label}功能");
            return false;
        }
    }

    public static void SyncForGameActivity(bool gamePlaying)
    {
        if (gamePlaying)
        {
            if (!pausedBySaucy)
            {
                Lock();
                pausedBySaucy = true;
            }

            return;
        }

        ResumeIfPausedBySaucy();
    }

    public static void ResumeIfPausedBySaucy()
    {
        if (!pausedBySaucy)
        {
            return;
        }

        pausedBySaucy = false;
        Unlock();
    }

    private static void Lock()
    {
        if (EzSharedData.TryGet<HashSet<string>>(StopRequestsKey, out var data))
        {
            data.Add(Svc.PluginInterface.Manifest.InternalName);
        }
    }

    private static void Unlock()
    {
        if (EzSharedData.TryGet<HashSet<string>>(StopRequestsKey, out var data))
        {
            data.Remove(Svc.PluginInterface.Manifest.InternalName);
        }
    }
}
