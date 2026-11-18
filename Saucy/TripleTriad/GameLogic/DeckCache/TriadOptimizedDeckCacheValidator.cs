using System.Collections.Generic;
using System.Linq;
namespace Saucy.TripleTriad.GameLogic;

internal static class TriadOptimizedDeckCacheValidator
{
    // 兩個 id 集合在呼叫間重用：每次都先逐筆比對來源，來源不同才清空重建，所以集合永遠等於這一次的來源。
    private static readonly object IdSetLock = new();
    private static readonly List<int> ownedIdsSource = [];
    private static readonly HashSet<int> ownedIdSet = [];
    private static readonly List<int> snapshotIdsSource = [];
    private static readonly HashSet<int> snapshotIdSet = [];

    public static int[] CaptureOwnedCardSnapshot()
    {
        GameCardDB.Get().Refresh();
        return
        [
            .. PlayerSettingsDB.Get().ownedCards
                .Select(card => card?.Id ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
        ];
    }

    public static int CountNewOwnedCardsSinceBuild(string sessionKey, int npcId)
    {
        GameCardDB.Get().Refresh();
        var owned = PlayerSettingsDB.Get().ownedCards;
        if (owned.Count == 0)
        {
            return 0;
        }

        if (!TriadOptimizedDeckCacheStore.TryGetOwnedSnapshotForNpc(npcId, sessionKey, out var ownedAtBuild))
        {
            return 0;
        }

        lock (IdSetLock)
        {
            return CountNewOwnedCards(ownedAtBuild, OwnedIdSetLocked(owned));
        }
    }

    public static bool ShouldRebuildDeckForNewCards(string sessionKey, int npcId) =>
        CountNewOwnedCardsSinceBuild(sessionKey, npcId) >= TriadOptimizedDeckCacheStore.RebuildAfterNewCardCount;

    private static int CountNewOwnedCards(int[] ownedAtBuild, HashSet<int> currentOwnedIds)
    {
        if (ownedAtBuild == null || ownedAtBuild.Length == 0)
        {
            return 0;
        }

        var snapshot = SnapshotIdSetLocked(ownedAtBuild);
        var count = 0;
        foreach (var id in currentOwnedIds)
        {
            if (!snapshot.Contains(id))
            {
                count++;
            }
        }

        return count;
    }

    public static bool TryGetUsableEntry(string sessionKey, out TriadOptimizedDeckCacheEntry? entry)
    {
        entry = null;

        if (!TriadOptimizedDeckCacheStore.TryGetEntry(sessionKey, out var cached))
        {
            return false;
        }

        if (!HasValidCardIds(cached!.CardIds))
        {
            TriadOptimizedDeckCacheStore.Remove(sessionKey);
            return false;
        }

        GameCardDB.Get().Refresh();
        var owned = PlayerSettingsDB.Get().ownedCards;
        if (owned.Count == 0)
        {
            return false;
        }

        bool allOwned;
        lock (IdSetLock)
        {
            allOwned = AllOwnedLocked(cached.CardIds, OwnedIdSetLocked(owned));
        }

        if (!allOwned)
        {
            TriadOptimizedDeckCacheStore.Remove(sessionKey);
            return false;
        }

        if (!TryBuildSolverDeck(cached.CardIds, out var _))
        {
            TriadOptimizedDeckCacheStore.Remove(sessionKey);
            return false;
        }

        entry = cached;
        return true;
    }

    private static bool AllOwnedLocked(ushort[] cardIds, HashSet<int> ownedIds)
    {
        foreach (var cardId in cardIds)
        {
            if (!ownedIds.Contains(cardId))
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<int> OwnedIdSetLocked(List<TriadCard> owned)
    {
        var same = ownedIdsSource.Count == owned.Count;
        for (var idx = 0; idx < owned.Count && same; idx++)
        {
            same = ownedIdsSource[idx] == owned[idx].Id;
        }

        if (!same)
        {
            ownedIdsSource.Clear();
            ownedIdSet.Clear();
            foreach (var card in owned)
            {
                ownedIdsSource.Add(card.Id);
                ownedIdSet.Add(card.Id);
            }
        }

        return ownedIdSet;
    }

    private static HashSet<int> SnapshotIdSetLocked(int[] ids)
    {
        var same = snapshotIdsSource.Count == ids.Length;
        for (var idx = 0; idx < ids.Length && same; idx++)
        {
            same = snapshotIdsSource[idx] == ids[idx];
        }

        if (!same)
        {
            snapshotIdsSource.Clear();
            snapshotIdSet.Clear();
            foreach (var id in ids)
            {
                snapshotIdsSource.Add(id);
                snapshotIdSet.Add(id);
            }
        }

        return snapshotIdSet;
    }

    private static bool HasValidCardIds(ushort[] cardIds)
    {
        if (cardIds == null || cardIds.Length != TriadOptimizedDeckCacheEntry.DeckSize)
        {
            return false;
        }

        return cardIds.All(id => id > 0);
    }

    public static bool TryBuildSolverDeck(ushort[] cardIds, out TriadDeck? deck)
    {
        deck = null;
        var parseCtx = new GameUIParser();
        var cards = new TriadCard[TriadOptimizedDeckCacheEntry.DeckSize];
        for (var idx = 0; idx < TriadOptimizedDeckCacheEntry.DeckSize; idx++)
        {
            var card = parseCtx.cards.FindById(cardIds[idx]);
            if (card == null)
            {
                parseCtx.OnFailedCard($"id:{cardIds[idx]}");
                return false;
            }

            cards[idx] = card;
        }

        if (parseCtx.HasErrors)
        {
            return false;
        }

        deck = new(cards);
        return deck.GetDeckState() == ETriadDeckState.Valid;
    }
}
