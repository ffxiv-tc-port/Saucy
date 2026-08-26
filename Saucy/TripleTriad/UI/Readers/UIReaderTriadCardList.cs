using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
namespace Saucy.TripleTriad.UI;

internal static class CardListFilterMapping
{
    // Old FFXIVClientStructs' AgentGoldSaucer has no CardListFilterMode field; the addon
    // itself (AddonGSInfoCardList.FilterMode) tracks the same "which cards are shown" state.
    public static byte ToCollectionFilter(GSInfoCardListFilterMode mode) =>
        mode switch
        {
            GSInfoCardListFilterMode.DisplayOwnedCards => (byte)GameCardCollectionFilter.OnlyOwned,
            GSInfoCardListFilterMode.DisplayUnownedCards => (byte)GameCardCollectionFilter.OnlyMissing,
            var _ => (byte)GameCardCollectionFilter.All
        };
}

public unsafe class UIReaderTriadCardList : IUIReader
{
    public enum Status
    {
        NoErrors,
        AddonNotFound,
        AddonNotVisible,
        NodesNotReady
    }

    private nint cachedAddonAgentPtr;

    public UIStateTriadCardList cachedState = new();
    private int lastNotifiedCardId = -1;
    public Action<UIStateTriadCardList>? OnUIStateChanged;
    public Action<bool>? OnVisibilityChanged;
    private int pendingNavAttempts;
    private int pendingNavCardId;
    private int pendingNavCell = -1;
    private int pendingNavGraceFrames;
    private int pendingNavPage = -1;
    private int pendingNavSourceCardId = -1;

    public Status status = Status.AddonNotFound;
    public bool IsVisible => status is not Status.AddonNotFound and not Status.AddonNotVisible;
    public bool IsPendingCardNavigation => pendingNavPage >= 0;

    public string GetAddonName() => "GSInfoCardList";

    public void OnAddonLost()
    {
        cachedAddonAgentPtr = nint.Zero;
        ClearPendingNavigation();
        SetStatus(Status.AddonNotFound);
    }

    public void OnAddonShown(nint addonPtr)
    {
        cachedAddonAgentPtr = (addonPtr != nint.Zero) ? Svc.GameGui.FindAgentInterface(addonPtr) : nint.Zero;

        if (cachedAddonAgentPtr == nint.Zero)
        {
            cachedAddonAgentPtr = LoadFailsafeAgent();
#if DEBUG
            Svc.Log.Info($"using agentPtr from failsafe: {(ulong)cachedAddonAgentPtr:X}");
#endif
        }

        if (addonPtr != nint.Zero)
        {
            var addon = (AddonGSInfoCardList*)addonPtr;
            if (addon->AtkUnitBase.RootNode != null)
            {
                (cachedState.screenPos, cachedState.screenSize) =
                    GUINodeUtils.GetNodePosAndSize(addon->AtkUnitBase.RootNode);
            }

            SetStatus(Status.NodesNotReady);
        }
    }

    public void OnAddonUpdate(nint addonPtr)
    {
        var addon = (AddonGSInfoCardList*)addonPtr;
        (cachedState.screenPos, cachedState.screenSize) = GUINodeUtils.GetNodePosAndSize(addon->AtkUnitBase.RootNode);

        var descNode = addon->SelectedCardDescription;
        if (descNode == null)
        {
            SetStatus(Status.NodesNotReady);
            return;
        }

        (cachedState.descriptionPos, cachedState.descriptionSize) =
            GUINodeUtils.GetNodePosAndSize(&descNode->AtkResNode);

        var newPageIndex = (byte)addon->SelectedPage;
        var newCardIndex = (byte)addon->SelectedCardIndex;
        var newFilterMode = CardListFilterMapping.ToCollectionFilter(addon->FilterMode);

        AgentGoldSaucer* agent = null;
        if (cachedAddonAgentPtr != nint.Zero)
        {
            agent = (AgentGoldSaucer*)cachedAddonAgentPtr;
        }

        var displayCardId = TriadCardListSelectionReader.TryParseCardIdFromDisplayLabel(addon);
        var gameSelectedCardId = TriadCardListSelectionReader.ReadSelectedCardId(addon, newFilterMode, agent, displayCardId);
        var newSelectionMasked = TriadCardListSelectionReader.IsMaskedUnownedSelection(addon, displayCardId);
        var selectedCardId = gameSelectedCardId;

        if (pendingNavPage >= 0 && pendingNavCardId > 0)
        {
            if (pendingNavGraceFrames > 0)
            {
                pendingNavGraceFrames--;
            }

            var atPendingCell = newPageIndex == pendingNavPage && newCardIndex == pendingNavCell;

            if (IsPendingNavigationComplete(addon))
            {
                ClearPendingNavigation();
            }
            else if (gameSelectedCardId == pendingNavCardId)
            {
                ClearPendingNavigation();
            }
            else if (gameSelectedCardId > 0 &&
                     gameSelectedCardId != pendingNavSourceCardId &&
                     gameSelectedCardId != pendingNavCardId)
            {
                ClearPendingNavigation();
            }
            else if (gameSelectedCardId == pendingNavSourceCardId && !atPendingCell)
            {
                selectedCardId = pendingNavCardId;
                if (IsCardUnowned(pendingNavCardId))
                {
                    newSelectionMasked = true;
                }
            }
        }

        var selectionChanged = cachedState.pageIndex != newPageIndex ||
                               cachedState.cardIndex != newCardIndex ||
                               cachedState.filterMode != newFilterMode ||
                               cachedState.selectionMasked != newSelectionMasked ||
                               cachedState.iconId != AddonGSInfoCardListExtensions.CardIconId(addon) ||
                               cachedState.numU != AddonGSInfoCardListExtensions.NumSideU(addon) ||
                               cachedState.numL != AddonGSInfoCardListExtensions.NumSideL(addon) ||
                               cachedState.numD != AddonGSInfoCardListExtensions.NumSideD(addon) ||
                               cachedState.numR != AddonGSInfoCardListExtensions.NumSideR(addon) ||
                               cachedState.rarity != AddonGSInfoCardListExtensions.CardRarity(addon) ||
                               cachedState.type != (byte)AddonGSInfoCardListExtensions.CardType(addon) ||
                               cachedState.selectedCardId != selectedCardId;

        cachedState.numU = AddonGSInfoCardListExtensions.NumSideU(addon);
        cachedState.numL = AddonGSInfoCardListExtensions.NumSideL(addon);
        cachedState.numD = AddonGSInfoCardListExtensions.NumSideD(addon);
        cachedState.numR = AddonGSInfoCardListExtensions.NumSideR(addon);
        cachedState.rarity = AddonGSInfoCardListExtensions.CardRarity(addon);
        cachedState.type = (byte)AddonGSInfoCardListExtensions.CardType(addon);
        cachedState.iconId = AddonGSInfoCardListExtensions.CardIconId(addon);
        cachedState.pageIndex = newPageIndex;
        cachedState.cardIndex = newCardIndex;
        cachedState.filterMode = newFilterMode;
        cachedState.isDeckEditMode = TriadDeckEditUi.IsDeckEditScreenOpen();
        cachedState.selectedCardId = selectedCardId;
        cachedState.selectionMasked = newSelectionMasked;

        var resolvedId = cachedState.ResolveCardId(new());
        if (selectionChanged || resolvedId != lastNotifiedCardId)
        {
            lastNotifiedCardId = resolvedId;
            OnUIStateChanged?.Invoke(cachedState);
        }

        TickPendingCardNavigation(addonPtr);

        SetStatus(Status.NoErrors);
    }

    public void RefreshLiveSelectionState()
    {
        var addonPtr = ResolveAddonPtr();
        if (addonPtr != nint.Zero)
        {
            OnAddonUpdate(addonPtr);
        }
    }

    public TriadCard? ResolveSelectedCard()
    {
        if (cachedState.selectedCardId > 0)
        {
            var card = TriadCardDB.Get().FindById(cachedState.selectedCardId);
            if (card != null)
            {
                return card;
            }
        }

        if (cachedState.IsMaskedSelection())
        {
            return cachedState.ToTriadCardFromGrid(new());
        }

        var fromGrid = cachedState.ToTriadCardFromGrid(new());
        if (fromGrid != null)
        {
            return fromGrid;
        }

        var fromIcon = TriadCardDB.Get().TryGetCardIdFromIconId(cachedState.iconId);
        if (fromIcon >= 0)
        {
            return TriadCardDB.Get().FindById(fromIcon);
        }

        return cachedState.ToTriadCard(new());
    }

    public bool SetPageAndGridView(int pageIndex, int cellIndex, int cardId = 0)
    {
        var addonPtr = ResolveAddonPtr();
        OnAddonShown(addonPtr);

        if (addonPtr == nint.Zero || cachedAddonAgentPtr == nint.Zero)
        {
            return false;
        }

        var addon = (AddonGSInfoCardList*)addonPtr;
        var agent = (AgentGoldSaucer*)cachedAddonAgentPtr;
        var filterMode = CardListFilterMapping.ToCollectionFilter(addon->FilterMode);
        var displayCardId = TriadCardListSelectionReader.TryParseCardIdFromDisplayLabel(addon);
        pendingNavSourceCardId = TriadCardListSelectionReader.ReadSelectedCardId(addon, filterMode, agent, displayCardId);

        pendingNavCardId = cardId;
        pendingNavAttempts = 90;
        pendingNavGraceFrames = 5;

        if (pageIndex < 0 || pageIndex >= GameCardDB.MaxGridPages || cellIndex < 0 || cellIndex >= GameCardDB.MaxGridCells)
        {
            pendingNavPage = -1;
            pendingNavCell = -1;
            if (cardId > 0)
            {
                cachedState.selectedCardId = cardId;
                cachedState.selectionMasked = IsCardUnowned(cardId);
                OnUIStateChanged?.Invoke(cachedState);
            }

            return false;
        }

        pendingNavPage = pageIndex;
        pendingNavCell = cellIndex;

        if (cardId > 0)
        {
            cachedState.selectedCardId = cardId;
            cachedState.selectionMasked = IsCardUnowned(cardId);
        }

        OnUIStateChanged?.Invoke(cachedState);

        TickPendingCardNavigation(addonPtr);
        return true;
    }

    private static bool IsCardUnowned(int cardId)
    {
        if (cardId <= 0)
        {
            return false;
        }

        if (TriadMemoryReads.IsAvailable)
        {
            return !TriadMemoryReads.TryIsCardOwned(cardId);
        }

        return !GameCardDB.Get().ownedCardIds.Contains(cardId);
    }

    private void ClearPendingNavigation()
    {
        pendingNavPage = -1;
        pendingNavCell = -1;
        pendingNavCardId = 0;
        pendingNavAttempts = 0;
        pendingNavGraceFrames = 0;
        pendingNavSourceCardId = -1;
    }

    private void TickPendingCardNavigation(nint addonPtr)
    {
        if (pendingNavPage < 0 || addonPtr == nint.Zero)
        {
            return;
        }

        if (--pendingNavAttempts <= 0)
        {
            ClearPendingNavigation();
            return;
        }

        var addon = (AddonGSInfoCardList*)addonPtr;
        if (cachedAddonAgentPtr != nint.Zero)
        {
            var agent = (AgentGoldSaucer*)cachedAddonAgentPtr;
            agent->EditDeckSelectedPage = pendingNavPage;
            agent->EditDeckSelectedCardIndex = pendingNavCell;
        }

        if (addon->SelectedPage != pendingNavPage)
        {
            // Old FFXIVClientStructs has no separate RequestedPage hint field; the tab
            // controller call below is what actually drives the page change.
            addon->TabController.SetTabIndexAndCallBack(pendingNavPage);
            addon->AtkUnitBase.Update(0);
            return;
        }

        if (!GoldSaucerCardListUi.TryClickCell(addonPtr, pendingNavCell))
        {
            return;
        }

        if (IsPendingNavigationComplete(addon))
        {
            ClearPendingNavigation();
        }
    }

    private bool IsPendingNavigationComplete(AddonGSInfoCardList* addon)
    {
        if (addon->SelectedPage != pendingNavPage || addon->SelectedCardIndex != pendingNavCell)
        {
            return false;
        }

        if (pendingNavCardId <= 0)
        {
            return true;
        }

        var gridMatch = GameCardDB.Get().FindByGridLocationAnyFilter(
            pendingNavPage,
            pendingNavCell,
            cachedState.filterMode);
        if (gridMatch?.CardId == pendingNavCardId)
        {
            return true;
        }

        var fromDisplay = TriadCardListSelectionReader.TryParseCardIdFromDisplayLabel(addon);
        if (fromDisplay == pendingNavCardId)
        {
            return true;
        }

        if (TriadCardListSelectionReader.IconMatchesCard(AddonGSInfoCardListExtensions.CardIconId(addon), pendingNavCardId))
        {
            return AddonStatsMatchCard(addon, pendingNavCardId);
        }

        return false;
    }

    private static bool AddonStatsMatchCard(AddonGSInfoCardList* addon, int cardId)
    {
        var hasSideStats = AddonGSInfoCardListExtensions.NumSideU(addon) != 0 || AddonGSInfoCardListExtensions.NumSideL(addon) != 0 || AddonGSInfoCardListExtensions.NumSideD(addon) != 0 || AddonGSInfoCardListExtensions.NumSideR(addon) != 0;
        if (!hasSideStats)
        {
            return true;
        }

        var expectedCard = TriadCardDB.Get().FindById(cardId);
        if (expectedCard == null || expectedCard.Sides == null || expectedCard.Sides.Length < 4)
        {
            return true;
        }

        return AddonGSInfoCardListExtensions.NumSideU(addon) == expectedCard.Sides[0] &&
               AddonGSInfoCardListExtensions.NumSideL(addon) == expectedCard.Sides[1] &&
               AddonGSInfoCardListExtensions.NumSideD(addon) == expectedCard.Sides[2] &&
               AddonGSInfoCardListExtensions.NumSideR(addon) == expectedCard.Sides[3];
    }

    private static nint ResolveAddonPtr()
    {
        for (var i = 0; i < 8; i++)
        {
            var handle = Svc.GameGui.GetAddonByName("GSInfoCardList", i);
            if (handle != nint.Zero)
            {
                return handle;
            }
        }

        return nint.Zero;
    }

    public static nint LoadFailsafeAgent()
    {
        var uiModule = (UIModule*)Svc.GameGui.GetUIModule();
        if (uiModule != null)
        {
            var agentModule = uiModule->GetAgentModule();
            if (agentModule != null)
            {
                var agentPtr = agentModule->GetAgentByInternalId(AgentId.GoldSaucer);
                if (agentPtr != null)
                {
                    return new(agentPtr);
                }
            }
        }

        return nint.Zero;
    }

    private void SetStatus(Status newStatus)
    {
        if (status != newStatus)
        {
            var wasVisible = IsVisible;
            status = newStatus;

            if (wasVisible != IsVisible)
            {
                OnVisibilityChanged?.Invoke(IsVisible);
            }
        }
    }
}
