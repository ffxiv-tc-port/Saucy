using ECommons.Automation.UIInput;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using System;
namespace Saucy.TripleTriad;

internal static unsafe partial class TriadDeckSelectAutomation
{
    /// <summary>非終結鈕（遊戲推薦牌組等）的點擊：按法鍵＝節點 id。終結鈕（確認 5／1）走 <see cref="TryRunConfirmChain"/>。</summary>
    private static bool TryClickSelectButton(AtkUnitBase* addon, uint buttonId)
    {
        if (!CanClickSelectButton(addon, buttonId))
        {
            return false;
        }

        if (!AddonPressGuard.TryBeginPress(SelDeckAddonName, addon, $"button|{buttonId}"))
        {
            return false;
        }

        return ClickSelectButtonRaw(addon, buttonId);
    }

    private static bool CanClickSelectButton(AtkUnitBase* addon, uint buttonId)
    {
        var button = addon->GetComponentButtonById(buttonId);
        // ⚠️ IsEnabled 解的是 OwnerNode，AtkResNode 的檢查擋不到它 → 用 IsEnabledSafe。
        return button != null && AddonButton.IsEnabledSafe(button) && button->AtkResNode != null && button->AtkResNode->IsVisible();
    }

    /// <summary>🔴 不經守衛的原始點擊；只准由已經登記過守衛的呼叫端使用。</summary>
    private static bool ClickSelectButtonRaw(AtkUnitBase* addon, uint buttonId)
    {
        var button = addon->GetComponentButtonById(buttonId);
        if (button == null)
        {
            return false;
        }

        try
        {
            button->ClickAddonButton(addon);
            addon->Update(0);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TriadAutomator] Deck select button {0} click failed", buttonId);
            return false;
        }
    }

    private static bool TrySelectPreferredProfileDeck(AtkUnitBase* addon)
    {
        if (!C.UseSimmedDeck || !TriadRun.TryResolveAutoPickProfileDeckId(out var profileDeckId))
        {
            return false;
        }

        if (!TriadRun.TryResolveDeckListIndex(profileDeckId, out var listIndex))
        {
            return false;
        }

        PrintAttemptMessage(profileDeckId, listIndex);
        pendingProfileDeckId = profileDeckId;
        pendingDeckIndex = listIndex;
        pendingSelectMethod = 0;
        awaitingConfirm = true;
        TryApplyDeckSelection(addon, profileDeckId, listIndex, 0);
        return true;
    }

    private static bool TrySelectVisibleSaucyDeck(AtkUnitBase* addon)
    {
        var expectedName = TriadRun.GetExpectedSaucyDeckName();
        var npcName = TriadRun.preGameNpc?.Name ?? string.Empty;
        for (var idx = 0; idx < uiReaderPrep.cachedState.decks.Count; idx++)
        {
            var deck = uiReaderPrep.cachedState.decks[idx];
            if (string.IsNullOrWhiteSpace(deck.name))
            {
                continue;
            }

            var isSaucyDeck = deck.name.Contains("(Sa", StringComparison.OrdinalIgnoreCase);
            if (!isSaucyDeck)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(expectedName) &&
                !TriadDeckNameHelper.RowMatchesNpc(deck.name, expectedName, npcName))
            {
                continue;
            }

            TriadDeckLog.Print("[Saucy] " + "Selecting \"??\"...".Loc(deck.name));
            var profileDeckId = TriadRun.HasOptimizedDeckApplied
                ? TriadRun.OptimizedDeckSlotId
                : deck.id;
            pendingProfileDeckId = profileDeckId;
            pendingDeckIndex = idx;
            pendingSelectMethod = 0;
            awaitingConfirm = true;
            TryApplyDeckSelection(addon, profileDeckId, idx, 0);
            return true;
        }

        return false;
    }

    private static void TryApplyDeckSelection(AtkUnitBase* addon, int profileDeckId, int listIndex, int method)
    {
        var selectionClosed = false;
        switch (method)
        {
            case 0:
                if (C.UseSimmedDeck &&
                    TriadRun.HasOptimizedDeckApplied &&
                    profileDeckId == TriadRun.OptimizedDeckSlotId)
                {
                    selectionClosed = TryFireDeckSelectConfirmCallback(addon, profileDeckId);
                    if (!selectionClosed)
                    {
                        TryFireDeckCallback(addon, 0, profileDeckId);
                    }
                }
                else
                {
                    TryClickDeckListRow(addon, listIndex);
                }

                break;
            case 1:
                TryFireDeckCallback(addon, 1, profileDeckId);
                break;
            case 2:
                TryFireDeckCallback(addon, 0, profileDeckId);
                break;
            case 3:
                TryFireDeckCallback(addon, 2, profileDeckId);
                break;
            case 4:
                selectionClosed = TryClickConfirmButton(addon);
                break;
        }

        addon->Update(0);

        if (!selectionClosed && method < 4)
        {
            TryClickConfirmButton(addon);
            addon->Update(0);
        }

        if (IsSelectionComplete())
        {
            confirmedThisScreen = true;
            if (!TriadUiState.IsBoardVisible() || IsBoardHandsPopulated())
            {
                ClearPending();
            }
        }
    }

    private static void ClearPending()
    {
        pendingDeckIndex = -1;
        pendingProfileDeckId = -1;
        pendingSelectMethod = 0;
        awaitingConfirm = false;
    }

    /// <summary>按確認：確認鈕 5 → 確認鈕 1 → callback 1（close）→ callback 0（close），一個守衛窗口只送一招。</summary>
    private static bool TryClickConfirmButton(AtkUnitBase* addon)
    {
        var deckValue = pendingProfileDeckId >= 0 ? pendingProfileDeckId : pendingDeckIndex;
        return TryRunConfirmChain(addon, deckValue, includeButtons: true);
    }

    private static void PrintAttemptMessage(int deck, int listIndex)
    {
        string message;
        if (attemptCount > 0 || AttemptedDeckIndices.Count > 0)
        {
            message = "[Saucy] " + "Retrying with deck ??...".Loc(deck + 1);
        }
        else if (C.UseSimmedDeck && TriadRun.HasOptimizedDeckApplied)
        {
            var deckName = TriadRun.GetProfileDeckName(deck);
            if (string.IsNullOrWhiteSpace(deckName))
            {
                deckName = TriadRun.GetExpectedSaucyDeckName();
            }

            message = !string.IsNullOrWhiteSpace(deckName)
                ? "[Saucy] " + "Selecting \"??\" (slot ??)...".Loc(deckName, deck + 1)
                : "[Saucy] " + "Selecting optimized deck ??...".Loc(deck + 1);
        }
        else
        {
            var deckName = TriadRun.GetProfileDeckName(deck);
            if (string.IsNullOrWhiteSpace(deckName) &&
                listIndex >= 0 && listIndex < uiReaderPrep.cachedState.decks.Count)
            {
                deckName = uiReaderPrep.cachedState.decks[listIndex].name;
            }

            message = !string.IsNullOrWhiteSpace(deckName)
                ? "[Saucy] " + "Selecting \"??\"...".Loc(deckName)
                : "[Saucy] " + "Selecting deck ??...".Loc(deck + 1);
        }

        if (C.UseSimmedDeck)
        {
            TriadDeckLog.Print(message);
        }
        else
        {
            Svc.Log.Verbose(message);
        }
    }

    private static bool TryClickDeckListRow(AtkUnitBase* addon, int listIndex)
    {
        if (listIndex < 0 || listIndex >= uiReaderPrep.cachedState.decks.Count)
        {
            return false;
        }

        var rowAddr = uiReaderPrep.cachedState.decks[listIndex].rootNodeAddr;
        if (rowAddr == 0)
        {
            return false;
        }

        var rowNode = (AtkResNode*)rowAddr;
        if (rowNode == null)
        {
            return false;
        }

        if (TryClickComponentButton(rowNode, addon, listIndex))
        {
            return true;
        }

        var children = GUINodeUtils.GetImmediateChildNodes(rowNode);
        if (children == null)
        {
            return false;
        }

        foreach (var child in children)
        {
            if (TryClickComponentButton(child, addon, listIndex))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryClickComponentButton(AtkResNode* node, AtkUnitBase* addon, int listIndex)
    {
        if (node == null)
        {
            return false;
        }

        var button = node->GetAsAtkComponentButton();
        // ⚠️ IsEnabled 解的是 OwnerNode，AtkResNode 的檢查擋不到它 → 用 IsEnabledSafe。
        if (button == null || !AddonButton.IsEnabledSafe(button) || button->AtkResNode == null || !button->AtkResNode->IsVisible())
        {
            return false;
        }

        // 點列不關窗：按法鍵＝列索引，同一幀之後的 deck callback／確認鈕各自成鍵不互擋。
        if (!AddonPressGuard.TryBeginPress(SelDeckAddonName, addon, $"row|{listIndex}"))
        {
            return false;
        }

        try
        {
            button->ClickAddonButton(addon);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TriadAutomator] Deck row button click failed");
            return false;
        }
    }
}
