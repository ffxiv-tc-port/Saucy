#nullable disable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
namespace Saucy.TripleTriad.Data;

public class TriadNpc
{
    public TriadDeck Deck;

    public bool hasLocMarkup;
    public int Id;
    public string Name = string.Empty;
    public Regex NamePartialRegex;

    public Regex NameRegex;
    public List<TriadGameModifier> Rules;

    public TriadNpc(int id, List<TriadGameModifier> rules, int[] cardsAlways, int[] cardsPool)
    {
        Id = id;
        Rules = rules;
        Deck = new(cardsAlways, cardsPool);
        hasLocMarkup = false;
    }

    public TriadNpc(int id, List<TriadGameModifier> rules, List<TriadCard> rewards, TriadDeck deck)
    {
        Id = id;
        Rules = rules;
        Deck = deck;
        hasLocMarkup = false;
    }

    public void OnNameUpdated()
    {
        hasLocMarkup = Name.Contains('[');
        if (hasLocMarkup)
        {
            var namePattern = Regex.Replace(NormalizeNameForMatch(Name), "\\[[a-z]+\\]", ".*");
            var regexOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            NameRegex = new(namePattern, regexOptions);

            var maxMatchLen = 15;
            var partialPattern = (namePattern.Length < maxMatchLen) ? namePattern : namePattern[..maxMatchLen].TrimEnd('*').TrimEnd('.');
            NamePartialRegex = new(partialPattern, regexOptions);
        }
    }

    // 兩邊都經過 NpcNameText.Normalize：7.25 前的舊中間點寫法與新寫法視為同一個名字。
    // 非 ASCII 名字的 ToLowerInvariant 每次都配置新字串，逐一比對整份清單時先正規化一次再傳 *Normalized。
    internal static string NormalizeNameForMatch(string name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : NpcNameText.Normalize(name.Trim().ToLowerInvariant());

    public override string ToString() => Name;

    public bool IsMatchingName(string testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
        {
            return false;
        }

        return IsMatchingNormalizedName(NormalizeNameForMatch(testName));
    }

    /// <summary><paramref name="normalizedTestName"/> 須是 <see cref="NormalizeNameForMatch"/> 對非空白名字的結果。</summary>
    internal bool IsMatchingNormalizedName(string normalizedTestName)
    {
        if (NameRegex != null)
        {
            return NameRegex.IsMatch(normalizedTestName);
        }

        return NpcNameText.Normalize(Name).Equals(normalizedTestName, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsMatchingNameStart(string testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
        {
            return false;
        }

        return IsMatchingNormalizedNameStart(NormalizeNameForMatch(testName));
    }

    /// <summary><paramref name="normalizedTestName"/> 須是 <see cref="NormalizeNameForMatch"/> 對非空白名字的結果。</summary>
    internal bool IsMatchingNormalizedNameStart(string normalizedTestName)
    {
        if (NamePartialRegex != null)
        {
            return NamePartialRegex.IsMatch(normalizedTestName);
        }

        var name = NpcNameText.Normalize(Name);
        return name.StartsWith(normalizedTestName, StringComparison.OrdinalIgnoreCase) ||
               normalizedTestName.StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }
}

public class TriadNpcDB
{
    private static readonly TriadNpcDB instance = new();
    public List<TriadNpc> npcs = [];

    // npcs 只在背景載入時變動：載入完成（最後一次變動之後）才發布唯讀快照，重新載入前（第一次變動之前）撤回。
    // 發布期間快照與清單內容相同；未發布時照舊每次複製。
    private TriadNpc[] frozenNpcs;

    public static TriadNpcDB Get() => instance;

    /// <summary>已發布的唯讀快照；載入中為 null。呼叫端不可修改內容。</summary>
    internal TriadNpc[] FrozenNpcs => Volatile.Read(ref frozenNpcs);

    internal void FreezeNpcs() => Volatile.Write(ref frozenNpcs, [.. npcs]);

    internal void UnfreezeNpcs() => Volatile.Write(ref frozenNpcs, null);

    private TriadNpc[] SnapshotNpcs() => Volatile.Read(ref frozenNpcs) ?? (npcs.Count == 0 ? [] : [.. npcs]);

    public TriadNpc Find(string Name)
    {
        foreach (var x in SnapshotNpcs())
        {
            if (x != null && x.IsMatchingName(Name))
            {
                return x;
            }
        }

        return null;
    }

    public TriadNpc FindByNameStart(string Name)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return null;
        }

        var normalized = TriadNpc.NormalizeNameForMatch(Name);
        foreach (var x in SnapshotNpcs())
        {
            if (x != null && x.IsMatchingNormalizedNameStart(normalized))
            {
                return x;
            }
        }

        return null;
    }

    public TriadNpc FindByID(int id)
    {
        foreach (var x in SnapshotNpcs())
        {
            if (x != null && x.Id == id)
            {
                return x;
            }
        }

        return null;
    }

    public TriadNpc FindMatchingName(string testName) =>
        string.IsNullOrWhiteSpace(testName) ? null : FindMatchingName(testName, SnapshotNpcs());

    /// <summary>在指定的快照裡找；<paramref name="snapshot"/> 必須是 <see cref="SnapshotNpcs"/> 或 <see cref="FrozenNpcs"/> 的結果。</summary>
    internal static TriadNpc FindMatchingName(string testName, TriadNpc[] snapshot)
    {
        if (string.IsNullOrWhiteSpace(testName))
        {
            return null;
        }

        var normalized = TriadNpc.NormalizeNameForMatch(testName);
        foreach (var npc in snapshot)
        {
            if (npc != null && npc.IsMatchingNormalizedName(normalized))
            {
                return npc;
            }
        }

        return null;
    }

    public TriadNpc GetByIndex(int index)
    {
        var snapshot = SnapshotNpcs();
        return index >= 0 && index < snapshot.Length ? snapshot[index] : null;
    }
}
