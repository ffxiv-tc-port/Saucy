using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Saucy.AirForce;
using Saucy.Framework;
using Saucy.OtherGames;

namespace Saucy;

internal static class GoldSaucerGameActivity
{
    public static bool IsAnyGamePlaying()
    {
        if (IsTriadSessionActive() ||
            IsAirForceSessionActive() ||
            IsGateAutoMovementActive())
        {
            return true;
        }

        return false;
    }

    private static bool IsTriadSessionActive() =>
        TriadRunSession.ModuleEnabled &&
        (TriadUiState.IsAutomationFlowActive() ||
         uiReaderGame.IsVisible ||
         TriadMapNavigation.IsNavigationActive ||
         TriadCardFarmSession.SessionActive);

    private static bool IsAirForceSessionActive() =>
        C.IsModuleEnabled(ModuleNames.AirForceOne) &&
        (Svc.Condition[ConditionFlag.BoundByDuty95] || AirForceAutomation.ShouldTrackReward);

    private static bool IsGateAutoMovementActive()
    {
        if (GateDirector.IsInGate(Module.GateType.AnyWayTheWindBlows) &&
            C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows) &&
            C.GoldSaucerGates.WindBlowsAutoMovement &&
            !AnyWayTheWindBlows.Stage.SafeSpot.On)
        {
            return true;
        }

        return false;
    }
}
