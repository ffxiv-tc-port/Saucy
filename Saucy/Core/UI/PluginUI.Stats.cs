using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using ECommons.LanguageHelpers;
using System;
using System.Linq;
using System.Numerics;
namespace Saucy;

public partial class PluginUI
{
    private void DrawStatsTab()
    {
        DrawStatsToolbar();

        (var life, var sess) = (C.Stats, C.SessionStats);

        DrawStatsCard("Triple Triad".Loc(), TriadHeadline(life), () => DrawTriadRows(life, sess));
        DrawStatsCard("Air Force One".Loc(), AirForceHeadline(life), () => DrawAirForceRows(life, sess));
    }

    private static void DrawStatsToolbar()
    {
        ImGui.TextDisabled("Hold Ctrl to reset stats.".Loc());
        ImGui.SameLine();
        var lifeLbl = "Reset Lifetime".Loc();
        var sessLbl = "Reset Session".Loc();
        var pad = ImGui.GetStyle().FramePadding.X * 2f;
        var lifeW = ImGui.CalcTextSize(lifeLbl).X + pad;
        var sessW = ImGui.CalcTextSize(sessLbl).X + pad;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rightX = ImGui.GetWindowContentRegionMax().X - lifeW - sessW - spacing;
        if (rightX > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(rightX);
        }
        using var disabled = ImRaii.Disabled(!ImGui.GetIO().KeyCtrl);
        if (ImGui.Button(lifeLbl))
        {
            C.Stats = new();
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button(sessLbl))
        {
            C.SessionStats = new();
            C.SessionStartTime = DateTime.UtcNow;
            StatsSessionClock.ResetAll();
        }
        ImGui.Dummy(new(0, 2));
    }

    private static string TriadHeadline(Stats s)
    {
        if (s.GamesPlayedWithSaucy == 0)
        {
            return "no games played".Loc();
        }
        var pct = Math.Round(s.GamesWonWithSaucy / (double)s.GamesPlayedWithSaucy * 100, 1);
        return "?? games \u00b7 ??% win".Loc($"{s.GamesPlayedWithSaucy:N0}", pct);
    }

    private static string AirForceHeadline(Stats s) =>
        s.AirForceGamesPlayed == 0 ? "no games played".Loc() : "?? games".Loc($"{s.AirForceGamesPlayed:N0}");

    private static void DrawTriadRows(Stats life, Stats sess)
    {
        using var table = ImRaii.Table("##stats_triad", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }
        StatsHeader();
        StatsRow("Games".Loc(), life.GamesPlayedWithSaucy, sess.GamesPlayedWithSaucy,
            perHour: SessionCountPerHour(sess.GamesPlayedWithSaucy, StatsSessionClock.GetTriadElapsedHours()));
        StatsRow("Wins".Loc(), life.GamesWonWithSaucy, sess.GamesWonWithSaucy);
        StatsRow("Losses".Loc(), life.GamesLostWithSaucy, sess.GamesLostWithSaucy);
        StatsRow("Draws".Loc(), life.GamesDrawnWithSaucy, sess.GamesDrawnWithSaucy);
        StatsRow("Cards won".Loc(), life.CardsDroppedWithSaucy, sess.CardsDroppedWithSaucy);
        StatsRow("Card resale value".Loc(), $"{GetDroppedCardValues(life):N0}", $"{GetDroppedCardValues(sess):N0}");
        StatsRow("MGP won".Loc(), $"{life.MGPWon:N0}", $"{sess.MGPWon:N0}", true,
            perHour: SessionMgpPerHour(sess.MGPWon, StatsSessionClock.GetTriadElapsedHours()));

        (var lifeNpcCount, var lifeNpcName) = TopNpcCell(life);
        (var sessNpcCount, var sessNpcName) = TopNpcCell(sess);
        StatsRow("Most played NPC".Loc(), lifeNpcCount, sessNpcCount, tooltipLife: lifeNpcName, tooltipSess: sessNpcName);

        (var lifeCardCount, var lifeCardName) = TopCardCell(life);
        (var sessCardCount, var sessCardName) = TopCardCell(sess);
        StatsRow("Most won card".Loc(), lifeCardCount, sessCardCount, tooltipLife: lifeCardName, tooltipSess: sessCardName);
    }

    private static void DrawAirForceRows(Stats life, Stats sess)
    {
        using var table = ImRaii.Table("##stats_airforce", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }
        StatsHeader();
        StatsRow("Games".Loc(), life.AirForceGamesPlayed, sess.AirForceGamesPlayed,
            perHour: SessionCountPerHour(sess.AirForceGamesPlayed, StatsSessionClock.GetAirForceElapsedHours()));
        StatsRow("MGP won".Loc(), $"{life.AirForceMGP:N0}", $"{sess.AirForceMGP:N0}", true,
            perHour: SessionMgpPerHour(sess.AirForceMGP, StatsSessionClock.GetAirForceElapsedHours()));
    }

    private static (string count, string? name) TopNpcCell(Stats s)
    {
        if (s.NPCsPlayed.Count == 0)
        {
            return ("\u2014", null);
        }
        var top = s.NPCsPlayed.OrderByDescending(x => x.Value).First();
        return ($"{top.Value:N0}", top.Key);
    }

    private static (string count, string? name) TopCardCell(Stats s)
    {
        if (s.CardsWon.Count == 0)
        {
            return ("\u2014", null);
        }
        var top = s.CardsWon.OrderByDescending(x => x.Value).First();
        return ($"{top.Value:N0}", TriadCardDB.Get().FindById((int)top.Key)?.Name);
    }

    private static void StatsHeader()
    {
        ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableSetupColumn("Lifetime", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Session", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("PerHour", ImGuiTableColumnFlags.WidthStretch, 0.20f);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        RightAlignCellText("Lifetime".Loc(), SaucyTheme.ColorOr(SaucyTheme.ColumnHeader, ImGuiCol.Text));
        ImGui.TableNextColumn();
        RightAlignCellText("Session".Loc(), SaucyTheme.ColorOr(SaucyTheme.ColumnHeader, ImGuiCol.Text));
        ImGui.TableNextColumn();
        RightAlignCellText("Per hour".Loc(), SaucyTheme.ColorOr(SaucyTheme.ColumnHeader, ImGuiCol.Text));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Session rate since this minigame first counted a result.".Loc());
        }
    }

    private static void StatsRow(string label, int life, int sess, bool accent = false, string? perHour = null) =>
        StatsRow(label, life.ToString("N0"), sess.ToString("N0"), accent, perHour: perHour);

    private static void StatsRow(string label, string life, string sess, bool accent = false,
        string? tooltipLife = null, string? tooltipSess = null, string? perHour = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);

        var col = accent
            ? SaucyTheme.ColorOr(SaucyTheme.BodyTextAccent, ImGuiCol.Text)
            : SaucyTheme.ColorOr(SaucyTheme.BodyText, ImGuiCol.Text);

        ImGui.TableNextColumn();
        RightAlignCellText(life, col);
        if (tooltipLife != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltipLife);
        }

        ImGui.TableNextColumn();
        RightAlignCellText(sess, col);
        if (tooltipSess != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltipSess);
        }

        ImGui.TableNextColumn();
        if (!string.IsNullOrEmpty(perHour))
        {
            RightAlignCellText(perHour, col);
        }
    }

    private static void RightAlignCellText(string text, Vector4 color)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var pad = ImGui.GetStyle().CellPadding;
        var avail = ImGui.GetContentRegionAvail();
        var tw = ImGui.CalcTextSize(text).X;
        var offset = Math.Max(0f, avail.X - tw - pad.X);
        if (offset > 0f)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        }

        ImGui.TextColored(color, text);
    }

    private static void DrawStatsCard(string name, string subtitle, Action body) =>
        SaucyTheme.DrawCard(name, subtitle, body);

    private static string SessionMgpPerHour(int sessionMgp, double elapsedHours)
    {
        if (sessionMgp <= 0)
        {
            return "-";
        }

        return $"{(int)Math.Round(sessionMgp / elapsedHours):N0}";
    }

    private static string SessionCountPerHour(int sessionCount, double elapsedHours)
    {
        if (sessionCount <= 0)
        {
            return "-";
        }

        return $"{(int)Math.Round(sessionCount / elapsedHours):N0}";
    }

    private static int GetDroppedCardValues(Stats stat)
    {
        var output = 0;
        foreach (var card in stat.CardsWon)
        {
            var info = GameCardDB.Get().FindById((int)card.Key);
            if (info != null)
            {
                output += info.SaleValue * stat.CardsWon[card.Key];
            }
        }
        return output;
    }
}
