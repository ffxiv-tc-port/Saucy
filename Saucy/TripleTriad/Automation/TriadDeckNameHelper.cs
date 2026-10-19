using System;
namespace Saucy.TripleTriad;

internal static class TriadDeckNameHelper
{
    public static bool NamesMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return NpcNameText.Normalize(a.Trim()).Equals(NpcNameText.Normalize(b.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    public static bool RowMatchesNpc(string deckRowName, string expectedName, string npcName)
    {
        if (string.IsNullOrWhiteSpace(deckRowName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedName) && NamesMatch(deckRowName, expectedName))
        {
            return true;
        }

        // 7.25 前寫進遊戲的牌組名仍是舊中間點寫法，與現在的 NPC 名比對前兩邊都正規化。
        deckRowName = NpcNameText.Normalize(deckRowName);
        npcName = NpcNameText.Normalize(npcName);

        if (!string.IsNullOrEmpty(npcName) &&
            deckRowName.StartsWith(npcName, StringComparison.OrdinalIgnoreCase) &&
            deckRowName.Contains("(Sa", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var deckBase = StripSuffix(deckRowName);
        return deckBase.Length >= 4 &&
               !string.IsNullOrEmpty(npcName) &&
               npcName.StartsWith(deckBase, StringComparison.OrdinalIgnoreCase);
    }

    public static string StripSuffix(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var tagIdx = name.IndexOf("(Sa", StringComparison.OrdinalIgnoreCase);
        var stripped = tagIdx > 0 ? name[..tagIdx] : name;
        return stripped.TrimEnd(' ', '.', '…');
    }
}
