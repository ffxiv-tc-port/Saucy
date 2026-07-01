using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game;
using Saucy.AirForce;
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
