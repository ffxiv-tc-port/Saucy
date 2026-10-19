using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.TripleTriad.Utils;
using System;
using System.Linq;

namespace Saucy.Framework.UI;

internal static unsafe class GoldSaucerRewardMgpParser
{
    public static bool TryParseFromAddon(AtkUnitBase* baseNode, out int mgp)
    {
        mgp = -1;
        if (baseNode is null)
        {
            return false;
        }

        TryParseFromUldManager(baseNode, ref mgp);

        if (mgp < 0 && baseNode->RootNode is not null)
        {
            ScanVisibleTree(baseNode->RootNode, ref mgp);
        }

        return mgp >= 0;
    }

    public static bool TryParseFromVisibleTree(AtkResNode* root, out int mgp)
    {
        mgp = -1;
        if (root is null)
        {
            return false;
        }

        ScanVisibleTree(root, ref mgp);
        return mgp >= 0;
    }

    private static void TryParseFromUldManager(AtkUnitBase* baseNode, ref int mgp)
    {
        ref var uld = ref baseNode->UldManager;
        // 🔴 NodeListCount 非 0 不保證 NodeList 已配置（元件還在載入時就是 null）——
        //    上界之外還要判指標，否則索引到的是野位址，AccessViolation 攔不到。
        if (uld.NodeList is null)
        {
            return;
        }

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node is null)
            {
                continue;
            }

            TryParseFromNode(node, ref mgp);
            var component = node->GetComponent();
            if (component is null)
            {
                continue;
            }

            ref var innerUld = ref component->UldManager;
            if (innerUld.NodeList is null)
            {
                continue;
            }

            for (var j = 0; j < innerUld.NodeListCount; j++)
            {
                var innerNode = innerUld.NodeList[j];
                if (innerNode is null)
                {
                    continue;
                }

                TryParseFromNode(innerNode, ref mgp);
                var innerComponent = innerNode->GetComponent();
                if (innerComponent is null)
                {
                    continue;
                }

                ref var deepestUld = ref innerComponent->UldManager;
                if (deepestUld.NodeList is null)
                {
                    continue;
                }

                for (var k = 0; k < deepestUld.NodeListCount; k++)
                {
                    var deepestNode = deepestUld.NodeList[k];
                    if (deepestNode is not null)
                    {
                        TryParseFromNode(deepestNode, ref mgp);
                    }
                }
            }
        }
    }

    private static void ScanVisibleTree(AtkResNode* root, ref int mgp)
    {
        foreach (var node in GUINodeUtils.GetAllChildNodes(root) ?? [])
        {
            if (!GUINodeUtils.IsNodeVisible(node))
            {
                continue;
            }

            TryParseFromNode(node, ref mgp);
        }
    }

    private static void TryParseFromNode(AtkResNode* node, ref int bestMgp)
    {
        var text = GUINodeUtils.GetNodeText(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 台服的 MGP 官方譯名是「金碟幣」（Addon 9506/9513/9519/9522「金碟幣持有數/上限」、
        // 9525「0 金碟幣」）；沒有這一條時台服只能退回「抓最大的數字」的模糊解析。
        if ((text.Contains("mgp", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("金碟幣", StringComparison.Ordinal)) &&
            TryParseMgpDigits(text, out var labeled) &&
            labeled > 0)
        {
            bestMgp = labeled;
            return;
        }

        if (!TryParseMgpDigits(text, out var parsed) || parsed <= 0)
        {
            return;
        }

        if (parsed > bestMgp)
        {
            bestMgp = parsed;
        }
    }

    internal static bool TryParseMgpDigits(string? text, out int mgp)
    {
        mgp = -1;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = new string([.. text.Where(char.IsDigit)]);
        return digits.Length > 0 && int.TryParse(digits, out mgp) && mgp > 0;
    }
}
