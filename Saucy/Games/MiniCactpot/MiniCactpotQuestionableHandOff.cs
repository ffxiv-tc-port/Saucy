using System;

namespace Saucy.MiniCactpot;

/// <summary>
/// 仙人微彩當天打完之後交棒給 Questionable 友好部族日常的判斷。純邏輯、不碰遊戲、不配置。
/// </summary>
internal static class MiniCactpotQuestionableHandOff
{
    /// <summary>關掉最後一張之後，發放員的道別對話最多等這麼久；過了照樣呼叫（Questionable 自己也會等對話結束）。</summary>
    public const long BusyWaitMs = 10_000;

    public enum PendingStep : byte
    {
        Wait,
        Call,
        Drop
    }

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
