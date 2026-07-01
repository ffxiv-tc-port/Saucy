using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
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

        DrawStatsCard("Triple Triad", TriadHeadline(life), () => DrawTriadRows(life, sess));
        DrawStatsCard("Cuff-a-Cur", CuffHeadline(life), () => DrawCuffRows(life, sess));
        DrawStatsCard("Out on a Limb", LimbHeadline(life), () => DrawLimbRows(life, sess));
        DrawStatsCard("Air Force One", AirForceHeadline(life), () => DrawAirForceRows(life, sess));
    }

    private static void DrawStatsToolbar()
    {
        ImGui.TextDisabled("按住 Ctrl 以重置統計。");
        ImGui.SameLine();
        const string lifeLbl = "重置累計";
        const string sessLbl = "重置本次";
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
            return "\u5c1a\u7121\u5c0d\u6230\u7d00\u9304";
        }
        var pct = Math.Round(s.GamesWonWithSaucy / (double)s.GamesPlayedWithSaucy * 100, 1);
        return $"{s.GamesPlayedWithSaucy:N0} \u5834 \u00b7 \u52dd\u7387 {pct}%";
    }

    private static string CuffHeadline(Stats s) =>
        s.CuffGamesPlayed == 0 ? "\u5c1a\u7121\u5c0d\u6230\u7d00\u9304" : $"{s.CuffGamesPlayed:N0} \u5834";

    private static string LimbHeadline(Stats s) =>
        s.LimbGamesPlayed == 0 ? "\u5c1a\u7121\u5c0d\u6230\u7d00\u9304" : $"{s.LimbGamesPlayed:N0} \u5834";

    private static string AirForceHeadline(Stats s) =>
        s.AirForceGamesPlayed == 0 ? "\u5c1a\u7121\u5c0d\u6230\u7d00\u9304" : $"{s.AirForceGamesPlayed:N0} \u5834";

    private static void DrawTriadRows(Stats life, Stats sess)
    {
        using var table = ImRaii.Table("##stats_triad", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }
        StatsHeader();
        StatsRow("對戰場數", life.GamesPlayedWithSaucy, sess.GamesPlayedWithSaucy,
            perHour: SessionCountPerHour(sess.GamesPlayedWithSaucy, StatsSessionClock.GetTriadElapsedHours()));
        StatsRow("勝場", life.GamesWonWithSaucy, sess.GamesWonWithSaucy);
        StatsRow("敗場", life.GamesLostWithSaucy, sess.GamesLostWithSaucy);
        StatsRow("平手", life.GamesDrawnWithSaucy, sess.GamesDrawnWithSaucy);
        StatsRow("獲得卡片", life.CardsDroppedWithSaucy, sess.CardsDroppedWithSaucy);
        StatsRow("卡片轉賣價值", $"{GetDroppedCardValues(life):N0}", $"{GetDroppedCardValues(sess):N0}");
        StatsRow("獲得 MGP", $"{life.MGPWon:N0}", $"{sess.MGPWon:N0}", true,
            perHour: SessionMgpPerHour(sess.MGPWon, StatsSessionClock.GetTriadElapsedHours()));

        (var lifeNpcCount, var lifeNpcName) = TopNpcCell(life);
        (var sessNpcCount, var sessNpcName) = TopNpcCell(sess);
        StatsRow("最常對戰 NPC", lifeNpcCount, sessNpcCount, tooltipLife: lifeNpcName, tooltipSess: sessNpcName);

        (var lifeCardCount, var lifeCardName) = TopCardCell(life);
        (var sessCardCount, var sessCardName) = TopCardCell(sess);
        StatsRow("最常獲得卡片", lifeCardCount, sessCardCount, tooltipLife: lifeCardName, tooltipSess: sessCardName);
    }

    private static void DrawCuffRows(Stats life, Stats sess)
    {
        using var table = ImRaii.Table("##stats_cuff", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }
        StatsHeader();
        StatsRow("對戰場數", life.CuffGamesPlayed, sess.CuffGamesPlayed,
            perHour: SessionCountPerHour(sess.CuffGamesPlayed, StatsSessionClock.GetCuffElapsedHours()));
        StatsRow("Bruisings", life.CuffBruisings, sess.CuffBruisings);
        StatsRow("Punishings", life.CuffPunishings, sess.CuffPunishings);
        StatsRow("Brutals", life.CuffBrutals, sess.CuffBrutals);
        StatsRow("獲得 MGP", $"{life.CuffMGP:N0}", $"{sess.CuffMGP:N0}", true,
            perHour: SessionMgpPerHour(sess.CuffMGP, StatsSessionClock.GetCuffElapsedHours()));
    }

    private static void DrawLimbRows(Stats life, Stats sess)
    {
        using var table = ImRaii.Table("##stats_limb", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }
        StatsHeader();
        StatsRow("對戰場數", life.LimbGamesPlayed, sess.LimbGamesPlayed,
            perHour: SessionCountPerHour(sess.LimbGamesPlayed, StatsSessionClock.GetLimbElapsedHours()));
        StatsRow("獲得 MGP", $"{life.LimbMGP:N0}", $"{sess.LimbMGP:N0}", true,
            perHour: SessionMgpPerHour(sess.LimbMGP, StatsSessionClock.GetLimbElapsedHours()));
    }

    private static void DrawAirForceRows(Stats life, Stats sess)
    {
        using var table = ImRaii.Table("##stats_airforce", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }
        StatsHeader();
        StatsRow("對戰場數", life.AirForceGamesPlayed, sess.AirForceGamesPlayed,
            perHour: SessionCountPerHour(sess.AirForceGamesPlayed, StatsSessionClock.GetAirForceElapsedHours()));
        StatsRow("獲得 MGP", $"{life.AirForceMGP:N0}", $"{sess.AirForceMGP:N0}", true,
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
        ImGui.TableSetupColumn("項目", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableSetupColumn("累計", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("本次", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("每小時", ImGuiTableColumnFlags.WidthStretch, 0.20f);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        RightAlignCellText("累計", SaucyTheme.ColorOr(SaucyTheme.ColumnHeader, ImGuiCol.Text));
        ImGui.TableNextColumn();
        RightAlignCellText("本次", SaucyTheme.ColorOr(SaucyTheme.ColumnHeader, ImGuiCol.Text));
        ImGui.TableNextColumn();
        RightAlignCellText("每小時", SaucyTheme.ColorOr(SaucyTheme.ColumnHeader, ImGuiCol.Text));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("自本小遊戲首次計入紀錄以來的本次場次速率。");
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
