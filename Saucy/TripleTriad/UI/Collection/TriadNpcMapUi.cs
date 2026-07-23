using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using ECommons.LanguageHelpers;
using Saucy.IPC;
namespace Saucy.TripleTriad;

internal static class TriadNpcMapUi
{
    public static void DrawMapLocationRow(MapLinkPayload location, string showOnMapTooltip, TriadNpc? npc = null)
    {
        var label = $"{location.PlaceName} {location.CoordinateString}";
        var tooltip = BuildTooltip(location, showOnMapTooltip, npc);
        var leftClick = false;
        var rightClick = false;

        ImGui.AlignTextToFramePadding();
        var rowY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(rowY - ImGui.GetStyle().FramePadding.Y);

        ImGuiComponents.IconButton(FontAwesomeIcon.Map);
        CollectMapNavigationClick(ref leftClick, ref rightClick);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        ImGui.SetCursorPosY(rowY);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();

        using var link = ImGuiLayout.PlainTextLink();
        ImGui.Selectable(label);
        CollectMapNavigationClick(ref leftClick, ref rightClick);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        if (TriadBattleHall.ShouldBlockMapNavigation(npc, location))
        {
            if (leftClick || rightClick)
            {
                TriadBattleHall.PrintNavigationBlocked();
            }

            return;
        }

        if (rightClick)
        {
            TriadMapNavigation.HandleMapClick(location, npc, goal: TriadNavigationGoal.FarmMgp);
        }
        else if (leftClick)
        {
            TriadMapNavigation.HandleMapClick(location, npc, goal: TriadNavigationGoal.FarmCards);
        }
    }

    private static void CollectMapNavigationClick(ref bool leftClick, ref bool rightClick)
    {
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            leftClick = true;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            rightClick = true;
        }
    }

    private static string BuildTooltip(MapLinkPayload location, string showOnMapTooltip, TriadNpc? npc = null)
    {
        if (TriadBattleHall.ShouldBlockMapNavigation(npc, location))
        {
            return showOnMapTooltip + "\n" + "The Battlehall is a Duty Finder instance.\nSaucy cannot path there.".Loc();
        }

        var unlockLine = TriadNpcUnlockHelper.TryGetTooltipLine(npc);
        if (unlockLine != null)
        {
            return $"{showOnMapTooltip}\n{unlockLine}";
        }

        if (!Vnavmesh.IsInstalled)
        {
            return showOnMapTooltip + "\n" + "Install vnavmesh to walk to this NPC.".Loc();
        }

        var lines = showOnMapTooltip + "\n" + "Left-click: path there and farm missing cards.".Loc();
        if (npc != null)
        {
            lines += "\n" + "Right-click: path there and farm MGP.".Loc();
            lines += "\n" + "Enables Triple Triad automation on arrival.".Loc();
            lines += "\n" + "Left-click uses MGP farm if you already have every card from this NPC.".Loc();
            lines += "\n" + "Left-click with missing cards builds an optimized deck even if that option is off.".Loc();
        }
        else
        {
            lines = showOnMapTooltip + "\n" + "Click to path with vnavmesh.".Loc();
        }

        if (Lifestream.IsInstalled)
        {
            lines += "\n" + "Uses Lifestream for travel (aetheryte or aethernet shard).".Loc();
            var route = MultiAreaRouteRegistry.FindRoute(location);
            if (route?.TooltipHint != null)
            {
                lines += $"\n{route.TooltipHint}";
            }
        }

        return lines;
    }
}
