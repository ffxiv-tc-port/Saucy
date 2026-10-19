using System;
namespace Saucy.TripleTriad;

public enum TriadRunMode
{
    None = -1,
    PlayXTimes = 0,
    PlayUntilAnyCard = 1,
    PlayUntilAllCards = 2
}

public enum TriadNavigationGoal
{
    FarmCards = 0,
    FarmMgp = 1
}

internal static class TriadTargetNpc
{
    // 比對結果只取決於名字位元組（SeString 解析到第一個 0 為止）與已發布的 NPC 快照：兩者都沒變就沿用上次結果。
    // 只存位元組內容，不存指標；名字在取得目標的同一幀、同一個位址上讀，與 Name 屬性讀的是同一段記憶體。
    private static readonly byte[] lastTargetName = new byte[64];
    private static int lastTargetNameLength;
    private static TriadNpc? lastTargetNpc;
    private static TriadNpc[]? lastTargetNpcSnapshot;

    public static unsafe TriadNpc? FromWorldTarget()
    {
        var target = Svc.Targets.Target;
        if (target == null)
        {
            return null;
        }

        var frozen = TriadNpcDB.Get().FrozenNpcs;
        if (frozen == null)
        {
            return TriadNpcDB.Get().FindMatchingName(target.Name.TextValue);
        }

        ReadOnlySpan<byte> raw = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address)->Name;
        var end = raw.IndexOf((byte)0);
        var name = end < 0 ? raw : raw[..end];
        if (ReferenceEquals(frozen, lastTargetNpcSnapshot) &&
            name.SequenceEqual(lastTargetName.AsSpan(0, lastTargetNameLength)))
        {
            return lastTargetNpc;
        }

        var npc = TriadNpcDB.FindMatchingName(target.Name.TextValue, frozen);
        name.CopyTo(lastTargetName);
        lastTargetNameLength = name.Length;
        lastTargetNpc = npc;
        lastTargetNpcSnapshot = frozen;
        return npc;
    }

    public static TriadNpc? FromSolverContext() =>
        TriadRun.preGameNpc ?? TriadRun.currentNpc ?? TriadRun.lastGameNpc;

    public static TriadNpc? FromRunContext(GameNpcInfo? runTargetNpc)
    {
        if (runTargetNpc != null)
        {
            return TriadNpcDB.Get().FindByID(runTargetNpc.npcId);
        }

        return FromSolverContext();
    }
}

internal static class TriadRunTarget
{
    public static GameNpcInfo? Resolve()
    {
        TriadRun.EnsureRunTargetNpcSynced();

        var npcId = TriadRun.preGameNpc?.Id ?? TriadRun.currentNpc?.Id ?? TriadRun.lastGameNpc?.Id ?? -1;
        if (npcId >= 0 && GameNpcDB.Get().mapNpcs.TryGetValue(npcId, out var npcInfo))
        {
            if (TriadRunSession.PlayUntilAllCardsDropOnce)
            {
                TriadCardFarmSession.SyncDisplay(npcInfo);
            }

            return npcInfo;
        }

        return null;
    }

    public static void RefreshFromPrep()
    {
        if (!TriadRunSession.PlayUntilAllCardsDropOnce)
        {
            return;
        }

        try
        {
            if (TriadUiState.IsMatchRegistrationVisible())
            {
                uiReaderPrep.SyncMatchRegistrationFromLiveAddon();
            }
            else if (TriadUiState.IsPrepDeckSelectVisible())
            {
                uiReaderPrep.SyncDeckSelectFromLiveAddon();
            }

            TriadRun.EnsureRunTargetNpcSynced(
                deckSelectScreen: uiReaderPrep.HasDeckSelectionUI && !TriadUiState.IsMatchRegistrationVisible());
            TriadCardFarmSession.SyncDisplay(Resolve());
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[TriadRunTarget] RefreshFromPrep failed");
        }
    }
}
