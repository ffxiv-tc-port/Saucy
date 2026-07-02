using Dalamud.Game.ClientState.Conditions;
using Saucy.Framework;
using Saucy.IPC;
using System;
namespace Saucy;

public sealed partial class Saucy
{
    private void RunBot(IFramework framework)
    {
        try
        {
            SubscriptionManager.Subscribe();
            YesAlready.SyncForGameActivity(GoldSaucerGameActivity.IsAnyGamePlaying());
            UpdateGateAutoOpen();
            GateScheduleAutomation.Tick();
            TriadOptimizedDeckCacheStore.TickCharacter();
            TriadMapNavigation.Tick();

            var deltaSeconds = (float)framework.UpdateDelta.TotalSeconds;
            uiReaderScheduler.Update(deltaSeconds);
            if (uiReaderPrep.HasMatchRequestUI || uiReaderPrep.HasDeckSelectionUI ||
                TriadUiState.IsMatchRegistrationVisible() || TriadUiState.IsPrepDeckSelectVisible())
            {
                TriadRunTarget.RefreshFromPrep();
            }

            if (TriadUiState.IsAutomationFlowActive() || uiReaderGame.IsVisible)
            {
                TriadRun.EnsureRunTargetNpcSynced();
            }

            UpdateTriadAutoOpen();
            if (Svc.Condition[ConditionFlag.WaitingForDutyFinder])
            {
                TriadRunSession.ApplyDutyFinderDeferIfNeeded();
            }

            if (C.UseSimmedDeck && TriadRun.ShouldBuildOptimizedDeck())
            {
                TriadRun.SyncDeckOptimizerPauseForVnavmesh();
                TriadDeckOptimizerJobs.Tick();
                if (!Vnavmesh.ShouldDeferDeckOptimizerWork())
                {
                    TriadRun.TickWorldTargetDeckOptimizer();
                }
            }

            if (TriadDialogueSkip.ShouldRun())
            {
                TriadDialogueSkip.Tick();
            }

            if (TriadRunSession.ModuleEnabled || TriadUiState.IsResultVisible() ||
                TriadMapNavigation.IsNavigationActive)
            {
                if (!TriadRunSession.ShouldContinue())
                {
                    TriadAutomator.RunModule();

                    if (TriadRunSession.CanFinalize())
                    {
                        if (TriadCardFarmSession.HasPendingDrops() &&
                            (TriadCardFarmSession.IsModeActive() || TriadCardFarmSession.SessionActive))
                        {
                            return;
                        }

                        if (TriadDialogueSkip.IsBlockingTriadSessionEnd())
                        {
                            TalkHelper.TryAdvance("Saucy.TriadTalk");
                        }
                        else
                        {
                            if (C.PlaySound && TriadRunSession.ShouldPlayRunCompleteSound())
                            {
                                PlaySound();
                            }

                            TriadRunSession.ModuleEnabled = false;
                            TriadRunSession.ClearDutyFinderDefer();
                            TriadCardFarmSession.DeactivateSession();
                            TriadRunSession.MatchesCompletedThisSession = 0;
                            TriadRunSession.NumberOfTimes = TriadRunSession.SessionInitialPlayCount;
                            TriadRematchAutomation.ClearRematchPending();

                            if (C.LogOutAfterTriadRun)
                            {
                                ScheduleLogout();
                            }
                        }
                    }

                    return;
                }

                TriadAutomator.RunModule();
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "bot update failed");
        }
    }

    private void OnTriadPrepUiChanged(bool isActive)
    {
        if (isActive)
        {
            _autoOpenedForTriadFlow = false;
            UpdateTriadAutoOpen();
        }
    }

    private void UpdateTriadAutoOpen()
    {
        if (!C.OpenAutomatically)
        {
            _autoOpenedForTriadFlow = false;
            return;
        }

        if (!IsTriadFlowActive())
        {
            _autoOpenedForTriadFlow = false;
            return;
        }

        if (_autoOpenedForTriadFlow)
        {
            return;
        }

        _pluginUi.OpenForTriad();
        _autoOpenedForTriadFlow = true;
    }

    private Module.GateType _lastAutoOpenedGate = Module.GateType.None;

    /// <summary>Opens the UI to the matching GATE panel the moment the player actually joins one
    /// on stage, so there's no need to manually click over from wherever the UI was left.</summary>
    private void UpdateGateAutoOpen()
    {
        if (!C.GoldSaucerGates.AutoOpenUiOnGateJoin || !GateDirector.InSaucer || !GateDirector.IsPlayerOnStage())
        {
            _lastAutoOpenedGate = Module.GateType.None;
            return;
        }

        var gate = GateDirector.GetCurrentGate();
        if (gate == Module.GateType.None || gate == _lastAutoOpenedGate)
        {
            return;
        }

        _lastAutoOpenedGate = gate;
        _pluginUi.OpenForGate(gate);
    }

    private static unsafe bool IsTriadFlowActive()
    {
        if (uiReaderPrep.HasMatchRequestUI ||
            uiReaderPrep.HasDeckSelectionUI ||
            uiReaderGame.IsVisible)
        {
            return true;
        }

        return TriadLocalClientStructs.TryGetRequest(out var _) ||
               TriadLocalClientStructs.TryGetSelDeck(out var _) ||
               TriadLocalClientStructs.TryGetBoard(out var _);
    }
}
