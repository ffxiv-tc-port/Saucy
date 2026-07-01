using ImGuiNET;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
using PunishLib.ImGuiMethods;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using static ECommons.GenericHelpers;
namespace Saucy;

public unsafe partial class PluginUI : Window
{
    private const long DeltaVisibleMs = 30_000;

    private const uint MgpItemId = 29;
    private const string KagekazuKofiUrl = "https://ko-fi.com/kagekazu";

    private static readonly string[] SidebarLabels =
    [
        "Wind Blows",
        "Triple Triad",
        "統計",
        "關於",
        "除錯",
        "Saucy 主題",
        "GATES",
        "OTHER GAMES"
    ];

    private static int _lastMgp = -1;
    private static long _lastMgpIncreaseMs;
    private NavItem _selectedNav = NavItem.TripleTriad;
    private SaucyTheme.ThemeScope? _themeScope;

    public PluginUI() : base("Saucy###Saucy")
    {
        Size = new Vector2(310, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(280, 240), MaximumSize = new(float.MaxValue, float.MaxValue)
        };

        TitleBarButtons.Add(new()
        {
            ShowTooltip = () => ImGui.SetTooltip("♥ Ko-fi（支持我的轉蛋成癮症）"),
            Icon = FontAwesomeIcon.Heart,
            IconOffset = new(1, 1),
            Click = _ => ShellStart(KagekazuKofiUrl)
        });
    }

    public bool Enabled { get; set; } = false;

    public void OpenForTriad()
    {
        _selectedNav = NavItem.TripleTriad;
        IsOpen = true;
    }

    public void OpenForDebug()
    {
        _selectedNav = NavItem.Debug;
        IsOpen = true;
    }

    private static float CalcSidebarWidth()
    {
        var style = ImGui.GetStyle();
        var maxLabel = 0f;
        foreach (var s in SidebarLabels)
        {
            var w = ImGui.CalcTextSize(s).X;
            if (w > maxLabel)
            {
                maxLabel = w;
            }
        }
        var checkboxExtra = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X;
        return maxLabel + checkboxExtra + style.WindowPadding.X * 2f + style.FramePadding.X * 2f;
    }

    public override void PreDraw()
    {
        _themeScope?.Dispose();
        _themeScope = SaucyTheme.PushScope();

        var info = BuildBannerInfo();

        if (_lastMgp >= 0 && info.Mgp > _lastMgp)
        {
            _lastMgpIncreaseMs = Environment.TickCount64;
        }
        _lastMgp = info.Mgp;

        var showDelta = info.SessionDelta > 0
                        && Environment.TickCount64 - _lastMgpIncreaseMs < DeltaVisibleMs;
        var delta = showDelta ? $"  +{info.SessionDelta:N0}" : "";
        var status = info.ModuleStatus == "閒置" ? "閒置" : $"已啟用：{info.ModuleStatus}";
        WindowName = $"Saucy  \u2022  {status}  \u2022  MGP {info.Mgp:N0}{delta}###Saucy";
    }

    public override void PostDraw()
    {
        _themeScope?.Dispose();
        _themeScope = null;
    }

    public override void Draw()
    {
        var sidebarW = CalcSidebarWidth();
        var availY = ImGui.GetContentRegionAvail().Y;

        using (var sidebar = ImRaii.Child("##Sidebar", new(sidebarW, availY), true))
        {
            if (sidebar)
            {
                DrawSidebar();
            }
        }

        ImGui.SameLine();

        using (var panel = ImRaii.Child("##Panel", new(0, availY), false))
        {
            if (panel)
            {
                DrawPanel();
            }
        }

        TitleBarVersion.DrawFromContext(
            TitleBarButtons.Count,
            AllowPinning || AllowClickthrough);
    }

    private void DrawSidebar()
    {
        DrawSidebarHeader("GATES");
        NavSelectable("Wind Blows", NavItem.AnyWayTheWindBlows);
        NavSelectable("Air Force One", NavItem.AirForceOne);

        ImGui.Dummy(new(0, 6));
        DrawSidebarHeader("OTHER GAMES");
        NavSelectable("Triple Triad", NavItem.TripleTriad);

        ImGui.Dummy(new(0, 6));
        ImGui.Separator();
        NavSelectable("統計", NavItem.Stats);
        NavSelectable("關於", NavItem.About);
        NavSelectable("除錯", NavItem.Debug);

        var style = ImGui.GetStyle();
        var checkboxH = ImGui.GetFrameHeight();
        var creditH = ImGui.GetTextLineHeight();
        var bottomBlockH = style.ItemSpacing.Y + 1f + style.ItemSpacing.Y + checkboxH + style.ItemSpacing.Y + creditH;
        var targetY = ImGui.GetWindowHeight() - style.WindowPadding.Y - bottomBlockH;
        if (targetY > ImGui.GetCursorPosY())
        {
            ImGui.SetCursorPosY(targetY);
        }

        ImGui.Separator();
        var on = C.SaucyThemeEnabled;
        if (ImGui.Checkbox("Saucy 主題", ref on))
        {
            C.SaucyThemeEnabled = on;
            C.Save();
        }
        ImGui.TextDisabled("設計者：Wah");
    }

    private void NavSelectable(string label, NavItem item)
    {
        if (ImGui.Selectable(label, _selectedNav == item))
        {
            _selectedNav = item;
        }
    }

    private static void DrawSidebarHeader(string label) => ImGui.TextColored(SaucyTheme.ColorOr(SaucyTheme.SectionTitle, ImGuiCol.TextDisabled), label);

    private void DrawPanel()
    {
        switch (_selectedNav)
        {
            case NavItem.TripleTriad: DrawTriadPanel(); break;
            case NavItem.AnyWayTheWindBlows: DrawWindBlowsPanel(); break;
            case NavItem.AirForceOne: DrawAirForcePanel(); break;
            case NavItem.Stats: DrawStatsTab(); break;
            case NavItem.About: AboutTab.Draw("Saucy"); break;
            case NavItem.Debug: DrawDebugTab(); break;
        }
    }

    private static void DrawTriadPanel()
    {
        DrawPanelHeader("Triple Triad");
        ImGuiEx.EzTabBar("###Triad",
            ("主要", TriadSettingsUi.Draw, null, false),
            ("快取", TriadCacheSettingsUi.Draw, null, false));
    }

    private static void DrawPanelHeader(string title, string? subtitle = null) =>
        SaucyTheme.DrawPanelHeader(title, subtitle);

    private void DrawDebugTab()
    {
        ImGuiLayout.DrawCollapsingSection("黃金水都 GATE", ImGuiTreeNodeFlags.DefaultOpen, () =>
        {
            if (GoldSaucerManager.Instance() != null && GoldSaucerManager.Instance()->CurrentGFateDirector != null)
            {
                var dir = GoldSaucerManager.Instance()->CurrentGFateDirector;
                ImGui.Text($"GateType: {dir->GateType}");
                ImGui.Text($"GatePositionType: {dir->GatePositionType}");
                ImGui.Text($"Flags: {dir->Flags}");
            }
            else
            {
                ImGui.TextDisabled("目前沒有作用中的 GATE 導演。");
            }
        });

        ImGuiLayout.DrawCollapsingSection("Triple Triad NPC 選單", ImGuiTreeNodeFlags.DefaultOpen, () =>
        {
            ImGui.Text($"Navigation active: {TriadMapNavigation.IsNavigationActive}");
            ImGui.Text($"Awaiting triad start: {TriadMapNavigation.IsAwaitingTriadStartDialog()}");

            var menuLines = new List<string>();
            SelectStringHelper.CollectTriadMenuDebugLines(menuLines);
            if (menuLines.Count == 0)
            {
                ImGui.TextDisabled("目前沒有開啟選項選單。");
            }
            else
            {
                var listHeight = Math.Clamp(menuLines.Count * ImGui.GetTextLineHeightWithSpacing() + 8f, 60f, 200f);
                using var scroll = ImRaii.Child("##TriadMenuDebug", new(0, listHeight), true);
                if (scroll)
                {
                    foreach (var line in menuLines)
                    {
                        ImGui.TextUnformatted(line);
                    }
                }
            }
        });

        ImGuiLayout.DrawCollapsingSection("Leap of Faith 路徑記錄", ImGuiTreeNodeFlags.DefaultOpen, DrawLeapOfFaithRecorder);
    }

    private static void DrawLeapOfFaithRecorder()
    {
        ImGui.TextWrapped("手動玩一次 Leap of Faith 期間點選「開始記錄」，完成後點「停止」再「匯出」，" +
                           "會在外掛設定資料夾產生一份路線 JSON 檔案。");

        var recording = LeapOfFaith.LeapOfFaithRecorder.IsRecording;
        using (ImRaii.Disabled(recording))
        {
            if (ImGui.Button("開始記錄"))
            {
                LeapOfFaith.LeapOfFaithRecorder.StartRecording();
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!recording))
        {
            if (ImGui.Button("停止"))
            {
                LeapOfFaith.LeapOfFaithRecorder.StopRecording();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("清除"))
        {
            LeapOfFaith.LeapOfFaithRecorder.Clear();
        }

        var count = LeapOfFaith.LeapOfFaithRecorder.Points.Count;
        ImGui.Text($"已記錄點數：{count}");

        using (ImRaii.Disabled(count == 0))
        {
            if (ImGui.Button("匯出路線 JSON"))
            {
                var path = LeapOfFaith.LeapOfFaithRecorder.Export();
                Svc.Chat.Print($"[Saucy] 已匯出 Leap of Faith 路線至 {path}");
            }
        }
    }

    private enum NavItem
    {
        TripleTriad,
        AnyWayTheWindBlows,
        AirForceOne,
        Stats,
        About,
        Debug
    }
}
