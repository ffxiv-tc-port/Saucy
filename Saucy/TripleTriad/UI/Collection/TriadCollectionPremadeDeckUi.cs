using ImGuiNET;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
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

        ImGui.Text("最佳化卡組");
        ImGuiComponents.HelpMarker(
            "使用你擁有的卡片建構一副卡組，並儲存至設定檔卡組槽 5。建議在移動前執行，以便在準備對戰時已經就緒。");

        var status = TriadRun.DescribePremadeDeckOptimizerStatus(npc);
        if (!string.IsNullOrEmpty(status))
        {
            ImGui.TextWrapped(status);
        }

        var canRun = TriadRun.CanRequestPremadeDeckOptimizer(npc, out var blockReason);
        var hasReady = TriadRun.HasPremadeDeckReadyForNpc(npc);
        var isRunning = TriadRun.IsPremadeOptimizerForNpc(npc);

        using var buildDisabled = ImRaii.Disabled(!canRun || isRunning);
        if (ImGui.Button("建構卡組", new(-1, 0)))
        {
            TriadRun.RequestPremadeDeckOptimizer(npc);
        }

        if (!canRun && !string.IsNullOrEmpty(blockReason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(blockReason);
        }

        if (hasReady)
        {
            using var rebuildDisabled = ImRaii.Disabled(isRunning);
            if (ImGui.Button("重新建構卡組", new(-1, 0)))
            {
                TriadRun.RequestPremadeDeckOptimizer(npc, true);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("執行全新建構並覆寫設定檔卡組槽 5 中的卡組。");
            }
        }
    }
}
