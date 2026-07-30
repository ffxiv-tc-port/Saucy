using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
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
        "Wind Blows",
        "Air Force One",
        "Leap of Faith",
        "Cliffhanger",
        "Slice is Right",
        "GATE schedule",
        "Navigation",
        "Triple Triad",
        "Mini Cactpot",
        "Stats",
        "About",
        "Debug",
        "Saucy theme",
        "GATES",
        "OTHER GAMES"
    ];

    private static int _lastMgp = -1;
    private static bool _attemptedCliffhangerRouteAutoLoad;
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
            ShowTooltip = () => ImGui.SetTooltip("♥ Ko-fi (to support my gacha addiction)".Loc()),
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
            var w = ImGui.CalcTextSize(s.Loc()).X;
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
        var status = info.ModuleStatus == "Idle" ? "Idle".Loc() : "Enabled: ??".Loc(info.ModuleStatus.Loc());
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
        DrawSidebarHeader("GATES".Loc());
        NavSelectable("Wind Blows".Loc(), NavItem.AnyWayTheWindBlows);
        NavSelectable("Air Force One".Loc(), NavItem.AirForceOne);
        NavSelectable("Leap of Faith".Loc(), NavItem.LeapOfFaith);
        NavSelectable("Cliffhanger".Loc(), NavItem.Cliffhanger);
        NavSelectable("Slice is Right".Loc(), NavItem.SliceIsRight);
        NavSelectable("GATE schedule".Loc(), NavItem.GateSchedule);

        ImGui.Dummy(new(0, 6));
        DrawSidebarHeader("OTHER GAMES".Loc());
        NavSelectable("Triple Triad".Loc(), NavItem.TripleTriad);
        NavSelectable("Mini Cactpot".Loc(), NavItem.MiniCactpot);
        NavSelectable("Navigation".Loc(), NavItem.Navigation);

        ImGui.Dummy(new(0, 6));
        ImGui.Separator();
        NavSelectable("Stats".Loc(), NavItem.Stats);
        NavSelectable("About".Loc(), NavItem.About);
        NavSelectable("Debug".Loc(), NavItem.Debug);

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
        if (ImGui.Checkbox("Saucy theme".Loc(), ref on))
        {
            C.SaucyThemeEnabled = on;
            C.Save();
        }
        ImGui.TextDisabled("Designed by Wah".Loc());
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
            case NavItem.MiniCactpot: DrawMiniCactpotPanel(); break;
            case NavItem.GateSchedule: DrawGateSchedulePanel(); break;
            case NavItem.Navigation: DrawNavigationPanel(); break;
            case NavItem.Stats: DrawStatsTab(); break;
            case NavItem.About: AboutTab.Draw("Saucy"); break;
            case NavItem.Debug: DrawDebugTab(); break;
        }
    }

    private static void DrawTriadPanel()
    {
        DrawPanelHeader("Triple Triad".Loc());
        ImGuiEx.EzTabBar("###Triad",
            ("Main".Loc(), TriadSettingsUi.Draw, null, false),
            ("Cache".Loc(), TriadCacheSettingsUi.Draw, null, false));
    }

    private static void DrawPanelHeader(string title, string? subtitle = null) =>
        SaucyTheme.DrawPanelHeader(title, subtitle);

    private void DrawDebugTab()
    {
        ImGuiLayout.DrawCollapsingSection("物件 Debug 標記", ImGuiTreeNodeFlags.DefaultOpen, () =>
        {
            ImGui.TextWrapped("在附近每個物件的位置畫出 DataId、種類、座標與跟玩家的高度差/距離，" +
                               "用來排查「這個預測圈/目標到底是抓到哪個物件」之類的問題。");
            var enabled = global::Saucy.Framework.ObjectDebugOverlay.Enabled;
            if (ImGui.Checkbox("啟用物件標記##ObjectDebugOverlayEnabled", ref enabled))
            {
                global::Saucy.Framework.ObjectDebugOverlay.Enabled = enabled;
            }

            var eventObjOnly = global::Saucy.Framework.ObjectDebugOverlay.EventObjOnly;
            if (ImGui.Checkbox("僅顯示 EventObj##ObjectDebugOverlayEventObjOnly", ref eventObjOnly))
            {
                global::Saucy.Framework.ObjectDebugOverlay.EventObjOnly = eventObjOnly;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("GATE 內的炸彈/目標/裝飾物通常都是 EventObj，關閉可看到玩家/NPC/怪物等其他種類。");
            }

            var maxDistance = global::Saucy.Framework.ObjectDebugOverlay.MaxDistance;
            if (ImGui.SliderFloat("最大顯示距離##ObjectDebugOverlayMaxDistance", ref maxDistance, 5f, 100f, "%.0f"))
            {
                global::Saucy.Framework.ObjectDebugOverlay.MaxDistance = maxDistance;
            }
        });

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

        ImGuiLayout.DrawCollapsingSection("搶救小鳥大作戰 跳躍測試（3點）", ImGuiTreeNodeFlags.DefaultOpen, DrawCliffhangerJumpTest);
    }

    /// <summary>Scoped-down jump-mechanics test: record just 3 points (approach start, jump
    /// takeoff, where the next segment should start after landing) and test moving/jumping between
    /// them directly, without touching the full ordered route list — for quickly iterating on one
    /// jump segment at a time.</summary>
    private static void DrawCliffhangerJumpTest()
    {
        ImGui.TextWrapped("站到定點後按對應「記錄」按鈕：先錄跳躍前的起點，走到跳躍點按「記錄跳躍點」，" +
                           "跳過去落地後按「記錄跳躍後起點」。錄完可以用下面的按鈕個別測試移動/跳躍是否準確。");

        // Self-contained so this debug page doesn't depend on also having the main Cliffhanger
        // page open with its own "測試模式" checked — otherwise every button here silently does
        // nothing (OnUpdate's movement logic only runs when inGate, which needs TestRunActive true)
        // and looks like the buttons themselves are broken ("移動到這裡並跳躍沒有反應").
        var testActive = Cliffhanger.CliffhangerAutomation.TestRunActive;
        if (ImGui.Checkbox("啟用測試模式（活動外也可測試）##CliffhangerJumpTestActive", ref testActive))
        {
            Cliffhanger.CliffhangerAutomation.TestRunActive = testActive;
        }
        if (!testActive)
        {
            SaucyTheme.TextMuted("尚未啟用測試模式：下面的移動/跳躍按鈕不會有反應。");
        }

        // TestRunActive alone isn't enough — CliffhangerAutomation.OnUpdate() (which TickManualMove
        // lives inside) only ever runs at all if the Cliffhanger MODULE itself is enabled (separate
        // checkbox on its own page). Without it, Framework.Update never calls into this module, so
        // PreciseMovement's desired direction never gets set no matter how many times a move button
        // is clicked — confirmed live via SkippedNoDirection dominating the call breakdown even with
        // TestRunActive on ("啟用測試模式 有沒有勾都測過 不是原因"). Surface + auto-fix it here too.
        var moduleEnabled = C.IsModuleEnabled(ModuleNames.Cliffhanger);
        if (!moduleEnabled)
        {
            SaucyTheme.TextMuted("Cliffhanger 模組本身尚未啟用（在「搶救小鳥大作戰」頁面的「啟用」）：" +
                                  "自動移動邏輯完全不會執行，按鈕會自動幫你啟用。");
        }

        // Movement now goes through PreciseMovement (hooks the game's own movement-input read,
        // see Framework/PreciseMovement.cs) instead of simulated WASD — if the hook failed to
        // attach (e.g. the signature it scans for doesn't match this client's game build), every
        // move button will silently do nothing with no other visible symptom, so surface its
        // ready-state directly here rather than requiring a dev-console log check.
        SaucyTheme.TextMuted($"PreciseMovement 已就緒：{PreciseMovement.IsReady}" +
                              (PreciseMovement.IsReady ? "" : "（表示移動 hook 掛載失敗，按鈕移動不會有任何反應）"));
        SaucyTheme.TextMuted($"輸入啟用判斷已就緒：{PreciseMovement.IsInputCheckReady}" +
                              (PreciseMovement.InputCheckError is { } err ? $"　錯誤：{err}" : ""));
        SaucyTheme.TextMuted($"移動函式呼叫次數：{PreciseMovement.TotalCalls}　實際覆寫次數：{PreciseMovement.OverriddenCalls}");
        SaucyTheme.TextMuted($"跳過原因 - 附加呼叫：{PreciseMovement.SkippedAdditive}　輸入未啟用：{PreciseMovement.SkippedInputDisabled}　" +
                              $"無目標方向：{PreciseMovement.SkippedNoDirection}　判定為玩家已輸入：{PreciseMovement.SkippedRealInput}");
        SaucyTheme.TextMuted($"實際下達指令強度（0~1，1=全速）：{PreciseMovement.LastCommandedMagnitude:F2}　" +
                              $"角色實測速度：{Cliffhanger.CliffhangerAutomation.MeasuredSpeed:F2} m/s");

        DrawJumpTestSpotRow("跳躍起點（跳躍前站的位置）", C.GoldSaucerGates.CliffhangerJumpTestStart, isJumpPoint: false);
        DrawJumpTestSpotRow("跳躍點（起跳的位置）", C.GoldSaucerGates.CliffhangerJumpTestJump, isJumpPoint: true);
        DrawJumpTestSpotRow("跳躍後的下一個起點（落地後應該站的位置）", C.GoldSaucerGates.CliffhangerJumpTestNextStart, isJumpPoint: false);

        ImGui.Dummy(new(0, 4));
        var allThreeRecorded = C.GoldSaucerGates.CliffhangerJumpTestStart.Recorded &&
                                C.GoldSaucerGates.CliffhangerJumpTestJump.Recorded &&
                                C.GoldSaucerGates.CliffhangerJumpTestNextStart.Recorded;
        using (ImRaii.Disabled(!allThreeRecorded))
        {
            if (ImGui.Button("三點連續測試（依序跑完整段）##CliffhangerSequentialJumpTest"))
            {
                Cliffhanger.CliffhangerAutomation.TestRunActive = true;
                C.SetModuleEnabled(ModuleNames.Cliffhanger, true);
                Cliffhanger.CliffhangerAutomation.StartSequentialJumpTest(
                    new(C.GoldSaucerGates.CliffhangerJumpTestStart.X, C.GoldSaucerGates.CliffhangerJumpTestStart.Y, C.GoldSaucerGates.CliffhangerJumpTestStart.Z),
                    new(C.GoldSaucerGates.CliffhangerJumpTestJump.X, C.GoldSaucerGates.CliffhangerJumpTestJump.Y, C.GoldSaucerGates.CliffhangerJumpTestJump.Z),
                    new(C.GoldSaucerGates.CliffhangerJumpTestNextStart.X, C.GoldSaucerGates.CliffhangerJumpTestNextStart.Y, C.GoldSaucerGates.CliffhangerJumpTestNextStart.Z));
            }
        }
        if (!allThreeRecorded)
        {
            ImGui.SameLine();
            SaucyTheme.TextMuted("三個點都要先記錄才能連續測試");
        }
        else if (Cliffhanger.CliffhangerAutomation.IsSequentialTestRunning)
        {
            ImGui.SameLine();
            SaucyTheme.TextMuted(Cliffhanger.CliffhangerAutomation.SequentialTestStatus);
        }

        if (Player.Available && C.GoldSaucerGates.CliffhangerJumpTestNextStart.Recorded)
        {
            var nextStart = new Vector3(
                C.GoldSaucerGates.CliffhangerJumpTestNextStart.X,
                C.GoldSaucerGates.CliffhangerJumpTestNextStart.Y,
                C.GoldSaucerGates.CliffhangerJumpTestNextStart.Z);
            SaucyTheme.TextMuted($"目前位置跟「跳躍後起點」的距離：{Vector3.Distance(Player.Position, nextStart):F2}m（跳完落地後看這個數字準不準）");
        }
    }

    private static void DrawJumpTestSpotRow(string label, CliffhangerJumpTestSpot spot, bool isJumpPoint)
    {
        ImGui.PushID(label);
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        if (ImGui.SmallButton("記錄目前位置"))
        {
            if (Player.Available)
            {
                spot.X = Player.Position.X;
                spot.Y = Player.Position.Y;
                spot.Z = Player.Position.Z;
                spot.Recorded = true;
                C.Save();
            }
        }

        if (spot.Recorded)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"（{spot.X:F3}, {spot.Y:F3}, {spot.Z:F3}）");

            ImGui.SameLine();
            if (isJumpPoint)
            {
                if (ImGui.SmallButton("移動到這裡並跳躍"))
                {
                    // "三個移動到這裡沒有反應" turned out to be two separate prerequisites getting
                    // silently missed: TestRunActive is plain in-memory state (reset every reload),
                    // and the Cliffhanger MODULE checkbox on its own page is what actually makes
                    // Framework.Update call into CliffhangerAutomation.OnUpdate() at all — without
                    // it, nothing here ever runs regardless of TestRunActive. Force both on click.
                    Cliffhanger.CliffhangerAutomation.TestRunActive = true;
                    C.SetModuleEnabled(ModuleNames.Cliffhanger, true);
                    Cliffhanger.CliffhangerAutomation.TryMoveNowToAndJump(new(spot.X, spot.Y, spot.Z));
                }
            }
            else
            {
                if (ImGui.SmallButton("移動到這裡"))
                {
                    Cliffhanger.CliffhangerAutomation.TestRunActive = true;
                    C.SetModuleEnabled(ModuleNames.Cliffhanger, true);
                    Cliffhanger.CliffhangerAutomation.TryMoveNowTo(new(spot.X, spot.Y, spot.Z));
                }
            }
        }
        else
        {
            ImGui.SameLine();
            SaucyTheme.TextMuted("尚未記錄");
        }

        ImGui.PopID();
    }

    /// <summary>
    /// Event Coordinator NPCs ("活動解說員") exist at multiple locations around the Gold Saucer,
    /// so unlike each GATE's single registration-NPC spot, this is a free-form list the user adds
    /// to and deletes from directly — never a guessed/hardcoded position.
    /// </summary>
    private static void DrawGateSchedulePanel()
    {
        DrawPanelHeader("GATE schedule".Loc(), "GATE schedule automation".Loc());
        ImGui.TextWrapped("每小時 :55/:15/:35（活動開始前5分鐘）自動導航至最近的已記錄「活動解說員」；" +
                           "每小時 :00/:20/:40 若在已記錄的支援 GATE NPC 附近，自動互動並嘗試參加。");

        var autoOpen = C.GoldSaucerGates.AutoOpenUiOnGateJoin;
        if (ImGui.Checkbox("加入 GATE 時自動開啟並切換到對應頁面##AutoOpenUiOnGateJoin", ref autoOpen))
        {
            C.GoldSaucerGates.AutoOpenUiOnGateJoin = autoOpen;
            C.Save();
        }

        ImGui.Dummy(new(0, 4));
        var coordinatorAuto = C.GoldSaucerGates.EventCoordinatorAutoNavigate;
        if (ImGui.Checkbox("自動導航至活動解說員（:55/:15/:35）##EventCoordinatorAutoNavigate", ref coordinatorAuto))
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

        SaucyTheme.TextMuted("自動參加支援的 GATE（:00/:20/:40）——每個 GATE 可個別開關：");
        var airForceAutoJoin = C.GoldSaucerGates.AirForceAutoJoin;
        if (ImGui.Checkbox("空軍裝甲駕駛員##AirForceAutoJoin", ref airForceAutoJoin))
        {
            C.GoldSaucerGates.AirForceAutoJoin = airForceAutoJoin;
            C.Save();
        }
        ImGui.SameLine();
        var windBlowsAutoJoin = C.GoldSaucerGates.WindBlowsAutoJoin;
        if (ImGui.Checkbox("暴風倖存者##WindBlowsAutoJoin", ref windBlowsAutoJoin))
        {
            C.GoldSaucerGates.WindBlowsAutoJoin = windBlowsAutoJoin;
            C.Save();
        }
        ImGui.SameLine();
        var sliceIsRightAutoJoin = C.GoldSaucerGates.SliceIsRightAutoJoin;
        if (ImGui.Checkbox("必中一閃快刀斬魔##SliceIsRightAutoJoin", ref sliceIsRightAutoJoin))
        {
            C.GoldSaucerGates.SliceIsRightAutoJoin = sliceIsRightAutoJoin;
            C.Save();
        }
        ImGui.SameLine();
        var cliffhangerAutoJoin = C.GoldSaucerGates.CliffhangerAutoJoin;
        if (ImGui.Checkbox("搶救小鳥大作戰##CliffhangerAutoJoin", ref cliffhangerAutoJoin))
        {
            C.GoldSaucerGates.CliffhangerAutoJoin = cliffhangerAutoJoin;
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("僅在玩家靠近已記錄的對應 NPC 時才會嘗試互動並確認參加。報名互動之間有獨立的 30 秒冷卻，不會連續報名。");
        }
        ImGui.SameLine();
        if (ImGui.Button("立即開始導航（不等時間）##ManualStartJoin"))
        {
            GateScheduleAutomation.TriggerManualJoin();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("忽略 :00/:20/:40 時間限制，現在就開始搜尋附近的支援 NPC 並嘗試前往互動（60 秒內有效）。");
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
            if (ImGui.SmallButton($"立即互動##EventCoordinatorSpotInteract{i}"))
            {
                GateNpcNavigation.TryInteractNow(spot);
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
        DrawGateNpcNavigationControls("空軍裝甲駕駛員（與登高跳跳樂共用同一個NPC）", "AirForceNpc", C.GoldSaucerGates.AirForceNpcSpot,
            () => C.GoldSaucerGates.AirForceNpcAutoNavigate, v => C.GoldSaucerGates.AirForceNpcAutoNavigate = v);
        DrawGateNpcNavigationControls("暴風倖存者", "WindBlowsNpc", C.GoldSaucerGates.WindBlowsNpcSpot,
            () => C.GoldSaucerGates.WindBlowsNpcAutoNavigate, v => C.GoldSaucerGates.WindBlowsNpcAutoNavigate = v);
        DrawGateNpcNavigationControls("必中一閃快刀斬魔", "SliceIsRightNpc", C.GoldSaucerGates.SliceIsRightNpcSpot,
            () => C.GoldSaucerGates.SliceIsRightNpcAutoNavigate, v => C.GoldSaucerGates.SliceIsRightNpcAutoNavigate = v);

        // Cliffhanger's registration NPC has two physical spots (confirmed by user), so it gets
        // its own add/delete list instead of the single-spot record button used above.
        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("搶救小鳥大作戰　報名 NPC 自動導航（這個 GATE 有兩個報名 NPC，逐一鎖定後新增）：");
        var cliffhangerSpots = C.GoldSaucerGates.CliffhangerNpcSpots;
        for (var i = cliffhangerSpots.Count - 1; i >= 0; i--)
        {
            var spot = cliffhangerSpots[i];
            ImGui.TextUnformatted($"{spot.NpcName}（{spot.X:F1}, {spot.Y:F1}, {spot.Z:F1}）");
            ImGui.SameLine();
            if (ImGui.SmallButton($"立即移動##CliffhangerNpcMove{i}"))
            {
                GateNpcNavigation.TryMoveNow(spot);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"立即互動##CliffhangerNpcInteract{i}"))
            {
                GateNpcNavigation.TryInteractNow(spot);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"刪除##CliffhangerNpc{i}"))
            {
                cliffhangerSpots.RemoveAt(i);
                C.Save();
            }
        }

        if (ImGui.Button("鎖定 NPC 後按此新增##AddCliffhangerNpc"))
        {
            if (GateNpcNavigation.TryRecordNewListEntry(cliffhangerSpots, out var message))
            {
                Svc.Chat.Print($"[Saucy] {message}");
            }
            else
            {
                Svc.Chat.PrintError($"[Saucy] {message}");
            }
        }

        var cliffhangerAutoNav = C.GoldSaucerGates.CliffhangerNpcAutoNavigate;
        if (ImGui.Checkbox("自動導航至最近的報名點##CliffhangerNpcAutoNavigate", ref cliffhangerAutoNav))
        {
            C.GoldSaucerGates.CliffhangerNpcAutoNavigate = cliffhangerAutoNav;
            C.Save();
        }
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

        // Plugin reload clears in-memory Points, which disables "匯出路線 JSON" even though a real
        // recording from earlier this session already exists on disk ("匯出按鈕不可用") — recover
        // it automatically (once) so the button reflects reality instead of looking permanently
        // broken. Guarded by a one-shot flag rather than re-checking every draw call/frame.
        if (!_attemptedCliffhangerRouteAutoLoad && Cliffhanger.CliffhangerRecorder.Points.Count == 0 &&
            !Cliffhanger.CliffhangerRecorder.IsRecording)
        {
            _attemptedCliffhangerRouteAutoLoad = true;
            Cliffhanger.CliffhangerRecorder.TryLoadLatestExportedRoute();
        }

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
                // "按停止時沒有匯出" — stopping and exporting used to be two separate manual
                // steps (this button, then a second "匯出路線 JSON" click), easy to forget the
                // second one entirely. Export automatically right when recording stops instead.
                Cliffhanger.CliffhangerRecorder.StopRecording();
                var path = Cliffhanger.CliffhangerRecorder.Export();
                Svc.Chat.Print($"[Saucy] 已停止記錄並匯出 搶救小鳥大作戰 至：\n{path}");
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
        MiniCactpot,
        GateSchedule,
        Navigation,
        Stats,
        About,
        Debug
    }
}
