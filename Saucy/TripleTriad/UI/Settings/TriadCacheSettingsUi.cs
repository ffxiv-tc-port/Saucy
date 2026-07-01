using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Linq;
namespace Saucy.TripleTriad;

internal static class TriadCacheSettingsUi
{
    public static void Draw()
    {
        ImGui.TextDisabled("各角色的最佳化卡組");

        ImGui.Dummy(new(0, 4));

        var views = TriadOptimizedDeckCacheStore.GetCharacterCacheViews();
        if (views.Count == 0)
        {
            if (Svc.ClientState.IsLoggedIn)
            {
                ImGui.TextDisabled("目前尚無快取卡組。");
            }
            else
            {
                ImGui.TextDisabled("請登入以查看快取卡組。");
            }
        }
        else
        {
            var listHeight = Math.Clamp(views.Count * 28 + views.Sum(v => v.Entries.Count * 18), 120f, 320f);
            using var scroll = ImRaii.Child("TriadCacheList", new(0, listHeight), true);
            if (scroll)
            {
                foreach (var character in views)
                {
                    DrawCharacterCache(character);
                    ImGui.Dummy(new(0, 4));
                }
            }
        }

        ImGui.Dummy(new(0, 4));
        DrawClearButton();
    }

    private static void DrawCharacterCache(TriadOptimizedDeckCacheCharacterView character)
    {
        var deckCount = character.Entries.Count;
        var deckCountLabel = deckCount switch
        {
            0 => "無快取卡組",
            1 => "1 副快取卡組",
            var _ => $"{deckCount} 副快取卡組"
        };

        var header = $"{character.DisplayName} — {deckCountLabel}";
        var flags = character.IsCurrentCharacter ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (ImGui.CollapsingHeader(header, flags))
        {
            DrawCharacterEntries(character);
        }
    }

    private static void DrawCharacterEntries(TriadOptimizedDeckCacheCharacterView character)
    {
        if (character.Entries.Count == 0)
        {
            ImGui.TextDisabled("此角色尚未儲存任何最佳化卡組。");
            return;
        }

        using var indent = ImRaii.PushIndent();
        foreach (var entry in character.Entries)
        {
            ImGui.BulletText(FormatCacheEntryLine(entry));
        }
    }

    private static string FormatCacheEntryLine(TriadOptimizedDeckCacheEntry entry)
    {
        var npcLabel = string.IsNullOrWhiteSpace(entry.NpcName) ? $"NPC {entry.NpcId}" : entry.NpcName;
        var rulesLabel = FormatRulesLabel(entry.SessionKey);
        var builtLabel = entry.BuiltUtcTicks > 0
            ? new DateTime(entry.BuiltUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g")
            : "未知時間";
        var winLabel = entry.EstWinChance > 0f ? $" · 開局勝率 {entry.EstWinChance * 100f:F0}%" : string.Empty;

        return string.IsNullOrEmpty(rulesLabel)
            ? $"{npcLabel}{winLabel} · {builtLabel}"
            : $"{npcLabel} ({rulesLabel}){winLabel} · {builtLabel}";
    }

    private static string FormatRulesLabel(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
        {
            return string.Empty;
        }

        var parts = sessionKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return string.Empty;
        }

        return string.Join(", ", parts.Skip(1));
    }

    private static void DrawClearButton()
    {
        var ctrlHeld = ImGui.GetIO().KeyCtrl;
        using var clearDisabled = ImRaii.Disabled(!ctrlHeld);
        if (ImGui.Button("清除此角色的卡組快取"))
        {
            TriadOptimizedDeckCacheStore.ClearActiveCharacter();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                ctrlHeld
                    ? "刪除目前登入角色的 OptimizedDeckCache.json。"
                    : "按住 Ctrl 並點擊以清除此角色的快取。");
        }
    }
}
