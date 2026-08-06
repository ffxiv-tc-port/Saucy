using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Saucy.AirForce;
using Saucy.Cliffhanger;
using Saucy.Framework;
using Saucy.IPC;
using Saucy.LeapOfFaith;
using Saucy.OtherGames;
using System;
using System.Numerics;
namespace Saucy;

public unsafe partial class PluginUI
{
    private static void DrawWindBlowsPanel()
    {
        DrawPanelHeader("Wind Blows".Loc(), "Statistical safe spot".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows);
        if (ImGui.Checkbox("啟用##Wind", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.AnyWayTheWindBlows, enabled);
            C.Save();
        }

        ImGui.TextWrapped("在 GATE 期間顯示統計上的安全站位點。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var autoMove = C.GoldSaucerGates.WindBlowsAutoMovement;
            if (ImGui.Checkbox("自動移動（vnavmesh）##WindAuto", ref autoMove))
            {
                C.GoldSaucerGates.WindBlowsAutoMovement = autoMove;
                C.Save();
            }

            if (autoMove)
            {
                SaucyTheme.TextMuted("當你不在安全點上時，會自動導航你移動到安全點。");
            }

            if (ImGui.Button("強制移動測試（忽略 GATE/安全點判定）##WindForceMove"))
            {
                AnyWayTheWindBlows.TriggerForceMove();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("暴風倖存者還是沒自動移動時用這個測試——按一下觸發一次移動嘗試，跳過 GATE/安全點判定。");
            }
        }

        ImGui.Dummy(new(0, 4));
        var expectedGateType = global::Saucy.Framework.Module.GateType.AnyWayTheWindBlows;
        var currentGate = GateDirector.InSaucer && GateDirector.IsPlayerOnStage()
            ? GateDirector.GetCurrentGate()
            : global::Saucy.Framework.Module.GateType.None;
        SaucyTheme.TextMuted($"目前偵測到的 GateType：{currentGate}（本模組認定的暴風倖存者值：{expectedGateType}）");
        SaucyTheme.TextMuted($"vnavmesh 已安裝：{Vnavmesh.IsInstalled}　導航就緒：{Vnavmesh.IsNavReady()}　移動中：{Vnavmesh.IsMoving()}");
        SaucyTheme.TextMuted($"在安全點上：{AnyWayTheWindBlows.Stage.SafeSpot.On}　靠近安全點：{AnyWayTheWindBlows.Stage.SafeSpot.Near}");
        if (Player.Available)
        {
            SaucyTheme.TextMuted($"IsOnPlatform 判定：{WindBlowsGateMovement.DebugIsOnPlatform(Player.Position)}　" +
                                  $"與安全點距離：{Vector3.Distance(Player.Position, AnyWayTheWindBlows.Stage.SafeSpot.Position):F2}");
        }
        if (Vnavmesh.IsInstalled && Player.Available)
        {
            var floorHere = Vnavmesh.TryGetPointOnFloor(Player.Position, allowUnlandable: false, halfExtentXz: 1.5f);
            var floorText = floorHere is { } f ? $"有 (Y={f.Y:F1}，玩家 Y={Player.Position.Y:F1})" : "沒有（會改用玩家實際 Y 判斷）";
            SaucyTheme.TextMuted($"vnavmesh 在玩家目前位置查得到地板：{floorText}");
        }

        ImGui.Dummy(new(0, 4));
        SaucyTheme.DrawCard("相依項目", "選用整合功能", GoldSaucerGateDependenciesUi.DrawWindBlows);
    }

    /// <summary>
    /// Shared "walk to the registration NPC beforehand" controls, called from the unified
    ///「活動解說員排程」page (see PluginUI.cs DrawGateSchedulePanel) rather than from each GATE's
    /// own panel — per user request to consolidate them ("把自動報名NPC的區塊統一移動到活動排程").
    /// Deliberately stops at recording the NPC's position + navigating near it — targeting/talking/
    /// confirming registration stays manual by design (per user: "3 不用做" / "NPC 可手動登記"),
    /// and the position is never hardcoded/guessed, only ever whatever the user personally had
    /// targeted when they hit the record button (see the DataId-guessing lessons in the
    /// ffxiv-dalamud-plugins skill for why guessing game object identity/position here would be a
    /// mistake).
    /// </summary>
    internal static void DrawGateNpcNavigationControls(string label, string idSuffix, GateNpcSpot spot, Func<bool> getAutoNav, Action<bool> setAutoNav)
    {
        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted($"{label}　報名 NPC 自動導航（走到附近後會自動互動，確認參加交給其他插件處理）：");
        ImGui.TextWrapped(spot.Recorded
            ? $"已記錄：{spot.NpcName}（{spot.X:F1}, {spot.Y:F1}, {spot.Z:F1}）"
            : "尚未記錄 NPC 位置——請先在遊戲中鎖定該 NPC。");

        if (ImGui.Button($"記錄目前鎖定的 NPC 位置##{idSuffix}"))
        {
            if (GateNpcNavigation.TryRecordCurrentTarget(spot, out var message))
            {
                Svc.Chat.Print($"[Saucy] {message}");
            }
            else
            {
                Svc.Chat.PrintError($"[Saucy] {message}");
            }
        }

        using var disabled = ImRaii.Disabled(!spot.Recorded);
        var autoNav = getAutoNav();
        if (ImGui.Checkbox($"自動導航至報名點##{idSuffix}", ref autoNav))
        {
            setAutoNav(autoNav);
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"立即互動##{idSuffix}"))
        {
            GateNpcNavigation.TryInteractNow(spot);
        }

        // Replaces the old "立即移動" button, which called Vnavmesh.TryMoveTo once and threw the
        // result away — so it silently did nothing whenever vnavmesh was missing or the navmesh was
        // still building, and never reported arrival. The navigator waits for the mesh, reports
        // success/failure, and can be stopped from the Navigation panel.
        DrawRecordedSpotNavigationRow(spot, idSuffix);
    }

    private static void DrawAirForcePanel()
    {
        DrawPanelHeader("Air Force One".Loc(), "Rail shooter minigame".Loc());
        ImGuiEx.EzTabBar("###AirForce",
            ("Main".Loc(), DrawAirForceMain, null, false),
            ("Debug".Loc(), AirForceAutomation.DrawDebug, null, false));
    }

    private static void DrawAirForceMain()
    {
        var enabled = C.IsModuleEnabled(ModuleNames.AirForceOne);
        if (ImGui.Checkbox("啟用##AirForce", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.AirForceOne, enabled);
            if (!enabled)
            {
                AirForceAutomation.ClearRewardTracking();
            }

            C.Save();
        }

        ImGui.TextWrapped("啟用後將自動執行，為你自動遊玩 Air Force One 射擊乘坐小遊戲。");

        ImGui.Dummy(new(0, 4));
        var bombRadius = C.GoldSaucerGates.AirForceBombAvoidRadius;
        if (ImGui.SliderFloat("炸彈避讓半徑（像素）##AirForceBombRadius", ref bombRadius, 40f, 400f, "%.0f"))
        {
            C.GoldSaucerGates.AirForceBombAvoidRadius = bombRadius;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("目標畫面上跟炸彈距離小於這個值就會跳過不打，太常打到炸彈就調大，目標一直跳過不打就調小。");
        }

        var showCircles = C.GoldSaucerGates.AirForceShowPredictionCircles;
        if (ImGui.Checkbox("顯示炸彈/目標預測圈##AirForceShowCircles", ref showCircles))
        {
            C.GoldSaucerGates.AirForceShowPredictionCircles = showCircles;
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("關閉可提升畫面幀數，僅影響顯示，不影響自動避讓/鎖定邏輯。");
        }
    }

    private static void DrawLeapOfFaithPanel()
    {
        DrawPanelHeader("Leap of Faith".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.LeapOfFaith);
        if (ImGui.Checkbox("啟用##LeapOfFaith", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.LeapOfFaith, enabled);
            C.Save();
        }

        ImGui.TextWrapped("開啟後會在畫面上標出目前偵測到的終點或仙人掌盃位置與距離。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var autoMove = C.GoldSaucerGates.LeapOfFaithAutoMovement;
            if (ImGui.Checkbox("自動移動＋跳躍（實驗性）##LeapOfFaithAuto", ref autoMove))
            {
                C.GoldSaucerGates.LeapOfFaithAutoMovement = autoMove;
                C.Save();
            }

            if (autoMove)
            {
                SaucyTheme.TextMuted("沒有跳台碰撞偵測，只會朝目標方向移動並定時跳躍，可能會摔落，請留意。");
                SaucyTheme.TextMuted("跳躍時機改為觀察已記錄的藍色軌道自動判斷，不再是固定間隔。");
            }
        }

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("路徑繪製選項（畫太多會讓幀數大幅下降，可依需要關閉）：");
        var showPlatforms = C.GoldSaucerGates.LeapOfFaithShowPlatformMarkers;
        if (ImGui.Checkbox("顯示平台推測標記##LeapOfFaithShowPlatforms", ref showPlatforms))
        {
            C.GoldSaucerGates.LeapOfFaithShowPlatformMarkers = showPlatforms;
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("點數量多時最耗效能的一項，優先關閉這個。");
        }

        var showOwnTrail = C.GoldSaucerGates.LeapOfFaithShowOwnTrail;
        if (ImGui.Checkbox("顯示自己的路徑##LeapOfFaithShowOwnTrail", ref showOwnTrail))
        {
            C.GoldSaucerGates.LeapOfFaithShowOwnTrail = showOwnTrail;
            C.Save();
        }

        var showOtherTrails = C.GoldSaucerGates.LeapOfFaithShowOtherPlayerTrails;
        if (ImGui.Checkbox("顯示其他玩家路徑##LeapOfFaithShowOtherTrails", ref showOtherTrails))
        {
            C.GoldSaucerGates.LeapOfFaithShowOtherPlayerTrails = showOtherTrails;
            C.Save();
        }

        var showPointer = C.GoldSaucerGates.LeapOfFaithShowTargetPointer;
        if (ImGui.Checkbox("顯示目標指標##LeapOfFaithShowPointer", ref showPointer))
        {
            C.GoldSaucerGates.LeapOfFaithShowTargetPointer = showPointer;
            C.Save();
        }

        ImGui.Dummy(new(0, 4));
        var expectedGateType = global::Saucy.Framework.Module.GateType.LeapOfFaith;
        SaucyTheme.TextMuted($"偵測到的 GateType：{LeapOfFaith.LeapOfFaithAutomation.LastObservedGateType}" +
                              $"（本模組認定的 Leap of Faith 值：{expectedGateType}）");
        SaucyTheme.TextMuted($"已記錄平台點數：{LeapOfFaith.LeapOfFaithPlatformObserver.ObservedPlatforms.Count}");
        if (ImGui.Button("清除已記錄平台點##LeapOfFaithClearPlatforms"))
        {
            LeapOfFaith.LeapOfFaithPlatformObserver.Clear();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("清掉舊版偵測邏輯留下的雜訊點（例如深淵/半空中的點），之後會用新邏輯重新累積。");
        }

        // Answers "有沒有比等人踩過更好的地板偵測方式" — checks live whether vnavmesh's navmesh
        // actually has floor data over Leap of Faith's platforms (unknown until tested, since its
        // navmesh is baked from static collision and these platforms may be dynamic/instance-only).
        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted($"vnavmesh 已安裝：{Vnavmesh.IsInstalled}　導航就緒：{Vnavmesh.IsNavReady()}");
        if (Vnavmesh.IsInstalled && Player.Available)
        {
            var floorHere = Vnavmesh.TryGetPointOnFloor(Player.Position, allowUnlandable: false, halfExtentXz: 2f);
            var floorText = floorHere is { } f ? $"有 (Y={f.Y:F1}，玩家 Y={Player.Position.Y:F1})" : "沒有";
            SaucyTheme.TextMuted($"vnavmesh 在玩家目前位置查得到地板：{floorText}");
        }
    }

    private static void DrawCliffhangerPanel()
    {
        DrawPanelHeader("Cliffhanger".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.Cliffhanger);
        if (ImGui.Checkbox("啟用##Cliffhanger", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.Cliffhanger, enabled);
            C.Save();
        }

        ImGui.TextWrapped("啟用後會朝最近的迷路陸行鳥雛鳥移動，並在炸彈太靠近時嘗試遠離。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var autoMove = C.GoldSaucerGates.CliffhangerAutoMovement;
            if (ImGui.Checkbox("自動移動（實驗性）##CliffhangerAuto", ref autoMove))
            {
                C.GoldSaucerGates.CliffhangerAutoMovement = autoMove;
                C.Save();
            }

            if (autoMove)
            {
                SaucyTheme.TextMuted("沒有跳躍/碰撞偵測，只會朝雛鳥方向移動並嘗試遠離炸彈，請留意安全。");

                if (ImGui.Button("啟動移動（從路線第一點開始跑）##CliffhangerStartTestRun"))
                {
                    CliffhangerAutomation.StartTestRun();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("不等 GATE 實際開始，直接在目前位置從已錄製路線的第一個點開始跑；按一次就會重新從頭開始。");
                }
                ImGui.SameLine();
                if (ImGui.Button("停止測試##CliffhangerStopTestRun"))
                {
                    CliffhangerAutomation.TestRunActive = false;
                }
                ImGui.SameLine();
                SaucyTheme.TextMuted(CliffhangerAutomation.TestRunActive ? "測試中" : "未在測試");

                DrawCliffhangerSparseRoute();

                SaucyTheme.TextMuted($"診斷：vnavmesh 已安裝={Vnavmesh.IsInstalled}　導航就緒={Vnavmesh.IsNavReady()}　" +
                                      $"移動中={Vnavmesh.IsMoving()}　目前路點索引={CliffhangerAutomation.RouteIndex}/{C.GoldSaucerGates.CliffhangerRoute.Count}");

                var route = C.GoldSaucerGates.CliffhangerRoute;
                var idx = CliffhangerAutomation.RouteIndex;
                if (Player.Available && idx >= 0 && idx < route.Count)
                {
                    var wp = route[idx];
                    var dest = new Vector3(wp.X, wp.Y, wp.Z);
                    var dist = Vector3.Distance(Player.Position, dest);
                    SaucyTheme.TextMuted($"目前路點：{wp.Label}（跳躍點={wp.IsJumpPoint}）　" +
                                          $"跟玩家距離={dist:F2}m　炸彈阻擋中={CliffhangerAutomation.IsBlockedByBomb}");
                }
            }
        }

        ImGui.Dummy(new(0, 4));
        var blastRadius = C.GoldSaucerGates.CliffhangerBombBlastRadiusGuess;
        if (ImGui.SliderFloat("炸彈波及範圍猜測（公尺）##CliffhangerBlastRadius", ref blastRadius, 1f, 15f, "%.1f"))
        {
            C.GoldSaucerGates.CliffhangerBombBlastRadiusGuess = blastRadius;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("實際爆炸範圍未知，這只是畫面上紅色圓圈的猜測值，可依實際遊玩觀察調整。");
        }

        var bombDisplaySeconds = C.GoldSaucerGates.CliffhangerBombDisplaySeconds;
        if (ImGui.SliderFloat("炸彈標示顯示時間（秒）##CliffhangerBombDisplay", ref bombDisplaySeconds, 0.5f, 10f, "%.1f"))
        {
            C.GoldSaucerGates.CliffhangerBombDisplaySeconds = bombDisplaySeconds;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("炸彈出現後標記/範圍圈只顯示這麼久就自動消失（閃避邏輯本身不受影響，一直到炸彈消失都會避開）。");
        }

        var showOwnTrail = C.GoldSaucerGates.CliffhangerShowOwnTrail;
        if (ImGui.Checkbox("顯示自己的路徑##CliffhangerShowOwnTrail", ref showOwnTrail))
        {
            C.GoldSaucerGates.CliffhangerShowOwnTrail = showOwnTrail;
            C.Save();
        }

        var showBlast = C.GoldSaucerGates.CliffhangerShowBombBlastCircles;
        if (ImGui.Checkbox("顯示炸彈範圍圈##CliffhangerShowBlast", ref showBlast))
        {
            C.GoldSaucerGates.CliffhangerShowBombBlastCircles = showBlast;
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("關閉可提升畫面幀數，僅影響顯示，不影響炸彈閃避邏輯。");
        }

        ImGui.Dummy(new(0, 4));
        ImGui.TextWrapped("路線固定但有炸彈障礙物。若要重新確認目標/炸彈 DataId，可到「除錯」分頁用" +
                           "「搶救小鳥大作戰 路徑記錄」再錄一次手動遊玩紀錄。");

        var expectedGateType = global::Saucy.Framework.Module.GateType.Cliffhanger;
        SaucyTheme.TextMuted($"目前偵測到的 GateType：{CliffhangerAutomation.LastObservedGateType}" +
                              $"（本模組認定的 Cliffhanger 值：{expectedGateType}）");
    }

    /// <summary>
    /// Sparse, user-marked route (start / jump points with a separately-recorded direction / end)
    /// — per user request ("由我記錄點...跳躍點 方向 另外錄"), takes priority over the dense
    /// auto-recorded replay when set up. Order in the list IS the walking order; add points one at
    /// a time by standing at each spot and clicking "新增目前位置".
    /// </summary>
    private static void DrawCliffhangerSparseRoute()
    {
        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("手動標記路線（起點/跳躍點/終點，依清單順序走）：");

        var route = C.GoldSaucerGates.CliffhangerRoute;
        SaucyTheme.TextMuted("只要清單裡有路點，「自動移動」開啟時就會強制優先使用這條路線（取代自動錄製重播），不需要另外開關。");

        for (var i = 0; i < route.Count; i++)
        {
            var wp = route[i];
            ImGui.PushID(i);
            ImGui.TextUnformatted($"{i + 1}. {wp.Label}（{wp.X:F1}, {wp.Y:F1}, {wp.Z:F1}）");

            var isJumpPoint = wp.IsJumpPoint;
            if (ImGui.Checkbox("跳躍點##IsJumpPoint", ref isJumpPoint))
            {
                wp.IsJumpPoint = isJumpPoint;
                C.Save();
            }

            if (wp.IsJumpPoint)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("移動到跳躍點並跳躍##CliffhangerRouteJump"))
                {
                    // "立即移動沒有用" — without forcing these on, OnUpdate's `inGate` check (real
                    // GATE state OR TestRunActive) can be false while just testing the route outside
                    // an actual run, so it exits before ever reaching the manual-move logic at all.
                    // Same fix already applied to the debug panel's 3-point test buttons.
                    CliffhangerAutomation.TestRunActive = true;
                    C.SetModuleEnabled(ModuleNames.Cliffhanger, true);
                    CliffhangerAutomation.TryMoveNowToAndJump(new(wp.X, wp.Y, wp.Z));
                }
            }
            else
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("立即移動##CliffhangerRouteMove"))
                {
                    CliffhangerAutomation.TestRunActive = true;
                    C.SetModuleEnabled(ModuleNames.Cliffhanger, true);
                    CliffhangerAutomation.TryMoveNowTo(new(wp.X, wp.Y, wp.Z));
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("刪除"))
            {
                route.RemoveAt(i);
                C.Save();
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }

        if (ImGui.Button("新增目前位置到路線結尾##AddCliffhangerRoutePoint"))
        {
            if (Player.Available)
            {
                route.Add(new()
                {
                    X = Player.Position.X, Y = Player.Position.Y, Z = Player.Position.Z,
                    Label = route.Count == 0 ? "起點" : $"路點 {route.Count + 1}"
                });
                C.Save();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("清空路線##ClearCliffhangerRoute"))
        {
            route.Clear();
            C.Save();
        }
    }

    private static void DrawSliceIsRightPanel()
    {
        DrawPanelHeader("Slice is Right".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.SliceIsRight);
        if (ImGui.Checkbox("啟用##SliceIsRight", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.SliceIsRight, enabled);
            C.Save();
        }

        ImGui.TextWrapped("這關的實際機制由 BossModReborn 接管，這裡只負責啟用/停用；報名 NPC 導航設定已統一移到「活動解說員排程」頁面。");

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("進入 GATE 後先移動到場地邊界，再讓 BossModReborn 接管（不會再碰移動）：");
        var startSpot = C.GoldSaucerGates.SliceIsRightStartSpot;
        ImGui.TextWrapped(startSpot.Recorded
            ? $"已記錄：{startSpot.NpcName}（{startSpot.X:F1}, {startSpot.Y:F1}, {startSpot.Z:F1}）"
            : "尚未記錄場地邊界位置——請先進入 GATE 站到你要的位置。");

        if (ImGui.Button("記錄目前站立位置##SliceIsRightStartSpot"))
        {
            GateNpcNavigation.RecordCurrentPosition(startSpot, "場地邊界");
        }

        using var disabled = ImRaii.Disabled(!startSpot.Recorded);
        var startAutoNav = C.GoldSaucerGates.SliceIsRightStartAutoNavigate;
        if (ImGui.Checkbox("進入 GATE 後自動移動至此##SliceIsRightStartAutoNavigate", ref startAutoNav))
        {
            C.GoldSaucerGates.SliceIsRightStartAutoNavigate = startAutoNav;
            C.Save();
        }
    }

    private static void DrawMiniCactpotPanel()
    {
        DrawPanelHeader("Mini Cactpot".Loc(), "Daily scratch lottery".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.MiniCactpot);
        if (ImGui.Checkbox("啟用##MiniCactpot", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.MiniCactpot, enabled);
            C.Save();
        }

        ImGui.TextWrapped("啟用後，開啟仙人微彩（每日刮刮樂）面板時會自動依期望值翻格、選線並領獎。" +
                          "未啟用時不註冊任何監聽，手動遊玩不會被搶操作。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var playAgain = C.MiniCactpotAutoPlayAgain;
            if (ImGui.Checkbox("自動購買下一張（一次完成當日全部彩券）##MiniCactpotPlayAgain", ref playAgain))
            {
                C.MiniCactpotAutoPlayAgain = playAgain;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("一張完成關窗後，自動在「要購買下一張彩票嗎」按確認。" +
                                 "只會按仙人微彩自己的購票確認框（長按式按鈕版面），其他對話框一律不碰。");
            }

            ImGui.Dummy(new(0, 4));

            var clickInterval = C.MiniCactpotClickIntervalMs;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("點擊間隔（毫秒）##MiniCactpotClickInterval", ref clickInterval,
                    Configuration.MiniCactpotMinClickIntervalMs, Configuration.MiniCactpotMaxClickIntervalMs))
            {
                C.MiniCactpotClickIntervalMs = clickInterval;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("每次翻格／選線之間至少間隔這麼久。\n" +
                                 "下限刻意設在 400 毫秒：金蝶遊樂園的自動操作是伺服器看得見的行為，" +
                                 "「看起來像人在操作」本身就有價值，不建議為了快而壓到極限。");
            }

            var closeDelay = C.MiniCactpotCloseDelayMs;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("開獎後關窗延遲（毫秒）##MiniCactpotCloseDelay", ref closeDelay,
                    0, Configuration.MiniCactpotMaxCloseDelayMs))
            {
                C.MiniCactpotCloseDelayMs = closeDelay;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("全部翻開後等這麼久再領獎關窗，讓開獎動畫與派彩數字跑完——" +
                                 "設成 0 就會立刻關掉，你會看不到中了多少。");
            }
        }

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("第一張彩券的購買確認（花費 10 MGP）永遠由你自己按下——模組只接手面板開啟後的流程。");

        var module = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.MiniCactpot.MiniCactpotModule>();
        SaucyTheme.TextMuted($"狀態：{(module == null ? "模組未載入" : enabled ? module.LastAction : "未啟用")}");
    }

    private static void DrawOutOnALimbPanel()
    {
        DrawPanelHeader("Out on a Limb".Loc(), "Chocobo Square logging machine".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.OutOnALimb);
        if (ImGui.Checkbox("啟用##OutOnALimb", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.OutOnALimb, enabled);
            C.Save();
        }

        ImGui.TextWrapped("啟用後，孤樹無援的機台畫面開著時會自動幫你停力量表、並依每一刀的系統訊息手感" +
                          "（沒手感／接觸到／很接近／正中目標）逐步收斂出最佳砍伐位置後揮斧。" +
                          "除非你另外打開下面的「連續遊玩」並按下開始，" +
                          "模組不會自己去找機台、不會自己互動、也不會自己開始新的一局。");

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("這是伺服器看得見的行為模式：自動出手沒有人類的反應延遲，連續遊玩時尤其明顯。" +
                             "模組只讀取畫面上本來就顯示給你看的資料（機台面板欄位、指針刻度、樹的量表），" +
                             "不讀寫遊戲封包、不修改遊戲記憶體，出手方式就是按下畫面上原本就有的按鈕。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();

            var difficulty = C.OutOnALimb.Difficulty;
            ImGui.SetNextItemWidth(140f);
            if (ImGuiEx.EnumCombo("力量表目標##LimbDifficulty", ref difficulty))
            {
                C.OutOnALimb.Difficulty = difficulty;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("第一階段力量表要停在哪一格。泰坦最快（獎勵最高）也最難停中，仙人掌怪最慢最好停。\n" +
                                 "三個名字對應的是「當下量到的三段寬度排名」（最窄＝泰坦），" +
                                 "不是寫死的節點編號——伺服器每一局給的區間寬度可以不一樣。");
            }

            var tolerance = C.OutOnALimb.Tolerance;
            ImGui.SetNextItemWidth(140f);
            if (ImGui.SliderInt("容許誤差##LimbTolerance", ref tolerance, 1, 4))
            {
                C.OutOnALimb.Tolerance = Math.Clamp(tolerance, 1, 4);
                C.Save();
            }

            var requiredFps = RequiredFramerate(C.OutOnALimb.Difficulty, C.OutOnALimb.Tolerance);
            var currentFps = (int)ImGui.GetIO().Framerate;
            SaucyTheme.TextMuted($"參考畫面更新率：{requiredFps} FPS（你目前 {currentFps} FPS）。" +
                                 "現在除了「這一幀夠不夠近」之外，還會判斷「兩幀之間有沒有掃過目標」，" +
                                 "所以更新率不夠時不會像以前那樣整段掃過去都按不到；這個數字只是精準度的參考。");

            var autoPowerMeter = C.OutOnALimb.AutoPowerMeter;
            if (ImGui.Checkbox("自動停力量表##LimbAutoPowerMeter", ref autoPowerMeter))
            {
                C.OutOnALimb.AutoPowerMeter = autoPowerMeter;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("力量表畫面是孤樹無援與礦脈探索共用的，所以只有在附近確實認得出" +
                                 "孤樹無援機台時才會幫你停——認不出來就完全不碰，由你自己停表，砍伐階段照樣會接手。");
            }

            var autoContinue = C.OutOnALimb.AutoContinue;
            if (ImGui.Checkbox("自動接受「挑戰翻倍」##LimbAutoContinue", ref autoContinue))
            {
                C.OutOnALimb.AutoContinue = autoContinue;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("預設關閉：砍完一棵樹後的續戰確認框交給你自己按。" +
                                 "開啟後，只有在剩餘時間足夠時才會接受；讀不到剩餘時間一律按「否」收工。");
            }

            if (autoContinue)
            {
                var stopAt = C.OutOnALimb.StopAtSecondsRemaining;
                ImGui.SetNextItemWidth(140f);
                if (ImGui.DragInt("剩餘秒數低於此值就收工##LimbStopAt", ref stopAt, 0.5f, 0, 60))
                {
                    C.OutOnALimb.StopAtSecondsRemaining = Math.Clamp(stopAt, 0, 60);
                    C.Save();
                }
            }

            DrawOutOnALimbAutoReplay();

            ImGui.Dummy(new(0, 4));
            var logDiag = C.OutOnALimb.LogBoardDiagnostics;
            if (ImGui.Checkbox("把機台面板欄位寫進 log##LimbDiag", ref logDiag))
            {
                C.OutOnALimb.LogBoardDiagnostics = logDiag;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("診斷用：每秒把機台面板的 AtkValue[0..15] 與指針刻度寫一行到 log（Information 等級）。" +
                                 "只有要回報問題時才需要打開。");
            }
        }

        ImGui.Dummy(new(0, 4));
        var limbModule = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.OutOnALimb.OutOnALimbModule>();
        SaucyTheme.TextMuted($"狀態：{(limbModule == null ? "模組未載入" : enabled ? limbModule.LastAction : "未啟用")}");
        if (limbModule != null && enabled)
        {
            SaucyTheme.TextMuted($"解題器：{limbModule.SolverSummary}");

            // ⚠️ 回饋來源的健康狀況放在列上而不是 tooltip：
            // 「解題器沒有資料可學」是使用者必須一眼看到的事，不是起疑才查的事。
            SaucyTheme.TextMuted(limbModule.FeedbackSummary);
        }
    }

    /// <summary>連續遊玩區塊。開關是設定、真正會動作的是「開始」按鈕——
    /// 而且停止鈕永遠畫在列上（不藏在 tooltip 或折疊區裡），跑起來時一眼就找得到。</summary>
    private static void DrawOutOnALimbAutoReplay()
    {
        ImGui.Dummy(new(0, 6));
        ImGui.Separator();
        ImGui.Dummy(new(0, 2));

        var module = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.OutOnALimb.OutOnALimbModule>();
        var running = module?.AutoReplayRunning == true;

        var autoReplay = C.OutOnALimb.AutoReplay;
        if (ImGui.Checkbox("允許連續遊玩##LimbAutoReplay", ref autoReplay))
        {
            C.OutOnALimb.AutoReplay = autoReplay;
            C.Save();
            if (!autoReplay)
            {
                module?.StopAutoReplay("設定已關閉");
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("一局結束後自動再跟機台開下一局，省掉中間的等待。\n" +
                             "🔴 預設關閉，而且光是打開這個開關還不會動作——一定要按下面的「開始連續遊玩」。\n" +
                             "這是設計成短時間、有人在旁邊看著的用法，不是掛機：跑滿設定的局數就會自己停。");
        }

        if (!autoReplay)
        {
            SaucyTheme.TextMuted("連續遊玩關閉中：一局結束後由你自己跟機台開下一局。");
            return;
        }

        var maxGames = C.OutOnALimb.AutoReplayMaxGames;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.SliderInt("最多自動幾局##LimbReplayCap", ref maxGames, 1, 20))
        {
            C.OutOnALimb.AutoReplayMaxGames = Math.Clamp(maxGames, 1, 20);
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("到達這個局數就自己停下來，要再跑得重新按一次「開始連續遊玩」。");
        }

        if (module == null)
        {
            SaucyTheme.TextMuted("模組未載入。");
            return;
        }

        ImGui.Dummy(new(0, 2));
        if (running)
        {
            using (ImRaii.PushColor(ImGuiCol.Button, SaucyTheme.TextErrorColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, SaucyTheme.TextErrorColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, SaucyTheme.TextErrorColor))
            {
                if (ImGui.Button("■ 停止連續遊玩##LimbReplayStop", new(220f, 0f)))
                {
                    module.StopAutoReplay("使用者按下停止");
                }
            }

            ImGui.SameLine();
            SaucyTheme.TextWarning($"進行中：{module.AutoReplayGamesDone}/{Math.Max(1, C.OutOnALimb.AutoReplayMaxGames)} 局");
        }
        else
        {
            if (ImGui.Button("▶ 開始連續遊玩##LimbReplayStart", new(220f, 0f)))
            {
                module.StartAutoReplay();
            }

            ImGui.SameLine();
            SaucyTheme.TextMuted("按下之後才會自己跟機台互動；站到機台旁邊再按。");
        }
    }

    /// <summary>指針掃過目標所需的取樣率——容許誤差越窄、難度越高，指針停留在窗口內的時間越短。
    /// 數值沿用上游 Saucy 的實測表。</summary>
    private static int RequiredFramerate(global::Saucy.OutOnALimb.LimbDifficulty difficulty, int tolerance)
    {
        int[] table = difficulty switch
        {
            global::Saucy.OutOnALimb.LimbDifficulty.Titan => [480, 240, 120, 90, 60],
            global::Saucy.OutOnALimb.LimbDifficulty.Morbol => [240, 120, 90, 60, 30],
            var _ => [120, 90, 60, 30, 15]
        };

        var index = Math.Clamp(tolerance, 0, table.Length - 1);
        return table[index];
    }

    private static BannerInfo BuildBannerInfo()
    {
        var im = InventoryManager.Instance();
        var mgp = im != null ? im->GetInventoryItemCount(MgpItemId, false, false, false) : 0;

        string status;
        if (TriadRunSession.ModuleEnabled)
        {
            status = "Triple Triad";
        }
        else if (C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows))
        {
            status = "Wind Blows";
        }
        else if (C.IsModuleEnabled(ModuleNames.AirForceOne))
        {
            status = "Air Force One";
        }
        else if (C.IsModuleEnabled(ModuleNames.LeapOfFaith))
        {
            status = "Leap of Faith";
        }
        else if (C.IsModuleEnabled(ModuleNames.Cliffhanger))
        {
            status = "Cliffhanger";
        }
        else if (C.IsModuleEnabled(ModuleNames.MiniCactpot))
        {
            status = "Mini Cactpot";
        }
        else
        {
            status = "Idle";
        }

        var sessionDelta = C.SessionStats.MGPWon + C.SessionStats.CuffMGP + C.SessionStats.LimbMGP +
                           C.SessionStats.AirForceMGP;

        return new()
        {
            Mgp = mgp, SessionDelta = sessionDelta, ModuleStatus = status
        };
    }
}
