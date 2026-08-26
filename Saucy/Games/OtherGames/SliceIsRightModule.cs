using ECommons.GameHelpers;
using Saucy.Framework;
using Saucy.IPC;
using System.Numerics;
namespace Saucy.OtherGames;

/// <summary>
/// "必中一閃快刀斬魔" (Slice is Right, GateType 8) — the actual minigame mechanics are a real duty
/// handled by BossModReborn once the fight starts, so this module deliberately does nothing but
/// walk the player to the registration NPC beforehand, and (once actually inside the GATE) to the
/// recorded field boundary/starting spot, then hands off entirely for BMR to take over
/// ("先移動到場地邊界 再讓BMR接管").
/// </summary>
public class SliceIsRight : Module
{
    private const float StartSpotArrivalRange = 2f;

    // Same precision hand-off already applied to Wind Blows/Cliffhanger: vnavmesh's own arrival
    // tolerance is coarser than would be needed for a tight approach, so once close, stop relying
    // on it and steer directly via PreciseMovement (hooks the game's movement-input read, same
    // technique as BossModReborn — see Framework/PreciseMovement.cs) instead.
    private const float ManualApproachRadius = 3f;

    // Once the initial walk-to-boundary is done (arrived, or gave up), never touch movement again
    // for the rest of this GATE — otherwise a later mechanic knockback pushing the player off that
    // exact spot would make IsWithinHorizontalRange go false again and re-trigger a move, yanking
    // control away from BossModReborn mid-fight ("只移動一次 不要搶控制權"). Reset only on the next
    // fresh GATE entry.
    private bool _navigatedThisEntry;
    private bool _wasInGate;

    // "斬魔 報名後等待30秒 用新的移動方式移動到定點" — right after interacting with the
    // registration NPC, the actual teleport onto the arena hasn't happened yet; starting to steer
    // toward the recorded start spot immediately (even once IsInGate reads true) can aim at a
    // destination that only makes sense post-teleport, from wherever the player still physically
    // was to register.
    private const double PostJoinSettleSeconds = 30;

    public override string Name => "Slice is Right";

    public override void Enable() => Svc.Framework.Update += OnUpdate;

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        GateNpcNavigation.ReleaseIfOwned(GateType.SliceIsRight);
        PreciseMovement.SetDesiredDirection(null);
        if (Vnavmesh.IsInstalled)
        {
            Vnavmesh.StopPath();
        }
    }

    private void OnUpdate(IFramework _)
    {
        var inGate = IsInGate(GateType.SliceIsRight);
        if (inGate && !_wasInGate)
        {
            _navigatedThisEntry = false;
        }
        _wasInGate = inGate;

        if (inGate)
        {
            TickMoveToStartSpot();
            return;
        }

        // "傳送後 會立刻跳下場地回去找報名NPC" — right after registering/teleporting in, IsInGate
        // can briefly still read false while the GATE state finishes settling, which would otherwise
        // make this fall through here and start walking back toward the (now far outside the arena)
        // registration NPC. Merely skipping the Tick call here wasn't enough on its own — a
        // pre-registration vnavmesh path can already be in flight, and Tick is also what's
        // responsible for stopping it; explicitly release it instead of leaving it to keep issuing
        // move commands toward pre-teleport coordinates during the whole settle window
        // ("從傳送前位置導航 所以會跳下場地").
        if (GateScheduleAutomation.IsWithinPostJoinSettle(GateType.SliceIsRight, PostJoinSettleSeconds))
        {
            GateNpcNavigation.ReleaseIfOwned(GateType.SliceIsRight);
            return;
        }

        GateNpcNavigation.Tick(GateType.SliceIsRight, C.GoldSaucerGates.SliceIsRightNpcSpot, C.GoldSaucerGates.SliceIsRightNpcAutoNavigate);
    }

    /// <summary>Walks to the recorded field boundary/starting spot exactly once per GATE entry,
    /// then never touches movement again — win, loss, knockback, whatever happens after that is
    /// entirely BossModReborn's to handle.</summary>
    private void TickMoveToStartSpot()
    {
        if (_navigatedThisEntry)
        {
            return;
        }

        // "地圖還沒完全載入 他就開始計算新的路線了 這是造成摔落的主因" — a fixed time delay alone
        // doesn't guarantee vnavmesh has actually finished (re)building the mesh for the new area;
        // wait for its own readiness signal too, not just the settle timer.
        if (GateScheduleAutomation.IsWithinPostJoinSettle(GateType.SliceIsRight, PostJoinSettleSeconds) ||
            (Vnavmesh.IsInstalled && !Vnavmesh.IsNavReady()))
        {
            return;
        }

        var spot = C.GoldSaucerGates.SliceIsRightStartSpot;
        if (!C.GoldSaucerGates.SliceIsRightStartAutoNavigate || !spot.Recorded || !Vnavmesh.IsInstalled)
        {
            return;
        }

        var destination = new Vector3(spot.X, spot.Y, spot.Z);
        if (Vnavmesh.IsWithinHorizontalRange(destination, StartSpotArrivalRange))
        {
            PreciseMovement.SetDesiredDirection(null);
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }
            _navigatedThisEntry = true;
            return;
        }

        if (Vnavmesh.IsWithinHorizontalRange(destination, ManualApproachRadius))
        {
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            var toTarget = destination - Player.Position;
            toTarget.Y = 0;
            PreciseMovement.SetDesiredDirection(toTarget.LengthSquared() < 0.0001f ? null : toTarget);
            return;
        }

        if (!Vnavmesh.IsMoving())
        {
            Vnavmesh.TryMoveTo(destination, false, StartSpotArrivalRange);
        }
    }
}
