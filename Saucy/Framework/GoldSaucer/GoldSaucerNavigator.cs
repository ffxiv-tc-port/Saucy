using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using Lumina.Excel.Sheets;
using Saucy.IPC;
using Saucy.TripleTriad;
using System;
using System.Numerics;
using LuminaMap = Lumina.Excel.Sheets.Map;
namespace Saucy.Framework.GoldSaucer;

/// <summary>
/// One user-triggered "go there" journey inside (or to) the Gold Saucer.
///
/// Replaces what the GATE panels used to do — a bare <c>Vnavmesh.TryMoveTo</c> whose result was
/// thrown away — with an actual route: teleport into the Saucer when the player is somewhere else,
/// hop the Saucer's own aethernet when that genuinely saves a walk, then path the rest with
/// vnavmesh, and say something when it arrives (or when it can't).
///
/// Every integration is a soft dependency. With no vnavmesh installed this degrades to a map flag
/// plus coordinates; with no Lifestream it simply walks. Neither is allowed to throw.
///
/// Scope: it only ever *moves* the player and (on arrival) targets the NPC. It never interacts,
/// registers, or plays anything — those stay with each module's own existing automation.
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
    private static readonly TimeSpan AethernetSettleDelay = TimeSpan.FromSeconds(1.5);

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
        if (!GoldSaucerVenue.TryGetNearestInstance(destination, origin, out var npcId, out var position))
        {
            Svc.Chat.PrintError("[Saucy] " + "Could not resolve a position for ??.".Loc(label));
            return;
        }

        Begin(new Journey
        {
            Label = label,
            NpcId = npcId,
            Destination = destination,
            Target = position
        });
    }

    /// <summary>
    /// Walks to a user-recorded GateNpcSpot using the same route planning, feedback and cancellation
    /// as the sheet-backed destinations.
    ///
    /// GATE registration NPCs are spawned per-GATE and have no Level rows (verified against the TC
    /// 7.20 EXD dump), so a recorded coordinate really is the only way to know where they stand —
    /// but there was never a reason for the *walk* to be worse than everywhere else. The old
    /// "立即移動" button called Vnavmesh.TryMoveTo once and discarded the result, so nothing happened
    /// and nothing was said whenever vnavmesh was missing or the navmesh was still building.
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
        var label = (spot.DataId != 0 ? GoldSaucerVenue.TryGetNpcName(spot.DataId) : null)
                    ?? (string.IsNullOrWhiteSpace(spot.NpcName) ? "the recorded spot".Loc() : spot.NpcName);

        Begin(new Journey
        {
            Label = label,
            NpcId = spot.DataId,
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
            ForcedAethernetName = stop.Name
        });
    }

    public static void Cancel(bool announce = true)
    {
        if (journey == null)
        {
            return;
        }

        var label = journey.Label;
        journey = null;

        if (Vnavmesh.IsInstalled)
        {
            Vnavmesh.StopPath();
        }

        Lifestream.TryAbort();

        if (announce)
        {
            Svc.Chat.Print("[Saucy] " + "Navigation to ?? cancelled.".Loc(label));
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
    /// aethernet first or just walk. This is the piece the old GATE navigation never had.</summary>
    private static void StartInSaucerLeg(Journey active)
    {
        if (active.AethernetOnly)
        {
            if (!TryStartAethernetHop(active, active.ForcedAethernetName!))
            {
                journey = null;
            }

            return;
        }

        // The live object table beats the sheet once we are in the zone.
        RefreshTarget(active);

        if (!active.TriedAethernet && TryPickAethernetStop(active.Target, out var stop))
        {
            if (TryStartAethernetHop(active, stop.Name))
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

    private static bool TryStartAethernetHop(Journey active, string stopName)
    {
        // Release our own path FIRST. Lifestream walks the player to the nearest aethernet access
        // point using vnavmesh, so stopping the path after issuing the command would race with — and
        // sometimes cancel — Lifestream's own movement.
        if (Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopPath();
        }

        if (!Lifestream.TryAethernetViaLiCommand(stopName))
        {
            if (active.AethernetOnly)
            {
                Svc.Chat.PrintError("[Saucy] " + "Lifestream could not start aethernet to ??.".Loc(stopName));
            }

            return false;
        }

        // Latched so a journey can never chain two hops — one is always enough inside a single
        // zone, and re-evaluating after arrival could ping-pong between two nearly-equal stops.
        active.TriedAethernet = true;
        active.AethernetSeenBusy = false;
        active.AethernetClearedUtc = null;
        SetPhase(active, Phase.RidingAethernet, "Aethernet to ??...".Loc(stopName));
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

        SetPhase(active, Phase.Walking, "Walking to ??...".Loc(active.Label));
    }

    public static void Tick()
    {
        if (journey is not { } active)
        {
            return;
        }

        // Zoning: hold everything, and keep the clock from running out mid-loading-screen.
        if (Svc.Condition[ConditionFlag.BetweenAreas] || !Player.Available)
        {
            active.StartedUtc = DateTime.UtcNow;
            active.PhaseStartedUtc = DateTime.UtcNow;
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

    /// <summary>Combat, a manual movement key, or a timeout all end the trip. The user asked for
    /// "cancellable at any time", and silently steering someone who has grabbed the keyboard back is
    /// the single most irritating thing an auto-walker can do.</summary>
    private static bool ShouldAbort(Journey active)
    {
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            Fail(active, "Stopped navigation: you are in combat.".Loc());
            return true;
        }

        if (IsManualMovementRequested())
        {
            Fail(active, "Stopped navigation: manual movement detected.".Loc());
            return true;
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

    private static bool IsManualMovementRequested()
    {
        foreach (var key in MovementKeys)
        {
            if (Svc.KeyState[key])
            {
                return true;
            }
        }

        return false;
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
        if (Lifestream.IsBusyNow() || Vnavmesh.IsMoving())
        {
            active.AethernetSeenBusy = true;
            active.AethernetClearedUtc = null;
            return;
        }

        // Lifestream never picked the request up (busy elsewhere, destination not unlocked, …).
        // Fall through to walking rather than standing still until the phase times out.
        if (!active.AethernetSeenBusy)
        {
            if (DateTime.UtcNow - active.PhaseStartedUtc <= TimeSpan.FromSeconds(15))
            {
                return;
            }

            if (active.AethernetOnly)
            {
                Fail(active, "Aethernet travel did not start.".Loc());
                return;
            }

            Svc.Chat.Print("[Saucy] " + "Aethernet travel did not start. Walking instead.".Loc());
            StartWalking(active);
            return;
        }

        active.AethernetClearedUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - active.AethernetClearedUtc.Value < AethernetSettleDelay)
        {
            return;
        }

        if (!Player.Interactable || Player.IsAnimationLocked)
        {
            return;
        }

        if (active.AethernetOnly)
        {
            Finish(active);
            return;
        }

        RefreshTarget(active);
        StartWalking(active);
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
        RefreshTarget(active);

        if (GoldSaucerVenue.HorizontalDistance(Player.Position, active.Target) <= ArriveRange)
        {
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }

            TargetOnArrival(active);
            Finish(active);
            return;
        }

        if (Vnavmesh.IsMoving())
        {
            return;
        }

        // Re-issuing is normal: vnavmesh finishes a path slightly short, or the NPC's live position
        // moved the goal. Give up after a few attempts instead of looping forever.
        if (active.MoveRetries > 5)
        {
            Fail(active, "Could not path to ??.".Loc(active.Label));
            return;
        }

        if (!Vnavmesh.TryMoveTo(active.Target, false, ArriveRange))
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
                    active.Destination, Player.Position, out var npcId, out var position))
            {
                active.NpcId = npcId;
                active.Target = position;
            }

            return;
        }

        // Recorded-spot journey: the stored coordinate is only a starting hint. Once the NPC is
        // actually loaded, walk to where it really is — GATE registration NPCs are respawned per
        // GATE and do not always reappear on the exact yalm they were recorded at.
        if (active.NpcId == 0)
        {
            return;
        }

        var live = ObjectHelper.FindNearestByBaseId(active.NpcId);
        if (live != null)
        {
            active.Target = live.Position;
        }
    }

    /// <summary>Soft-targets the NPC on arrival so the player can just press their interact key.
    /// Deliberately does NOT interact — registering for an activity stays a manual choice.</summary>
    private static void TargetOnArrival(Journey active)
    {
        if (active.NpcId == 0)
        {
            return;
        }

        var npc = ObjectHelper.FindNearestByBaseId(active.NpcId, ArriveRange + 4f);
        if (npc != null)
        {
            Svc.Targets.Target = npc;
        }
    }

    private static void Finish(Journey active)
    {
        journey = null;
        Svc.Chat.Print("[Saucy] " + "Arrived at ??.".Loc(active.Label));
    }

    private static void Fail(Journey active, string message)
    {
        journey = null;
        if (Vnavmesh.IsInstalled)
        {
            Vnavmesh.StopPath();
        }

        Lifestream.TryAbort();
        Svc.Chat.PrintError("[Saucy] " + message);
    }

    private static void SetPhase(Journey active, Phase phase, string status)
    {
        active.Phase = phase;
        active.PhaseStartedUtc = DateTime.UtcNow;
        active.Status = status;
        active.MoveRetries = 0;
    }

    /// <summary>vnavmesh-less fallback: mark the destination on the map and print its coordinates,
    /// so the feature still does something useful instead of silently doing nothing (which is what
    /// the old "立即移動" button did whenever vnavmesh was absent).</summary>
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
        public DateTime? AethernetClearedUtc;
        public bool AethernetSeenBusy;
        public GoldSaucerDestination? Destination;
        public string? ForcedAethernetName;
        public required string Label;
        public int MoveRetries;
        public uint NpcId;
        public Phase Phase;
        public DateTime PhaseStartedUtc;
        public DateTime StartedUtc;
        public string Status = string.Empty;
        public required Vector3 Target;
        public bool TriedAethernet;
    }
}
