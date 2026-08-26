using Saucy.AirForce;
namespace Saucy;

public sealed partial class Saucy
{
    private void CheckAirForceResults(UIStateAirForceResults results)
    {
        if (!C.IsModuleEnabled(ModuleNames.AirForceOne) || !AirForceAutomation.ShouldTrackReward)
        {
            return;
        }

        StatsSessionClock.MarkAirForceActive();
        C.UpdateStats(stats =>
        {
            stats.AirForceMGP += StatsBonusHelper.ApplyMgpBonus(results.numMGP);
            stats.AirForceGamesPlayed++;
        });

        AirForceAutomation.ConsumeRewardTracking();
        uiReaderGamesResults.SetIsResultsUI(false);
        C.Save();
    }

    private void CheckResults(UIStateTriadResults obj)
    {
        if (TriadRunSession.ModuleEnabled)
        {
            StatsSessionClock.MarkTriadActive();
            C.UpdateStats(stats =>
            {
                stats.GamesPlayedWithSaucy++;
                stats.MGPWon += StatsBonusHelper.ApplyMgpBonus(obj.numMGP);

                var npcName = TriadRun.lastGameNpc?.Name ?? "Unknown";
                if (stats.NPCsPlayed.TryGetValue(npcName, out var plays))
                {
                    stats.NPCsPlayed[npcName] += 1;
                }
                else
                {
                    stats.NPCsPlayed.TryAdd(npcName, 1);
                }

                if (obj.isLose)
                {
                    stats.GamesLostWithSaucy++;
                }
                if (obj.isDraw)
                {
                    stats.GamesDrawnWithSaucy++;
                }
            });

            if (obj.isWin)
            {
                C.UpdateStats(stats => stats.GamesWonWithSaucy++);

                var cardStatsRecorded = false;
                if (TriadCardFarmSession.IsModeActive())
                {
                    TriadCardFarmSession.DetectAndProcessDrops(obj.cardItemId);
                    // Only count as handled when the drop maps to a farm target;
                    // duplicates of owned cards fall through to the generic recorder.
                    cardStatsRecorded = TriadCardFarmSession.IsFarmRewardItem(obj.cardItemId);
                    if (!TriadCardFarmSession.IsComplete() &&
                        TriadCardFarmSession.ShouldScheduleDropVerification(obj.cardItemId))
                    {
                        TriadCardFarmSession.ScheduleDropVerification(obj.cardItemId);
                    }
                }
                else if (TriadRunSession.PlayUntilCardDrops && obj.cardItemId > 0)
                {
                    var droppedCard = GameCardDB.Get().FindByItemId(obj.cardItemId);
                    if (droppedCard != null)
                    {
                        TriadRewardDropTracker.ProcessVerifiedCardDrop(droppedCard);
                        cardStatsRecorded = true;
                    }
                    else
                    {
                        TriadRewardDropTracker.NotifyPlayUntilAnyCardDropped();
                    }

                    if (!TriadRunSession.ShouldContinue())
                    {
                        TriadRematchAutomation.RequestSessionEndDismiss();
                    }
                }
                else if (TriadRewardDropTracker.TryGetVerifiedNpcCardDrop(out var droppedCard, obj.cardItemId) &&
                         droppedCard != null)
                {
                    TriadRewardDropTracker.ProcessVerifiedCardDrop(droppedCard);
                    cardStatsRecorded = true;
                }

                if (!cardStatsRecorded)
                {
                    TryRecordTriadCardStatsFromResult(obj.cardItemId);
                }
            }

            TriadRematchAutomation.RecordMatchResultIfNeeded();

            C.Save();
        }
    }

    private static void TryRecordTriadCardStatsFromResult(uint cardItemId)
    {
        if (cardItemId == 0)
        {
            return;
        }

        var droppedCard = GameCardDB.Get().FindByItemId(cardItemId);
        if (droppedCard == null)
        {
            return;
        }

        C.UpdateStats(stats =>
        {
            stats.CardsDroppedWithSaucy++;
            if (stats.CardsWon.TryGetValue((uint)droppedCard.CardId, out var count))
            {
                stats.CardsWon[(uint)droppedCard.CardId] = count + 1;
            }
            else
            {
                stats.CardsWon[(uint)droppedCard.CardId] = 1;
            }
        });
    }
}
