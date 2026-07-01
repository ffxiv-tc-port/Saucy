using ImGuiNET;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Components;
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
            return $"{showOnMapTooltip}\n決鬥擂台是任務搜索器副本。\nSaucy 無法自動前往。";
        }

        var unlockLine = TriadNpcUnlockHelper.TryGetTooltipLine(npc);
        if (unlockLine != null)
        {
            return $"{showOnMapTooltip}\n{unlockLine}";
        }

        if (!Vnavmesh.IsInstalled)
        {
            return $"{showOnMapTooltip}\n請安裝 vnavmesh 以自動前往此 NPC。";
        }

        var lines = $"{showOnMapTooltip}\n左鍵：自動前往並farm缺少的卡片。";
        if (npc != null)
        {
            lines += "\n右鍵：自動前往並farm MGP。";
            lines += "\n抵達後將啟用九宮飛牌自動化。";
            lines += "\n若已擁有此 NPC 的所有卡片，左鍵將改為farm MGP。";
            lines += "\n即使該選項關閉，左鍵在缺卡時仍會建立最佳化牌組。";
        }
        else
        {
            lines = $"{showOnMapTooltip}\n點擊以使用 vnavmesh 自動前往。";
        }

        if (Lifestream.IsInstalled)
        {
            lines += "\n使用 Lifestream 進行移動（傳送水晶或以太之光）。";
            var route = MultiAreaRouteRegistry.FindRoute(location);
            if (route?.TooltipHint != null)
            {
                lines += $"\n{route.TooltipHint}";
            }
        }

        return lines;
    }
}
