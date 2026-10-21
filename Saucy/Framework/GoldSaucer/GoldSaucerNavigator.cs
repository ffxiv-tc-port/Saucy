using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Saucy.IPC;
using Saucy.JumboCactpot;
using Saucy.MiniCactpot;
using Saucy.TripleTriad;
using System;
using System.Numerics;
using LuminaMap = Lumina.Excel.Sheets.Map;
namespace Saucy.Framework.GoldSaucer;

/// <summary>
/// One user-triggered "go there" journey inside (or to) the Gold Saucer.
/// Every integration is a soft dependency. With no vnavmesh installed this degrades to a map flag plus coordinates; with no Lifestream it simply walks. Neither is allowed to throw.
/// Scope: it only ever *moves* the player and (on arrival) targets the NPC. It never interacts, registers, or plays anything — those stay with each module's own existing automation.
/// One opt-in exception (off by default): arriving at the Mini Cactpot broker hands off to MiniCactpotAutoPlay. A ticket is 10 MGP and locks the player into nothing, unlike a GATE registration.
/// The Jumbo Cactpot broker has the same opt-in hand-off (JumboCactpotAutoTalk); it stops once the ticket panel opens, and every purchase is still confirmed by the player.
/// </summary>
internal static class GoldSaucerNavigator
{
    /// <summary>Stop this far from the destination. Slightly wider than the 3y interact range so the
    /// path does not fight the NPC's own collision capsule.</summary>
    private const float ArriveRange = 3.5f;

    /// <summary>Below this the aethernet is never worth considering — just walk.</summary>
    private const float MinWalkBeforeAethernet = 45f;

    /// <summary>An aethernet hop has to save at least this much walking to be worth the loading
    /// screen and the walk to the shard on the far side.</summary>
    private const float MinAethernetSavings = 35f;

    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AethernetTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NavReadyTimeout = TimeSpan.FromMinutes(5);

    private static Journey? journey;

    public static bool IsActive => journey != null;

    /// <summary>Already-localized one-line status for the panel. Null when idle.</summary>
    public static string? StatusText => journey?.Status;

    /// <summary>Walks to an activity's acceptance point, teleporting/aethernetting in as needed.</summary>
    public static void Start(GoldSaucerDestination destination)
    {
        var label = destination.LabelKey.Loc();

        // Resolve which physical counter to head for up front so a destination whose NPC the sheets
        // don't know about fails loudly here instead of starting a journey to Vector3.Zero.
        var origin = Player.Available ? Player.Position : Vector3.Zero;
        if (!GoldSaucerVenue.TryGetNearestInstance(destination, origin, out var objectId, out var position))
        {
            Svc.Chat.PrintError("[Saucy] " + "Could not resolve a position for ??.".Loc(label));
            return;
        }

        Begin(new Journey
        {
            Label = label,
            ObjectId = objectId,
            Destination = destination,
            Target = position
        });
    }

    /// <summary>
    /// Walks to a user-recorded GateNpcSpot using the same route planning, feedback and cancellation as the sheet-backed destinations.
    /// GATE registration NPCs are spawned per-GATE and have no Level rows, so a recorded coordinate really is the only way to know where they stand.
    /// </summary>
    public static void StartRecordedSpot(GateNpcSpot spot)
    {
        if (!spot.Recorded)
        {
            return;
        }

        // The recorded NpcName is whatever the player had targeted, i.e. already in their own
        // language — but prefer the sheet name when the DataId resolves, so a spot recorded on a
        // different client still reads correctly.
        var label = (spot.DataId != 0 ? GoldSaucerVenue.TryGetObjectName(spot.DataId) : null)
                    ?? (string.IsNullOrWhiteSpace(spot.NpcName) ? "the recorded spot".Loc() : spot.NpcName);

        Begin(new Journey
        {
            Label = label,
            ObjectId = spot.DataId,
            Target = new Vector3(spot.X, spot.Y, spot.Z)
        });
    }

    /// <summary>Rides the Saucer's internal aethernet to one of its stops. Pure Lifestream — no
    /// walking leg — so it is also the graceful answer when vnavmesh is missing.</summary>
    public static void StartAethernet(GoldSaucerVenue.AethernetStop stop)
    {
        Begin(new Journey
        {
            Label = stop.Name,
            Target = stop.Position,
            AethernetOnly = true,
            ForcedAethernetStop = stop
        });
    }

    public static void Cancel(bool announce = true)
    {
        if (journey == null)
        {
            return;
        }

        var active = journey;
        journey = null;
        Log(active.Label, announce ? "已取消" : "被新的導航取代");

        StopOwnPath(active);
        Lifestream.TryAbort();

        if (announce)
        {
            Svc.Chat.Print("[Saucy] " + "Navigation to ?? cancelled.".Loc(active.Label));
        }
    }

    private static void Begin(Journey next)
    {
        // Starting a second journey silently replaces the first; stop the old path so vnavmesh is
        // not left steering toward the previous destination.
        if (journey != null)
        {
            Cancel(false);
        }

        MiniCactpotAutoPlay.Cancel(false);
        JumboCactpotAutoTalk.Cancel(false);

        journey = next;
        journey.StartedUtc = DateTime.UtcNow;
        journey.PhaseStartedUtc = DateTime.UtcNow;

        // No vnavmesh means we can still be useful: drop a map flag and print the coordinates so the
        // player can walk it themselves. An aethernet-only trip needs no pathing at all, so it is
        // allowed through.
        if (!Vnavmesh.IsInstalled && !next.AethernetOnly)
        {
            DropMapFlag(next);
            journey = null;
            return;
        }

        AdvanceToFirstPhase();
    }

    private static void AdvanceToFirstPhase()
    {
        if (journey is not { } active)
        {
            return;
        }

        if (!GoldSaucerVenue.InSaucer)
        {
            if (!TryStartTeleportToSaucer(active))
            {
                journey = null;
            }

            return;
        }

        StartInSaucerLeg(active);
    }

    private static bool TryStartTeleportToSaucer(Journey active)
    {
        if (!Lifestream.IsInstalled)
        {
            Svc.Chat.PrintError("[Saucy] " +
                "Not in the Gold Saucer. Install Lifestream to teleport there automatically.".Loc());
            return false;
        }

        if (!AetheryteHelper.IsUnlockedForTravel(GoldSaucerVenue.AetheryteId))
        {
            Svc.Chat.PrintError("[Saucy] " + "The Gold Saucer aetheryte is not unlocked yet.".Loc());
            return false;
        }

        if (!Lifestream.TryTeleport(GoldSaucerVenue.AetheryteId))
        {
            Svc.Chat.PrintError("[Saucy] " + "Lifestream could not start the teleport.".Loc());
            return false;
        }

        SetPhase(active, Phase.TeleportingToSaucer, "Teleporting to the Gold Saucer...".Loc());
        return true;
    }

    /// <summary>Decides, now that we are actually standing in the Saucer, whether to ride the
    /// aethernet first or just walk.</summary>
    private static void StartInSaucerLeg(Journey active)
    {
        if (active.AethernetOnly)
        {
            if (active.ForcedAethernetStop is not { } forced || !TryStartAethernetHop(active, forced))
            {
                journey = null;
            }

            return;
        }

        // The live object table beats the sheet once we are in the zone.
        RefreshTarget(active);

        if (!active.TriedAethernet && TryPickAethernetStop(active.Target, out var stop))
        {
            if (TryStartAethernetHop(active, stop))
            {
                return;
            }
        }

        StartWalking(active);
    }

    /// <summary>Picks the stop that is meaningfully closer to the destination than the player is,
    /// or reports none. Uses the sheet-derived stop list, so it also covers the hub crystal.</summary>
    private static bool TryPickAethernetStop(Vector3 target, out GoldSaucerVenue.AethernetStop stop)
    {
        stop = default;

        if (!Lifestream.IsInstalled ||
            !AetheryteHelper.IsUnlockedForTravel(GoldSaucerVenue.AetheryteId) ||
            !Player.Available)
        {
            return false;
        }

        var walkDistance = GoldSaucerVenue.HorizontalDistance(Player.Position, target);
        if (walkDistance < MinWalkBeforeAethernet)
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        var found = false;
        foreach (var candidate in GoldSaucerVenue.AethernetStops)
        {
            var distance = GoldSaucerVenue.HorizontalDistance(candidate.Position, target);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                stop = candidate;
                found = true;
            }
        }

        if (!found || walkDistance - bestDistance < MinAethernetSavings)
        {
            return false;
        }

        // Already standing on the best stop — hopping to it would be a no-op loading screen.
        return GoldSaucerVenue.HorizontalDistance(Player.Position, stop.Position) > Vnavmesh.AetheryteCloseRange;
    }

    private static bool TryStartAethernetHop(Journey active, GoldSaucerVenue.AethernetStop stop)
    {
        // Release our own path FIRST. Lifestream walks the player to the nearest aethernet access
        // point using vnavmesh, so stopping the path after issuing the command would race with — and
        // sometimes cancel — Lifestream's own movement.
        StopOwnPath(active);

        if (!Lifestream.TryAethernetViaLiCommand(stop.Name))
        {
            if (active.AethernetOnly)
            {
                Svc.Chat.PrintError("[Saucy] " + "Lifestream could not start aethernet to ??.".Loc(stop.Name));
            }

            return false;
        }

        // Latched so a journey can never chain two hops — one is always enough inside a single
        // zone, and re-evaluating after arrival could ping-pong between two nearly-equal stops.
        active.TriedAethernet = true;
        active.Hop = new AethernetHopWatch($"金碟導航「{active.Label}」", stop.Name, GoldSaucerVenue.TerritoryId, stop.Position);
        SetPhase(active, Phase.RidingAethernet, "Aethernet to ??...".Loc(stop.Name));
        return true;
    }

    private static void StartWalking(Journey active)
    {
        if (!Vnavmesh.IsNavReady())
        {
            SetPhase(active, Phase.WaitingForNavMesh, "Waiting for the navmesh to finish building...".Loc());
            Vnavmesh.TryEnsureNavMeshLoading();
            return;
        }

        active.Steer.Reset();
        SetPhase(active, Phase.Walking, "Walking to ??...".Loc(active.Label));
    }

    public static void Tick()
    {
        if (journey is not { } active)
        {
            return;
        }

        if (active.NavOwned && !Vnavmesh.IsMoving())
        {
            active.NavOwned = false;
        }

        // Zoning: hold everything, and keep the clock from running out mid-loading-screen.
        if (Svc.Condition[ConditionFlag.BetweenAreas] || !Player.Available)
        {
            active.StartedUtc = DateTime.UtcNow;
            active.PhaseStartedUtc = DateTime.UtcNow;
            if (active.Phase == Phase.RidingAethernet)
            {
                active.Hop?.NoteLoading();
            }

            return;
        }

        if (ShouldAbort(active))
        {
            return;
        }

        switch (active.Phase)
        {
            case Phase.TeleportingToSaucer:
                TickTeleport(active);
                return;
            case Phase.RidingAethernet:
                TickAethernet(active);
                return;
            case Phase.WaitingForNavMesh:
                TickWaitingForNavMesh(active);
                return;
            case Phase.Walking:
                TickWalking(active);
                return;
        }
    }

    /// <summary>Combat or a timeout ends the trip. Steering someone who has grabbed the controls back is
    /// the single most irritating thing an auto-walker can do: while walking, manual movement makes
    /// TickWalking yield instead; in the other phases a movement key still ends the trip.</summary>
    private static bool ShouldAbort(Journey active)
    {
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            Fail(active, "Stopped navigation: you are in combat.".Loc());
            return true;
        }

        if (active.Phase != Phase.Walking && IsManualMovementRequested())
        {
            Fail(active, "Stopped navigation: manual movement detected.".Loc());
            return true;
        }

        // 讓位期間等的是使用者，不算逾時；讓位本身有自己的上限。
        if (active.Steer.Yielding)
        {
            return false;
        }

        if (DateTime.UtcNow - active.StartedUtc > OverallTimeout)
        {
            Fail(active, "Navigation to ?? timed out.".Loc(active.Label));
            return true;
        }

        var phaseTimeout = active.Phase switch
        {
            Phase.TeleportingToSaucer => TeleportTimeout,
            Phase.RidingAethernet => AethernetTimeout,
            Phase.WaitingForNavMesh => NavReadyTimeout,
            _ => OverallTimeout
        };

        if (DateTime.UtcNow - active.PhaseStartedUtc > phaseTimeout)
        {
            Fail(active, "Navigation to ?? timed out.".Loc(active.Label));
            return true;
        }

        return false;
    }

    internal static bool IsManualMovementRequested()
    {
        // Typing "was" into the chat box must not read as "the player grabbed the controls".
        // Svc.KeyState reflects raw key state, which the game keeps updating while a text field has
        // focus, so without this guard any chat message containing W/A/S/D would cancel the trip.
        if (IsTextInputActive())
        {
            return false;
        }

        foreach (var key in MovementKeys)
        {
            if (Svc.KeyState[key])
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe bool IsTextInputActive()
    {
        var module = RaptureAtkModule.Instance();
        return module != null && module->AtkModule.IsTextInputActive();
    }

    private static readonly VirtualKey[] MovementKeys =
    [
        VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D,
        VirtualKey.UP, VirtualKey.DOWN, VirtualKey.LEFT, VirtualKey.RIGHT
    ];

    private static void TickTeleport(Journey active)
    {
        if (Lifestream.IsBusyNow() || !Player.Interactable || Player.IsAnimationLocked)
        {
            return;
        }

        if (!GoldSaucerVenue.InSaucer)
        {
            return;
        }

        StartInSaucerLeg(active);
    }

    private static void TickAethernet(Journey active)
    {
        if (active.Hop is not { } hop)
        {
            Fail(active, "Aethernet travel did not start.".Loc());
            return;
        }

        switch (hop.Update(Lifestream.IsBusyNow() || Vnavmesh.IsMoving()))
        {
            case AethernetHopWatch.Result.Waiting:
                return;

            case AethernetHopWatch.Result.Landed:
                if (active.AethernetOnly)
                {
                    Finish(active);
                    return;
                }

                RefreshTarget(active);
                StartWalking(active);
                return;

            case AethernetHopWatch.Result.TimedOut:
                Fail(active, "Navigation to ?? timed out.".Loc(active.Label));
                return;

            default:
                // Lifestream never picked the request up, or finished without the hop happening:
                // nothing moved the character, so a path planned now starts where it really stands.
                if (active.AethernetOnly)
                {
                    Fail(active, "Aethernet travel did not start.".Loc());
                    return;
                }

                Svc.Chat.Print("[Saucy] " + "Aethernet travel did not start. Walking instead.".Loc());
                StartWalking(active);
                return;
        }
    }

    private static void TickWaitingForNavMesh(Journey active)
    {
        if (!Vnavmesh.IsNavReady())
        {
            Vnavmesh.TryEnsureNavMeshLoading();
            return;
        }

        StartWalking(active);
    }

    private static void TickWalking(Journey active)
    {
        if (!GoldSaucerVenue.InSaucer)
        {
            Fail(active, "Stopped navigation: you left the Gold Saucer.".Loc(), releaseMovement: false);
            return;
        }

        RefreshTarget(active);

        if (GoldSaucerVenue.HorizontalDistance(Player.Position, active.Target) <= ArriveRange)
        {
            StopOwnPath(active);
            TargetOnArrival(active);
            Finish(active);
            if (active.Destination?.Key == GoldSaucerVenue.MiniCactpotKey)
            {
                MiniCactpotAutoPlay.OnArrived();
            }
            else if (active.Destination?.Key == GoldSaucerVenue.JumboCactpotKey)
            {
                JumboCactpotAutoTalk.OnArrived();
            }

            return;
        }

        if (TickManualSteer(active))
        {
            return;
        }

        if (Vnavmesh.IsMoving())
        {
            return;
        }

        // Re-issuing is normal: vnavmesh finishes a path slightly short, or the NPC's live position
        // moved the goal. Only reached while the player stands still, so it never fights their input.
        // Give up after a few failed starts instead of looping forever.
        if (active.MoveRetries > 5)
        {
            Fail(active, "Could not path to ??.".Loc(active.Label));
            return;
        }

        active.NavOwned = Vnavmesh.TryMoveTo(active.Target, false, ArriveRange);
        if (!active.NavOwned)
        {
            active.MoveRetries++;
        }
    }

    /// <summary>Keeps the goal honest while walking: once the NPC is loaded, its real position beats
    /// the Level-sheet one.</summary>
    private static void RefreshTarget(Journey active)
    {
        if (active.AethernetOnly || !Player.Available)
        {
            return;
        }

        if (active.Destination != null)
        {
            if (GoldSaucerVenue.TryGetNearestInstance(
                    active.Destination, Player.Position, out var objectId, out var position))
            {
                active.ObjectId = objectId;
                active.Target = position;
            }

            return;
        }

        // Recorded-spot journey: the stored coordinate is only a starting hint. Once the NPC is
        // actually loaded, walk to where it really is — GATE registration NPCs are respawned per
        // GATE and do not always reappear on the exact yalm they were recorded at.
        if (active.ObjectId == 0)
        {
            return;
        }

        var live = ObjectHelper.FindNearestByBaseId(active.ObjectId);
        if (live != null)
        {
            active.Target = live.Position;
        }
    }

    /// <summary>Soft-targets the NPC on arrival so the player can just press their interact key.
    /// Deliberately does NOT interact — registering for an activity stays a manual choice.</summary>
    private static void TargetOnArrival(Journey active)
    {
        if (active.ObjectId == 0)
        {
            return;
        }

        var npc = ObjectHelper.FindNearestByBaseId(active.ObjectId, ArriveRange + 4f);
        if (npc != null)
        {
            Svc.Targets.Target = npc;
        }
    }

    private static void Finish(Journey active)
    {
        journey = null;
        Log(active.Label, "已抵達");
        Svc.Chat.Print("[Saucy] " + "Arrived at ??.".Loc(active.Label));
    }

    /// <summary>Hands the controls back to the player: no new path while they move, cast or talk, and
    /// one resume from where they stand once they have been still for
    /// <see cref="ManualSteerWatch.ResumeQuietMs"/>. Returns true when this frame must not send a path.</summary>
    private static bool TickManualSteer(Journey active)
    {
        var navigating = Vnavmesh.IsMoving();
        var busy = Svc.Condition[ConditionFlag.Casting] ||
                   Svc.Condition[ConditionFlag.OccupiedInEvent] ||
                   Svc.Condition[ConditionFlag.OccupiedInQuestEvent];
        var step = active.Steer.Observe(
            Player.Position, navigating, IsManualMovementRequested(), busy, Environment.TickCount64);
        switch (step)
        {
            case ManualSteerWatch.Step.Started:
                StopOwnPath(active);
                active.YieldStartedUtc = DateTime.UtcNow;
                active.Status = "Paused: you are moving on your own. Picks up again once you stop for ?? seconds."
                    .Loc(ManualSteerWatch.ResumeQuietMs / 1000);
                Log(active.Label, "你自己接手了（按移動鍵、vnavmesh 停著角色仍在動、或詠唱／對話中），讓位、不重送路徑");
                return true;

            case ManualSteerWatch.Step.Resume:
                var paused = DateTime.UtcNow - active.YieldStartedUtc;
                active.StartedUtc += paused;
                active.PhaseStartedUtc += paused;
                active.Status = "Walking to ??...".Loc(active.Label);
                Log(active.Label, $"停手滿 {ManualSteerWatch.ResumeQuietMs / 1000} 秒，從目前位置接續（讓位 {paused.TotalSeconds:0.0} 秒）");
                return false;

            case ManualSteerWatch.Step.GaveUp:
                // 讓位時路徑早就停了；這時候在跑的移動或 Lifestream 是使用者自己的，不去停它。
                Fail(active, "Stopped navigation: you kept moving on your own for ?? seconds.".Loc(ManualSteerWatch.MaxYieldMs / 1000),
                    releaseMovement: false);
                return true;

            case ManualSteerWatch.Step.Settle:
                return true;

            default:
                return active.Steer.Yielding;
        }
    }

    private static void Fail(Journey active, string message, bool releaseMovement = true)
    {
        journey = null;
        Log(active.Label, $"停止（階段 {active.Phase}）：{message}");
        if (releaseMovement)
        {
            StopOwnPath(active);
            Lifestream.TryAbort();
        }

        Svc.Chat.PrintError("[Saucy] " + message);
    }

    /// <summary>只停這一趟自己送的步行路徑；別的插件的路徑不碰。Lifestream 的移動由 <see cref="Lifestream.TryAbort"/> 自己收。</summary>
    private static void StopOwnPath(Journey active)
    {
        if (!active.NavOwned)
        {
            return;
        }

        active.NavOwned = false;
        if (Vnavmesh.IsMoving())
        {
            Vnavmesh.StopOwnPath();
        }
    }

    private static void SetPhase(Journey active, Phase phase, string status)
    {
        active.Phase = phase;
        active.PhaseStartedUtc = DateTime.UtcNow;
        active.Status = status;
        active.MoveRetries = 0;
        Log(active.Label, $"階段 {phase}（{status}）");
    }

    /// <summary>One Information line per step change, never per frame.</summary>
    private static void Log(string label, string message) =>
        Svc.Log.Information($"[Saucy] 金碟導航「{label}」：{message}；{AethernetHopWatch.DescribeLocation()}。");

    /// <summary>vnavmesh-less fallback: mark the destination on the map and print its coordinates,
    /// so the feature still does something useful instead of silently doing nothing.</summary>
    private static void DropMapFlag(Journey active)
    {
        Svc.Chat.Print("[Saucy] " +
            "vnavmesh is not installed — marking ?? on the map instead.".Loc(active.Label));

        var mapId = Svc.Data.GetExcelSheet<TerritoryType>()
            ?.GetRowOrDefault(GoldSaucerVenue.TerritoryId)?.Map.RowId ?? 0;
        var mapRow = mapId != 0 ? Svc.Data.GetExcelSheet<LuminaMap>()?.GetRowOrDefault(mapId) : null;
        if (mapRow is not { } map)
        {
            Svc.Chat.Print($"[Saucy] {active.Label}: ({active.Target.X:F1}, {active.Target.Z:F1})");
            return;
        }

        var mapX = WorldToMapCoordinate(active.Target.X, map.SizeFactor, map.OffsetX);
        var mapY = WorldToMapCoordinate(active.Target.Z, map.SizeFactor, map.OffsetY);

        try
        {
            Svc.GameGui.OpenMapWithMapLink(
                new MapLinkPayload(GoldSaucerVenue.TerritoryId, mapId, mapX, mapY));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Could not open the map link for the Gold Saucer destination.");
        }

        Svc.Chat.Print($"[Saucy] {active.Label}: ({mapX:F1}, {mapY:F1})");
    }

    /// <summary>Standard world -> map-coordinate conversion (the same one the game's own flag uses).</summary>
    private static float WorldToMapCoordinate(float value, ushort sizeFactor, short offset)
    {
        var scale = sizeFactor / 100f;
        var scaled = (value + offset) * scale;
        return (41f / scale * ((scaled + 1024f) / 2048f)) + 1f;
    }

    private enum Phase
    {
        TeleportingToSaucer,
        RidingAethernet,
        WaitingForNavMesh,
        Walking
    }

    private sealed class Journey
    {
        public bool AethernetOnly;
        public GoldSaucerDestination? Destination;
        public GoldSaucerVenue.AethernetStop? ForcedAethernetStop;
        public AethernetHopWatch? Hop;
        public required string Label;
        public int MoveRetries;

        /// <summary>vnavmesh 現在走的是這一趟自己送的步行路徑：送路徑成功才設，vnavmesh 停了就清。</summary>
        public bool NavOwned;

        public uint ObjectId;
        public Phase Phase;
        public DateTime PhaseStartedUtc;
        public DateTime StartedUtc;
        public string Status = string.Empty;
        public readonly ManualSteerWatch Steer = new();
        public required Vector3 Target;
        public bool TriedAethernet;
        public DateTime YieldStartedUtc;
    }
}
