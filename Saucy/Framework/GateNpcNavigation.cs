using Dalamud.Game.ClientState.Conditions;
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
    public const float CloseRange = GateApproachPolicy.CloseRange;

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
    // Wind Blows and Slice is Right share one position (77.6/77.9, -69.8 — 0.3y apart) and so are mutually reachable at ANY radius including the old 5y; they are kept apart by standing down whenever the player is on ANY GATE's stage (GateDirector.IsPlayerOnStage) plus each GATE's own enable toggle, not by this distance.
    private const float MaxTriggerDistance = 25f;

    // 站在活動解說員這麼近（水平）就當成在找解說員，不從這裡出發去報名 NPC。
    private const float NearCoordinatorRange = 5f;

    private static readonly ConditionFlag[] OccupiedFlags =
    [
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied33, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
        ConditionFlag.OccupiedSummoningBell, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78
    ];

    /// <summary>一個報名 NPC 一份：登高跳跳樂與 Air Force One 共用同一個 NPC，合成一份（鍵是 AirForceOne）。</summary>
    private sealed class Approach(Module.GateType key)
    {
        public readonly string Label = key == Module.GateType.AirForceOne
            ? "GATE 報名 NPC 自動靠近（AirForceOne／LeapOfFaith）"
            : $"GATE 報名 NPC 自動靠近（{key}）";

        public readonly string InteractKey = $"Saucy.GateNpc.{key}";
        public readonly GateApproachPolicy Policy = new();
        public bool Owns;
        public DateTime LastTickUtc;
    }

    private static readonly Dictionary<Module.GateType, Approach> approaches = [];

    private static Module.GateType ControllerKey(Module.GateType gate) =>
        gate == Module.GateType.LeapOfFaith ? Module.GateType.AirForceOne : gate;

    private static Approach GetApproach(Module.GateType key)
    {
        if (!approaches.TryGetValue(key, out var approach))
        {
            approach = new Approach(key);
            approaches[key] = approach;
        }

        return approach;
    }

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
        var key = ControllerKey(gate);
        if (!BeginTick(key, out var approach))
        {
            return;
        }

        if (!enabled || !spot.Recorded || !CanRun())
        {
            StandDown(approach);
            return;
        }

        var destination = new Vector3(spot.X, spot.Y, spot.Z);
        Step(key, approach, destination, Vnavmesh.IsWithinHorizontalRange(destination, MaxTriggerDistance), spot.DataId);
    }

    /// <summary>Same as Tick, but for a GATE whose registration NPC has more than one physical
    /// spot (e.g. Cliffhanger, confirmed to have two) — picks whichever recorded spot is nearest
    /// each call instead of assuming a single fixed one.</summary>
    public static void TickList(Module.GateType gate, List<GateNpcSpot> spots, bool enabled)
    {
        var key = ControllerKey(gate);
        if (!BeginTick(key, out var approach))
        {
            return;
        }

        if (!enabled || spots.Count == 0 || !CanRun())
        {
            StandDown(approach);
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

        if (nearest is not { } target)
        {
            Step(key, approach, default, false, 0);
            return;
        }

        Step(key, approach, new Vector3(target.X, target.Y, target.Z), nearestDist <= MaxTriggerDistance, target.DataId);
    }

    // 同一幀同一個 NPC 只跑一次（登高跳跳樂與 Air Force One 兩個模組都會呼叫）。
    private static bool BeginTick(Module.GateType key, out Approach approach)
    {
        approach = GetApproach(key);
        var frame = Svc.Framework.LastUpdateUTC;
        if (approach.LastTickUtc == frame)
        {
            return false;
        }

        approach.LastTickUtc = frame;
        return true;
    }

    // 人在任何一個 GATE 的場上（不只自己這個）就不動：暴風倖存者與必中一閃的報名 NPC 就在舞台邊、觸發範圍蓋得到舞台。
    // GATE 結束後走回活動解說員的那段時間也讓開，不把人拉回報名處。
    private static bool CanRun() =>
        Vnavmesh.IsInstalled && GateDirector.InSaucer && Player.Available && !GateScheduleAutomation.IsReturningAfterGate &&
        !GateDirector.IsPlayerOnStage();

    private static void StandDown(Approach approach)
    {
        Release(approach);
        approach.Policy.Forget();
    }

    private static void Step(Module.GateType key, Approach approach, Vector3 destination, bool inRange, uint dataId)
    {
        var player = Player.Position;
        var dx = player.X - destination.X;
        var dz = player.Z - destination.Z;
        var distance = MathF.Sqrt((dx * dx) + (dz * dz));
        var navigating = inRange && Vnavmesh.IsMoving();
        var coordinatorRun = inRange && GateScheduleAutomation.IsCoordinatorRunActive;
        var occupied = inRange && IsOccupied();
        var nearCoordinator = inRange && IsNearRecordedCoordinator(player);
        var manualKey = inRange && GoldSaucerNavigator.IsManualMovementRequested();
        var decision = approach.Policy.Decide(Environment.TickCount64, player, distance, inRange, approach.Owns, navigating, manualKey,
            coordinatorRun, occupied, nearCoordinator, out var note);

        switch (decision)
        {
            case GateApproachPolicy.Decision.OutOfRange:
                Release(approach);
                return;

            case GateApproachPolicy.Decision.Blocked:
                Release(approach);
                if (note == GateApproachPolicy.Note.Blocked)
                {
                    Svc.Log.Information($"[Saucy] {approach.Label}：{BlockText(approach.Policy.Block, nearCoordinator)}");
                }

                return;

            case GateApproachPolicy.Decision.Close:
                Release(approach);
                if (dataId != 0 && !IsInteractOnCooldown &&
                    ObjectHelper.TryInteractWithBaseId(dataId, CloseRange, approach.InteractKey))
                {
                    MarkInteracted();
                    GateScheduleAutomation.MarkJoined(key);
                    if (key == Module.GateType.AirForceOne)
                    {
                        GateScheduleAutomation.MarkJoined(Module.GateType.LeapOfFaith);
                    }
                }

                return;

            case GateApproachPolicy.Decision.Hold:
                if (note == GateApproachPolicy.Note.YieldStarted)
                {
                    var stopped = navigating && approach.Owns;
                    if (stopped)
                    {
                        Vnavmesh.StopPath();
                    }

                    Svc.Log.Information($"[Saucy] {approach.Label}：你自己接手移動，讓位、不重送路徑" +
                                        (stopped ? "" : "（這時沒有在走自己送的路徑）"));
                }

                return;

            case GateApproachPolicy.Decision.GaveUp:
                // 讓位時自己的路徑早就停了；這時候在跑的移動是使用者自己的，不去停它。
                approach.Owns = false;
                if (note == GateApproachPolicy.Note.WalkingAway)
                {
                    Svc.Log.Information($"[Saucy] {approach.Label}：你在走開（離目的地 {distance:F1} 米，比讓位中最近時遠 " +
                                        $"{distance - approach.Policy.YieldMinDistance:F1} 米），這次不再接續，離開 {MaxTriggerDistance:0} 米再回來才重新啟用");
                }
                else
                {
                    Svc.Log.Information($"[Saucy] {approach.Label}：你自己移動超過 {ManualSteerWatch.MaxYieldMs / 1000} 秒，不再自動接續");
                    Svc.Chat.Print("[Saucy] " + "Stopped walking to the GATE registration NPC: you kept moving on your own for ?? seconds. It starts again after you leave the area and come back.".Loc(ManualSteerWatch.MaxYieldMs / 1000));
                }

                return;
        }

        if (note == GateApproachPolicy.Note.Resumed)
        {
            Svc.Log.Information($"[Saucy] {approach.Label}：停手滿 {ManualSteerWatch.ResumeQuietMs / 1000} 秒，從目前位置接續（讓位 {approach.Policy.LastYieldMs / 1000.0:0.0} 秒）");
        }

        if (approach.Owns)
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
            approach.Owns = true;
        }
    }

    private static string BlockText(GateApproachPolicy.BlockReason reason, bool nearCoordinator) => reason switch
    {
        GateApproachPolicy.BlockReason.CoordinatorRun => "活動解說員排程進行中，不出發",
        GateApproachPolicy.BlockReason.Occupied => nearCoordinator ? "在跟活動解說員對話，不出發也不互動" : "在對話、過場或換區中，不出發也不互動",
        _ => "你在活動解說員旁，不出發"
    };

    private static bool IsOccupied()
    {
        foreach (var flag in OccupiedFlags)
        {
            if (Svc.Condition[flag])
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNearRecordedCoordinator(Vector3 player)
    {
        foreach (var spot in C.GoldSaucerGates.EventCoordinatorSpots)
        {
            var dx = player.X - spot.X;
            var dz = player.Z - spot.Z;
            if (spot.Recorded && (dx * dx) + (dz * dz) <= NearCoordinatorRange * NearCoordinatorRange)
            {
                return true;
            }
        }

        return false;
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
        if (approaches.TryGetValue(ControllerKey(gate), out var approach))
        {
            Release(approach);
        }
    }

    private static void Release(Approach approach)
    {
        if (!approach.Owns)
        {
            return;
        }

        approach.Owns = false;
        if (Vnavmesh.IsInstalled)
        {
            Vnavmesh.StopPath();
        }
    }
}
