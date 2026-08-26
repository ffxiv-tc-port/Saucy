using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Saucy.Framework;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
namespace Saucy.TripleTriad;

internal static unsafe class TriadDialogueSkip
{
    private const string TalkThrottleKey = "Saucy.TriadTalk";

    private static bool talkListenerRegistered;

    public static void EnsureTalkListener()
    {
        if (talkListenerRegistered)
        {
            return;
        }

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", OnTalkUpdate);
        talkListenerRegistered = true;
    }

    public static bool ShouldRun()
    {
        if (IsGoldSaucerMinigameOccupied())
        {
            return false;
        }

        TriadNpcGate.SyncTrackedNpc();
        TriadNpcGate.RefreshDialogueFlow();

        if (TriadMapNavigation.IsAwaitingTriadStartDialog())
        {
            return true;
        }

        if (!TriadRunSession.ModuleEnabled && !TriadCardFarmSession.SessionActive)
        {
            return false;
        }

        if (TriadUiState.IsAutomationFlowActive() || TriadCardFarmSession.SessionActive)
        {
            return true;
        }

        if (IsTriadSessionUiVisible())
        {
            return true;
        }

        if (CanAutomateTalk())
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return false;
        }

        return TriadNpcGate.HasInitiatedDialogue() || TriadNpcGate.IsInDialogueFlow();
    }

    public static bool IsBlockingTriadSessionEnd()
    {
        if (IsGoldSaucerMinigameOccupied())
        {
            return false;
        }

        TriadNpcGate.SyncTrackedNpc();

        if (TalkHelper.IsVisible())
        {
            return TriadNpcGate.IsTargeting() || TriadNpcGate.IsInDialogueFlow() || CanAutomateTalk();
        }

        return SelectYesnoHelper.TryGetVisible(out var yesno) && SelectYesnoHelper.IsTriadYesno(yesno);
    }

    public static void Tick()
    {
        EnsureTalkListener();
        TriadNpcGate.SyncTrackedNpc();
        TriadNpcGate.RefreshDialogueFlow();

        if (TriadMapNavigation.IsAwaitingTriadStartDialog())
        {
            if (TriadMapNavigation.TryAdvanceTriadStartDialog())
            {
                TriadNpcGate.MarkDialogueFlow();
            }
        }

        if (!ShouldRun())
        {
            return;
        }

        RunDialogueAutomation();
    }

    private static void OnTalkUpdate(AddonEvent type, AddonArgs args)
    {
        if (!TriadRunSession.ModuleEnabled && !TriadCardFarmSession.SessionActive)
        {
            return;
        }

        if (IsGoldSaucerMinigameOccupied())
        {
            return;
        }

        TriadNpcGate.SyncTrackedNpc();
        TriadNpcGate.RefreshDialogueFlow();

        if (!TalkHelper.IsVisible() || !CanAutomateTalk())
        {
            return;
        }

        TryAdvanceTalk();
    }

    private static void RunDialogueAutomation()
    {
        // SelectIconString can stay "visible" after picking Triad while Talk is already open.
        if (TalkHelper.IsVisible())
        {
            TryAdvanceTalk();
            TryAdvanceTriadYesno();
            return;
        }

        if (SelectStringHelper.IsNpcListMenuVisible())
        {
            if (!CanAutomateSelectString())
            {
                return;
            }

            if (SelectStringHelper.TrySelectTriadEntry("SaucyTriadSelectString"))
            {
                TriadNpcGate.MarkDialogueFlow();
            }

            return;
        }

        TryAdvanceTriadYesno();
    }

    private static void TryAdvanceTalk()
    {
        if (!CanAutomateTalk())
        {
            return;
        }

        if (QuestDialogueGuard.ShouldBlockTalk(
            TriadNpcGate.IsTargeting() ||
            TriadNpcGate.IsInDialogueFlow() ||
            TriadTargetNpc.FromWorldTarget() != null))
        {
            return;
        }

        if (SelectYesnoHelper.TryGetVisible(out var blockingYesno) &&
            !SelectYesnoHelper.ShouldPressTriadYesno(blockingYesno))
        {
            return;
        }

        if (TalkHelper.TryAdvance(TalkThrottleKey))
        {
            TriadNpcGate.MarkDialogueFlow();
        }
    }

    private static void TryAdvanceTriadYesno()
    {
        if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            !SelectYesnoHelper.TryGetVisible(out var yesno) ||
            !SelectYesnoHelper.ShouldPressTriadYesno(yesno))
        {
            return;
        }

        if (!TriadNpcGate.CanAutomateYesno())
        {
            return;
        }

        if (QuestDialogueGuard.ShouldBlockYesno(
            TriadNpcGate.IsTargeting() || TriadNpcGate.IsInDialogueFlow()))
        {
            return;
        }

        if (SelectYesnoHelper.PressYes(yesno))
        {
            TriadNpcGate.MarkDialogueFlow();
        }
    }

    private static bool CanAutomateSelectString() =>
        TriadMapNavigation.IsAwaitingTriadStartDialog() ||
        TriadNpcGate.HasInitiatedDialogue() ||
        TriadNpcGate.IsInDialogueFlow();

    private static bool CanAutomateTalk() =>
        TriadMapNavigation.IsAwaitingTriadStartDialog() ||
        TriadNpcGate.HasInitiatedDialogue() ||
        TriadNpcGate.IsInDialogueFlow() ||
        (TriadRunSession.ModuleEnabled && TriadTargetNpc.FromWorldTarget() != null);

    /// <summary>玩家是否正身處金蝶遊樂園的街機小遊戲中——是的話幻卡對話自動化整個讓開，
    /// 不要跟另一個小遊戲模組搶著按確認框。
    /// <para>⚠️ 這個函式原本是 <c>=&gt; false</c> 的空殼（2026-07-01 cbfd349 移除街機模組時
    /// 拆一半留下的殘骸），名字宣稱一個判斷、實作卻是無條件常數，三個呼叫端全部靜默失效。
    /// 孤樹無援模組加回來之後這個判斷重新有了意義，所以補回真正的實作。</para>
    /// <para>🔑 刻意設計成兩邊都不會壞：<c>IsAgentActive()</c> 在台服用戶端到底是
    /// 「機台介面開著」還是「人在金蝶遊樂園就一直是 true」**無法離線證明**，
    /// 所以額外要求「畫面上沒有任何幻卡 UI、且幻卡 agent 不在作用中」才算被佔用。
    /// 即使前一個假設完全錯誤，只要幻卡流程正在跑，自動化仍然照常運作、不會整個停擺。</para></summary>
    private static bool IsGoldSaucerMinigameOccupied()
    {
        if (!AgentHelper.IsActive(AgentId.GoldSaucerMiniGame))
        {
            return false;
        }

        return !IsTriadSessionUiVisible() && !AgentHelper.IsActive(AgentId.TrippleTriad);
    }

    private static bool IsTriadSessionUiVisible() =>
        uiReaderPrep.HasMatchRequestUI ||
        uiReaderPrep.HasDeckSelectionUI ||
        uiReaderGame.IsVisible ||
        TriadUiState.IsMatchRegistrationVisible() ||
        TriadUiState.IsPrepDeckSelectVisible() ||
        TriadUiState.IsResultVisible();
}
