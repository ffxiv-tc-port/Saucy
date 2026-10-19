using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Saucy;

/// <summary>
/// 台服 7.25 起遊戲文字的中間點由「·」(U+00B7) 改成「・」(U+30FB)，7.25 前存下的 NPC 名
/// （對戰次數統計、寫進遊戲的「(Saucy)」牌組名）仍是舊寫法。
/// 以 NPC 名為鍵或逐字比對 NPC 名的地方一律先經過 <see cref="Normalize"/>，以 U+30FB 為準。
/// </summary>
internal static class NpcNameText
{
    private const char LegacyMiddleDot = (char)0x00B7;
    private const char MiddleDot = (char)0x30FB;

    /// <summary>不含舊寫法時回傳同一個字串、不配置，每幀路徑可以直接呼叫。</summary>
    [return: NotNullIfNotNull(nameof(name))]
    public static string? Normalize(string? name) =>
        name != null && name.Contains(LegacyMiddleDot) ? name.Replace(LegacyMiddleDot, MiddleDot) : name;

    /// <summary>計數表的鍵改成新寫法，兩種寫法並存時次數相加（保留先出現的位置）。
    /// 回傳改寫的鍵數；0＝沒有舊寫法，原表不動。</summary>
    public static int NormalizeCountKeys(ref Dictionary<string, int> counts)
    {
        if (counts == null)
        {
            return 0;
        }

        var renamed = 0;
        foreach (var key in counts.Keys)
        {
            if (key.Contains(LegacyMiddleDot))
            {
                renamed++;
            }
        }

        if (renamed == 0)
        {
            return 0;
        }

        var result = new Dictionary<string, int>(counts.Count, counts.Comparer);
        foreach (var (key, value) in counts)
        {
            var name = Normalize(key);
            result[name] = result.TryGetValue(name, out var existing) ? existing + value : value;
        }

        counts = result;
        return renamed;
    }
}
