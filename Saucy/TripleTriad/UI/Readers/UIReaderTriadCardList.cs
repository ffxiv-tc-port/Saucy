using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
    private bool readyGateBlocked;
    private string readyGateReason = "";

    public Status status = Status.AddonNotFound;
    public bool IsVisible => status is not Status.AddonNotFound and not Status.AddonNotVisible;
    public bool IsPendingCardNavigation => pendingNavPage >= 0;

    public string GetAddonName() => "GSInfoCardList";

    public void OnAddonLost()
    {
        ClearPendingNavigation();
        SetStatus(Status.AddonNotFound);
    }

    public void OnAddonShown(nint addonPtr)
    {
#if DEBUG
        // agent 指標本身已不再快取(見 ResolveAgent),這裡只保留原本「走了 failsafe 路徑」的診斷。
        nint shownAgentPtr = (addonPtr != nint.Zero) ? Svc.GameGui.FindAgentInterface(addonPtr) : nint.Zero;
        if (shownAgentPtr == nint.Zero)
        {
            Svc.Log.Info($"using agentPtr from failsafe: {(ulong)LoadFailsafeAgent():X}");
        }
#endif

        if (addonPtr != nint.Zero)
        {
            var addon = (AddonGSInfoCardList*)addonPtr;
            if (IsAddonReadyForNodeReads(addon, out _))
            {
                (cachedState.screenPos, cachedState.screenSize) =
                    GUINodeUtils.GetNodePosAndSize(addon->AtkUnitBase.RootNode);
            }

            SetStatus(Status.NodesNotReady);
        }
    }

    public void OnAddonUpdate(nint addonPtr)
    {
        if (addonPtr == nint.Zero)
        {
            SetStatus(Status.AddonNotFound);
            return;
        }

        var addon = (AddonGSInfoCardList*)addonPtr;

        // 🔴 這道閘門必須留在「每次進入」的位置:拆除可以發生在任何一幀,而且 addon 指標是每幀
        // 重查的,所以「上一幀就緒」對這一幀沒有任何保證。
        if (!EnsureAddonReadyForNodeReads(addon))
        {
            return;
        }

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

        // 🔴 每次使用時重新取得,不沿用任何跨幀保存的 agent 指標(見 ResolveAgent)。
        // 取不到時是 null,ReadSelectedCardId 的 agent 參數本來就允許 null。
        var agent = ResolveAgent(addonPtr);

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

        if (addonPtr == nint.Zero)
        {
            return false;
        }

        // 🔴 每次使用時重新取得,不沿用跨幀快取(見 ResolveAgent)。
        // 判斷點刻意留在就緒閘門之前,與原本「agent 指標為 0 就直接 return false」的順序一致。
        var agent = ResolveAgent(addonPtr);
        if (agent == null)
        {
            return false;
        }

        var addon = (AddonGSInfoCardList*)addonPtr;
        if (!EnsureAddonReadyForNodeReads(addon))
        {
            return false;
        }

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

        // 🔴 這裡是「寫」不是「讀」:寫到一個過期的 agent 位址不會擲例外也不會有任何徵兆,
        // 所以更不能用快取指標。每次重查。
        var agent = ResolveAgent(addonPtr);
        if (agent != null)
        {
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

    /// <summary>
    /// 進入任何節點讀取前的就緒閘門。
    /// </summary>
    /// <remarks>
    /// 🔴 這裡擋的不是「指標是不是 null」—— addon 指標每幀都由 GetAddonByName 重查,本身沒有跨幀
    /// 保存的問題。擋的是另一件事:AtkUnitBase 還掛在 addon 清單上(所以 GetAddonByName 仍然回得到,
    /// RootNode 欄位也還留著舊值),但 UldManager 已經把節點釋放掉了。此時 SelectedCardName /
    /// SelectedCardNumber / SelectedCardDescription 這些欄位是「非 null 的野指標」,判空完全擋不住,
    /// GUINodeUtils.GetNodeText 一讀 node->Type 就是 AccessViolationException。
    /// <para>
    /// 🔴 AVE 在 .NET Core 是 corrupted-state exception,try/catch 與 HookSafety.ExecuteSafe 都攔不到,
    /// 整個遊戲行程直接死,所以只能靠事前閘門,不能靠例外隔離。
    /// </para>
    /// <para>
    /// 🔑 真正能分辨死活的是 UldManager.LoadedState:節點的生命週期由它管,Unload() 會先把它設回
    /// Unloaded 才去釋放節點,是唯一在「欄位還是非 null」時仍然正確的旗標。它是 AtkUnitBase 的內嵌
    /// 欄位(位址就在 addon 自己身上),讀它不需要任何指標跳躍,所以放在最前面檢查是安全的。
    /// </para>
    /// <para>
    /// ⚠️ 可見度沿用 UIReaderScheduler.IsAddonVisible 既有的寬鬆定義(AtkUnitBase.IsVisible 或
    /// RootNode 自己可見),刻意不收緊成只看 AtkUnitBase.IsVisible —— 收緊等於回退既有行為。
    /// </para>
    /// </remarks>
    private static bool IsAddonReadyForNodeReads(AddonGSInfoCardList* addon, out string reason)
    {
        if (addon == null)
        {
            reason = "addon 指標為 null";
            return false;
        }

        if (addon->AtkUnitBase.UldManager.LoadedState != AtkLoadState.Loaded)
        {
            reason = $"ULD 尚未載入或已卸載(LoadedState={addon->AtkUnitBase.UldManager.LoadedState})";
            return false;
        }

        if (addon->AtkUnitBase.RootNode == null)
        {
            reason = "RootNode 為 null";
            return false;
        }

        if (!addon->AtkUnitBase.IsVisible && !addon->AtkUnitBase.RootNode->IsVisible())
        {
            reason = "addon 不可見";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// <see cref="IsAddonReadyForNodeReads"/> 的具狀態包裝:未就緒時設定狀態並安靜 return。
    /// </summary>
    /// <remarks>
    /// 只在「就緒 ↔ 未就緒」轉換時印一行 Information(使用者跑 LogLevel 2,Debug/Verbose 收不到),
    /// 所以逐幀阻擋不會刷版面。
    /// </remarks>
    private bool EnsureAddonReadyForNodeReads(AddonGSInfoCardList* addon)
    {
        if (IsAddonReadyForNodeReads(addon, out var reason))
        {
            if (readyGateBlocked)
            {
                readyGateBlocked = false;
                Svc.Log.Information($"[卡片收藏] GSInfoCardList 已就緒,恢復讀取節點(先前擋下的原因:{readyGateReason})。");
            }

            return true;
        }

        if (!readyGateBlocked || readyGateReason != reason)
        {
            readyGateBlocked = true;
            readyGateReason = reason;
            Svc.Log.Information($"[卡片收藏] GSInfoCardList 尚未就緒,跳過本幀所有節點讀取:{reason}");
        }

        SetStatus(Status.NodesNotReady);
        return false;
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

    /// <summary>
    /// 每次使用時重新取得金碟遊樂場 agent,取不到回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 這個方法取代了原本的 <c>cachedAddonAgentPtr</c> 欄位。原本的做法是在 OnAddonShown
    /// 解析一次就存進欄位,之後 OnAddonUpdate / SetPageAndGridView / TickPendingCardNavigation
    /// 全部沿用那份快取 —— 那就是「跨幀保存原生指標」:存下去那一刻起就再也不會重新解析,
    /// 而且欄位只有 OnAddonLost 會清,addon 在兩次 shown/lost 之間被換掉、agent module 重建、
    /// 或當初 FindAgentInterface 回的是別的 addon 的 agent,快取都不會知道。
    /// <para>
    /// 🔴 這裡的用途包含<b>寫入</b>(EditDeckSelectedPage / EditDeckSelectedCardIndex),
    /// 寫到過期位址不會擲例外、也不會有任何徵兆;而 AccessViolationException 在 .NET Core 是
    /// corrupted-state exception,try/catch 一樣攔不到。所以只能靠「每次重查」,不能靠例外隔離。
    /// </para>
    /// <para>
    /// 🔑 保存的是<b>身分</b>不是位址:當幀的 addon 指標(由 GetAddonByName 重查而來)與
    /// <see cref="AgentId.GoldSaucer" />。解析順序刻意與原本的 OnAddonShown 一致 ——
    /// 先問 addon 對應的 agent,失敗才退到 agent module 的 failsafe 查詢。
    /// </para>
    /// </remarks>
    private static AgentGoldSaucer* ResolveAgent(nint addonPtr)
    {
        // ⚠️ 型別要寫死 nint:FindAgentInterface 回的是 AgentInterfacePtr,與 nint 互為隱含轉換,
        // 用 var 接會是 CS0172(條件運算式型別無法判定)。
        nint agentPtr = (addonPtr != nint.Zero) ? Svc.GameGui.FindAgentInterface(addonPtr) : nint.Zero;

        if (agentPtr == nint.Zero)
        {
            agentPtr = LoadFailsafeAgent();
        }

        return (AgentGoldSaucer*)agentPtr;
    }

    public static nint LoadFailsafeAgent()
    {
        var uiModule = (UIModule*)Svc.GameGui.GetUIModule().Address;
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
