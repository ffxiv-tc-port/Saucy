using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using static Saucy.Framework.ImGuiScopes;
namespace Saucy.Framework;

/// <summary>
/// General-purpose "label every nearby object with its DataId/Kind/world position" overlay, for
/// diagnosing exactly which real-world object a prediction circle or targeting decision is keying
/// off of — e.g. this is how the Air Force One "地下物件" bug (a decorative object sharing a real
/// bomb/target DataId, sitting far below the play field) got tracked down: WorldToScreen alone
/// can't tell you WHY something projected where it did, but the raw DataId + Y position can.
/// Not tied to any single GATE — toggle from the Debug tab whenever a similar "why is this circle/
/// target here" question comes up for any minigame.
/// </summary>
internal static class ObjectDebugOverlay
{
    public static bool Enabled;
    public static bool EventObjOnly = true;
    public static float MaxDistance = 50f;

    public static void Draw()
    {
        if (!Enabled || !Player.Available)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var overlay = Window(
            "SaucyObjectDebugOverlay",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus);
        if (!overlay.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var playerPos = Player.Position;

        foreach (var obj in Svc.Objects)
        {
            if (obj == null || (EventObjOnly && obj.ObjectKind != ObjectKind.EventObj))
            {
                continue;
            }

            var dist = Vector3.Distance(playerPos, obj.Position);
            if (dist > MaxDistance)
            {
                continue;
            }

            if (!Svc.GameGui.WorldToScreen(obj.Position, out var screen))
            {
                continue;
            }

            // The Y-relative-to-player difference is what actually exposed the "地下物件" bug —
            // shown explicitly (not just raw Y) so an underground/overhead duplicate is obvious at
            // a glance without doing the subtraction by hand.
            var relativeY = obj.Position.Y - playerPos.Y;
            var label =
                $"DataId {obj.DataId}  {obj.ObjectKind}\n" +
                $"({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1})  ΔY {relativeY:+0.0;-0.0}\n" +
                $"{dist:F1}m";

            drawList.AddCircleFilled(screen, 3f, EzColor.Yellow);
            drawList.AddText(screen + new Vector2(6, -8), EzColor.Yellow, label);
        }
    }
}
