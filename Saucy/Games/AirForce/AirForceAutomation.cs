using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.Throttlers;
using ECommons.WindowsFormsReflector;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System;
using System.Linq;
namespace Saucy.AirForce;

internal static unsafe class EventObjExtensions
{
    // Old Dalamud's IEventObj has no AnimationId wrapper property; read the
    // underlying FFXIVClientStructs GameObject's EventState directly instead.
    public static byte AnimationId(this IGameObject obj) =>
        obj.Address == nint.Zero ? (byte)0 : ((GameObject*)obj.Address)->EventState;

    // Local shim for ECommons.CSExtensions.EqualsAny — that namespace only exists
    // in ECommons versions that also reference Dalamud.Game.NativeWrapper.AtkUnitBasePtr,
    // a type missing from this TW client's older Dalamud build.
    public static bool EqualsAny<T>(this T value, params T[] candidates) where T : struct =>
        Array.IndexOf(candidates, value) >= 0;
}

public static unsafe class AirForceAutomation
{
    private static DateTime? rewardWindowUntilUtc;
    private static bool wasInDuty;
    private static readonly System.Collections.Generic.Dictionary<ulong, byte> lastEventState = [];

    public static bool ShouldTrackReward
        => rewardWindowUntilUtc != null && DateTime.UtcNow <= rewardWindowUntilUtc.Value;

    public static void ClearRewardTracking()
    {
        rewardWindowUntilUtc = null;
        wasInDuty = false;
    }

    public static void ConsumeRewardTracking() => rewardWindowUntilUtc = null;

    public static void OnUpdate()
    {
        if (!C.IsModuleEnabled(ModuleNames.AirForceOne))
        {
            ClearRewardTracking();
            return;
        }

        var inDuty = Svc.Condition[ConditionFlag.BoundByDuty95] &&
                     GenericHelpers.TryGetAddonByName("RideShooting", out AddonRideShooting* rideAddon) &&
                     rideAddon->AtkUnitBase.IsReady();

        if (inDuty)
        {
            wasInDuty = true;
            rewardWindowUntilUtc = null;

            LogEventStateChanges();

            // AnimationId() (GameObject.EventState) never varies from 0 in this build — either the
            // pop-up/ready gating mechanic doesn't exist in this older game version, or the field
            // just isn't the right one here. Confirmed via live diagnostic (LogEventStateChanges)
            // showing 0 across every known target at every distance. Dropped the ==1 gate entirely
            // and just treat all known non-excluded targets as shootable when in view.
            foreach (var x in Svc.Objects.OfType<IEventObj>().Where(x => x.DataId.EqualsAny<uint>(
                2009678,
                2009676,
                2009677,
                2009679,
                2015180,
                2015179,
                2015178,
                2015183
            )).OrderBy(Player.DistanceTo))
            {
                if (x.DataId.EqualsAny<uint>(
                    2015183,
                    2009679
                ))
                {
                    continue;
                }

                if (Svc.GameGui.WorldToScreen(x.Position, out var screen))
                {
                    if (EzThrottler.Throttle("Shoot", 250) && RideShootingAim.TrySetScreenAim(screen))
                    {
                        Svc.Framework.RunOnTick(() =>
                            {
                                _ = WindowsKeypress.SendKeypress(Keys.Space);
                            },
                            delayTicks: 1);
                        break;
                    }
                }
            }

            return;
        }

        if (wasInDuty)
        {
            wasInDuty = false;
            rewardWindowUntilUtc = DateTime.UtcNow.AddMinutes(2);
        }
    }

    /// <summary>
    /// Diagnostic aid: prints to chat whenever a known target's EventState value changes, with a
    /// timestamp/name/distance, so you don't have to freeze-frame the Debug panel to catch the
    /// moment a target visibly becomes shootable — just watch the chat log afterward and compare
    /// against what you remember seeing.
    /// </summary>
    private static void LogEventStateChanges()
    {
        if (!EzThrottler.Throttle("Saucy.AirForce.EventStateLog", 100))
        {
            return;
        }

        foreach (var x in Svc.Objects.OfType<IEventObj>().Where(x => x.DataId.EqualsAny<uint>(
            2009678, 2009676, 2009677, 2009679, 2015180, 2015179, 2015178, 2015183)))
        {
            var current = x.AnimationId();
            if (lastEventState.TryGetValue(x.GameObjectId, out var previous) && previous == current)
            {
                continue;
            }

            lastEventState[x.GameObjectId] = current;
            Svc.Chat.Print($"[Saucy][AF1診斷] {DateTime.Now:HH:mm:ss.fff} {x.Name} ({x.DataId}) " +
                            $"EventState {previous}→{current} dist={Player.DistanceTo(x):F1}");
        }
    }

    public static void DrawDebug()
    {
        ImGuiEx.Text($"Enabled: {C.IsModuleEnabled(ModuleNames.AirForceOne)}");
        ImGuiEx.Text($"In duty: {Svc.Condition[ConditionFlag.BoundByDuty95]}");
        ImGuiEx.Text($"Tracking reward: {ShouldTrackReward}");

        var addonReady = GenericHelpers.TryGetAddonByName("RideShooting", out AddonRideShooting* rideAddon) &&
                         rideAddon->AtkUnitBase.IsReady();
        ImGuiEx.Text($"RideShooting addon ready: {addonReady}");

        var parityOk = RideShootingAim.VerifyLayoutParity(out var parityDetail);
        ImGuiEx.Text($"Legacy vs typed layout: {(parityOk ? "OK" : "MISMATCH")} — {parityDetail}");

        if (RideShootingAim.TryReadAim(out var aim))
        {
            ImGuiEx.Text($"Current aim: ({aim.X:F1}, {aim.Y:F1})");
        }

        var targets = Svc.Objects.OfType<IEventObj>().Where(x => x.DataId.EqualsAny<uint>(
            2009678, 2009676, 2009677, 2009679, 2015180, 2015179, 2015178, 2015183
        )).Where(x => !x.DataId.EqualsAny<uint>(2015183, 2009679)).OrderBy(Player.DistanceTo).Take(3).ToArray();
        ImGuiEx.Text($"Shootable targets (excl. avoid-list): {targets.Length}");
        foreach (var t in targets)
        {
            ImGuiEx.Text($"  {t.Name} ({t.DataId}) dist={Player.DistanceTo(t):F1}");
        }

        // Diagnostic: AnimationId() reading GameObject.EventState is an unverified guess (never
        // tested in-game). Lists the RAW value for every known target DataId regardless of the
        // ==1 filter above, so if auto-shoot never fires, compare this against what you actually
        // see in-game (a target visibly poppable vs. not) to find the right field/threshold.
        var allKnown = Svc.Objects.OfType<IEventObj>().Where(x => x.DataId.EqualsAny<uint>(
            2009678, 2009676, 2009677, 2009679, 2015180, 2015179, 2015178, 2015183
        )).OrderBy(Player.DistanceTo).Take(8).ToArray();
        ImGuiEx.Text($"[診斷] 附近已知目標原始 EventState 值 ({allKnown.Length}):");
        foreach (var t in allKnown)
        {
            ImGuiEx.Text($"  {t.Name} ({t.DataId}) EventState={t.AnimationId()} dist={Player.DistanceTo(t):F1}");
        }
    }
}
