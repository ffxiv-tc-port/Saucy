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

    // After a shot is fired at a target, skip it for a window so the loop moves on to a different
    // target instead of re-aiming/re-firing at the same one repeatedly.
    private static readonly System.Collections.Generic.Dictionary<ulong, DateTime> recentlyFiredAtUtc = [];
    private const int RecentlyFiredCooldownMs = 250;

    // Ground truth confirmed by the user via direct eyes-on-screen observation of point values
    // (not a diagnostic-dump guess, which had gotten this wrong twice before):
    //   2009676 = 1-star target (50pts)   2009677 = 2-star target (100pts)
    //   2009678 = 3-star target (300pts)  2009679 = bomb (-20pts)   2009701 = player's own bullet
    private const uint BombDataId = 2009679;

    // Firing the instant a target is chosen looked robotic ("1星目標鎖定太快 太像機器人") — hold
    // the aim on a newly-picked target for a short randomized delay before allowing the first shot,
    // mimicking human reaction time. Only applies to the first shot on a given target; once locked,
    // subsequent throttled shots at the same target aren't delayed again.
    private static readonly Random rng = new();
    private static ulong lockedTargetId;
    private static DateTime lockedSinceUtc;
    private static int currentLockDelayMs;

    // Exposed for the "draw prediction circles" overlay so the avoid radius can be tuned visually
    // instead of by trial-and-error against the slider. DataId is included so it can be labeled
    // on screen — DataId misidentification has bitten this module twice already, so being able to
    // eyeball "which DataId is this thing on screen" without opening the debug tab is worth it.
    public readonly record struct BombCircle(System.Numerics.Vector2 Screen, float Radius, uint DataId);
    public readonly record struct TargetCircle(System.Numerics.Vector2 Screen, bool SkippedForBomb, uint DataId);

    public static BombCircle[] LastBombCircles { get; private set; } = [];
    public static TargetCircle[] LastTargetCircles { get; private set; } = [];

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

            // Bomb avoid radius must scale with distance — a bomb close to camera fills a much
            // bigger area of the screen than the exact same bomb far away, so a flat pixel radius
            // either misses close bombs or refuses to shoot anything near far ones. Anchored at
            // ReferenceDistance: a bomb at that distance uses the configured slider radius as-is;
            // closer bombs get a proportionally bigger avoid radius, farther ones smaller.
            const float ReferenceDistance = 20f;
            var bombs = Svc.Objects.OfType<IEventObj>()
                .Where(x => x.DataId == BombDataId)
                .Select(x => (Screen: Svc.GameGui.WorldToScreen(x.Position, out var s) ? s : (System.Numerics.Vector2?)null, Dist: Player.DistanceTo(x)))
                .Where(b => b.Screen.HasValue)
                .Select(b => (Screen: b.Screen!.Value, AvoidRadius: Math.Clamp(
                    C.GoldSaucerGates.AirForceBombAvoidRadius * (ReferenceDistance / Math.Max(b.Dist, 1f)),
                    40f, 500f)))
                .ToArray();
            LastBombCircles = bombs.Select(b => new BombCircle(b.Screen, b.AvoidRadius, BombDataId)).ToArray();

            var targetCircles = new System.Collections.Generic.List<TargetCircle>();

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

                if (recentlyFiredAtUtc.TryGetValue(x.GameObjectId, out var firedAt) &&
                    (DateTime.UtcNow - firedAt).TotalMilliseconds < RecentlyFiredCooldownMs)
                {
                    continue;
                }

                if (Svc.GameGui.WorldToScreen(x.Position, out var screen))
                {
                    var skippedForBomb = bombs.Any(b => System.Numerics.Vector2.Distance(b.Screen, screen) < b.AvoidRadius);
                    targetCircles.Add(new TargetCircle(screen, skippedForBomb, x.DataId));
                    if (skippedForBomb)
                    {
                        continue;
                    }

                    RideShootingAim.TrySetScreenAim(screen);

                    if (x.GameObjectId != lockedTargetId)
                    {
                        lockedTargetId = x.GameObjectId;
                        lockedSinceUtc = DateTime.UtcNow;
                        currentLockDelayMs = rng.Next(80, 200);
                    }

                    var lockedFor = (DateTime.UtcNow - lockedSinceUtc).TotalMilliseconds;
                    if (lockedFor >= currentLockDelayMs && EzThrottler.Throttle("Shoot", 60))
                    {
                        recentlyFiredAtUtc[x.GameObjectId] = DateTime.UtcNow;
                        Svc.Framework.RunOnTick(() => RideShootingAim.FireClick(screen), delayTicks: 1);
                    }

                    break;
                }
            }

            LastTargetCircles = [.. targetCircles];
            return;
        }

        LastBombCircles = [];
        LastTargetCircles = [];

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

        // Raw addresses for manual memory scanning (e.g. Cheat Engine) — the AimScreenX/Y offset
        // (0xC70/0xC74) is an unverified guess reading garbage in this build. Use these as base
        // pointers to scan for the real offset instead of the whole module.
        var agent = AgentRideShooting.TryGet();
        ImGuiEx.Text($"AgentRideShooting address: 0x{(nint)agent:X}");
        if (agent != null)
        {
            ImGuiEx.Text($"Handler pointer (at +0x30): 0x{(nint)agent->Handler:X}");
        }

        var parityOk = RideShootingAim.VerifyLayoutParity(out var parityDetail);
        ImGuiEx.Text($"Legacy vs typed layout: {(parityOk ? "OK" : "MISMATCH")} — {parityDetail}");

        if (RideShootingAim.TryReadAim(out var aim))
        {
            ImGuiEx.Text($"Current aim: ({aim.X:F1}, {aim.Y:F1})");
        }

        var targets = Svc.Objects.OfType<IEventObj>().Where(x => x.DataId.EqualsAny<uint>(
            2009678, 2009676, 2009677
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

        // If "Shootable targets" above is 0 while actual balloons/targets are visible on screen,
        // the whitelisted DataIds (from the original non-TW-client source) may not match this
        // build's real IDs. Dump every nearby EventObj unfiltered so the real ID can be found by
        // comparing distance/name against what's visibly on screen at that moment.
        var nearbyEventObjs = Svc.Objects.OfType<IEventObj>()
            .Where(x => Player.DistanceTo(x) < 60)
            .OrderBy(Player.DistanceTo).Take(15).ToArray();
        ImGuiEx.Text($"[診斷] 附近所有 EventObj，不限 DataId ({nearbyEventObjs.Length}):");
        foreach (var t in nearbyEventObjs)
        {
            ImGuiEx.Text($"  {t.Name} DataId={t.DataId} EventState={t.AnimationId()} dist={Player.DistanceTo(t):F1}");
        }

        // A reported "ground target" being shot at doesn't show up above if it isn't actually an
        // EventObj (e.g. a Companion/BattleNpc/EventNpc decoration) — dump every object kind here,
        // with world Y, so a ground-level decoy can be distinguished from the airborne real targets
        // by height even before its exact DataId/kind is known.
        var nearbyAny = Svc.Objects
            .Where(x => x != null && Player.DistanceTo(x) < 60)
            .OrderBy(Player.DistanceTo).Take(15).ToArray();
        ImGuiEx.Text($"[診斷] 附近所有物件，不限種類 ({nearbyAny.Length}):");
        foreach (var t in nearbyAny)
        {
            ImGuiEx.Text($"  {t.Name} Kind={t.ObjectKind} DataId={t.DataId} Y={t.Position.Y:F1} dist={Player.DistanceTo(t):F1}");
        }
    }
}
