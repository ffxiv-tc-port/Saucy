using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
namespace Saucy.TripleTriad.UI;

public class UIReaderTriadGame : IUIReader
{
    public enum Status
    {
        NoErrors,
        AddonNotFound,
        AddonNotVisible,
        PvPMatch,
        FailedToReadMove,
        FailedToReadRules,
        FailedToReadRedPlayer,
        FailedToReadCards
    }

    private nint addonPtr;

    public UIStateTriadGame? currentState;
    public Status status = Status.AddonNotFound;
    public bool HasErrors => status >= Status.FailedToReadMove;
    public bool IsVisible => status is not Status.AddonNotFound and not Status.AddonNotVisible;

    public string GetAddonName() => "TripleTriad";

    public void OnAddonLost()
    {
        SetStatus(Status.AddonNotFound);
        SetCurrentState(null);
        addonPtr = nint.Zero;
    }

    public void OnAddonShown(nint addonPtr)
    {
        this.addonPtr = addonPtr;
        TriadMgpTracker.SnapshotAtMatchStart();
        TriadRun.matchNpc = null;
    }

    public unsafe void OnAddonUpdate(nint addonPtr)
    {
        this.addonPtr = addonPtr;
        var addon = (AddonTripleTriad*)addonPtr;
        if (addon == null)
        {
            return;
        }

        var newState = BuildState(addon);
        SetCurrentState(newState);

        if (newState.isPvP)
        {
            SetStatus(Status.PvPMatch);
        }
    }

    public unsafe void SyncCurrentFromAddon(nint addonPtr)
    {
        var addon = (AddonTripleTriad*)addonPtr;
        if (addon == null)
        {
            return;
        }

        status = Status.NoErrors;
        currentState = BuildState(addon);
    }

    public unsafe void RefreshFromVisibleAddon()
    {
        if (!TriadLocalClientStructs.TryGetBoard(out var addon))
        {
            return;
        }

        addonPtr = (nint)addon;
        SyncCurrentFromAddon((nint)addon);
    }

    private unsafe UIStateTriadGame BuildState(AddonTripleTriad* addon)
    {
        status = Status.NoErrors;
        var newState = new UIStateTriadGame
        {
            isPlayerTurn = TriadTurnState.ReadIsPlayerTurn(&addon->AtkUnitBase), move = (byte)addon->TurnState, turnBannerVisible = TriadTurnState.IsTurnBannerVisible(&addon->AtkUnitBase)
        };

        using var nodeArrL0 = GUINodeUtils.BorrowImmediateChildNodes(addon->AtkUnitBase.RootNode);
        newState.redPlayerDesc = GetUIDescriptionRedPlayer(nodeArrL0);
        if (newState.redPlayerDesc.Count == 0)
        {
            newState.redPlayerDesc = CollectVisibleNpcCandidateText(addon->AtkUnitBase.RootNode);
        }
        newState.rules = GetUIDescriptionRules(nodeArrL0);
        newState.isPvP = GetUIStatePvP(nodeArrL0) && newState.redPlayerDesc.Count == 0;

        for (var idx = 0; idx < 5; idx++)
        {
            newState.blueDeck[idx] = GetCardData(addon->BlueDeck[idx]);
            newState.redDeck[idx] = GetCardData(addon->RedDeck[idx]);
        }

        for (var idx = 0; idx < 9; idx++)
        {
            newState.board[idx] = GetCardData(addon->Board[idx]);
        }

        return newState;
    }

    public event Action<UIStateTriadGame?>? OnUIStateChanged;

    private void SetStatus(Status newStatus)
    {
        if (status != newStatus)
        {
            status = newStatus;
            if (HasErrors)
            {
                Svc.Log.Error("ui reader error: " + newStatus);
            }
        }
    }

    private void SetCurrentState(UIStateTriadGame? newState)
    {
        var isEmpty = newState == null;
        var wasEmpty = currentState == null;

        if (isEmpty && wasEmpty)
        {
            return;
        }

        var changed = (isEmpty != wasEmpty);
        if (!changed && newState != null && currentState != null)
        {
            changed = !currentState.Equals(newState);
        }

        if (changed)
        {
            currentState = newState;
            OnUIStateChanged?.Invoke(newState);
        }
    }

    private unsafe List<string> GetUIDescriptionRedPlayer(in GUINodeUtils.BorrowedNodes level0)
    {
        var listRedDesc = new List<string>();

        var nodeName0 = GUINodeUtils.PickNode(level0, 6, 12);
        using var nodeArrNameL1 = GUINodeUtils.BorrowImmediateChildNodes(nodeName0);
        var nodeNameL1 = GUINodeUtils.PickNode(nodeArrNameL1, 0, 5);
        using var nodeArrNameL2 = GUINodeUtils.BorrowAllChildNodes(nodeNameL1);
        var numParsed = 0;
        if (nodeArrNameL2.Count > 0)
        {
            for (var nodeIdx = 0; nodeIdx < nodeArrNameL2.Count; nodeIdx++)
            {
                var testNode = nodeArrNameL2[nodeIdx];
                var isVisible = (testNode != null) && (testNode->NodeFlags & NodeFlags.Visible) == NodeFlags.Visible;
                if (isVisible)
                {
                    numParsed++;
                    var text = GUINodeUtils.GetNodeText(testNode);
                    if (!string.IsNullOrEmpty(text))
                    {
                        listRedDesc.Add(text);
                    }
                }
            }
        }

        return listRedDesc;
    }

    private unsafe List<string> CollectVisibleNpcCandidateText(AtkResNode* rootNode)
    {
        var candidates = new List<string>();
        CollectVisibleTextNodes(rootNode, candidates);
        return candidates;
    }

    private static unsafe void CollectVisibleTextNodes(AtkResNode* node, List<string> output)
    {
        if (node == null || !node->IsVisible())
        {
            return;
        }

        if ((int)node->Type == (int)NodeType.Text)
        {
            var text = GUINodeUtils.GetNodeText(node)?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Length >= 3)
            {
                output.Add(text);
            }
        }

        using var children = GUINodeUtils.BorrowImmediateChildNodes(node);
        for (var idx = 0; idx < children.Count; idx++)
        {
            CollectVisibleTextNodes(children[idx], output);
        }
    }

    private unsafe List<string> GetUIDescriptionRules(in GUINodeUtils.BorrowedNodes level0)
    {
        var listRuleDesc = new List<string>();

        var nodeRule0 = GUINodeUtils.PickNode(level0, 4, 12);
        using var nodeArrRule = GUINodeUtils.BorrowImmediateChildNodes(nodeRule0);
        if (nodeArrRule.Count == 5)
        {
            for (var idx = 0; idx < 4; idx++)
            {
                var text = GUINodeUtils.GetNodeText(nodeArrRule[4 - idx]);
                if (!string.IsNullOrEmpty(text))
                {
                    listRuleDesc.Add(text);
                }
            }
        }
        else
        {
            for (var nodeIdx = 0; nodeIdx < nodeArrRule.Count; nodeIdx++)
            {
                var text = GUINodeUtils.GetNodeText(nodeArrRule[nodeIdx]);
                if (!string.IsNullOrEmpty(text))
                {
                    listRuleDesc.Add(text);
                }
            }
        }

        return listRuleDesc;
    }

    private unsafe bool GetUIStatePvP(in GUINodeUtils.BorrowedNodes level0)
    {
        var nodePvPButton = GUINodeUtils.PickNode(level0, 11, 12);
        return nodePvPButton != null && nodePvPButton->IsVisible();
    }

    private unsafe (string, bool) GetCardTextureData(AddonTripleTriad.TripleTriadCard addonCard)
    {
        var nodeA = GUINodeUtils.PickChildNode(addonCard.CardDropControl, 1, 3);
        var nodeB = GUINodeUtils.PickChildNode(nodeA, 0, 2);
        var nodeC = GUINodeUtils.PickChildNode(nodeB, 3, 21);
        var texPath = GUINodeUtils.GetNodeTexturePath(nodeC);

        var isLocked = (nodeB != null) && (nodeB->MultiplyRed < 100);
        return (texPath ?? "", isLocked);
    }

    private UIStateTriadCard GetCardData(AddonTripleTriad.TripleTriadCard addonCard)
    {
        var resultOb = new UIStateTriadCard();
        if (addonCard.HasCard)
        {
            resultOb.isPresent = true;
            resultOb.owner = (byte)addonCard.CardOwner;

            var isKnown = (addonCard.NumSideU != 0);
            if (isKnown)
            {
                resultOb.numU = addonCard.NumSideU;
                resultOb.numL = addonCard.NumSideL;
                resultOb.numD = addonCard.NumSideD;
                resultOb.numR = addonCard.NumSideR;
                resultOb.rarity = addonCard.CardRarity;
                resultOb.type = (byte)addonCard.CardType;

                (resultOb.texturePath, resultOb.isLocked) = GetCardTextureData(addonCard);
            }
        }

        return resultOb;
    }
}
