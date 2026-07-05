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
        GateNpcNavigation.ReleaseIfOwned(GateType.LeapOfFaith);
    }

    // Leap of Faith and Air Force One register at the same physical NPC (confirmed by user:
    // "報名登高跳跳樂 和 報名空軍裝甲 共用NPC") — reuse AirForceNpcSpot rather than asking the
    // user to record the same location twice under a different name.
    private void OnUpdate(IFramework _)
    {
        LeapOfFaithAutomation.OnUpdate();
        GateNpcNavigation.Tick(GateType.LeapOfFaith, C.GoldSaucerGates.AirForceNpcSpot, C.GoldSaucerGates.AirForceNpcAutoNavigate);
    }

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

    // "改為整合其他玩家多次路線的優化路線繪製" — only draw segments enough independent passes have
    // confirmed to count as part of the real route; a stretch only ever seen once is more likely
    // noise (a player briefly testing a doomed jump) than a safe path, so it's left out entirely
    // rather than adding to the visual clutter.
    private const int MinObservationsForOptimizedRoute = 2;

    /// <summary>Draws the merged, cross-player-confirmed route — segments walked by multiple
    /// players (or the same player multiple times) get drawn brighter/thicker than ones only just
    /// crossing the confirmation threshold, so the more "obviously the real route" a stretch is,
    /// the more it stands out.</summary>
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

        // Consecutive same-player samples get connected into a line so forks/branches in the path
        // draw naturally (a point can appear in more than one segment). Drawn as a plain thin line
        // now, not a filled ~1m-wide quad — the thick fill stacked into an illegible solid blob
        // wherever many segments overlapped, per user feedback ("不要畫粗線了"). Auto-movement
        // still follows the underlying segment data exactly the same either way — only the drawing
        // changed, not the pathing. Segments below the confirmation threshold are skipped entirely
        // (see MinObservationsForOptimizedRoute) so only the merged, multi-pass-confirmed route
        // shows up instead of every single-pass sample.
        foreach (var segment in LeapOfFaithPlatformObserver.ComputeLinearSegments())
        {
            if (segment.ObservationCount < MinObservationsForOptimizedRoute)
            {
                continue;
            }

            if (Vector3.Distance(playerPos, segment.A) > MaxRenderDistance && Vector3.Distance(playerPos, segment.B) > MaxRenderDistance)
            {
                continue;
            }

            if (Svc.GameGui.WorldToScreen(segment.A, out var screenA) && Svc.GameGui.WorldToScreen(segment.B, out var screenB))
            {
                var confidence = Math.Min(segment.ObservationCount, 6);
                var thickness = 1.5f + (confidence * 0.4f);
                var alpha = (byte)Math.Min(255, 120 + (confidence * 25));
                var color = (EzColor.Blue & 0x00FFFFFFu) | ((uint)alpha << 24);
                drawList.AddLine(screenA, screenB, color, thickness);
            }
        }
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
