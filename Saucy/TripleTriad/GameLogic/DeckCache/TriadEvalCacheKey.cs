using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
namespace Saucy.TripleTriad.GameLogic;

internal static class TriadEvalCacheKey
{
    private const string RulesVersion = "v4";

    // The key is a pure function of npcName, the runtime type of each rule in list order, and the current culture
    // (OrderBy uses the culture-sensitive default string comparer): a hit needs all three to match. A null rule
    // never hits, so it still throws like the uncached path.
    private const int RecentKeyCount = 4;
    private static readonly object RecentLock = new();
    private static readonly RecentKey[] recentKeys = new RecentKey[RecentKeyCount];
    private static int nextRecentSlot;

    private sealed class RecentKey
    {
        public CultureInfo Culture = CultureInfo.InvariantCulture;
        public string NpcName = string.Empty;
        public Type[] RuleTypes = [];
        public string Key = string.Empty;
    }

    public static string Build(TriadNpc npc, IEnumerable<TriadGameModifier> regionMods) =>
        npc == null ? string.Empty : Build(npc.Name, regionMods);

    public static string Build(string npcName, IEnumerable<TriadGameModifier> rules)
    {
        if (string.IsNullOrEmpty(npcName))
        {
            return string.Empty;
        }

        if (rules == null)
        {
            return npcName;
        }

        if (rules is not IReadOnlyList<TriadGameModifier> ruleList)
        {
            return BuildUncached(npcName, rules);
        }

        lock (RecentLock)
        {
            foreach (var recent in recentKeys)
            {
                if (recent != null && IsSameInput(recent, npcName, ruleList))
                {
                    return recent.Key;
                }
            }
        }

        var culture = CultureInfo.CurrentCulture;
        var key = BuildUncached(npcName, ruleList);
        var ruleTypes = new Type[ruleList.Count];
        for (var idx = 0; idx < ruleTypes.Length; idx++)
        {
            ruleTypes[idx] = ruleList[idx].GetType();
        }

        lock (RecentLock)
        {
            recentKeys[nextRecentSlot] = new() { Culture = culture, NpcName = npcName, RuleTypes = ruleTypes, Key = key };
            nextRecentSlot = (nextRecentSlot + 1) % RecentKeyCount;
        }

        return key;
    }

    private static bool IsSameInput(RecentKey recent, string npcName, IReadOnlyList<TriadGameModifier> rules)
    {
        if (!string.Equals(recent.NpcName, npcName, StringComparison.Ordinal) || recent.RuleTypes.Length != rules.Count ||
            !recent.Culture.Equals(CultureInfo.CurrentCulture))
        {
            return false;
        }

        for (var idx = 0; idx < recent.RuleTypes.Length; idx++)
        {
            var rule = rules[idx];
            if (rule == null || rule.GetType() != recent.RuleTypes[idx])
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildUncached(string npcName, IEnumerable<TriadGameModifier> rules)
    {
        var builder = new StringBuilder(npcName);
        builder.Append('|').Append(RulesVersion);
        if (rules != null)
        {
            foreach (var mod in rules.OrderBy(mod => mod.GetType().FullName))
            {
                builder.Append('|');
                builder.Append(mod.GetType().Name);
            }
        }

        return builder.ToString();
    }
}
