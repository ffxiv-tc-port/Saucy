using ImGuiNET;
using ECommons.ImGuiMethods;
using Saucy.Framework;
using static Saucy.Framework.ImGuiScopes;
namespace Saucy.AirForce;

public class AirForceOne : Module
{
    public override string Name => "Air Force One";

    public override void Enable()
    {
        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw += Draw;
    }

    public override void Disable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= Draw;
        AirForceAutomation.ClearRewardTracking();
        GateNpcNavigation.ReleaseIfOwned(GateType.AirForceOne);
    }

    private static void OnFrameworkUpdate(IFramework _)
    {
        AirForceAutomation.OnUpdate();
        GateNpcNavigation.Tick(GateType.AirForceOne, C.GoldSaucerGates.AirForceNpcSpot, C.GoldSaucerGates.AirForceNpcAutoNavigate);
    }

    /// <summary>Draws the bomb avoid-radius circles and target lock circles used to tune
    /// C.GoldSaucerGates.AirForceBombAvoidRadius visually instead of by trial and error.</summary>
    private static void Draw()
    {
        if (!C.GoldSaucerGates.AirForceShowPredictionCircles)
        {
            return;
        }

        if (AirForceAutomation.LastBombCircles.Length == 0 && AirForceAutomation.LastTargetCircles.Length == 0)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var overlay = Window(
            "AirForcePredictionCircles",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus);
        if (!overlay.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        foreach (var bomb in AirForceAutomation.LastBombCircles)
        {
            drawList.AddCircle(bomb.Screen, bomb.Radius, EzColor.Red, 0, 2.5f);
            drawList.AddCircleFilled(bomb.Screen, 5f, EzColor.Red);
            drawList.AddText(bomb.Screen + new System.Numerics.Vector2(-15, 10), EzColor.Red, bomb.DataId.ToString());
        }

        foreach (var target in AirForceAutomation.LastTargetCircles)
        {
            var color = target.SkippedForBomb ? EzColor.Orange : EzColor.Green;
            drawList.AddCircle(target.Screen, 12f, color, 0, 2f);
            drawList.AddText(target.Screen + new System.Numerics.Vector2(-15, 15), color, target.DataId.ToString());
        }
    }
}
