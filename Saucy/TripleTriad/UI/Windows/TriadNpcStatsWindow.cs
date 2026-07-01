using ImGuiNET;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using System;
namespace Saucy.TripleTriad;

public class TriadNpcStatsWindow : Window, IDisposable
{
    private readonly StatTracker statTracker;

    private GameNpcInfo? npcInfo;
    private string? npcName;

    public TriadNpcStatsWindow(StatTracker statTracker) : base("NPC 統計")
    {
        this.statTracker = statTracker;

        IsOpen = false;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(350, 0), MaximumSize = new(700, 800)
        };

        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = false;
    }

    public void Dispose()
    {
    }

    public void SetupAndOpen(TriadNpc? triadNpc)
    {
        this.npcInfo = null;

        if (triadNpc == null)
        {
            return;
        }

        if (GameNpcDB.Get().mapNpcs.TryGetValue(triadNpc.Id, out var npcInfo))
        {
            this.npcInfo = npcInfo;
            npcName = triadNpc.Name;

            IsOpen = true;
        }
    }

    public override void Draw()
    {
        var colorName = SaucyTheme.ColorOr(SaucyTheme.SectionTitle, ImGuiCol.Text);
        var colorValue = SaucyTheme.ColorOr(SaucyTheme.BodyTextAccent, ImGuiCol.Text);
        var colorGray = SaucyTheme.TextMutedColor;

        if (npcInfo != null)
        {
            ImGui.TextColored(colorName, npcName);

            var savedStats = statTracker.GetNpcStatsOrDefault(npcInfo);
            var numMatches = savedStats.GetNumMatches();

            ImGui.Text($"已追蹤場次：{numMatches}");
            ImGui.Spacing();

            ImGui.Text("對戰統計：");
            ImGui.Indent();
            ImGui.Text($"{savedStats.NumWins} 勝，");
            ImGui.SameLine();
            ImGui.Text($"{savedStats.NumDraws} 平，");
            ImGui.SameLine();
            ImGui.Text($"{savedStats.NumLosses} 敗");
            if (numMatches > 0)
            {
                var winPctDesc = (1.0f * savedStats.NumWins / numMatches).ToString("P1").Replace("%", "%%");
                ImGui.TextColored(colorValue, $"勝率 {winPctDesc}");
            }
            ImGui.Unindent();
            ImGui.Spacing();

            ImGui.Text("獎勵統計：");
            ImGui.Indent();
            ImGui.Text($"MGP：{savedStats.NumCoins}");

            var cardDB = TriadCardDB.Get();
            var gameCardDB = GameCardDB.Get();
            var sumNetGain = savedStats.NumCoins - (numMatches * npcInfo.matchFee);
            foreach (var kvp in savedStats.Cards)
            {
                if (kvp.Key >= 0 && kvp.Key < cardDB.cards.Count && kvp.Value > 0)
                {
                    var cardOb = cardDB.FindById(kvp.Key);
                    if (cardOb != null && cardOb.IsValid() && gameCardDB.mapCards.TryGetValue(kvp.Key, out var cardInfo))
                    {
                        ImGui.Text($"{cardOb.Name}：{kvp.Value}");
                        sumNetGain += kvp.Value * cardInfo.SaleValue;

                        if (savedStats.NumWins > 0)
                        {
                            var dropPct = 1.0f * kvp.Value / savedStats.NumWins;

                            ImGui.SameLine();
                            ImGui.TextColored(colorValue, dropPct.ToString("P1").Replace("%", "%%"));
                        }
                    }
                }
            }

            ImGui.Unindent();
            ImGui.Spacing();

            ImGui.Text("每場 MGP：");
            ImGui.SameLine();
            if (numMatches > 0)
            {
                ImGui.TextColored(colorValue, $"{(1.0f * sumNetGain / numMatches):0.#}");
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("包含出售卡片獲得的 MGP");
            }
            else
            {
                ImGui.TextColored(colorGray, "--");
            }

            ImGui.NewLine();

            if (ImGui.Button("複製"))
            {
                CopyStatstoClipboard(savedStats);
            }
            ImGui.SameLine();
            if (ImGui.Button("重置"))
            {
                statTracker.RemoveNpcStats(npcInfo);
            }
        }
        else
        {
            ImGui.Text("NPC 統計");
            ImGui.SameLine();
            ImGui.TextColored(colorGray, "--");
        }
    }

    private void CopyStatstoClipboard(TriadNpcStatRecord savedStats)
    {
        var desc = $"{npcName} 統計：\n{savedStats.GetNumMatches()} 場（勝:{savedStats.NumWins}/平:{savedStats.NumDraws}/敗:{savedStats.NumLosses}）";
        if (savedStats.Cards.Count > 0)
        {
            var cardDB = TriadCardDB.Get();
            foreach (var kvp in savedStats.Cards)
            {
                if (kvp.Key >= 0 && kvp.Key < cardDB.cards.Count && kvp.Value > 0)
                {
                    var cardOb = cardDB.FindById(kvp.Key);
                    if (cardOb != null && cardOb.IsValid())
                    {
                        desc += $"\n[{cardOb.Id}]:{cardOb.Name} => {kvp.Value}";
                    }
                }
            }
        }
        else
        {
            desc += "\n無卡片掉落";
        }

        ImGui.SetClipboardText(desc);
    }
}
