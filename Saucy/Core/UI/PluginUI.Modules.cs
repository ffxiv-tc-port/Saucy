using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game;
using Saucy.AirForce;
using Saucy.Cliffhanger;
using Saucy.Framework;
using Saucy.IPC;
using Saucy.LeapOfFaith;
using Saucy.OtherGames;
using System;
namespace Saucy;

public unsafe partial class PluginUI
{
    private static void DrawWindBlowsPanel()
    {
        DrawPanelHeader("暴風倖存者", "統計安全點");
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
        }

        ImGui.Dummy(new(0, 4));
        var expectedGateType = global::Saucy.Framework.Module.GateType.AnyWayTheWindBlows;
        var currentGate = GateDirector.InSaucer && GateDirector.IsPlayerOnStage()
            ? GateDirector.GetCurrentGate()
            : global::Saucy.Framework.Module.GateType.None;
        SaucyTheme.TextMuted($"目前偵測到的 GateType：{currentGate}（本模組認定的暴風倖存者值：{expectedGateType}）");
        SaucyTheme.TextMuted($"vnavmesh 已安裝：{Vnavmesh.IsInstalled}　導航就緒：{Vnavmesh.IsNavReady()}　移動中：{Vnavmesh.IsMoving()}");
        SaucyTheme.TextMuted($"在安全點上：{AnyWayTheWindBlows.Stage.SafeSpot.On}　靠近安全點：{AnyWayTheWindBlows.Stage.SafeSpot.Near}");
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
        if (ImGui.Button($"立即移動##{idSuffix}"))
        {
            GateNpcNavigation.TryMoveNow(spot);
        }
    }

    private static void DrawAirForcePanel()
    {
        DrawPanelHeader("空軍裝甲駕駛員", "射擊乘坐小遊戲");
        ImGuiEx.EzTabBar("###AirForce",
            ("主要", DrawAirForceMain, null, false),
            ("除錯", AirForceAutomation.DrawDebug, null, false));
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
        DrawPanelHeader("登高跳跳樂大挑戰");
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

                var invert = C.GoldSaucerGates.LeapOfFaithInvertTurn;
                if (ImGui.Checkbox("反轉轉向##LeapOfFaithInvert", ref invert))
                {
                    C.GoldSaucerGates.LeapOfFaithInvertTurn = invert;
                    C.Save();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("如果自動移動時角色一直轉錯方向，勾選這個試試看。");
                }

                var jumpInterval = C.GoldSaucerGates.LeapOfFaithJumpIntervalSeconds;
                if (ImGui.SliderFloat("跳躍間隔（秒）##LeapOfFaithJumpInterval", ref jumpInterval, 0.6f, 3f, "%.1f"))
                {
                    C.GoldSaucerGates.LeapOfFaithJumpIntervalSeconds = jumpInterval;
                    C.Save();
                }
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
        DrawPanelHeader("搶救小鳥大作戰");
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

                var invert = C.GoldSaucerGates.CliffhangerInvertTurn;
                if (ImGui.Checkbox("反轉轉向##CliffhangerInvert", ref invert))
                {
                    C.GoldSaucerGates.CliffhangerInvertTurn = invert;
                    C.Save();
                }
            }
        }

        ImGui.Dummy(new(0, 4));
        var blastRadius = C.GoldSaucerGates.CliffhangerBombBlastRadiusGuess;
        if (ImGui.SliderFloat("炸彈波及範圍猜測（公尺）##CliffhangerBlastRadius", ref blastRadius, 1f, 15f, "%.1f"))
        {
            C.GoldSaucerGates.CliffhangerBombBlastRadiusGuess = blastRadius;
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

    private static void DrawSliceIsRightPanel()
    {
        DrawPanelHeader("必中一閃快刀斬魔");
        var enabled = C.IsModuleEnabled(ModuleNames.SliceIsRight);
        if (ImGui.Checkbox("啟用##SliceIsRight", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.SliceIsRight, enabled);
            C.Save();
        }

        ImGui.TextWrapped("這關的實際機制由 BossModReborn 接管，這裡只負責啟用/停用；報名 NPC 導航設定已統一移到「活動解說員排程」頁面。");
    }

    private static BannerInfo BuildBannerInfo()
    {
        var im = InventoryManager.Instance();
        var mgp = im != null ? im->GetInventoryItemCount(MgpItemId, false, false, false) : 0;

        string status;
        if (TriadRunSession.ModuleEnabled)
        {
            status = "九宮幻卡";
        }
        else if (C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows))
        {
            status = "暴風倖存者";
        }
        else if (C.IsModuleEnabled(ModuleNames.AirForceOne))
        {
            status = "空軍裝甲駕駛員";
        }
        else if (C.IsModuleEnabled(ModuleNames.LeapOfFaith))
        {
            status = "登高跳跳樂大挑戰";
        }
        else if (C.IsModuleEnabled(ModuleNames.Cliffhanger))
        {
            status = "搶救小鳥大作戰";
        }
        else
        {
            status = "閒置";
        }

        var sessionDelta = C.SessionStats.MGPWon + C.SessionStats.CuffMGP + C.SessionStats.LimbMGP +
                           C.SessionStats.AirForceMGP;

        return new()
        {
            Mgp = mgp, SessionDelta = sessionDelta, ModuleStatus = status
        };
    }
}
