#nullable disable
using ECommons.LanguageHelpers;

namespace Saucy.TripleTriad.UI;

public partial class TriadSession
{
    public bool CanRequestPremadeDeckOptimizer(TriadNpc npc, out string blockReason)
    {
        blockReason = null;

        if (npc == null)
        {
            blockReason = "No NPC selected.".Loc();
            return false;
        }

        if (!dataLoader.IsDataReady)
        {
            blockReason = "Card data is still loading.".Loc();
            return false;
        }

        if (!C.UseSimmedDeck)
        {
            blockReason = "Turn on Auto-pick best deck in Triad settings.".Loc();
            return false;
        }

        GameCardDB.Get().Refresh();

        if (PlayerSettingsDB.Get().ownedCards.Count == 0)
        {
            blockReason = "No owned cards found in the collection cache.".Loc();
            return false;
        }

        if (OptimizerInProgress && !IsPremadeOptimizerForNpc(npc))
        {
            blockReason = "Another deck optimization is already running.".Loc();
            return false;
        }

        return true;
    }

    public void RequestPremadeDeckOptimizer(TriadNpc npc, bool forceRebuild = false)
    {
        if (!CanRequestPremadeDeckOptimizer(npc, out var blockReason))
        {
            if (!string.IsNullOrEmpty(blockReason))
            {
                TriadDeckLog.Print($"[Saucy] {blockReason}");
            }

            return;
        }

        preGameNpc = npc;
        lastGameNpc = npc;
        preGameMods = [];

        if (OptimizerInProgress && IsPremadeOptimizerForNpc(npc))
        {
            return;
        }

        StartDeckOptimizer(npc, preGameMods, true, forceRebuild);
    }

    public string DescribePremadeDeckOptimizerStatus(TriadNpc npc)
    {
        if (npc == null)
        {
            return string.Empty;
        }

        if (!C.UseSimmedDeck)
        {
            return "Enable Auto-pick best deck in Triad settings.".Loc();
        }

        if (OptimizerInProgress && IsPremadeOptimizerForNpc(npc) &&
            TriadDeckOptimizerJobs.TryGetActive(out var job))
        {
            var best = job.FormatBestWinChance();
            if (string.IsNullOrEmpty(best) || best == "…")
            {
                return "Building deck… ??%".Loc(job.ProgressPercent);
            }

            return "Building deck… ??% (??)".Loc(job.ProgressPercent, best);
        }

        if (HasPremadeDeckReadyForNpc(npc))
        {
            return "Ready in profile slot ??".Loc(SaucyProfileDeckSlotIndex + 1);
        }

        if (_optimizerTimedOut && preGameNpc?.Id == npc.Id)
        {
            return "Last build timed out; try again.".Loc();
        }

        return string.Empty;
    }

    public bool IsPremadeOptimizerForNpc(TriadNpc npc) =>
        npc != null &&
        preGameNpc?.Id == npc.Id &&
        TriadDeckOptimizerJobs.IsRunningForSessionKey(BuildOptimizerSessionKey(npc, []));

    public bool HasPremadeDeckReadyForNpc(TriadNpc npc)
    {
        if (npc == null || !HasOptimizedDeckApplied)
        {
            return false;
        }

        var sessionKey = BuildOptimizerSessionKey(npc, []);
        if (_optimizerSessionKey != sessionKey)
        {
            return false;
        }

        if (profileGS == null || profileGS.HasErrors)
        {
            return preGameNpc?.Id == npc.Id;
        }

        return TryFindSaucyDeckProfileSlot(npc, out var _);
    }
}
