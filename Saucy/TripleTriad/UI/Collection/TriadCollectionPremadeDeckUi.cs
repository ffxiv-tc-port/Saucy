using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ECommons.LanguageHelpers;
namespace Saucy.TripleTriad;

internal static class TriadCollectionPremadeDeckUi
{
    public static void DrawForNpc(TriadNpc npc)
    {
        if (npc == null)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Optimized deck".Loc());
        ImGuiComponents.HelpMarker(
            "Builds a deck from your owned cards and saves it to profile slot 5. Run this before travel so it is ready at match prep.".Loc());

        var status = TriadRun.DescribePremadeDeckOptimizerStatus(npc);
        if (!string.IsNullOrEmpty(status))
        {
            ImGui.TextWrapped(status);
        }

        var canRun = TriadRun.CanRequestPremadeDeckOptimizer(npc, out var blockReason);
        var hasReady = TriadRun.HasPremadeDeckReadyForNpc(npc);
        var isRunning = TriadRun.IsPremadeOptimizerForNpc(npc);

        using var buildDisabled = ImRaii.Disabled(!canRun || isRunning);
        if (ImGui.Button("Build deck".Loc(), new(-1, 0)))
        {
            TriadRun.RequestPremadeDeckOptimizer(npc);
        }

        if (!canRun && !string.IsNullOrEmpty(blockReason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            // 這裡還在 ImRaii.Disabled 區段內，不把 Alpha 推回 1 說明框會跟著變淡。
            using var tooltipAlpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f);
            ImGui.SetTooltip(blockReason);
        }

        if (hasReady)
        {
            using var rebuildDisabled = ImRaii.Disabled(isRunning);
            if (ImGui.Button("Rebuild deck".Loc(), new(-1, 0)))
            {
                TriadRun.RequestPremadeDeckOptimizer(npc, true);
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                using var tooltipAlpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f);
                ImGui.SetTooltip("Runs a fresh build and overwrites the deck in profile slot 5.".Loc());
            }
        }
    }
}
