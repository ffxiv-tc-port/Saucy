using ImGuiNET;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
using PunishLib.ImGuiMethods;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
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
        "暴風倖存者",
        "空軍裝甲駕駛員",
        "登高跳跳樂大挑戰",
        "搶救小鳥大作戰",
        "必中一閃快刀斬魔",
        "活動解說員排程",
        "九宮幻卡",
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

    public void OpenForGate(Module.GateType gate)
    {
        var nav = gate switch
        {
            Module.GateType.AnyWayTheWindBlows => NavItem.AnyWayTheWindBlows,
            Module.GateType.AirForceOne => NavItem.AirForceOne,
            Module.GateType.LeapOfFaith => NavItem.LeapOfFaith,
            Module.GateType.Cliffhanger => NavItem.Cliffhanger,
            Module.GateType.SliceIsRight => NavItem.SliceIsRight,
            _ => (NavItem?)null
        };

        if (nav == null)
        {
            return;
        }

        _selectedNav = nav.Value;
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
        NavSelectable("暴風倖存者", NavItem.AnyWayTheWindBlows);
        NavSelectable("空軍裝甲駕駛員", NavItem.AirForceOne);
        NavSelectable("登高跳跳樂大挑戰", NavItem.LeapOfFaith);
        NavSelectable("搶救小鳥大作戰", NavItem.Cliffhanger);
        NavSelectable("必中一閃快刀斬魔", NavItem.SliceIsRight);
        NavSelectable("活動解說員排程", NavItem.GateSchedule);

        ImGui.Dummy(new(0, 6));
        DrawSidebarHeader("OTHER GAMES");
        NavSelectable("九宮幻卡", NavItem.TripleTriad);

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
            case NavItem.LeapOfFaith: DrawLeapOfFaithPanel(); break;
            case NavItem.Cliffhanger: DrawCliffhangerPanel(); break;
            case NavItem.SliceIsRight: DrawSliceIsRightPanel(); break;
            case NavItem.GateSchedule: DrawGateSchedulePanel(); break;
            case NavItem.Stats: DrawStatsTab(); break;
            case NavItem.About: AboutTab.Draw("Saucy"); break;
            case NavItem.Debug: DrawDebugTab(); break;
        }
    }

    private static void DrawTriadPanel()
    {
        DrawPanelHeader("九宮幻卡");
        ImGuiEx.EzTabBar("###Triad",
            ("主要", TriadSettingsUi.Draw, null, false),
            ("快取", TriadCacheSettingsUi.Draw, null, false));
    }

    private static void DrawPanelHeader(string title, string? subtitle = null) =>
        SaucyTheme.DrawPanelHeader(title, subtitle);

    private void DrawDebugTab()
    {
        ImGuiLayout.DrawCollapsingSection("金碟遊樂園 GATE", ImGuiTreeNodeFlags.DefaultOpen, () =>
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

            ImGui.Dummy(new(0, 4));
            ImGui.TextWrapped("目前作用中的 ConditionFlag（若 Leap of Faith 沒有 GATE 導演，" +
                               "這裡應該有其他旗標在跳台期間是 True）：");
            using var scroll = ImRaii.Child("##ActiveConditionFlags", new(0, 120), true);
            if (scroll)
            {
                foreach (Dalamud.Game.ClientState.Conditions.ConditionFlag flag in
                         Enum.GetValues<Dalamud.Game.ClientState.Conditions.ConditionFlag>())
                {
                    if (Svc.Condition[flag])
                    {
                        ImGui.TextUnformatted($"{(int)flag}: {flag}");
                    }
                }
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

        ImGuiLayout.DrawCollapsingSection("Triple Triad 棋盤記憶體", ImGuiTreeNodeFlags.DefaultOpen, DrawTriadBoardMemoryDebug);

        ImGuiLayout.DrawCollapsingSection("Leap of Faith 路徑記錄", ImGuiTreeNodeFlags.DefaultOpen, DrawLeapOfFaithRecorder);

        ImGuiLayout.DrawCollapsingSection("搶救小鳥大作戰 路徑記錄", ImGuiTreeNodeFlags.DefaultOpen, DrawCliffhangerRecorder);
    }

    /// <summary>
    /// Event Coordinator NPCs ("活動解說員") exist at multiple locations around the Gold Saucer,
    /// so unlike each GATE's single registration-NPC spot, this is a free-form list the user adds
    /// to and deletes from directly — never a guessed/hardcoded position.
    /// </summary>
    private static void DrawGateSchedulePanel()
    {
        DrawPanelHeader("活動解說員排程", "GATE 排程自動化");
        ImGui.TextWrapped("每小時 :10/:30/:50 自動導航至最近的已記錄「活動解說員」；" +
                           "每小時 :00/:20/:40 若在已記錄的支援 GATE NPC 附近，自動互動並嘗試參加。");

        var autoOpen = C.GoldSaucerGates.AutoOpenUiOnGateJoin;
        if (ImGui.Checkbox("加入 GATE 時自動開啟並切換到對應頁面##AutoOpenUiOnGateJoin", ref autoOpen))
        {
            C.GoldSaucerGates.AutoOpenUiOnGateJoin = autoOpen;
            C.Save();
        }

        ImGui.Dummy(new(0, 4));
        var coordinatorAuto = C.GoldSaucerGates.EventCoordinatorAutoNavigate;
        if (ImGui.Checkbox("自動導航至活動解說員（:10/:30/:50）##EventCoordinatorAutoNavigate", ref coordinatorAuto))
        {
            C.GoldSaucerGates.EventCoordinatorAutoNavigate = coordinatorAuto;
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("立即移動至最近的活動解說員##MoveNowNearestCoordinator"))
        {
            var playerPos = Player.Available ? Player.Position : default;
            var nearest = C.GoldSaucerGates.EventCoordinatorSpots
                .Where(s => s.Recorded)
                .OrderBy(s => Vector3.Distance(playerPos, new Vector3(s.X, s.Y, s.Z)))
                .FirstOrDefault();
            if (nearest != null)
            {
                GateScheduleAutomation.TriggerManualCoordinatorMove(nearest);
            }
        }

        var autoJoin = C.GoldSaucerGates.AutoJoinNearSupportedNpc;
        if (ImGui.Checkbox("自動參加支援的 GATE（:00/:20/:40）##AutoJoinNearSupportedNpc", ref autoJoin))
        {
            C.GoldSaucerGates.AutoJoinNearSupportedNpc = autoJoin;
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("僅在玩家靠近已記錄的空軍裝甲駕駛員/暴風倖存者/必中一閃快刀斬魔 NPC 時才會嘗試互動並確認參加。");
        }

        ImGui.Dummy(new(0, 4));
        ImGui.TextWrapped("已記錄的活動解說員位置：");

        if (ImGui.Button("鎖定 NPC 後按此新增##AddEventCoordinatorSpot"))
        {
            if (GateNpcNavigation.TryRecordNewListEntry(C.GoldSaucerGates.EventCoordinatorSpots, out var message))
            {
                Svc.Chat.Print($"[Saucy] {message}");
            }
            else
            {
                Svc.Chat.PrintError($"[Saucy] {message}");
            }
        }

        var spots = C.GoldSaucerGates.EventCoordinatorSpots;
        if (spots.Count == 0)
        {
            SaucyTheme.TextMuted("尚未記錄任何活動解說員位置。");
        }

        for (var i = spots.Count - 1; i >= 0; i--)
        {
            var spot = spots[i];
            ImGui.TextUnformatted($"{spot.NpcName}（{spot.X:F1}, {spot.Y:F1}, {spot.Z:F1}）");
            ImGui.SameLine();
            if (ImGui.SmallButton($"立即移動##EventCoordinatorSpotMove{i}"))
            {
                GateScheduleAutomation.TriggerManualCoordinatorMove(spot);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"刪除##EventCoordinatorSpot{i}"))
            {
                spots.RemoveAt(i);
                C.Save();
            }
        }

        // Each supported GATE's registration-NPC recording/auto-navigate controls, consolidated
        // here instead of scattered across each GATE's own panel (per user request).
        ImGui.Dummy(new(0, 8));
        ImGui.Separator();
        ImGui.TextWrapped("支援 GATE 的報名 NPC：");
        DrawGateNpcNavigationControls("空軍裝甲駕駛員", "AirForceNpc", C.GoldSaucerGates.AirForceNpcSpot,
            () => C.GoldSaucerGates.AirForceNpcAutoNavigate, v => C.GoldSaucerGates.AirForceNpcAutoNavigate = v);
        DrawGateNpcNavigationControls("暴風倖存者", "WindBlowsNpc", C.GoldSaucerGates.WindBlowsNpcSpot,
            () => C.GoldSaucerGates.WindBlowsNpcAutoNavigate, v => C.GoldSaucerGates.WindBlowsNpcAutoNavigate = v);
        DrawGateNpcNavigationControls("必中一閃快刀斬魔", "SliceIsRightNpc", C.GoldSaucerGates.SliceIsRightNpcSpot,
            () => C.GoldSaucerGates.SliceIsRightNpcAutoNavigate, v => C.GoldSaucerGates.SliceIsRightNpcAutoNavigate = v);
    }

    /// <summary>
    /// Raw addresses for the TripleTriad addon and the offsets AddonTripleTriad currently assumes
    /// for TurnState/BlueDeck/RedDeck/Board — these were reconstructed without any old-client
    /// reference and may not match this TW build's actual memory layout. Exposed here so they can
    /// be cross-checked with external memory scanning against a known on-screen board state.
    /// </summary>
    private static unsafe void DrawTriadBoardMemoryDebug()
    {
        if (!TriadLocalClientStructs.TryGetBoard(out var addon, requireVisible: false))
        {
            ImGui.TextDisabled("找不到 TripleTriad addon。");
            return;
        }

        var baseAddr = (nint)addon;
        ImGui.Text($"Addon 基底位址：0x{baseAddr:X}");
        ImGui.Text($"IsVisible：{addon->AtkUnitBase.IsVisible}");
        ImGui.Text($"TurnState (+0x238)：{addon->TurnState}");
        ImGui.Text($"BlueDeck 起始位址 (+0x240)：0x{baseAddr + 0x240:X}");
        ImGui.Text($"RedDeck 起始位址 (+0x588)：0x{baseAddr + 0x588:X}");
        ImGui.Text($"Board 起始位址 (+0x8d0)：0x{baseAddr + 0x8d0:X}");

        ImGui.Dummy(new(0, 4));
        ImGui.TextWrapped("目前讀到的 9 格棋盤（若下方全部顯示「空」，代表位移量對不上，需要用記憶體掃描重新比對）：");
        ImGui.Text($"sizeof(TripleTriadCard) = 0x{sizeof(AddonTripleTriad.TripleTriadCard):X}");
        for (var i = 0; i < 9; i++)
        {
            var card = addon->Board[i];
            var cardAddr = (nint)(&addon->Board[i]);
            ImGui.Text(card.HasCard
                ? $"  格 {i} @0x{cardAddr:X}: owner={card.CardOwner} U{card.NumSideU} L{card.NumSideL} D{card.NumSideD} R{card.NumSideR} rarity={card.CardRarity} type={card.CardType}"
                : $"  格 {i} @0x{cardAddr:X}: 空");
        }
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
        var objCount = LeapOfFaith.LeapOfFaithRecorder.Objects.Count;
        var attempts = LeapOfFaith.LeapOfFaithRecorder.Points.Count > 0
            ? LeapOfFaith.LeapOfFaithRecorder.Points[^1].AttemptIndex + 1
            : 0;
        ImGui.Text($"已記錄玩家座標點數：{count}　附近物件取樣數：{objCount}　偵測到的嘗試次數：{attempts}");

        using (ImRaii.Disabled(count == 0))
        {
            if (ImGui.Button("匯出路線 JSON"))
            {
                var path = LeapOfFaith.LeapOfFaithRecorder.Export();
                Svc.Chat.Print($"[Saucy] 已匯出 Leap of Faith 記錄至：\n{path}");
            }
        }
    }

    private static void DrawCliffhangerRecorder()
    {
        ImGui.TextWrapped("手動玩一次 搶救小鳥大作戰 期間點選「開始記錄」，完成後點「停止」再「匯出」，" +
                           "由於這關路線固定，錄好一次後可作為之後自動化的參考路線。");

        var recording = Cliffhanger.CliffhangerRecorder.IsRecording;
        using (ImRaii.Disabled(recording))
        {
            if (ImGui.Button("開始記錄##Cliffhanger"))
            {
                Cliffhanger.CliffhangerRecorder.StartRecording();
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!recording))
        {
            if (ImGui.Button("停止##Cliffhanger"))
            {
                Cliffhanger.CliffhangerRecorder.StopRecording();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("清除##Cliffhanger"))
        {
            Cliffhanger.CliffhangerRecorder.Clear();
        }

        var count = Cliffhanger.CliffhangerRecorder.Points.Count;
        var objCount = Cliffhanger.CliffhangerRecorder.Objects.Count;
        var attempts = Cliffhanger.CliffhangerRecorder.Points.Count > 0
            ? Cliffhanger.CliffhangerRecorder.Points[^1].AttemptIndex + 1
            : 0;
        ImGui.Text($"已記錄玩家座標點數：{count}　附近物件取樣數：{objCount}　偵測到的嘗試次數：{attempts}");

        using (ImRaii.Disabled(count == 0))
        {
            if (ImGui.Button("匯出路線 JSON##Cliffhanger"))
            {
                var path = Cliffhanger.CliffhangerRecorder.Export();
                Svc.Chat.Print($"[Saucy] 已匯出 搶救小鳥大作戰 記錄至：\n{path}");
            }
        }
    }

    private enum NavItem
    {
        TripleTriad,
        AnyWayTheWindBlows,
        AirForceOne,
        LeapOfFaith,
        Cliffhanger,
        SliceIsRight,
        GateSchedule,
        Stats,
        About,
        Debug
    }
}
