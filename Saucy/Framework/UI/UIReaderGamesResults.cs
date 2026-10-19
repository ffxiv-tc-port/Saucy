using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.AirForce;
using Saucy.TripleTriad.Utils;
using System;
namespace Saucy.Framework.UI;

public class UIReaderGamesResults : IUIReader
{
    private UIStateAirForceResults airForceResults = new();

    private bool needsNotify;
    public Action<UIStateAirForceResults>? OnAirForceUpdated;

    public bool HasResultsUI { get; private set; }

    public string GetAddonName() => "GoldSaucerReward";

    public void OnAddonLost() => SetIsResultsUI(false);

    public void OnAddonShown(nint addonPtr)
    {
        needsNotify = true;
        if (AirForceAutomation.ShouldTrackReward)
        {
            SetIsResultsUI(true);
        }

        airForceResults = new();
    }

    public unsafe void OnAddonUpdate(nint addonPtr)
    {
        var baseNode = (AtkUnitBase*)addonPtr;
        if (baseNode == null || !needsNotify)
        {
            return;
        }

        if (!AirForceAutomation.ShouldTrackReward)
        {
            needsNotify = false;
            return;
        }

        UpdateCachedState(baseNode);

        var notified = false;

        if (AirForceAutomation.ShouldTrackReward)
        {
            if (airForceResults.numMGP >= 0)
            {
                notified = true;
                OnAirForceUpdated?.Invoke(airForceResults);
            }
        }

        if (notified)
        {
            needsNotify = false;
        }
    }

    public void SetIsResultsUI(bool value) => HasResultsUI = value;

    private unsafe void UpdateCachedState(AtkUnitBase* baseNode)
    {
        if (!AirForceAutomation.ShouldTrackReward)
        {
            airForceResults.numMGP = -1;
        }

        if (!TryGetRewardMgpTextNode(baseNode, out var number))
        {
            if (AirForceAutomation.ShouldTrackReward)
            {
                airForceResults.numMGP = -1;
            }

            return;
        }

        if (AirForceAutomation.ShouldTrackReward)
        {
            if (!GoldSaucerRewardMgpParser.TryParseMgpDigits(GUINodeUtils.GetNodeText((AtkResNode*)number), out airForceResults.numMGP))
            {
                airForceResults.numMGP = -1;
            }
        }
    }

    private static unsafe bool TryGetRewardMgpTextNode(AtkUnitBase* baseNode, out AtkTextNode* textNode)
    {
        textNode = null;
        if (baseNode == null)
        {
            return false;
        }

        ref var uld = ref baseNode->UldManager;
        if (uld.NodeListCount <= 4)
        {
            return false;
        }

        var node4 = uld.NodeList[4];
        if (node4 == null)
        {
            return false;
        }

        var component = node4->GetComponent();
        if (component == null)
        {
            return false;
        }

        ref var innerUld = ref component->UldManager;
        if (innerUld.NodeListCount <= 2)
        {
            return false;
        }

        var node2 = innerUld.NodeList[2];
        if (node2 == null)
        {
            return false;
        }

        var innerComponent = node2->GetComponent();
        if (innerComponent == null)
        {
            return false;
        }

        ref var deepestUld = ref innerComponent->UldManager;
        if (deepestUld.NodeListCount <= 1)
        {
            return false;
        }

        var node1 = deepestUld.NodeList[1];
        if (node1 == null)
        {
            return false;
        }

        textNode = node1->GetAsAtkTextNode();
        return textNode != null;
    }
}
