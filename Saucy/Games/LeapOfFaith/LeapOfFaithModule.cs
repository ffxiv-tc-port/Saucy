using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using Saucy.Framework;
using System.Numerics;
using static Saucy.Framework.ImGuiScopes;
namespace Saucy.LeapOfFaith;

public class LeapOfFaith : Module
{
    public override string Name => "Leap of Faith";

    public override void Enable()
    {
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += Draw;
    }

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= Draw;
    }

    private void OnUpdate(IFramework _) => LeapOfFaithAutomation.OnUpdate();

    public void Draw()
    {
        if (!IsInGate(GateType.LeapOfFaith))
        {
            return;
        }

        if (LeapOfFaithAutomation.CurrentTargetPosition is not { } target)
        {
            return;
        }

        if (!Svc.GameGui.WorldToScreen(target, out var pos))
        {
            return;
        }

        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new(pos.X - 15, pos.Y - 15));
        ImGui.SetNextWindowSize(new Vector2(140, 50) * ImGuiHelpers.GlobalScale);
        using var pointerWindow = Window(
            "LeapOfFaithPointer",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs);
        if (!pointerWindow.Success)
        {
            return;
        }

        var color = LeapOfFaithAutomation.CurrentTargetIsFinish ? EzColor.Green : EzColor.Yellow;
        ImGui.GetWindowDrawList().AddCircleFilled(pos, 6f, color);

        ImGui.SetCursorPosY(24f);
        using var child = ImRaii.Child("LeapOfFaithLabel", new Vector2(130f, 20f) * ImGuiHelpers.GlobalScale);
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0.8f));
        ImGui.SetCursorPosX(4f * ImGuiHelpers.GlobalScale);
        var distance = Vector3.Distance(Player.Position, target);
        ImGui.Text((LeapOfFaithAutomation.CurrentTargetIsFinish ? "終點" : "仙人掌盃") + $" {distance:F1}m");
    }
}
