using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game;
using Saucy.AirForce;
using Saucy.LeapOfFaith;
using Saucy.OtherGames;
namespace Saucy;

public unsafe partial class PluginUI
{
    private static void DrawWindBlowsPanel()
    {
        DrawPanelHeader("Any Way the Wind Blows", "統計安全點");
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
        SaucyTheme.DrawCard("相依項目", "選用整合功能", GoldSaucerGateDependenciesUi.DrawWindBlows);
    }

    private static void DrawAirForcePanel()
    {
        DrawPanelHeader("Air Force One", "射擊乘坐小遊戲");
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
    }

    private static void DrawLeapOfFaithPanel()
    {
        DrawPanelHeader("Leap of Faith", "登高跳跳樂");
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
        var expectedGateType = global::Saucy.Framework.Module.GateType.LeapOfFaith;
        SaucyTheme.TextMuted($"偵測到的 GateType：{LeapOfFaith.LeapOfFaithAutomation.LastObservedGateType}" +
                              $"（本模組認定的 Leap of Faith 值：{expectedGateType}）");
        SaucyTheme.TextMuted($"已記錄平台點數：{LeapOfFaith.LeapOfFaithPlatformObserver.ObservedPlatforms.Count}");
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
            status = "Any Way the Wind Blows";
        }
        else if (C.IsModuleEnabled(ModuleNames.AirForceOne))
        {
            status = "Air Force One";
        }
        else if (C.IsModuleEnabled(ModuleNames.LeapOfFaith))
        {
            status = "Leap of Faith";
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
