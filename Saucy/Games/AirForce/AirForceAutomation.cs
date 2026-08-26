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

    // Repeatedly re-locking the SAME nearest target every cycle (as soon as its short per-target
    // cooldown lapses) starved farther targets of a turn entirely, reported live as a target never
    // getting shot ("有漏打一個目標 注意不要同一點連射 更換目標或等待冷卻再射"). Remember the last
    // one actually fired at and, if any OTHER eligible target exists this pass, prefer it —
    // falling back to re-firing the same one only when it's truly the only option.
    private static ulong lastFiredTargetId;

    // Exposed for the "draw prediction circles" overlay so the avoid radius can be tuned visually
    // instead of by trial-and-error against the slider. DataId is included so it can be labeled
    // on screen — DataId misidentification has bitten this module twice already, so being able to
    // eyeball "which DataId is this thing on screen" without opening the debug tab is worth it.
    public readonly record struct BombCircle(System.Numerics.Vector2 Screen, float Radius, uint DataId);
    public readonly record struct TargetCircle(System.Numerics.Vector2 Screen, float Radius, bool SkippedForBomb, uint DataId);

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

            // Ground-truth DataIds (BombDataId and the target ids below) also match copies of the
            // same object sitting well below the actual playing field ("空軍裝甲 地下物件" — user
            // confirmed the one in the screenshot was far away, not right next to the player, and
            // pointed out it's UNDERGROUND). These are presumably template/pooled instances parked
            // below the map before being moved into play, or despawned instances not yet cleaned
            // up — either way, a real in-play bomb/target is never more than a few meters below the
            // player's own altitude (the ride flies roughly level with them), so anything sitting
            // much lower than that can't be a real target regardless of DataId match.
            const float MaxBelowPlayerY = 15f;

            var bombs = Svc.Objects.OfType<IEventObj>()
                .Where(x => x.DataId == BombDataId && Player.Position.Y - x.Position.Y < MaxBelowPlayerY)
                .Select(x => (Screen: Svc.GameGui.WorldToScreen(x.Position, out var s) ? s : (System.Numerics.Vector2?)null, Dist: Player.DistanceTo(x)))
                .Where(b => b.Screen.HasValue)
                .Select(b => (Screen: b.Screen!.Value, Dist: b.Dist, AvoidRadius: Math.Clamp(
                    C.GoldSaucerGates.AirForceBombAvoidRadius * (ReferenceDistance / Math.Max(b.Dist, 1f)),
                    40f, 500f)))
                .ToArray();
            LastBombCircles = bombs.Select(b => new BombCircle(b.Screen, b.AvoidRadius, BombDataId)).ToArray();

            var targetCircles = new System.Collections.Generic.List<TargetCircle>();
            (IGameObject Obj, System.Numerics.Vector2 Aim)? fallbackCandidate = null;
            var fired = false;

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

                if (Player.Position.Y - x.Position.Y >= MaxBelowPlayerY)
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
                    // A target isn't a single point — it has visible size, and can be hit anywhere
                    // within it, edges included ("目標可打邊邊"). Give it the same distance-scaled
                    // radius formula as the bomb avoid-radius ("範圍和炸彈一樣") instead of treating
                    // it as a point, so a bomb sitting near dead-center doesn't have to force a full
                    // skip — aim at whichever point on the target's edge is farthest from the bomb
                    // instead, and only skip entirely if even that edge point is still threatened.
                    var targetDist = Player.DistanceTo(x);
                    var targetRadius = Math.Clamp(
                        C.GoldSaucerGates.AirForceBombAvoidRadius * (ReferenceDistance / Math.Max(targetDist, 1f)),
                        40f, 500f);

                    var aimPoint = screen;
                    var skippedForBomb = false;
                    foreach (var bomb in bombs)
                    {
                        // A bomb and target can land at nearly the same screen point while sitting
                        // at very different real distances — whichever one is actually CLOSER to
                        // the player is what the shot connects with first (closer object wins along
                        // the same aim line), so a near bomb behind/around a far target is a bigger
                        // threat than the raw 2D screen overlap alone suggests, and a far bomb
                        // behind a near target barely matters at all ("小心同一點連射命中剛出現的
                        // 炸彈...目標和炸彈有遠近區分 近的會先被命中"). Bias the effective avoid
                        // radius by which one is actually nearer in world space.
                        var effectiveBombRadius = bomb.Dist < targetDist ? bomb.AvoidRadius * 1.5f : bomb.AvoidRadius * 0.6f;

                        var toCenter = screen - bomb.Screen;
                        var overlapDist = effectiveBombRadius + targetRadius;
                        if (toCenter.LengthSquared() >= overlapDist * overlapDist)
                        {
                            continue;
                        }

                        var away = toCenter.LengthSquared() > 1f
                            ? System.Numerics.Vector2.Normalize(toCenter)
                            : new System.Numerics.Vector2(1, 0);
                        var edgePoint = screen + (away * targetRadius * 0.85f);

                        if (System.Numerics.Vector2.Distance(edgePoint, bomb.Screen) < effectiveBombRadius)
                        {
                            skippedForBomb = true;
                            break;
                        }

                        aimPoint = edgePoint;
                    }

                    targetCircles.Add(new TargetCircle(screen, targetRadius, skippedForBomb, x.DataId));
                    if (skippedForBomb)
                    {
                        continue;
                    }

                    // Defer re-selecting the exact same target fired at last cycle — keep scanning
                    // for a different eligible one first, only falling back to this one if it turns
                    // out to be the sole option this pass.
                    if (x.GameObjectId == lastFiredTargetId && fallbackCandidate == null)
                    {
                        fallbackCandidate = (x, aimPoint);
                        continue;
                    }

                    AimAndFire(x, aimPoint);
                    fired = true;
                    break;
                }
            }

            if (!fired && fallbackCandidate is { } fallback)
            {
                AimAndFire(fallback.Obj, fallback.Aim);
            }

            LastTargetCircles = [.. targetCircles];
            return;

            void AimAndFire(IGameObject targetObj, System.Numerics.Vector2 aim)
            {
                RideShootingAim.TrySetScreenAim(aim);

                if (targetObj.GameObjectId != lockedTargetId)
                {
                    lockedTargetId = targetObj.GameObjectId;
                    lockedSinceUtc = DateTime.UtcNow;
                    currentLockDelayMs = rng.Next(80, 200);
                }

                var lockedFor = (DateTime.UtcNow - lockedSinceUtc).TotalMilliseconds;
                if (lockedFor >= currentLockDelayMs && EzThrottler.Throttle("Shoot", 60))
                {
                    recentlyFiredAtUtc[targetObj.GameObjectId] = DateTime.UtcNow;
                    lastFiredTargetId = targetObj.GameObjectId;
                    Svc.Framework.RunOnTick(() => RideShootingAim.FireClick(aim), delayTicks: 1);
                }
            }
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
