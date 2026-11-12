using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
namespace Saucy.Framework;

internal static unsafe class GateDirector
{
    public static bool InSaucer => Svc.ClientState.TerritoryType is 144;

    /// <summary>在副本型 GATE 裡：空軍裝甲駕駛員（832）或登高跳跳樂（792／899／1098）。</summary>
    public static bool InGateDuty => Svc.ClientState.TerritoryType is 832 or 792 or 899 or 1098;

    public static bool TryGetDirector(out GFateDirector* director)
    {
        director = null;
        var mgr = GoldSaucerManager.Instance();
        if (mgr is null)
        {
            return false;
        }

        director = mgr->CurrentGFateDirector;
        return director is not null;
    }

    public static bool IsPlayerOnStage()
    {
        if (!TryGetDirector(out var director))
        {
            return false;
        }

        return director->Flags.HasFlag(GFateDirectorFlag.IsJoined) &&
               !director->Flags.HasFlag(GFateDirectorFlag.IsFinished);
    }

    public static Module.GateType GetCurrentGate()
    {
        if (!TryGetDirector(out var director))
        {
            return Module.GateType.None;
        }

        return (Module.GateType)director->GateType;
    }

    public static bool IsInGate(Module.GateType gate) =>
        InSaucer && IsPlayerOnStage() && GetCurrentGate() == gate;
}
