using ImGuiNET;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using Saucy.Framework;
using System.Numerics;
using static Saucy.Framework.ImGuiScopes;
namespace Saucy.Cliffhanger;

public class Cliffhanger : Module
{
    public override string Name => "Cliffhanger";

    public override void Enable()
    {
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += Draw;
    }

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= Draw;
        PreciseMovement.SetDesiredDirection(null);
        GateNpcNavigation.ReleaseIfOwned(GateType.Cliffhanger);
    }

    private void OnUpdate(IFramework _)
    {
        CliffhangerAutomation.OnUpdate();
        GateNpcNavigation.TickList(GateType.Cliffhanger, C.GoldSaucerGates.CliffhangerNpcSpots, C.GoldSaucerGates.CliffhangerNpcAutoNavigate);
    }

    private void Draw()
    {
        if (!GateDirector.IsInGate(Module.GateType.Cliffhanger))
        {
            return;
        }

        if (C.GoldSaucerGates.CliffhangerShowOwnTrail)
        {
            DrawOwnTrail();
        }

        // Blast circles (drawn below) already mark bombs and respect the display-timeout — a
        // separate "炸彈" text marker on NearestBombPosition would stay visible past that timeout
        // (avoidance itself still tracks it after the display window, just not the label) and
        // contradict what's on screen, so bombs only get the blast-circle treatment.
        if (C.GoldSaucerGates.CliffhangerShowBombBlastCircles)
        {
            DrawBombBlastCircles();
        }

        DrawMarker(CliffhangerAutomation.CurrentTargetPosition, EzColor.Green, "雛鳥");
    }

    /// <summary>Draws the player's own path this run, same mechanism as Leap of Faith's trail.</summary>
    private static void DrawOwnTrail()
    {
        var trail = CliffhangerAutomation.OwnTrail;
        if (trail.Count < 2)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var overlay = Window(
            "CliffhangerOwnTrail",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus);
        if (!overlay.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        for (var i = 1; i < trail.Count; i++)
        {
            if (Vector3.Distance(trail[i - 1], trail[i]) > 15f)
            {
                continue;
            }

            if (Svc.GameGui.WorldToScreen(trail[i - 1], out var prevScreen) &&
                Svc.GameGui.WorldToScreen(trail[i], out var currScreen))
            {
                drawList.AddLine(prevScreen, currScreen, EzColor.Yellow, 2f);
            }
        }
    }

    /// <summary>
    /// Bomb blast radius is unknown — no confirmed value from a real explosion, only a guess
    /// (default 6 units, tunable in the panel). Drawn as a translucent red circle around every
    /// nearby bomb so the guess can be visually compared/tuned against real gameplay rather than
    /// trusted blindly.
    /// </summary>
    private static void DrawBombBlastCircles()
    {
        var bombs = CliffhangerAutomation.AllBombPositions;
        if (bombs.Count == 0)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var overlay = Window(
            "CliffhangerBombBlast",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus);
        if (!overlay.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var blastRadius = CliffhangerAutomation.BombBlastRadiusGuess;
        foreach (var bomb in bombs)
        {
            // Project the world-space radius to screen pixels by measuring the on-screen distance
            // between the bomb center and a point offset by the radius, rather than a fixed pixel
            // size — so the circle shrinks/grows correctly as the bomb gets farther/closer.
            if (!Svc.GameGui.WorldToScreen(bomb, out var center) ||
                !Svc.GameGui.WorldToScreen(bomb + new Vector3(blastRadius, 0, 0), out var edge))
            {
                continue;
            }

            var pixelRadius = Vector2.Distance(center, edge);
            var fill = (EzColor.Red & 0x00FFFFFFu) | (50u << 24);
            drawList.AddCircleFilled(center, pixelRadius, fill);
            drawList.AddCircle(center, pixelRadius, EzColor.Red, 0, 2f);
        }
    }

    private static void DrawMarker(Vector3? position, uint color, string label)
    {
        if (position is not { } pos || !Svc.GameGui.WorldToScreen(pos, out var screen))
        {
            return;
        }

        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new(screen.X - 15, screen.Y - 15));
        ImGui.SetNextWindowSize(new Vector2(90, 50) * ImGuiHelpers.GlobalScale);
        using var window = Window(
            $"CliffhangerMarker_{label}",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs);
        if (!window.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(screen, 10f, color);
        ImGui.SetCursorPosY(24f);
        ImGui.TextUnformatted(label);
    }
}
