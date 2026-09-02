using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using static ECommons.GenericHelpers;

namespace Saucy.TripleTriad;

internal static unsafe partial class TriadDeckSelectAutomation
{
    private const int MaxDeckSelectAttemptsPerScreen = 12;
    private const int DeckSelectRetryCooldownFrames = 30;
    private const int DeckSelectStuckResetFrames = 300;
    private const int DeckSelectBoardVisibleMaxFrames = 60;
    private const int DeckSelectBoardDismissDelayFrames = 15;
    private const int DeckSelectPostOptimizerCooldownFrames = TriadSession.DeckSelectPostProfileWriteFrames;
    private const int DeckSelectRecommendedSettleFrames = 15;
    private const int MaxDeckSelectMethods = 5;
    private const int MaxDeckSelectNodeScan = 48;

    /// <summary>選牌組窗的 addon 名稱。也是它在 <see cref="Saucy.Framework.AddonPressGuard"/> 裡的鍵。</summary>
    /// <remarks>
    /// 這扇窗<b>不是</b>單答窗：同一幀先點列、再送 deck callback、最後才按確認鈕是刻意的正常流程，
    /// 所以按法各自成鍵（<c>row|索引</c>／<c>cb|事件|牌組</c>／<c>list|索引</c>／<c>button|節點</c>）；
    /// 只有「按了窗就會走」的終結動作（確認鈕 5／1、<c>close:true</c> 的 callback）併成整扇窗一鍵，
    /// 登記之後同位址任何按法都不准——見 <see cref="TryRunConfirmChain"/>。
    /// </remarks>
    private const string SelDeckAddonName = "TripleTriadSelDeck";

    /// <summary>終結動作鏈走到第幾招，以及那是對哪一個實例（守衛走逃生口放行時才前進一招）。</summary>
    private static int confirmChainStage;

    private static nint confirmChainAddress;

    /// <summary>讀不到牌組清單時的盲試候選列索引（一個守衛窗口只試一副，見 <c>TryBlindDeckSelect</c>）。</summary>
    private static readonly int[] BlindDeckSweepIndices =
    [
        0, 1, 2, 3, 4
    ];

    /// <summary>盲試走到第幾副牌，以及那是對哪一個實例（🔴 位址只做等值比較，永不解參）。</summary>
    private static int blindSweepStage;

    private static nint blindSweepAddress;

    private static readonly uint[] DeckSelectConfirmButtonIds =
    [
        5, 1
    ];

    private static readonly uint[] DeckSelectRecommendedButtonIds =
    [
        0, 2, 4
    ];

    private static readonly HashSet<int> AttemptedDeckIndices = [];

    private static bool confirmedThisScreen;
    private static int attemptCount;
    private static int framesSinceAttempt;
    private static int pendingDeckIndex = -1;
    private static int pendingProfileDeckId = -1;
    private static int pendingSelectMethod;
    private static bool awaitingConfirm;
    private static bool recommendedClicked;
    private static int recommendedAttempts;
    private static bool forceDismissedForMatch;
    private static int boardDismissFrames;
    private static int boardVisibleFrames;

    public static int FramesOpen { get; private set; }

    public static bool ScreenActive { get; private set; }

    public static bool TickIfOpen()
    {
        if (!TriadLocalClientStructs.TryGetSelDeck(out var _, false))
        {
            if (ScreenActive)
            {
                ResetSession();
            }

            return false;
        }

        Tick();
        return BlocksBoardAutomation();
    }

    public static bool BlocksBoardAutomation()
    {
        if (!TriadLocalClientStructs.TryGetSelDeck(out var _, false))
        {
            return false;
        }
        if (TriadUiState.IsResultVisible() || TriadUiState.IsMatchRegistrationVisible())
        {
            return false;
        }

        var cardFarmActive = TriadCardFarmSession.IsModeActive() && TriadCardFarmSession.HasPendingDrops();
        if (!TriadRunSession.ShouldContinue() && !cardFarmActive)
        {
            return false;
        }

        if (TriadUiState.IsBoardVisible())
        {
            return false;
        }

        return true;
    }

    public static void Tick()
    {
        try
        {
            if (!TriadLocalClientStructs.TryGetSelDeck(out var selDeck, false))
            {
                ClearPending();
                ResetSession();
                return;
            }

            var addon = &selDeck->AtkUnitBase;
            if (TriadUiState.IsResultVisible())
            {
                TryCloseDeckSelectGracefully(addon);
                if (IsDeckSelectAddonPresent())
                {
                    TryForceHideLastResort(addon);
                }

                ReleaseDeckSelectForMatch();
                ResetSession();
                return;
            }

            if (TriadUiState.IsMatchRegistrationVisible() && !addon->IsVisible)
            {
                ReleaseDeckSelectForMatch();
                ResetSession();
                return;
            }

            if (TriadUiState.IsBoardVisible())
            {
                if (!forceDismissedForMatch)
                {
                    ReleaseDeckSelectForMatch();
                }

                TryCloseDeckSelectGracefully(addon);
                if (IsDeckSelectAddonPresent() && IsDeckSelectVisible())
                {
                    boardVisibleFrames++;
                    if (boardVisibleFrames < DeckSelectBoardDismissDelayFrames)
                    {
                        return;
                    }

                    TickBoardVisibleDismissal(addon);
                    return;
                }

                ResetSession();
                return;
            }

            boardVisibleFrames = 0;

            var cardFarmActive = TriadCardFarmSession.IsModeActive() && TriadCardFarmSession.HasPendingDrops();
            if (!TriadRunSession.ShouldContinue() && !cardFarmActive)
            {
                TryCloseDeckSelectGracefully(addon);
                if (IsDeckSelectAddonPresent())
                {
                    TryForceHideLastResort(addon);
                }
                ReleaseDeckSelectForMatch();
                ResetSession();
                return;
            }

            if (confirmedThisScreen)
            {
                if (IsSelectionSettled(addon))
                {
                    return;
                }

                if (FramesOpen < DeckSelectStuckResetFrames)
                {
                    return;
                }

                confirmedThisScreen = false;
            }

            FramesOpen++;

            if (!ScreenActive)
            {
                ResetSession();
                ScreenActive = true;
            }

            TriadRun.TickDeckSelectPostWriteCooldown();
            TriadRun.EnsureRunTargetNpcSynced(deckSelectScreen: true);
            if (!TriadUiState.IsBoardVisible())
            {
                TriadRun.EnsureExistingSaucyDeckForPrep();
            }

            if (framesSinceAttempt > 0)
            {
                framesSinceAttempt--;
                return;
            }

            if (awaitingConfirm)
            {
                if (IsSelectionComplete())
                {
                    confirmedThisScreen = true;
                    if (!TriadUiState.IsBoardVisible() || IsBoardHandsPopulated())
                    {
                        ClearPending();
                    }

                    return;
                }

                // 🔴 後援按法（非終結）一定要排在確認（終結動作）之前送。
                //    確認一登記，同一位址在 AddonPressGuard.TerminalHotFrames 幀內的任何按法都會被守衛擋掉；
                //    先送確認等於每一輪都自己把當輪要試的那一招擋掉 —— 五段後援階梯會被靜默燒光，
                //    每個牌組候選都被標成「試過」卻一下都沒真的按到。
                //    確認的嘗試次數並沒有變少：TryApplyDeckSelection 送完後援按法之後本來就會補一次確認，
                //    這裡只是把順序改回「先選、後確認」。
                if (pendingSelectMethod + 1 < MaxDeckSelectMethods)
                {
                    pendingSelectMethod++;
                    TryApplyDeckSelection(addon, pendingProfileDeckId, pendingDeckIndex, pendingSelectMethod);
                    framesSinceAttempt = DeckSelectRetryCooldownFrames;
                    if (IsSelectionComplete())
                    {
                        confirmedThisScreen = true;
                        if (!TriadUiState.IsBoardVisible() || IsBoardHandsPopulated())
                        {
                            ClearPending();
                        }
                    }

                    return;
                }

                // 後援按法用盡：只剩終結動作可以推進（守衛的逃生口讓確認鏈每輪換一招）。
                TryClickConfirmButton(addon);
                addon->Update(0);

                if (IsSelectionComplete())
                {
                    confirmedThisScreen = true;
                    if (!TriadUiState.IsBoardVisible() || IsBoardHandsPopulated())
                    {
                        ClearPending();
                    }

                    return;
                }

                if (IsSelectionSettled(addon))
                {
                    confirmedThisScreen = true;
                    ClearPending();
                    return;
                }

                AttemptedDeckIndices.Add(pendingProfileDeckId);
                attemptCount++;
                ClearPending();
                return;
            }

            if (!IsAddonReady(addon))
            {
                return;
            }

            uiReaderPrep.RefreshDeckSelectList((nint)addon);

            if (recommendedClicked ||
                (!C.UseSimmedDeck && C.SelectedDeckIndex == Configuration.GameRecommendedDeckIndex))
            {
                TickGameRecommendedDeck(addon);
                return;
            }

            if (attemptCount >= MaxDeckSelectAttemptsPerScreen)
            {
                if (C.UseSimmedDeck && TriadRun.IsDeckSelectPrepBlocking(C.UseSimmedDeck))
                {
                    return;
                }

                TickGameRecommendedDeck(addon);
                return;
            }

            if (C.UseSimmedDeck && TriadRun.IsDeckSelectPrepBlocking(C.UseSimmedDeck))
            {
                return;
            }

            if (C.UseSimmedDeck && attemptCount == 0 && AttemptedDeckIndices.Count == 0)
            {
                if (TrySelectPreferredProfileDeck(addon))
                {
                    attemptCount++;
                    framesSinceAttempt = DeckSelectRetryCooldownFrames;
                    return;
                }

                if (TriadRun.ShouldTryVisibleSaucyDeckRowSelect() && TrySelectVisibleSaucyDeck(addon))
                {
                    attemptCount++;
                    framesSinceAttempt = DeckSelectRetryCooldownFrames;
                    return;
                }

                if (uiReaderPrep.cachedState.decks.Count == 0 && TryBlindDeckSelect(addon))
                {
                    framesSinceAttempt = DeckSelectRetryCooldownFrames;
                    if (IsBlindDeckSweepExhausted)
                    {
                        // 盲試整輪（5 副牌）走完才算一次嘗試：中途每一副各佔一個守衛窗口，
                        // 一副就燒掉 MaxDeckSelectAttemptsPerScreen 的額度會讓這條路徑退化成只試第一副。
                        attemptCount++;
                    }

                    return;
                }
            }

            if (!TriadRun.TryGetDeckSelectCandidate(
                C.UseSimmedDeck,
                C.SelectedDeckIndex,
                AttemptedDeckIndices,
                out var deck))
            {
                TickGameRecommendedDeck(addon);
                return;
            }

            if (deck < 0)
            {
                return;
            }

            if (AttemptedDeckIndices.Contains(deck))
            {
                return;
            }

            if (!TriadRun.TryResolveDeckListIndex(deck, out var resolvedListIndex))
            {
                Svc.Chat.PrintError("[Saucy] " + "Could not find deck ?? in the selection list.".Loc(deck + 1));
                AttemptedDeckIndices.Add(deck);
                attemptCount++;
                return;
            }

            PrintAttemptMessage(deck, resolvedListIndex);

            pendingProfileDeckId = deck;
            pendingDeckIndex = resolvedListIndex;
            pendingSelectMethod = 0;
            awaitingConfirm = true;
            TryApplyDeckSelection(addon, deck, resolvedListIndex, 0);
            framesSinceAttempt = DeckSelectRetryCooldownFrames;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[TriadAutomator] DeckSelect failed");
        }
    }

    public static void ResetSession()
    {
        ClearPending();
        ScreenActive = false;
        confirmedThisScreen = false;
        forceDismissedForMatch = false;
        boardDismissFrames = 0;
        boardVisibleFrames = 0;
        AttemptedDeckIndices.Clear();
        attemptCount = 0;
        framesSinceAttempt = 0;
        FramesOpen = 0;
        blindSweepStage = 0;
        blindSweepAddress = nint.Zero;
        recommendedClicked = false;
        recommendedAttempts = 0;
    }

    public static void PrepareRetryWithOptimizedDeck(int deckId)
    {
        if (!TriadRunSession.ShouldContinue() || !TriadUiState.IsPrepDeckSelectVisible())
        {
            return;
        }

        if (TriadUiState.IsBoardVisible() || confirmedThisScreen)
        {
            return;
        }

        if (TriadLocalClientStructs.TryGetSelDeck(out var selDeck, false))
        {
            uiReaderPrep.RefreshDeckSelectList((nint)selDeck);
        }

        ScreenActive = true;
        ClearPending();
        AttemptedDeckIndices.Clear();
        attemptCount = 0;
        blindSweepStage = 0;
        blindSweepAddress = nint.Zero;
        recommendedClicked = false;
        recommendedAttempts = 0;
        framesSinceAttempt = DeckSelectPostOptimizerCooldownFrames;
        TriadRun.BeginDeckSelectPostWriteCooldown();
    }

    private static void TickBoardVisibleRecoverDeck(AtkUnitBase* addon)
    {
        boardDismissFrames++;

        if (!addon->IsVisible)
        {
            try
            {
                addon->IsVisible = true;
                addon->Update(0);
            }
            catch (Exception ex)
            {
                Svc.Log.Verbose(ex, "[TriadAutomator] Could not re-show deck select for recovery");
            }
        }

        if (pendingProfileDeckId >= 0 && pendingDeckIndex >= 0)
        {
            TryApplyDeckSelection(addon, pendingProfileDeckId, pendingDeckIndex, pendingSelectMethod);
        }
        else if (IsAddonReady(addon))
        {
            uiReaderPrep.RefreshDeckSelectList((nint)addon);
            if (C.UseSimmedDeck && TrySelectPreferredProfileDeck(addon))
            {
                framesSinceAttempt = DeckSelectRetryCooldownFrames;
            }
            else if (C.UseSimmedDeck && TriadRun.ShouldTryVisibleSaucyDeckRowSelect() && TrySelectVisibleSaucyDeck(addon))
            {
                framesSinceAttempt = DeckSelectRetryCooldownFrames;
            }
        }

        TryCloseDeckSelectGracefully(addon);

        if (boardDismissFrames == DeckSelectBoardVisibleMaxFrames)
        {
            Svc.Chat.PrintError("[Saucy] " + "Match started without a deck. Confirm deck selection manually.".Loc());
        }
    }

    internal static bool IsBoardHandsPopulated()
    {
        if (!TriadLocalClientStructs.TryGetBoard(out var board, false))
        {
            return false;
        }

        var blueCount = 0;
        var redCount = 0;
        for (var i = 0; i < 5; i++)
        {
            if (board->BlueDeck[i].HasCard)
            {
                blueCount++;
            }

            if (board->RedDeck[i].HasCard)
            {
                redCount++;
            }
        }

        return blueCount > 0 && redCount > 0;
    }
}
