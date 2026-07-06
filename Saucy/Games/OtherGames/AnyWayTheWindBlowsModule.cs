using ImGuiNET;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Numerics;
using static Saucy.Framework.ImGuiScopes;
namespace Saucy.OtherGames;

public class AnyWayTheWindBlows : Module
{
    public override string Name => "Any Way the Wind Blows";

    public override void Enable()
    {
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += Draw;
    }

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= Draw;
        WindBlowsGateMovement.ReleaseIfOwned();
        PreciseMovement.SetDesiredDirection(null);
        GateNpcNavigation.ReleaseIfOwned(GateType.AnyWayTheWindBlows);
    }

    // "暴風倖存者還是沒有自動導航到安全點 加一個手動觸發" — bypasses IsInGate/SafeSpot.On/
    // WindBlowsAutoMovement entirely so the underlying movement call can be tested directly,
    // regardless of whatever's gating the normal automatic path. True one-shot: each click fires
    // exactly one movement attempt on the next tick, not a timed window ("不要30秒 按一下觸發一
    // 次").
    private static bool forceMoveRequested;

    public static void TriggerForceMove() => forceMoveRequested = true;

    // "暴風倖存者 報名後等待30秒 用新的移動方式移動到定點" — see the settle-gate comment in
    // OnUpdate for why this delay is needed.
    private const double PostJoinSettleSeconds = 30;

    private void OnUpdate(IFramework _)
    {
        // "傳送後 會立刻跳下場地回去找報名NPC" — right after registering/teleporting in, IsInGate
        // can briefly still read false while the GATE state finishes settling. GateNpcNavigation.Tick
        // only checks IsInGate itself, so during that brief window it thinks registration hasn't
        // happened yet and starts walking back toward the (now far outside the arena) registration
        // NPC — same settle window already used to hold off SafeSpot movement covers this too.
        //
        // Merely SKIPPING the Tick call here wasn't enough — a pre-registration vnavmesh path can
        // already be in flight (started just before the teleport, still "owned") and Tick is also
        // what's responsible for stopping it; skipping the call left that stale path issuing move
        // commands toward pre-teleport coordinates completely unmanaged during the whole settle
        // window, walking the character off the new arena trying to reach a position that belongs
        // to an entirely different area ("從傳送前位置導航 所以會跳下場地"). Explicitly release any
        // owned path instead of just no-opping.
        if (GateScheduleAutomation.IsWithinPostJoinSettle(GateType.AnyWayTheWindBlows, PostJoinSettleSeconds))
        {
            GateNpcNavigation.ReleaseIfOwned(GateType.AnyWayTheWindBlows);
        }
        else
        {
            GateNpcNavigation.Tick(GateType.AnyWayTheWindBlows, C.GoldSaucerGates.WindBlowsNpcSpot, C.GoldSaucerGates.WindBlowsNpcAutoNavigate);
        }

        if (forceMoveRequested)
        {
            forceMoveRequested = false;

            // Bypasses WindBlowsGateMovement's own internal IsOnPlatform gate too, not just this
            // module's outer checks — that gate itself could be the actual blocker (wrong
            // platform-center/radius constants silently failing IsOnPlatform forever), so a real
            // force-test needs to skip it entirely to isolate whether vnavmesh itself can move at
            // all here.
            WindBlowsGateMovement.ForceMoveTo(Stage.SafeSpot.Position, PreciseCloseRange);
            return;
        }

        if (!IsInGate(GateType.AnyWayTheWindBlows))
        {
            WindBlowsGateMovement.ReleaseIfOwned();
            return;
        }

        // "報名後 還沒傳送 路徑就已經是錯的了" — right after interacting with the registration NPC,
        // the actual teleport onto the arena hasn't happened yet; starting to steer toward SafeSpot
        // immediately (even once IsInGate reads true) can aim at a destination that only makes
        // sense post-teleport, from wherever the player still physically was to register. Hold off
        // for a real settle window after that join before letting movement start at all.
        // "地圖還沒完全載入 他就開始計算新的路線了 這是造成摔落的主因" — a fixed time delay alone
        // doesn't guarantee vnavmesh has actually finished (re)building the mesh for the new area;
        // wait for its own readiness signal too, not just the settle timer.
        if (GateScheduleAutomation.IsWithinPostJoinSettle(GateType.AnyWayTheWindBlows, PostJoinSettleSeconds) ||
            (Vnavmesh.IsInstalled && !Vnavmesh.IsNavReady()))
        {
            WindBlowsGateMovement.ReleaseIfOwned();
            return;
        }

        if (Stage.SafeSpot.On)
        {
            WindBlowsGateMovement.ReleaseIfOwned();
            return;
        }

        if (!C.GoldSaucerGates.WindBlowsAutoMovement)
        {
            WindBlowsGateMovement.ReleaseIfOwned();
            return;
        }

        WindBlowsGateMovement.TryMoveTo(Stage.SafeSpot.Position, PreciseCloseRange);
    }

    // Confirmed there's no dynamic/statistical safe-spot detection anywhere — Stage.SafeSpot's
    // fixed coordinate IS the intended target (empirically recorded), not a placeholder for a
    // missing runtime system. SafeSpot.On requires being within 0.00025 units, though, which
    // TryMoveTo's default 0.25 closeRange arrival tolerance never actually satisfies — landing
    // "close enough" by the movement's own standard still reads as never truly on the exact spot.
    // Use a much tighter arrival tolerance so movement actually closes in on the real coordinate
    // instead of stopping short ("暴風倖存者 要移動到準座標").
    private const float PreciseCloseRange = 0.02f;

    public void Draw()
    {
        if (!IsInGate(GateType.AnyWayTheWindBlows))
        {
            return;
        }

        if (Svc.GameGui.WorldToScreen(Stage.SafeSpot.Position, out var pos))
        {
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new(pos.X - 15, pos.Y - 15));
            ImGui.SetNextWindowSize(new Vector2(90, 50) * ImGuiHelpers.GlobalScale);
            using var pointerWindow = Window(
                "Pointer",
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs);
            if (pointerWindow.Success)
            {
                ImGui.GetWindowDrawList().AddCircleFilled(pos, 5f, Stage.SafeSpot.On ? EzColor.Green : EzColor.Red);
                if (!Stage.SafeSpot.On && Stage.SafeSpot.Near)
                {
                    ImGui.SetCursorPosY(24f);
                    using var child = ImRaii.Child("GuideText", new Vector2(80f, 20f) * ImGuiHelpers.GlobalScale);
                    using var guideBg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0.8f));
                    ImGui.SetCursorPosX(4f * ImGuiHelpers.GlobalScale);

                    if (Player.Position.X - Stage.SafeSpot.Position.X > 0.015)
                    {
                        ImGui.Text("向左移動");
                    }
                    else if (Stage.SafeSpot.Position.X - Player.Position.X > 0.015)
                    {
                        ImGui.Text("向右移動");
                    }
                    else if (Player.Position.Z < Stage.SafeSpot.Position.Z)
                    {
                        ImGui.Text("向下移動");
                    }
                    else if (Player.Position.Z > Stage.SafeSpot.Position.Z)
                    {
                        ImGui.Text("向上移動");
                    }
                }
            }
        }
    }

    public class Stage
    {
        public static SafeSpotWrapper SafeSpot => new(66.96f, -4.48f, -24.69f);

        /// Event Square GATE circle (shared with Slice is Right). Safe spot sits on the southern wing.
        public static Vector3 PlatformCenter => new(67.0f, -4.48f, -24.55f);

        public const float PlatformRadius = 3.5f;

        public const float PlatformFloorY = -4.48f;

        public class SafeSpotWrapper
        {
            public SafeSpotWrapper(Vector3 position) => Position = position;
            public SafeSpotWrapper(float x, float y, float z) => Position = new(x, y, z);
            public Vector3 Position { get; }
            public bool On => Player.DistanceTo(Position) < 0.00025;
            public bool Near => Player.DistanceTo(Position) < 0.05;
        }
    }
}
