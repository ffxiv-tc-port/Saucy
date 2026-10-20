using Dalamud.Plugin.Ipc;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.JumboCactpot;
using Saucy.MiniCactpot;
using Saucy.TripleTriad;
using System;
using System.Collections.Generic;
using System.Threading;
namespace Saucy.IPC;

/// <summary>急停端點 <c>Saucy.StopAll</c>（Action&lt;string&gt;，參數是呼叫端署名，寫進記錄）。</summary>
/// <remarks>任何執行緒都可以呼叫、不會擲例外給呼叫端：在 framework 執行緒上當場停；其他執行緒只排隊，下一幀由 <see cref="ProcessPending"/> 停。
/// 停止點與使用者手動停止相同（<c>/saucy stop</c>、金碟導航／仙人微彩／仙人仙彩的「停止」按鈕），外加 GATE 手動觸發的移動；不改、不存設定。
/// 刻意只提供停止、不提供開始：開始一律要使用者自己按。</remarks>
internal sealed class StopAllIpc : IDisposable
{
    internal const string EndpointName = "Saucy.StopAll";

    private readonly ICallGateProvider<string, object> provider;

    // 其他執行緒排進來的停止要求（呼叫端署名）；null＝沒有。
    private string? pending;

    private volatile bool disposed;

    internal StopAllIpc()
    {
        provider = Svc.PluginInterface.GetIpcProvider<string, object>(EndpointName);
        provider.RegisterAction(Request);
    }

    private void Request(string? source)
    {
        if (disposed)
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(source) ? "?" : source.Trim();
        if (Svc.Framework.IsInFrameworkUpdateThread)
        {
            StopNow(label);
            return;
        }

        Interlocked.Exchange(ref pending, label);
    }

    /// <summary>只在 framework 執行緒呼叫。</summary>
    internal void ProcessPending()
    {
        var label = Interlocked.Exchange(ref pending, null);
        if (label != null && !disposed)
        {
            StopNow(label);
        }
    }

    private static void StopNow(string label)
    {
        try
        {
            var stopped = new List<string>();
            if (TriadRunSession.ModuleEnabled)
            {
                stopped.Add("九宮幻卡自動對戰");
            }

            if (TriadMapNavigation.IsNavigationActive)
            {
                stopped.Add("九宮幻卡 NPC 導航");
            }

            if (GoldSaucerNavigator.IsActive)
            {
                stopped.Add("金碟導航");
            }

            if (MiniCactpotAutoPlay.IsActive)
            {
                stopped.Add("仙人微彩自動遊玩");
            }

            if (JumboCactpotAutoTalk.IsActive)
            {
                stopped.Add("仙人仙彩自動對話");
            }

            if (GateScheduleAutomation.IsReturningAfterGate)
            {
                stopped.Add("GATE 結束後走回活動解說員");
            }

            if (global::Saucy.LeapOfFaith.LeapOfFaithQuickExit.IsActive)
            {
                stopped.Add("登高跳跳樂提早退出");
            }

            // 無論旗標如何都走一次：每個停止點都是冪等的，而且只收自己發起的導航與 AI。
            TriadRunSession.StopAllAutomation(announce: false);
            GoldSaucerNavigator.Cancel();
            MiniCactpotAutoPlay.Cancel();
            JumboCactpotAutoTalk.Cancel();
            if (GateScheduleAutomation.CancelManualTriggers())
            {
                stopped.Add("GATE 手動觸發的移動／報名");
            }

            var holdUntil = GateEmergencyHold.Begin(DateTime.Now);
            var what = stopped.Count == 0 ? "沒有偵測到執行中的自動化（各停止點仍走過一次）" : "已停止 " + string.Join("、", stopped);
            Svc.Log.Information($"[Saucy] 收到急停（{label}）：{what}；GATE 定時流程與 GATE 模組壓到 {holdUntil:HH:mm} 不自動重新開始（手動按開始即解除）。");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[Saucy] 急停（{label}）時發生例外");
        }
    }

    public void Dispose()
    {
        disposed = true;
        provider.UnregisterAction();
    }
}
