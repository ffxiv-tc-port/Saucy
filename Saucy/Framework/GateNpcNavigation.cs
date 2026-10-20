using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Numerics;
namespace Saucy.Framework;

/// <summary>
/// Auto-navigates near a GATE's registration NPC before it starts, using a position the user
/// personally recorded (via TryRecordCurrentTarget) rather than any hardcoded/guessed coordinate,
/// then targets and interacts once in range. The resulting
/// confirmation dialogue is left for another plugin (e.g. YesAlready) to handle, same as
/// GateScheduleAutomation's join-window flow, so this never presses anything itself.
/// </summary>
internal static class GateNpcNavigation
{
    public const float CloseRange = 3f;

    // Shared across every NPC-interact call site here AND in GateScheduleAutomation (per-GATE
    // auto-navigate, Event Coordinator, and GATE-join all funnel through this) — once any of them
    // fires an interact, hold off on firing another one anywhere for 30s. Without this, lingering near an
    // NPC while its window/condition stays true kept re-triggering the interact repeatedly.
    private const double InteractCooldownSeconds = 30;
    private static DateTime lastInteractUtc = DateTime.MinValue;

    public static bool IsInteractOnCooldown => (DateTime.UtcNow - lastInteractUtc).TotalSeconds < InteractCooldownSeconds;

    public static void MarkInteracted() => lastInteractUtc = DateTime.UtcNow;

    // This toggle is unconditional/always-on (unlike the GateScheduleAutomation join window, which only searches within a tight radius during the :00/:20/:40 window) — without a cap it would try to walk the player back toward this GATE's recorded spot from ANYWHERE, including right after a coordinator teleport dropped them somewhere else entirely for a different activity.
    // An Event Coordinator teleport always drops the player right next to the next GATE's registration NPC, so the cap only has to catch "yes, I'm actually here" while still rejecting every other unrelated recorded spot.
    // 25y instead, chosen from the actual spacing between the recorded venues rather than picked out of the air: the closest DISTINCT pair of registration NPCs is Air Force One (-57.9, -65.4) and Cliffhanger's first spot (-17.3, -83.2), 44y apart on the horizontal plane.
    // 25y therefore still cannot reach a neighbouring venue (so the "walks back to the previous activity" report stays fixed) while giving the toggle a genuinely useful approach distance.
    // Wind Blows and Slice is Right share one position (77.6/77.9, -69.8 — 0.3y apart) and so are mutually reachable at ANY radius including the old 5y; they are kept apart by GateDirector. IsInGate plus each GATE's own enable toggle, not by this distance.
    private const float MaxTriggerDistance = 25f;

    private static readonly Dictionary<Module.GateType, bool> owners = [];

    private static readonly Dictionary<Module.GateType, ManualSteerYield> steers = [];

    // 讓位滿上限之後，這個 GATE 的自動靠近停到使用者離開觸發範圍再回來為止。
    private static readonly HashSet<Module.GateType> gaveUp = [];

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
        spot.DataId = target.BaseId;
        spot.NpcName = target.Name.TextValue;
        C.Save();

        // DataId 0 means interact-by-id can never find this object again (e.g. the lock was on a
        // player/decoration/something without a real ENpcBase entry) — recording still succeeds
        // (position/navigation still work) but auto-interact will silently never fire. Flag it
        // immediately instead of letting it fail quietly later.
        message = target.BaseId == 0
            ? $"已記錄「{spot.NpcName}」的位置，但這個目標沒有有效的 DataId——導航會正常走到附近，但自動互動不會生效，請確認鎖定的是正確的 NPC。"
            : $"已記錄「{spot.NpcName}」的位置。";
        return true;
    }

    /// <summary>Records the player's own current position rather than a targeted NPC — for marking
    /// a plain location (e.g. a GATE's field boundary/starting spot) that has no NPC to lock onto.</summary>
    public static void RecordCurrentPosition(GateNpcSpot spot, string label)
    {
        spot.Recorded = true;
        spot.X = Player.Position.X;
        spot.Y = Player.Position.Y;
        spot.Z = Player.Position.Z;
        spot.DataId = 0;
        spot.NpcName = label;
        C.Save();
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
        // GATE 結束後走回活動解說員的那段時間讓開，不把人拉回報名處。
        if (!enabled || !spot.Recorded || !Vnavmesh.IsInstalled || !GateDirector.InSaucer || GateDirector.IsInGate(gate) ||
            !Player.Available || GateScheduleAutomation.IsReturningAfterGate)
        {
            ReleaseIfOwned(gate);
            ForgetManualSteer(gate);
            return;
        }

        var destination = new Vector3(spot.X, spot.Y, spot.Z);
        if (!Vnavmesh.IsWithinHorizontalRange(destination, MaxTriggerDistance))
        {
            ReleaseIfOwned(gate);
            ForgetManualSteer(gate);
            return;
        }

        if (Vnavmesh.IsWithinHorizontalRange(destination, CloseRange))
        {
            ReleaseIfOwned(gate);
            if (spot.DataId != 0 && !IsInteractOnCooldown &&
                ObjectHelper.TryInteractWithBaseId(spot.DataId, CloseRange, $"Saucy.GateNpc.{gate}"))
            {
                MarkInteracted();
                GateScheduleAutomation.MarkJoined(gate);
            }

            return;
        }

        if (HoldForManualSteer(gate))
        {
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

    /// <summary>Same as Tick, but for a GATE whose registration NPC has more than one physical
    /// spot (e.g. Cliffhanger, confirmed to have two) — picks whichever recorded spot is nearest
    /// each call instead of assuming a single fixed one.</summary>
    public static void TickList(Module.GateType gate, List<GateNpcSpot> spots, bool enabled)
    {
        if (!enabled || spots.Count == 0 || !Vnavmesh.IsInstalled || !GateDirector.InSaucer || GateDirector.IsInGate(gate) ||
            !Player.Available || GateScheduleAutomation.IsReturningAfterGate)
        {
            ReleaseIfOwned(gate);
            ForgetManualSteer(gate);
            return;
        }

        var playerPos = Player.Position;
        GateNpcSpot? nearest = null;
        var nearestDist = float.MaxValue;
        foreach (var spot in spots)
        {
            if (!spot.Recorded)
            {
                continue;
            }

            var dist = Vector3.Distance(playerPos, new Vector3(spot.X, spot.Y, spot.Z));
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = spot;
            }
        }

        if (nearest is not { } target || nearestDist > MaxTriggerDistance)
        {
            ReleaseIfOwned(gate);
            ForgetManualSteer(gate);
            return;
        }

        var destination = new Vector3(target.X, target.Y, target.Z);
        if (Vnavmesh.IsWithinHorizontalRange(destination, CloseRange))
        {
            ReleaseIfOwned(gate);
            if (target.DataId != 0 && !IsInteractOnCooldown &&
                ObjectHelper.TryInteractWithBaseId(target.DataId, CloseRange, $"Saucy.GateNpc.{gate}"))
            {
                MarkInteracted();
                GateScheduleAutomation.MarkJoined(gate);
            }

            return;
        }

        if (HoldForManualSteer(gate))
        {
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

    /// <summary>使用者自己在動（或剛停手不滿 2 秒、或這一趟已經放棄）時回 true：這一幀不要送路徑。</summary>
    private static bool HoldForManualSteer(Module.GateType gate)
    {
        if (gaveUp.Contains(gate))
        {
            return true;
        }

        if (!steers.TryGetValue(gate, out var steer))
        {
            steer = new ManualSteerYield($"GATE 報名 NPC 自動靠近（{gate}）");
            steers[gate] = steer;
        }

        var owns = owners.TryGetValue(gate, out var o) && o;
        switch (steer.Tick(owns, busy: false, out _))
        {
            case ManualSteerYield.Result.Hold:
                return true;
            case ManualSteerYield.Result.GaveUp:
                gaveUp.Add(gate);
                owners[gate] = false;
                Svc.Chat.Print("[Saucy] " + "Stopped walking to the GATE registration NPC: you kept moving on your own for ?? seconds. It starts again after you leave the area and come back.".Loc(ManualSteerWatch.MaxYieldMs / 1000));
                return true;
            default:
                return false;
        }
    }

    private static void ForgetManualSteer(Module.GateType gate)
    {
        gaveUp.Remove(gate);
        if (steers.TryGetValue(gate, out var steer))
        {
            steer.Reset();
        }
    }

    // Deliberately no one-shot "move now" helper here: a single Vnavmesh.TryMoveTo silently does nothing when
    // vnavmesh is missing or the navmesh is still building, and never reports arrival. Use
    // GoldSaucerNavigator.StartRecordedSpot (waits for the mesh, reports success/failure, cancellable).

    // ObjectHelper.TryInteractWithObject needs to be called across MULTIPLE ticks — the first call only sets
    // the target, a later throttled call actually fires the interact (see its own comment for why). A UI
    // button's OnClick fires once per click, so calling it straight from a button handler only ever completes
    // the "set target" phase. Keep retrying every frame (via TickManualInteract, called unconditionally from
    // Saucy.Tick.cs) until it actually succeeds or times out.
    private const double ManualInteractTimeoutSeconds = 5;
    private static GateNpcSpot? pendingManualInteractSpot;
    private static DateTime pendingManualInteractExpiresUtc;

    /// <summary>"立即互動" button — attempts to target+interact with the recorded NPC right now,
    /// without walking there first (only actually fires if the player happens to already be close
    /// enough and the NPC is currently loaded in the object table). Still respects the shared 30s
    /// interact cooldown.</summary>
    public static void TryInteractNow(GateNpcSpot spot)
    {
        if (!spot.Recorded || spot.DataId == 0)
        {
            return;
        }

        GateEmergencyHold.Release("手動與 GATE NPC 互動");
        pendingManualInteractSpot = spot;
        pendingManualInteractExpiresUtc = DateTime.UtcNow.AddSeconds(ManualInteractTimeoutSeconds);
    }

    /// <summary>Call every frame regardless of module/GATE state — keeps retrying a pending manual
    /// interact request (see TryInteractNow) until it actually fires or times out.</summary>
    public static void TickManualInteract()
    {
        if (pendingManualInteractSpot is not { } spot)
        {
            return;
        }

        if (DateTime.UtcNow >= pendingManualInteractExpiresUtc)
        {
            pendingManualInteractSpot = null;
            return;
        }

        if (IsInteractOnCooldown)
        {
            pendingManualInteractSpot = null;
            return;
        }

        if (ObjectHelper.TryInteractWithBaseId(spot.DataId, CloseRange, "Saucy.GateNpc.Manual"))
        {
            MarkInteracted();
            pendingManualInteractSpot = null;
        }
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
