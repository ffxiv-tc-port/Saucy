using Saucy.IPC;
using System;

namespace Saucy.MiniCactpot;

/// <summary>
/// 仙人微彩當天打完之後交棒給 Questionable 友好部族日常的判斷。純邏輯、不碰遊戲、不配置。
/// </summary>
internal static class MiniCactpotQuestionableHandOff
{
    /// <summary>關掉最後一張之後，發放員的道別對話最多等這麼久；過了照樣呼叫（Questionable 自己也會等對話結束）。</summary>
    public const long BusyWaitMs = 10_000;

    /// <summary>呼叫不到 Questionable（重載中、沒載入、太舊）時隔這麼久再試一次。</summary>
    public const long UnavailableRetryMs = 30_000;

    /// <summary>從第一次呼叫起算，呼叫不到的重試只在這段時間內。</summary>
    public const long UnavailableRetryWindowMs = 5 * 60_000;

    public enum PendingStep : byte
    {
        Wait,
        Call,
        Drop
    }

    public enum CallOutcome : byte
    {
        /// <summary>Questionable 收到了（開始或拒絕）：記成這個角色這個遊戲日已交棒。</summary>
        Record,

        /// <summary>沒收到：不記，<see cref="UnavailableRetryMs"/> 後再試。</summary>
        Retry,

        /// <summary>沒收到而且重試時間用完：不記、不再排。</summary>
        GiveUp
    }

    /// <summary>呼叫之後怎麼處理；只有 Questionable 真的收到才算用掉當天那一次。</summary>
    /// <param name="sinceFirstTryMs">距離這一輪第一次呼叫的毫秒數（第一次呼叫傳 0）。</param>
    public static CallOutcome AfterCall(Questionable.AlliedDailiesStart result, long sinceFirstTryMs) => result switch
    {
        Questionable.AlliedDailiesStart.Started or Questionable.AlliedDailiesStart.Declined => CallOutcome.Record,
        _ => sinceFirstTryMs + UnavailableRetryMs <= UnavailableRetryWindowMs ? CallOutcome.Retry : CallOutcome.GiveUp
    };

    /// <summary>關掉一張彩券面板的當下要不要排交棒。</summary>
    /// <param name="ticketFinished">這張面板九格都翻開過（不是玩到一半關掉）。</param>
    /// <param name="lastCalledUtc">這個角色上一次呼叫的時間；null＝沒呼叫過。</param>
    /// <param name="lastDailyResetUtc">最近一次每日重置。</param>
    public static bool ShouldArm(bool enabled, bool ticketFinished, bool remainingKnown, int remaining,
                                 DateTime? lastCalledUtc, DateTime lastDailyResetUtc) =>
        enabled && ticketFinished && remainingKnown && remaining == 0 &&
        !(lastCalledUtc is { } called && called >= lastDailyResetUtc);

    /// <summary>已排交棒之後的每一幀。</summary>
    /// <param name="stillEnabled">開關與模組都還開著。</param>
    /// <param name="ticketPanelOpen">又開了一張彩券面板＝今天其實還沒打完。</param>
    /// <param name="busy">還在對話、事件或確認框中。</param>
    public static PendingStep Pending(bool stillEnabled, bool ticketPanelOpen, bool busy, long elapsedMs)
    {
        if (!stillEnabled || ticketPanelOpen)
        {
            return PendingStep.Drop;
        }

        return busy && elapsedMs < BusyWaitMs ? PendingStep.Wait : PendingStep.Call;
    }
}
