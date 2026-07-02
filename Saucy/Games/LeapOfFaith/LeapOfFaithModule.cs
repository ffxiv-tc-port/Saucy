using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using Saucy.Framework;
using System;
using System.Collections.Generic;
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
        GameKeyInput.ReleaseHeldKey();
    }

    private void OnUpdate(IFramework _) => LeapOfFaithAutomation.OnUpdate();

    public void Draw()
    {
        if (!LeapOfFaithDetection.IsActive)
        {
            return;
        }

        if (C.GoldSaucerGates.LeapOfFaithShowPlatformMarkers)
        {
            DrawObservedPlatformMarkers();
        }

        if (C.GoldSaucerGates.LeapOfFaithShowOwnTrail)
        {
            DrawOwnTrail();
        }

        if (C.GoldSaucerGates.LeapOfFaithShowOtherPlayerTrails)
        {
            DrawOtherPlayerTrails();
        }

        if (C.GoldSaucerGates.LeapOfFaithShowTargetPointer)
        {
            DrawCurrentTargetPointer();
        }
    }

    /// <summary>Draws other nearby players' recent paths (excluding any that ended in a fall — see
    /// LeapOfFaithPlatformObserver.UpdatePlayerTrail) as thin cyan lines, distinct from the
    /// player's own yellow trail.</summary>
    private static void DrawOtherPlayerTrails()
    {
        var trails = LeapOfFaithPlatformObserver.OtherPlayerTrails;
        if (trails.Count == 0)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var overlay = Window(
            "LeapOfFaithOtherTrails",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus);
        if (!overlay.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        foreach (var trail in trails)
        {
            for (var i = 1; i < trail.Count; i++)
            {
                if (Svc.GameGui.WorldToScreen(trail[i - 1], out var prevScreen) &&
                    Svc.GameGui.WorldToScreen(trail[i], out var currScreen))
                {
                    drawList.AddLine(prevScreen, currScreen, EzColor.Cyan, 1.5f);
                }
            }
        }
    }

    /// <summary>Draws the player's own path this run as a connected line — sampled every 200ms
    /// (see LeapOfFaithAutomation.OwnTrail), so a jump naturally shows up as a curved arc through
    /// several points rather than a single straight segment.</summary>
    private static void DrawOwnTrail()
    {
        var trail = LeapOfFaithAutomation.OwnTrail;
        if (trail.Count < 2)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var overlay = Window(
            "LeapOfFaithOwnTrail",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus);
        if (!overlay.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        // Segment-by-segment rather than one AddPolyline call, since a teleport-sized jump between
        // consecutive samples (fell off, got returned to a checkpoint) would otherwise draw a
        // straight line across the whole map connecting the two unrelated points.
        for (var i = 1; i < trail.Count; i++)
        {
            var prev = trail[i - 1];
            var curr = trail[i];
            if (Vector3.Distance(prev, curr) > 15f)
            {
                continue;
            }

            if (Svc.GameGui.WorldToScreen(prev, out var prevScreen) &&
                Svc.GameGui.WorldToScreen(curr, out var currScreen))
            {
                drawList.AddLine(prevScreen, currScreen, EzColor.Yellow, 2f);
            }
        }
    }

    /// <summary>Marks every platform position inferred from other players, so the growing "known
    /// route" is directly visible on screen. Marker size/color scales with observation count —
    /// densely-observed points (many players landed/stood there) read as more confidently safe
    /// than a point only seen once or twice.</summary>
    private static void DrawObservedPlatformMarkers()
    {
        var points = LeapOfFaithPlatformObserver.ObservedPlatforms;
        if (points.Count == 0)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var overlay = Window(
            "LeapOfFaithPlatformMarkers",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus);
        if (!overlay.Success)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        // WorldToScreen has no occlusion/depth check — a point on a distant platform behind or
        // below the one you're standing on still projects onto whatever's in front of the camera,
        // reading as clutter scattered over unrelated terrain/sky ("下方有許多藍點"). Points from
        // far outside actual jumping range aren't useful to look at anyway, so cut them entirely
        // rather than just dimming them.
        const float MaxRenderDistance = 40f;
        var playerPos = Player.Position;

        // Consecutive same-player samples get connected into a thick ~1m-wide line instead of
        // staying individual dots — a point can appear in more than one segment (different
        // players' walks crossing/diverging), so forks/branches in the path draw naturally.
        foreach (var segment in LeapOfFaithPlatformObserver.ComputeLinearSegments())
        {
            if (Vector3.Distance(playerPos, segment.A) > MaxRenderDistance && Vector3.Distance(playerPos, segment.B) > MaxRenderDistance)
            {
                continue;
            }

            DrawThickWorldLine(drawList, segment.A, segment.B, LeapOfFaithPlatformObserver.PathSegmentWidth,
                (EzColor.Blue & 0x00FFFFFFu) | (140u << 24));
        }

        foreach (var point in points)
        {
            if (Vector3.Distance(playerPos, point.Position) > MaxRenderDistance)
            {
                continue;
            }

            if (Svc.GameGui.WorldToScreen(point.Position, out var screen))
            {
                // Kept small and dim on purpose — these dots are a background hint, not something
                // that should compete with the actual target pointer for attention. EzColor packs
                // ABGR with alpha in the top byte; adjust it directly rather than swapping colors.
                var alpha = (uint)Math.Clamp(90 + (point.ObservationCount * 15), 90, 220);
                var color = (EzColor.Blue & 0x00FFFFFFu) | (alpha << 24);
                drawList.AddCircleFilled(screen, 3.5f, color);
            }
        }
    }

    /// <summary>Draws a world-space line as a filled quad rather than a fixed-pixel-width
    /// AddLine, so its on-screen thickness correctly reflects the real ~1m walkway width at any
    /// camera distance (same world-to-screen-offset projection trick used for the bomb blast
    /// circles elsewhere in this codebase).</summary>
    private static void DrawThickWorldLine(ImDrawListPtr drawList, Vector3 a, Vector3 b, float worldWidth, uint color)
    {
        var dir = b - a;
        dir.Y = 0;
        if (dir.LengthSquared() < 0.0001f)
        {
            return;
        }
        dir = Vector3.Normalize(dir);
        var perp = new Vector3(-dir.Z, 0, dir.X) * (worldWidth / 2f);

        if (!Svc.GameGui.WorldToScreen(a - perp, out var s1) ||
            !Svc.GameGui.WorldToScreen(a + perp, out var s2) ||
            !Svc.GameGui.WorldToScreen(b + perp, out var s3) ||
            !Svc.GameGui.WorldToScreen(b - perp, out var s4))
        {
            return;
        }

        Span<Vector2> quad = [s1, s2, s3, s4];
        drawList.AddConvexPolyFilled(ref quad[0], quad.Length, color);
    }

    // Standing on a cactuar trophy for ~1s is what actually picks it up (per user), so once
    // the player is this close the pointer/label is just clutter over the thing they're already
    // standing on — hide it rather than let it linger and block the view.
    private const float CactuarPickupHideRadius = 3f;

    private static void DrawCurrentTargetPointer()
    {
        if (LeapOfFaithAutomation.CurrentTargetPosition is not { } target)
        {
            return;
        }

        if (LeapOfFaithAutomation.CurrentTargetIsCactuar &&
            Vector3.Distance(Player.Position, target) < CactuarPickupHideRadius)
        {
            return;
        }

        if (!Svc.GameGui.WorldToScreen(target, out var pos))
        {
            return;
        }

        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new(pos.X - 30, pos.Y - 30));
        ImGui.SetNextWindowSize(new Vector2(200, 70) * ImGuiHelpers.GlobalScale);
        using var pointerWindow = Window(
            "LeapOfFaithPointer",
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs);
        if (!pointerWindow.Success)
        {
            return;
        }

        var color = LeapOfFaithAutomation.CurrentTargetIsFinish ? EzColor.Green : EzColor.Yellow;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(pos, 16f, color);
        drawList.AddCircle(pos, 22f, color, 0, 3f);

        ImGui.SetCursorPosY(48f);
        using var child = ImRaii.Child("LeapOfFaithLabel", new Vector2(190f, 26f) * ImGuiHelpers.GlobalScale);
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0.8f));
        ImGui.SetCursorPosX(4f * ImGuiHelpers.GlobalScale);
        var distance = Vector3.Distance(Player.Position, target);
        var label = LeapOfFaithAutomation.CurrentTargetIsFinish ? "終點" :
            LeapOfFaithAutomation.CurrentTargetIsCactuar ? "仙人掌盃" : "推測平台";
        ImGui.TextColored(color, $"{label} {distance:F1}m");
    }
}
