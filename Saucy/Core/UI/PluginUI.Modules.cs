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
        DrawPanelHeader("Any Way the Wind Blows", "statistical safe spot");
        var enabled = C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows);
        if (ImGui.Checkbox("Enable##Wind", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.AnyWayTheWindBlows, enabled);
            C.Save();
        }

        ImGui.TextWrapped("Shows the statistical safe spot during the GATE.");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var autoMove = C.GoldSaucerGates.WindBlowsAutoMovement;
            if (ImGui.Checkbox("Automatic movement (vnavmesh)##WindAuto", ref autoMove))
            {
                C.GoldSaucerGates.WindBlowsAutoMovement = autoMove;
                C.Save();
            }

            if (autoMove)
            {
                SaucyTheme.TextMuted("Pathfinds you onto the safe spot while you are off it.");
            }
        }

        ImGui.Dummy(new(0, 4));
        SaucyTheme.DrawCard("Dependencies", "Optional integrations", GoldSaucerGateDependenciesUi.DrawWindBlows);
    }

    private static void DrawAirForcePanel()
    {
        DrawPanelHeader("Air Force One", "ride shooting minigame");
        ImGuiEx.EzTabBar("###AirForce",
            ("Main", DrawAirForceMain, null, false),
            ("Debug", AirForceAutomation.DrawDebug, null, false));
    }

    private static void DrawAirForceMain()
    {
        var enabled = C.IsModuleEnabled(ModuleNames.AirForceOne);
        if (ImGui.Checkbox("Enable##AirForce", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.AirForceOne, enabled);
            if (!enabled)
            {
                AirForceAutomation.ClearRewardTracking();
            }

            C.Save();
        }

        ImGui.TextWrapped("Runs automatically when enabled. Plays the Air Force One ride-shooting minigame for you.");
    }

    private static void DrawMiniCactpotPanel()
    {
        DrawPanelHeader("Mini-Cactpot", "daily 3\u00d73 scratcher");
        var enabled = C.IsModuleEnabled(ModuleNames.MiniCactpot);
        if (ImGui.Checkbox("Enable##Mini", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.MiniCactpot, enabled);
            C.Save();
            if (ModuleManager.GetModule<MiniCactpot.MiniCactpot>() is { } miniCactpot)
            {
                if (enabled && !miniCactpot.IsEnabled)
                {
                    miniCactpot.EnableInternal();
                }
                else if (!enabled && miniCactpot.IsEnabled)
                {
                    miniCactpot.DisableInternal();
                }
            }
        }

        ImGui.TextWrapped("Plays Mini Cactpot automatically when you open the daily scratcher at the Gold Saucer.");
    }

    private static void DrawJumboCactpotPanel()
    {
        DrawPanelHeader("Jumbo Cactpot", "weekly 4-digit raffle");
        var enabled = C.IsModuleEnabled(ModuleNames.JumboCactpot);
        if (ImGui.Checkbox("Enable##Jumbo", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.JumboCactpot, enabled);
            C.Save();
            if (ModuleManager.GetModule<JumboCactpot.JumboCactpot>() is { } jumboCactpot)
            {
                if (enabled && !jumboCactpot.IsEnabled)
                {
                    jumboCactpot.EnableInternal();
                }
                else if (!enabled && jumboCactpot.IsEnabled)
                {
                    jumboCactpot.DisableInternal();
                }
            }
        }

        ImGui.TextWrapped(
            "Collect prizes at the Cactpot cashier yourself. Saucy then paths you to the Jumbo " +
            "broker and handles ticket purchase dialogue, randomizing, and confirms.");
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
        else if (C.IsModuleEnabled(ModuleNames.MiniCactpot))
        {
            status = "Mini-Cactpot";
        }
        else if (C.IsModuleEnabled(ModuleNames.JumboCactpot))
        {
            status = "Jumbo Cactpot";
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
