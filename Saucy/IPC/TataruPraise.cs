using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using System;
namespace Saucy.IPC;

/// <summary>
/// 塔塔露誇獎（TataruPraise）的單向通知橋接：Saucy 判定出「中獎」時請它念一句。
///
/// <para>🔴 <b>只出不進、失敗即靜默。</b> 這條 IPC 從頭到尾不影響 Saucy 任何自動化流程：
/// 對方沒安裝、沒載入、還在冷卻、或整個擲例外，呼叫端一律當成「沒念」繼續往下走。</para>
///
/// <para>⚠️ 這裡刻意<b>不</b>走本 repo 其他整合用的 ECommons <c>[IPC]</c> + <see cref="SubscriptionManager"/>
/// 路徑。那條路徑把可用性綁在「<c>InstalledPlugins</c> 裡有這個 InternalName 而且 IsLoaded」上，
/// 對「有裝就順便念一句、沒裝就當沒這回事」的純通知來說是多餘的耦合；而且 TataruPraise 自己
/// 就提供了 <c>IsAvailable</c>（總開關開著＋池裡真的有已合成語音），那才是「現在叫得動嗎」的真值來源。</para>
///
/// <para>📌 契約名與情境鍵逐字取自 TataruPraise 的 <c>IpcContract.cs</c> / <c>PraiseCategory.cs</c>。
/// 🔴 Dalamud 的 CallGate 是<b>純字串比對</b>——這幾個字串打錯不會有任何錯誤訊息，
/// 只會永遠拿到「沒有人註冊」而靜默斷線。改字面前先去對方 repo 確認。</para>
///
/// <para>🔴 <b>只在主執行緒呼叫。</b> 目前的呼叫點都在 framework tick／addon 事件上。</para>
/// </summary>
internal static class TataruPraise
{
    /// <summary>從指定情境的誇獎池挑一句念。<c>Func&lt;string, bool&gt;</c>。</summary>
    public const string PraiseChannel = "TataruPraise.Praise";

    /// <summary>現在有沒有辦法出聲（總開關開著、而且池裡有可播的內容）。<c>Func&lt;bool&gt;</c>。</summary>
    public const string IsAvailableChannel = "TataruPraise.IsAvailable";

    /// <summary>「中獎」情境鍵，逐字對應 TataruPraise 的 <c>PraiseCategory.Jackpot</c>。</summary>
    public const string JackpotCategory = "中獎";

    private static ICallGateSubscriber<string, bool>? praiseSubscriber;
    private static ICallGateSubscriber<bool>? isAvailableSubscriber;

    /// <summary>請塔塔露念一句「中獎」。回傳「有沒有排進播放」——回 false 全部都是正常情形
    /// （沒安裝、總開關關著、還在冷卻、池裡沒有已合成的句子），不是錯誤。</summary>
    public static bool TryPraiseJackpot() => TryPraise(JackpotCategory);

    /// <summary>請塔塔露從指定情境念一句。</summary>
    public static bool TryPraise(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return false;
        }

        try
        {
            // 訂閱端可以在對方載入之前就先取得（CallGate 是後綁的），所以快取起來重用沒有問題。
            praiseSubscriber ??= Svc.PluginInterface.GetIpcSubscriber<string, bool>(PraiseChannel);
            isAvailableSubscriber ??= Svc.PluginInterface.GetIpcSubscriber<bool>(IsAvailableChannel);

            // 先問「叫得動嗎」再叫：對方沒載入時這一步就會擲 IpcNotReadyError，
            // 不會走到 Praise。
            if (!isAvailableSubscriber.InvokeFunc())
            {
                return false;
            }

            return praiseSubscriber.InvokeFunc(category);
        }
        catch (IpcNotReadyError)
        {
            // 沒安裝／還沒載入。這是最常見的「回 false」，連 log 都不必寫。
            return false;
        }
        catch (Exception ex)
        {
            // 型別不合、對方自己擲例外之類。純通知，吞掉就好，但留一行方便查。
            Svc.Log.Debug(ex, $"[Saucy] TataruPraise IPC 呼叫失敗（情境「{category}」），忽略。");
            return false;
        }
    }
}
