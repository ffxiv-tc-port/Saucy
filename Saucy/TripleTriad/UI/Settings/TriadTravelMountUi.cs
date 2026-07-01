using ImGuiNET;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
namespace Saucy.TripleTriad;

internal static class TriadTravelMountUi
{
    public static void Draw()
    {
        ImGui.TextWrapped("在使用 vnavmesh 導航前往 Triple Triad NPC 前召喚的坐騎。");
        ImGui.Dummy(new(0, 4));

        var selectedMountId = C.TriadCollection.TravelMountId;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        using var mountCombo = ImRaii.Combo("##TriadTravelMount", GetPreviewLabel(selectedMountId));
        if (mountCombo)
        {
            if (ImGui.Selectable("坐騎輪盤", selectedMountId == 0))
            {
                C.TriadCollection.TravelMountId = 0;
                C.Save();
            }

            foreach (var mount in GetOwnedMounts())
            {
                if (ImGui.Selectable(mount.Name, selectedMountId == mount.Id))
                {
                    C.TriadCollection.TravelMountId = mount.Id;
                    C.Save();
                }
            }
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "預設使用遊戲內建的坐騎輪盤通用動作。選擇特定坐騎後，地圖導航前會固定召喚該坐騎。");
    }

    private static string GetPreviewLabel(uint mountId)
    {
        if (mountId == 0)
        {
            return "坐騎輪盤";
        }

        var mountSheet = Svc.Data.GetExcelSheet<Mount>();
        var row = mountSheet?.GetRowOrDefault(mountId);
        if (row == null)
        {
            return $"坐騎 #{mountId}（無法使用）";
        }

        var name = row.Value.Singular.ExtractText();
        if (!TravelMountHelper.IsMountUnlocked(mountId))
        {
            return $"{name}（無法使用）";
        }

        return name;
    }

    private static (uint Id, string Name)[] GetOwnedMounts()
    {
        var mountSheet = Svc.Data.GetExcelSheet<Mount>();

        return
        [
            .. mountSheet
                .Where(mount => mount.RowId != 0 && TravelMountHelper.IsMountUnlocked(mount.RowId))
                .Select(mount => (Id: mount.RowId, Name: mount.Singular.ExtractText()))
                .Where(mount => !string.IsNullOrWhiteSpace(mount.Name))
                .OrderBy(mount => mount.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
