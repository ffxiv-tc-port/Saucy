using Saucy.Framework;
namespace Saucy.OtherGames;

/// <summary>
/// "必中一閃快刀斬魔" (Slice is Right, GateType 8) — the actual minigame mechanics are a real duty
/// handled by BossModReborn once the fight starts, so this module deliberately does nothing but
/// walk the player near the registration NPC beforehand (per user: "3 不用做" — no auto-target/
/// interact/confirm, that stays manual).
/// </summary>
public class SliceIsRight : Module
{
    public override string Name => "Slice is Right";

    public override void Enable() => Svc.Framework.Update += OnUpdate;

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        GateNpcNavigation.ReleaseIfOwned(GateType.SliceIsRight);
    }

    private void OnUpdate(IFramework _) =>
        GateNpcNavigation.Tick(GateType.SliceIsRight, C.GoldSaucerGates.SliceIsRightNpcSpot, C.GoldSaucerGates.SliceIsRightNpcAutoNavigate);
}
