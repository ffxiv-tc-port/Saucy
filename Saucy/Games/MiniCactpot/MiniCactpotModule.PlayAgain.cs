using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.LanguageHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static ECommons.GenericHelpers;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
using ConditionFlag = Dalamud.Game.ClientState.Conditions.ConditionFlag;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Saucy.MiniCactpot;

public unsafe partial class MiniCactpotModule
{
    /// <summary>「購買下一張」確認：只按 <see cref="OnYesnoPreSetup"/> 接手的那一扇（位址相同、AtkValues[0] 仍是購票確認）。</summary>
    /// <remarks>沒接手（還沒出現、認不出、交還了）就不按；交還時任務安靜結束。</remarks>
    private bool? TryPressPlayAgainYes()
    {
        if (!IsEnabled || !C.MiniCactpotAutoPlayAgain)
        {
            return true; // 中途被關掉：安靜結束
        }

        if (playAgainHandedBack)
        {
            return true;
        }

        if (playAgainClaimedYesno == nint.Zero ||
            !SelectYesnoHelper.TryGetVisibleAt(playAgainClaimedYesno, out var yesno))
        {
            return false; // 還沒出現或還沒就緒，繼續等（TaskManager 逾時自動放棄）
        }

        if (!SelectYesnoHelper.IsMiniCactpotPurchasePrompt(SelectYesnoHelper.GetValuePrompt(yesno)))
        {
            Log($"接手的確認框內文已不是購票確認，Saucy 不按：{FlattenPrompt(SelectYesnoHelper.GetValuePrompt(yesno))}");
            playAgainClaimedYesno = nint.Zero;
            return false;
        }

        if (!SelectYesnoHelper.PressYes(yesno))
        {
            return false;
        }

        playAgainClaimedYesno = nint.Zero;
        Log("Saucy 已按下「購買下一張」確認框的「是」");
        LastAction = "已確認購買下一張彩券";
        return true;
    }

    /// <summary>「購買下一張」等不到確認框時的收尾。逾時是這一步的<b>正常終局</b>——當日三張打完後
    /// 遊戲就不再問——所以沒有框時只記 <c>Information</c>；框出現了卻沒按下去才是真的異常，維持 <c>Warning</c>。</summary>
    /// <remarks>接手的那一扇還在期限內就延長 <paramref name="remainingTimeMS"/>：YesAlready 已放過它，Saucy 不按就沒人按。</remarks>
    private void OnPlayAgainTimedOut(TaskManagerTask task, ref long remainingTimeMS)
    {
        var now = DateTime.UtcNow;
        if (playAgainClaimedYesno != nint.Zero && now < playAgainClaimedUntilUtc)
        {
            remainingTimeMS = (long)Math.Ceiling((playAgainClaimedUntilUtc - now).TotalMilliseconds);
            return;
        }

        playAgainArmedUntilUtc = null;
        var boardBack = GetLotteryDailyAddon() != null ? "還在" : "已關";

        if (!SelectYesnoHelper.TryGetVisible(out var yesno))
        {
            LastAction = "沒有下一張可買（當日彩券已打完，或已離開面板）";
            Log($"等不到「購買下一張」確認框，放棄等待。yesno=無, 仙人微彩面板={boardBack}");
            return;
        }

        LogWarning(
            $"「購買下一張」確認框出現了卻沒被按下:" +
            $"owner={AgentHelper.DescribeOwner(&yesno->AtkUnitBase)}, " +
            $"ticketLayout={SelectYesnoHelper.IsTicketPurchaseLayout(yesno)}, " +
            $"lotteryYesno={SelectYesnoHelper.ShouldPressLotteryYesno(yesno, AgentId.LotteryDaily)}, " +
            $"purchasePrompt={SelectYesnoHelper.IsMiniCactpotPurchasePrompt(SelectYesnoHelper.GetValuePrompt(yesno))}, " +
            $"claimed={((nint)yesno == playAgainClaimedYesno)}, " +
            $"仙人微彩面板={boardBack}");
    }

    /// <summary>只有剩餘張數「確定是 0」才不等確認框；不知道（沒收到事件、換角色、跨過重置）一律照舊等。</summary>
    internal static bool ShouldWaitForPlayAgain(bool remainingKnown, int remaining) =>
        !(remainingKnown && remaining == 0);

    /// <summary>關掉一張面板的當下：今天確定打完、這個角色今天還沒交棒過，就排一次交棒（等發放員的道別對話結束才呼叫）。</summary>
    private void ArmQuestionableHandOff(bool ticketFinished, bool known, int remaining)
    {
        var enabled = C.MiniCactpotStartQuestionableAlliedDailies;
        if (!enabled || !ticketFinished)
        {
            return;
        }

        var contentId = Svc.PlayerState.ContentId;
        DateTime? lastCalled = HandOffCalledUtc.TryGetValue(contentId, out var called) ? called : null;
        if (MiniCactpotQuestionableHandOff.ShouldArm(
                enabled, ticketFinished, known, remaining, lastCalled, GoldSaucerEventTracker.LastDailyResetUtc()))
        {
            handOffArmedUtc = DateTime.UtcNow;
            handOffFirstTryUtc = null;
            handOffContentId = contentId;
            Log("今日剩餘張數確定是 0，等發放員的對話結束後請 Questionable 開始友好部族日常");
        }
        else if (!known)
        {
            Log("今日剩餘張數不知道，不交棒給 Questionable（只有確定打完才交棒）");
        }
        else if (remaining == 0)
        {
            Log($"這個角色這個遊戲日已經交棒過一次（{lastCalled?.ToLocalTime():HH:mm}），不再呼叫 Questionable");
        }
    }

    private void TickQuestionableHandOff()
    {
        if (handOffArmedUtc is not { } armedUtc || DateTime.UtcNow < armedUtc)
        {
            return;
        }

        if (Svc.PlayerState.ContentId != handOffContentId)
        {
            handOffArmedUtc = null;
            handOffFirstTryUtc = null;
            Log("取消交棒給 Questionable：已經換了角色或登出");
            return;
        }

        var step = MiniCactpotQuestionableHandOff.Pending(
            IsEnabled && C.MiniCactpotStartQuestionableAlliedDailies,
            GetLotteryDailyAddon() != null,
            IsHandOffBusy(),
            (long)(DateTime.UtcNow - armedUtc).TotalMilliseconds);
        if (step == MiniCactpotQuestionableHandOff.PendingStep.Wait)
        {
            return;
        }

        handOffArmedUtc = null;
        if (step == MiniCactpotQuestionableHandOff.PendingStep.Drop)
        {
            handOffFirstTryUtc = null;
            Log("取消交棒給 Questionable：又開了彩券面板，或開關／模組被關掉");
            return;
        }

        var now = DateTime.UtcNow;
        handOffFirstTryUtc ??= now;
        var result = Questionable.TryStartAlliedSocietyDailies(out var detail);
        var outcome = MiniCactpotQuestionableHandOff.AfterCall(result, (long)(now - handOffFirstTryUtc.Value).TotalMilliseconds);
        if (outcome == MiniCactpotQuestionableHandOff.CallOutcome.Record)
        {
            HandOffCalledUtc[handOffContentId] = now;
        }
        else if (outcome == MiniCactpotQuestionableHandOff.CallOutcome.Retry)
        {
            handOffArmedUtc = now.AddMilliseconds(MiniCactpotQuestionableHandOff.UnavailableRetryMs);
            Log($"呼叫不到 Questionable 的「開始友好部族日常」：{detail}；不算用掉今天這一次，{MiniCactpotQuestionableHandOff.UnavailableRetryMs / 1000} 秒後再試");
            return;
        }

        handOffFirstTryUtc = null;
        switch (result)
        {
            case Questionable.AlliedDailiesStart.Started:
                Log("已請 Questionable 開始友好部族日常，它回報已開始");
                Svc.Chat.Print("[Saucy] " + "Mini Cactpot is done for today; Questionable has started the allied society dailies.".Loc());
                break;
            case Questionable.AlliedDailiesStart.Declined:
                Log("已請 Questionable 開始友好部族日常，它回報不開始（原因寫在 Questionable 自己的 log）");
                Svc.Chat.Print("[Saucy] " + "Mini Cactpot is done for today, but Questionable did not start the allied society dailies (see its own message for why).".Loc());
                break;
            default:
                Log($"呼叫不到 Questionable 的「開始友好部族日常」：{detail}；重試 {MiniCactpotQuestionableHandOff.UnavailableRetryWindowMs / 60_000} 分鐘仍不行，這一輪不再呼叫");
                Svc.Chat.Print("[Saucy] " + "Mini Cactpot is done for today, but Questionable could not be asked to start the allied society dailies (not loaded, or too old).".Loc());
                break;
        }
    }

    private static bool IsHandOffBusy() =>
        Svc.Condition[ConditionFlag.OccupiedInQuestEvent] || Svc.Condition[ConditionFlag.OccupiedInEvent] ||
        TalkHelper.IsVisible() || SelectYesnoHelper.IsVisible() || SelectStringHelper.IsNpcListMenuVisible();
}
