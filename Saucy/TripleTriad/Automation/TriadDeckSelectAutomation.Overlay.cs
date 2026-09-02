using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using System;
using static ECommons.GenericHelpers;

namespace Saucy.TripleTriad;

internal static unsafe partial class TriadDeckSelectAutomation
{
    private static bool IsDeckSelectAddonPresent() =>
        TryGetAddonByName<AddonTripleTriadSelDeck>("TripleTriadSelDeck", out var _);

    private static bool IsDeckSelectVisible() =>
        TriadLocalClientStructs.TryGetSelDeck(out var _);

    private static bool IsSelectionComplete() =>
        !IsDeckSelectAddonPresent() ||
        (IsBoardHandsPopulated() && !IsDeckSelectVisible());

    private static bool IsSelectionSettled(AtkUnitBase* addon) =>
        IsSelectionComplete() || addon == null || !addon->IsVisible;

    private static void TickBoardVisibleDismissal(AtkUnitBase* addon)
    {
        boardDismissFrames++;

        if (!IsDeckSelectAddonPresent())
        {
            ResetSession();
            return;
        }

        if (!IsBoardHandsPopulated())
        {
            return;
        }

        if (!IsDeckSelectVisible())
        {
            ReleaseDeckSelectForMatch();
            return;
        }

        if (!confirmedThisScreen)
        {
            if (pendingProfileDeckId >= 0 && pendingDeckIndex >= 0)
            {
                TryApplyDeckSelection(addon, pendingProfileDeckId, pendingDeckIndex, pendingSelectMethod);
            }

            TryCloseDeckSelectGracefully(addon);
            confirmedThisScreen = true;
            framesSinceAttempt = DeckSelectRetryCooldownFrames;
            return;
        }

        TryCloseDeckSelectGracefully(addon);

        if (!IsDeckSelectVisible())
        {
            ReleaseDeckSelectForMatch();
            return;
        }

        if (boardDismissFrames < DeckSelectBoardVisibleMaxFrames)
        {
            return;
        }

        Svc.Log.Verbose("[TriadAutomator] Deck select overlay still visible after confirm; hiding overlay only");
        TryForceHideLastResort(addon);
        ReleaseDeckSelectForMatch();
        framesSinceAttempt = DeckSelectRetryCooldownFrames;
    }

    private static void ReleaseDeckSelectForMatch()
    {
        if (forceDismissedForMatch)
        {
            return;
        }

        forceDismissedForMatch = true;
        ClearPending();
        uiReaderPrep.OnDeckSelectLost();
    }

    private static void TryForceHideLastResort(AtkUnitBase* addon)
    {
        // 這條不是送輸入（agent Hide／直接改 IsVisible／Update(0)），但作用在剛被按過、可能正在關的窗上，
        // 而且呼叫端一律緊接在同一 tick 的 TryCloseDeckSelectGracefully 之後。只擋兩種狀態：
        // 已看過 PreFinalize 的實例、以及 15 幀內剛送過終結動作（確認鈕／close:true callback）的實例——
        // 終結動作之後窗若還在，就不是在關閉中，最後手段照常執行。
        if (!AddonPressGuard.TryTouch(SelDeckAddonName, addon))
        {
            return;
        }

        var agentHandle = Svc.GameGui.FindAgentInterface((nint)addon);
        if (agentHandle != nint.Zero)
        {
            var agent = (AgentInterface*)agentHandle.Address;
            agent->HideAddon();
            agent->Hide();
            addon->Update(0);
        }

        try
        {
            addon->IsVisible = false;
            addon->Update(0);
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TriadAutomator] Deck select last-resort hide failed");
        }
    }

    private static void TickGameRecommendedDeck(AtkUnitBase* addon)
    {
        if (recommendedAttempts >= MaxDeckSelectAttemptsPerScreen)
        {
            if (recommendedAttempts == MaxDeckSelectAttemptsPerScreen)
            {
                Svc.Chat.PrintError(
                    "[Saucy] " + "Could not use game recommended deck. Pick a deck manually or try another option.".Loc());
                recommendedAttempts++;
            }

            return;
        }

        if (!recommendedClicked)
        {
            if (!TryClickGameRecommendedButton(addon))
            {
                recommendedAttempts++;
                framesSinceAttempt = DeckSelectRetryCooldownFrames;
                return;
            }

            recommendedClicked = true;
            addon->Update(0);
            if (IsSelectionComplete() || IsSelectionSettled(addon))
            {
                confirmedThisScreen = true;
                ClearPending();
                return;
            }

            framesSinceAttempt = DeckSelectRecommendedSettleFrames;
            return;
        }

        if (TryConfirmGameRecommendedDeck(addon))
        {
            confirmedThisScreen = true;
            ClearPending();
            return;
        }

        recommendedAttempts++;
        framesSinceAttempt = DeckSelectRetryCooldownFrames;
    }

    private static bool TryClickGameRecommendedButton(AtkUnitBase* addon)
    {
        if (TryClickBottomDeckSelectActionButton(addon, preferLeft: true))
        {
            TriadDeckLog.Print("[Saucy] " + "Using game recommended deck...".Loc());
            return true;
        }

        var list = TryGetDeckSelectList(addon);
        foreach (var buttonId in DeckSelectRecommendedButtonIds)
        {
            var button = addon->GetComponentButtonById(buttonId);
            if (IsButtonInsideList(button, list) || !TryClickSelectButton(addon, buttonId))
            {
                continue;
            }

            TriadDeckLog.Print("[Saucy] " + "Using game recommended deck...".Loc());
            return true;
        }

        return false;
    }

    private static bool TryConfirmGameRecommendedDeck(AtkUnitBase* addon)
    {
        if (TryDispatchSelectedDeckListItem(addon))
        {
            addon->Update(0);
            if (IsSelectionComplete() || IsSelectionSettled(addon))
            {
                return true;
            }
        }

        TryClickConfirmButton(addon);
        addon->Update(0);
        if (IsSelectionComplete() || IsSelectionSettled(addon))
        {
            return true;
        }

        var selected = TryGetSelectedDeckListIndex(addon);
        if (selected >= 0 && TryFireDeckSelectConfirmCallback(addon, selected))
        {
            return true;
        }

        return IsSelectionComplete() || IsSelectionSettled(addon);
    }

    private static bool TryDispatchSelectedDeckListItem(AtkUnitBase* addon)
    {
        var list = TryGetDeckSelectList(addon);
        if (list is null)
        {
            return false;
        }

        var selected = list->SelectedItemIndex;
        if (selected < 0)
        {
            return false;
        }

        // 清單項目點擊不關窗：按法鍵＝選中索引。
        if (!AddonPressGuard.TryBeginPress(SelDeckAddonName, addon, $"list|{selected}"))
        {
            return false;
        }

        try
        {
            list->SelectItem(selected, true);
            list->DispatchItemEvent(selected, AtkEventType.ListItemClick);
            pendingDeckIndex = selected;
            pendingProfileDeckId = selected;
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TriadAutomator] Recommended deck list confirm failed for index {0}", selected);
            return false;
        }
    }

    private static int TryGetSelectedDeckListIndex(AtkUnitBase* addon)
    {
        var list = TryGetDeckSelectList(addon);
        return list is null ? -1 : list->SelectedItemIndex;
    }

    private static AtkComponentList* TryGetDeckSelectList(AtkUnitBase* addon)
    {
        AtkComponentList* best = null;
        var bestLen = 0;
        for (uint id = 1; id <= MaxDeckSelectNodeScan; id++)
        {
            var list = addon->GetComponentListById(id);
            if (list is null || list->ListLength <= 0)
            {
                continue;
            }

            if (list->ListLength <= bestLen)
            {
                continue;
            }

            best = list;
            bestLen = list->ListLength;
        }

        return best;
    }

    private static bool TryClickBottomDeckSelectActionButton(AtkUnitBase* addon, bool preferLeft)
    {
        var list = TryGetDeckSelectList(addon);
        AtkComponentButton* best = null;
        var bestX = preferLeft ? float.MaxValue : float.MinValue;
        var bestY = float.MinValue;
        for (uint id = 1; id <= MaxDeckSelectNodeScan; id++)
        {
            var button = addon->GetComponentButtonById(id);
            // ⚠ 上游這裡寫的是 button->IsEnabled，但 IsEnabled 解的是 OwnerNode(0xA8)，
            //    旁邊那個守衛檢的却是 AtkResNode(0xA0) —— 兩個不同欄位，擋不到。
            //    OwnerNode 為 null 時會擲 AccessViolationException，而 AVE 在 .NET Core 是
            //    corrupted-state exception，try/catch 檔不到 —— 改走本 fork 的 IsEnabledSafe。
            if (button is null ||
                !AddonButton.IsEnabledSafe(button) ||
                button->AtkResNode is null ||
                !button->AtkResNode->IsVisible() ||
                IsButtonInsideList(button, list))
            {
                continue;
            }

            var pos = GUINodeUtils.GetNodePosition(button->AtkResNode);
            if (pos.Y < bestY - 8f)
            {
                continue;
            }

            if (pos.Y > bestY + 8f)
            {
                best = button;
                bestX = pos.X;
                bestY = pos.Y;
                continue;
            }

            if (preferLeft ? pos.X < bestX : pos.X > bestX)
            {
                best = button;
                bestX = pos.X;
            }
        }

        if (best is null)
        {
            return false;
        }

        // 底部動作鈕：按法鍵＝節點 id（OwnerNode 已在上面 IsEnabledSafe 驗過非 null）。
        var bestNodeId = best->AtkComponentBase.OwnerNode->AtkResNode.NodeId;
        if (!AddonPressGuard.TryBeginPress(SelDeckAddonName, addon, $"button|{bestNodeId}"))
        {
            return false;
        }

        return AddonButton.TryClick(addon, best);
    }

    private static bool IsButtonInsideList(AtkComponentButton* button, AtkComponentList* list)
    {
        if (button is null || list is null || list->OwnerNode is null || button->AtkResNode is null)
        {
            return false;
        }

        var listNode = (AtkResNode*)list->OwnerNode;
        for (var node = button->AtkResNode; node is not null; node = node->ParentNode)
        {
            if (node == listNode)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBlindDeckSelect(AtkUnitBase* addon)
    {
        TriadDeckLog.Print("[Saucy] " + "Selecting first deck...".Loc());
        foreach (var listIndex in new[]
        {
            0, 1, 2, 3, 4
        })
        {
            TryFireDeckCallback(addon, 1, listIndex);
            TryFireDeckCallback(addon, 0, listIndex);
            addon->Update(0);
            TryClickConfirmButton(addon);
            addon->Update(0);
            if (IsSelectionComplete())
            {
                confirmedThisScreen = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>不關窗的 deck callback（選牌組）：按法鍵＝事件 id＋牌組值，同一幀送不同參數互不干擾。</summary>
    private static void TryFireDeckCallback(AtkUnitBase* addon, int eventId, int deckValue)
    {
        if (!AddonPressGuard.TryBeginPress(SelDeckAddonName, addon, $"cb|{eventId}|{deckValue}"))
        {
            return;
        }

        FireDeckCallbackRaw(addon, eventId, deckValue, close: false);
    }

    /// <summary>🔴 不經守衛的原始 callback；只准由已經登記過守衛的呼叫端使用。</summary>
    private static void FireDeckCallbackRaw(AtkUnitBase* addon, int eventId, int deckValue, bool close)
    {
        try
        {
            var values = stackalloc AtkValue[1];
            values[0] = new()
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = deckValue
            };
            addon->FireCallback((uint)eventId, values, close);
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TriadAutomator] Deck callback {0} failed for deck {1}", eventId, deckValue);
        }
    }

    private static bool TryFireDeckSelectConfirmCallback(AtkUnitBase* addon, int deckValue) =>
        deckValue >= 0 && TryRunConfirmChain(addon, deckValue, includeButtons: false);

    /// <summary>
    /// 終結動作鏈：確認鈕 5 → 確認鈕 1 → callback 1（close）→ callback 0（close）。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 原本是同一次呼叫內「按一招 → 看 <c>IsSelectionComplete</c>／<c>IsVisible</c> 沒落 → 換下一招」的級聯，
    /// 但關閉中的那幾幀窗還在、還可見，第二招就是打在正在關的窗上（攔不到的存取違規）。
    /// 改成：一個守衛窗口只送一招；守衛走逃生口放行（＝窗 90 幀都沒收掉，上一招真的沒生效）時才輪到下一招。
    /// 候選的順序與前置條件都沒變（鈕要可見可用、callback 要有牌組值），只是不再擠在同一次呼叫裡。
    /// 前置條件在登記守衛<b>之前</b>驗——登記了卻沒按會白白封鎖到逃生口。
    /// <para>
    /// 回傳值沿用舊語意：點了鈕回 <see langword="true"/>（送出成功），送了 callback 回「窗是否已收掉」；
    /// 被守衛擋下回 <see langword="false"/>（這一幀沒做成，呼叫端本來就是每幀重試）。
    /// </para>
    /// </remarks>
    private static bool TryRunConfirmChain(AtkUnitBase* addon, int deckValue, bool includeButtons)
    {
        // Kind 0 = 確認鈕（Value = 節點 id），Kind 1 = close:true callback（Value = 事件 id）。
        Span<(int Kind, int Value)> steps = stackalloc (int, int)[4];
        var count = 0;

        if (includeButtons)
        {
            foreach (var buttonId in DeckSelectConfirmButtonIds)
            {
                if (CanClickSelectButton(addon, buttonId))
                {
                    steps[count++] = (0, (int)buttonId);
                }
            }
        }

        if (deckValue >= 0)
        {
            steps[count++] = (1, 1);
            steps[count++] = (1, 0);
        }

        if (count == 0)
        {
            return false;
        }

        if (!AddonPressGuard.TryBeginPress(
                SelDeckAddonName, addon, AddonPressGuard.WholeWindowKey, AddonPressGuard.ReleaseEscapeFrames,
                out var viaEscape))
        {
            return false;
        }

        var address = (nint)addon;
        confirmChainStage = viaEscape && confirmChainAddress == address ? confirmChainStage + 1 : 0;
        confirmChainAddress = address;

        var (kind, value) = steps[confirmChainStage % count];
        if (kind == 0)
        {
            return ClickSelectButtonRaw(addon, (uint)value);
        }

        FireDeckCallbackRaw(addon, value, deckValue, close: true);
        addon->Update(0);
        return IsSelectionComplete() || !IsDeckSelectVisible();
    }

    private static void TryCloseDeckSelectGracefully(AtkUnitBase* addon)
    {
        var deckValue = pendingProfileDeckId >= 0 ? pendingProfileDeckId : pendingDeckIndex;
        if (!TryClickConfirmButton(addon) && deckValue >= 0)
        {
            TryFireDeckSelectConfirmCallback(addon, deckValue);
        }

        addon->Update(0);
    }
}
