using Saucy.Framework;
using Saucy.IPC;
using Saucy.LeapOfFaith;
namespace Saucy.OtherGames;

internal static class GateShared
{
    public static void StopAll()
    {
        BossMod.TryDisableGateAi();
        WindBlowsGateMovement.ReleaseIfOwned();
        GateScheduleAutomation.CancelAfterGateReturn("停止自動化");
        LeapOfFaithQuickExit.Cancel("停止自動化");
    }
}
