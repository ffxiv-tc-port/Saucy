using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
namespace Saucy;

/// <summary>
/// "Go to" shortcuts for every Gold Saucer activity acceptance point, plus the Saucer's own
/// aethernet stops.
///
/// This exists because the previous navigation only knew about GATE registration NPCs the user had
/// personally recorded, only engaged within 5 yalms of them, and never used a teleport or the
/// Saucer's internal aethernet. Everything listed here is resolved from the game's own sheets, so
/// it needs no setup and shows the NPC's real in-game name.
/// </summary>
public unsafe partial class PluginUI
{
    private static void DrawNavigationPanel()
    {
        DrawPanelHeader("Navigation".Loc(), "Travel to Gold Saucer activities".Loc());

        DrawNavigationStatus();

        ImGui.Dummy(new(0, 4));
        ImGui.TextWrapped("Pick an activity to walk there. Teleports into the Gold Saucer if needed and uses the Saucer's aethernet when that saves a long walk.".Loc());

        if (!Vnavmesh.IsInstalled)
        {
            SaucyTheme.TextMuted("vnavmesh is not installed — destinations will be marked on your map instead of walked to.".Loc());
        }

        ImGui.Dummy(new(0, 4));
        ImGui.Separator();
        SaucyTheme.TextMuted("Activity acceptance points".Loc());

        foreach (var destination in GoldSaucerVenue.Destinations)
        {
            DrawDestinationRow(destination);
        }

        DrawAethernetSection();
    }

    private static void DrawNavigationStatus()
    {
        if (GoldSaucerNavigator.IsActive)
        {
            ImGui.TextUnformatted(GoldSaucerNavigator.StatusText ?? string.Empty);
            ImGui.SameLine();
            if (ImGui.Button($"{"Stop".Loc()}##GoldSaucerNavStop"))
            {
                GoldSaucerNavigator.Cancel();
            }

            return;
        }

        SaucyTheme.TextMuted("Idle.".Loc());
    }

    private static void DrawDestinationRow(GoldSaucerDestination destination)
    {
        // Resolve the NPC name from ENpcResident so the row shows what the player actually sees
        // in-game. A destination whose row is missing is skipped entirely rather than rendered as an
        // unlabelled button that would navigate nowhere.
        var npcName = ResolveDestinationNpcName(destination);
        if (npcName == null)
        {
            return;
        }

        ImGui.PushID($"GoldSaucerNav{destination.Key}");

        if (ImGui.Button($"{"Go".Loc()}##Go"))
        {
            GoldSaucerNavigator.Start(destination);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(destination.LabelKey.Loc());
        ImGui.SameLine();
        SaucyTheme.TextMuted($"({npcName})");

        ImGui.PopID();
    }

    private static string? ResolveDestinationNpcName(GoldSaucerDestination destination)
    {
        foreach (var npcId in destination.NpcIds)
        {
            var name = GoldSaucerVenue.TryGetNpcName(npcId);
            if (name != null)
            {
                return name;
            }
        }

        return null;
    }

    private static void DrawAethernetSection()
    {
        var stops = GoldSaucerVenue.AethernetStops;
        if (stops.Count == 0)
        {
            return;
        }

        ImGui.Dummy(new(0, 8));
        ImGui.Separator();
        SaucyTheme.TextMuted("Gold Saucer aethernet".Loc());

        using var disabled = ImRaii.Disabled(!Lifestream.IsInstalled);
        if (!Lifestream.IsInstalled)
        {
            SaucyTheme.TextMuted("Requires Lifestream.".Loc());
        }

        foreach (var stop in stops)
        {
            ImGui.PushID($"GoldSaucerAethernet{stop.RowId}");
            if (ImGui.Button($"{"Go".Loc()}##Go"))
            {
                GoldSaucerNavigator.StartAethernet(stop);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(stop.Name);
            if (stop.IsHub)
            {
                ImGui.SameLine();
                SaucyTheme.TextMuted("(" + "hub".Loc() + ")");
            }

            ImGui.PopID();
        }
    }

    /// <summary>Shown on each GATE registration panel: those NPCs are spawned per-GATE and have no
    /// Level rows, so they still rely on a recorded spot — but the walk itself can now use the same
    /// cancellable navigator as everything else instead of a fire-and-forget TryMoveTo.</summary>
    internal static void DrawRecordedSpotNavigationRow(GateNpcSpot spot, string idSuffix)
    {
        if (!spot.Recorded)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button($"{"Go".Loc()}##GoldSaucerNavSpot{idSuffix}"))
        {
            GoldSaucerNavigator.StartRecordedSpot(spot);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Walks there with arrival and failure feedback, and stops if you take manual control or enter combat.".Loc());
        }
    }
}
