using ImGuiNET;
using System;
using System.Numerics;
namespace Saucy.TripleTriad;

internal static class TriadDeckOptimizerStatusUi
{
    public static void DrawInline(string? contextLabel = null)
    {
        if (!TriadRun.ShouldBuildOptimizedDeck() ||
            !TriadDeckOptimizerJobs.TryGetActive(out var job))
        {
            return;
        }

        DrawActiveJob(job, contextLabel);
    }

    private static void DrawActiveJob(TriadDeckOptimizerJobSnapshot job, string? contextLabel)
    {
        ImGui.Spacing();

        var header = string.IsNullOrEmpty(contextLabel)
            ? $"正在為 {job.NpcName} 建構卡組…"
            : $"{contextLabel}：{job.NpcName}";

        SaucyTheme.TextWarning(header);

        var openingLabel = job.FormatBestWinChance();
        if (!string.IsNullOrEmpty(openingLabel))
        {
            ImGui.Text($"開局勝率：{openingLabel}");
            if (job.OpeningEvalInFlight)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("（更新中…）");
            }
        }
        else if (job.OpeningEvalInFlight)
        {
            ImGui.TextDisabled("開局勝率：計算中…");
        }

        var progress = Math.Clamp(job.ProgressPercent, 0, 100) / 100f;
        ImGui.ProgressBar(progress, new Vector2(-1, 0));

        ImGui.TextDisabled($"擁有卡片數：{job.NumOwnedCards:N0}");
        ImGui.TextDisabled($"可能卡組數：{job.NumPossibleDecksDesc}");
        ImGui.TextDisabled($"已測試：{job.NumTestedDecksDesc}");
        ImGui.TextDisabled($"進度：{job.ProgressPercent}%");
        ImGui.TextDisabled($"剩餘時間：{job.FormatTimeLeftDesc()}");

        if (ImGui.Button("取消建構", new(-1, 0)))
        {
            TriadRun.CancelDeckOptimizerJob(userCancelled: true);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("停止目前的背景卡組建構。");
        }
    }
}
