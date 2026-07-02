using ECommons.GameHelpers;
using Saucy.IPC;
using System.Collections.Generic;
using System.Numerics;
namespace Saucy.Framework;

/// <summary>
/// Auto-navigates near a GATE's registration NPC before it starts, using a position the user
/// personally recorded (via TryRecordCurrentTarget) rather than any hardcoded/guessed coordinate,
/// then targets and interacts once in range — per user request ("要自動報名"). The resulting
/// confirmation dialogue is left for another plugin (e.g. YesAlready) to handle, same as
/// GateScheduleAutomation's join-window flow, so this never presses anything itself.
/// </summary>
internal static class GateNpcNavigation
{
    public const float CloseRange = 3f;

    // This toggle is unconditional/always-on (unlike the GateScheduleAutomation join window,
    // which only searches within a tight radius during the :00/:20/:40 window) — without a cap it
    // would try to walk the player back toward this GATE's recorded spot from ANYWHERE, including
    // right after a coordinator teleport dropped them somewhere else entirely for a different
    // activity ("他在傳送後一直往上一個活動NPC跑"). An Event Coordinator teleport always drops the
    // player right next to the next GATE's registration NPC, so 5m is enough to catch "yes, I'm
    // actually here" while still rejecting every other unrelated recorded spot.
    private const float MaxTriggerDistance = 5f;

    private static readonly Dictionary<Module.GateType, bool> owners = [];

    public static bool TryRecordCurrentTarget(GateNpcSpot spot, out string message)
    {
        var target = Svc.Targets.Target;
        if (target == null)
        {
            message = "請先在遊戲中鎖定 NPC，再按下記錄。";
            return false;
        }

        spot.Recorded = true;
        spot.X = target.Position.X;
        spot.Y = target.Position.Y;
        spot.Z = target.Position.Z;
        spot.DataId = target.DataId;
        spot.NpcName = target.Name.TextValue;
        C.Save();
        message = $"已記錄「{spot.NpcName}」的位置。";
        return true;
    }

    /// <summary>Appends a new named spot to a user-managed list (e.g. Event Coordinator NPCs,
    /// which exist at multiple locations) instead of overwriting a single fixed slot.</summary>
    public static bool TryRecordNewListEntry(List<GateNpcSpot> list, out string message)
    {
        var spot = new GateNpcSpot();
        if (!TryRecordCurrentTarget(spot, out message))
        {
            return false;
        }

        list.Add(spot);
        C.Save();
        return true;
    }

    /// <summary>Call every frame the owning module is enabled. No-ops (and releases any owned
    /// path) unless: auto-navigate is on, a spot was recorded, vnavmesh is available, the player
    /// is in the Gold Saucer, and the GATE itself hasn't already started — this only ever runs
    /// BEFORE the GATE, so it never fights with that GATE's own in-progress movement logic.</summary>
    public static void Tick(Module.GateType gate, GateNpcSpot spot, bool enabled)
    {
        if (!enabled || !spot.Recorded || !Vnavmesh.IsInstalled || !GateDirector.InSaucer || GateDirector.IsInGate(gate) ||
            !Player.Available)
        {
            ReleaseIfOwned(gate);
            return;
        }

        var destination = new Vector3(spot.X, spot.Y, spot.Z);
        if (!Vnavmesh.IsWithinHorizontalRange(destination, MaxTriggerDistance))
        {
            ReleaseIfOwned(gate);
            return;
        }

        if (Vnavmesh.IsWithinHorizontalRange(destination, CloseRange))
        {
            ReleaseIfOwned(gate);
            if (spot.DataId != 0)
            {
                ObjectHelper.TryInteractWithBaseId(spot.DataId, CloseRange, $"Saucy.GateNpc.{gate}");
            }

            return;
        }

        if (owners.TryGetValue(gate, out var owns) && owns)
        {
            if (!Vnavmesh.IsMoving())
            {
                Vnavmesh.TryMoveTo(destination, false, CloseRange);
            }
            return;
        }

        if (Vnavmesh.IsMoving())
        {
            return;
        }

        if (Vnavmesh.TryMoveTo(destination, false, CloseRange))
        {
            owners[gate] = true;
        }
    }

    /// <summary>One-shot manual trigger for the "立即移動" button — starts walking toward a
    /// recorded spot right away instead of waiting for the auto-navigate toggle's normal
    /// conditions (module enabled, not already in the GATE, etc.).</summary>
    public static bool TryMoveNow(GateNpcSpot spot)
    {
        if (!spot.Recorded || !Vnavmesh.IsInstalled)
        {
            return false;
        }

        return Vnavmesh.TryMoveTo(new Vector3(spot.X, spot.Y, spot.Z), false, CloseRange);
    }

    public static void ReleaseIfOwned(Module.GateType gate)
    {
        if (!owners.TryGetValue(gate, out var owns) || !owns)
        {
            return;
        }

        owners[gate] = false;
        if (Vnavmesh.IsInstalled)
        {
            Vnavmesh.StopPath();
        }
    }
}
