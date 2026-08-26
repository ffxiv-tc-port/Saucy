using Dalamud.Hooking;
using ECommons.GameHelpers;
using System;
using System.Runtime.InteropServices;
using System.Numerics;
namespace Saucy.Framework;

/// <summary>
/// Direct movement-input override, mirroring BossModReborn's approach
/// (BossMod/Framework/MovementOverride.cs, confirmed working on this same TW client via its
/// tc-7.15 branch) — hooks the game's own movement-input-read function ("RMIWalk") and writes a
/// desired direction directly into its output, instead of simulating WASD keypresses via
/// SendInput. Adopted after repeated precision/reliability failures with key simulation for
/// Cliffhanger's jump-point steering ("鍵盤模擬 現在完全不能用...vnavmesh 接近後逼近也是基於鍵盤
///模擬 反而會亂跑"). Deliberately simpler than BossModReborn's version: no legacy-movement-mode
/// or misdirection-status handling, since neither applies to steering toward a jump takeoff point.
/// </summary>
internal static unsafe class PreciseMovement
{
    // Same signature BossModReborn hooks for RMIWalk (BossMod/Framework/MovementOverride.cs) —
    // confirmed working against this client via BossModReborn's own TW-targeted branch, so it
    // should resolve the same address here.
    private const string RmiWalkSignature = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";

    // BossModReborn calls these two (never hooks them, just reads their result) before deciding
    // whether this frame is even safe to inject movement into — its own TODO notes "sometimes
    // [the game] skips reading input, and returning something non-zero breaks stuff". Missing this
    // check turned out to be exactly why writes "succeeded" (100% of hook calls overridden per our
    // own counters) yet the character never actually moved: the write can land during a frame the
    // game itself has already decided not to sample/apply movement input at all.
    private const string RmiWalkIsInputEnabled1Signature = "E8 ?? ?? ?? ?? 84 C0 75 10 38 43 3C";
    private const string RmiWalkIsInputEnabled2Signature = "E8 ?? ?? ?? ?? 84 C0 75 03 88 47 3F";

    private delegate void RmiWalkDelegate(nint self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    private delegate bool RmiWalkIsInputEnabledDelegate(nint self);

    private static Hook<RmiWalkDelegate>? hook;
    private static RmiWalkIsInputEnabledDelegate? isInputEnabled1;
    private static RmiWalkIsInputEnabledDelegate? isInputEnabled2;
    private static Vector3? desiredDirection;

    public static bool IsReady => hook != null;

    // Diagnostics for "hook says ready but movement still does nothing" reports — RMIWalk gets
    // called multiple times per frame with different purposes (see bAdditiveUnk below); if we're
    // overriding a call whose result gets discarded, TotalCalls will climb but nothing visibly
    // moves, which is a very different failure mode from the hook never firing at all.
    public static int TotalCalls { get; private set; }
    public static int OverriddenCalls { get; private set; }
    public static int SkippedAdditive { get; private set; }
    public static int SkippedNoDirection { get; private set; }
    public static int SkippedRealInput { get; private set; }
    public static int SkippedInputDisabled { get; private set; }

    public static bool IsInputCheckReady => isInputEnabled1 != null && isInputEnabled2 != null;
    public static string? InputCheckError { get; private set; }

    public static void Init()
    {
        if (hook != null)
        {
            return;
        }

        try
        {
            var address = Svc.SigScanner.ScanText(RmiWalkSignature);
            hook = Svc.Hook.HookFromAddress<RmiWalkDelegate>(address, RmiWalkDetour);
            hook.Enable();
            Svc.Log.Information($"[Saucy] PreciseMovement hooked RMIWalk @ 0x{address:X}");
        }
        catch (Exception ex)
        {
            // Silent failure here (hook stays null, IsReady false) previously showed up only as
            // "every move button does nothing" with zero other symptom ("現在三個鈕都沒用了") —
            // print to chat too, not just the dev log, since that's far more likely to actually be
            // seen when this fails.
            Svc.Log.Warning(ex, "[Saucy] Failed to hook RMIWalk for precise movement");
            Svc.Chat.PrintError($"[Saucy] 精準移動 hook 掛載失敗，相關自動移動/測試按鈕不會有反應：{ex.Message}");
            return;
        }

        // Resolved separately from the hook itself — a failure here previously showed up as "every
        // hook call gets overridden (100%), yet the character never actually moves at all"
        // ("移動函式呼叫次數也在增加...但角色不動"): our write was landing during frames the game
        // itself had already decided not to sample/apply movement input for, per BossModReborn's own
        // TODO ("sometimes [the game] skips reading input, and returning something non-zero breaks
        // stuff"). Without checking these, we can't tell which frames are actually safe to write.
        try
        {
            var enabled1Addr = Svc.SigScanner.ScanText(RmiWalkIsInputEnabled1Signature);
            var enabled2Addr = Svc.SigScanner.ScanText(RmiWalkIsInputEnabled2Signature);
            isInputEnabled1 = Marshal.GetDelegateForFunctionPointer<RmiWalkIsInputEnabledDelegate>(enabled1Addr);
            isInputEnabled2 = Marshal.GetDelegateForFunctionPointer<RmiWalkIsInputEnabledDelegate>(enabled2Addr);
            Svc.Log.Information("[Saucy] PreciseMovement resolved both RMIWalkIsInputEnabled functions");
        }
        catch (Exception ex)
        {
            InputCheckError = ex.Message;
            Svc.Log.Warning(ex, "[Saucy] Failed to resolve RMIWalkIsInputEnabled functions");
            Svc.Chat.PrintError($"[Saucy] 精準移動輸入啟用判斷掛載失敗，移動可能寫入了卻不會生效：{ex.Message}");
        }
    }

    public static void Shutdown()
    {
        hook?.Disable();
        hook?.Dispose();
        hook = null;
    }

    /// <summary>Set every tick with the world-space direction to move (Y ignored); pass null to
    /// stop overriding. Never fights the player's own manual input — only applies while the game
    /// itself reads zero real input this frame.</summary>
    public static void SetDesiredDirection(Vector3? direction) => desiredDirection = direction;

    private static bool IsLegacyMoveMode() => Svc.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;

    /// <summary>Same computation as BossModReborn's Camera.Update() (BossMod/Framework/Camera.cs) —
    /// derives the camera's horizontal facing angle from the active render camera's view matrix,
    /// without needing a per-frame Update() driver of our own.</summary>
    /// <summary>
    /// CameraManager.GetActiveCamera() is a ClientStructs <c>[MemberFunction]</c>, and
    /// CameraManager.Instance() just forwards to Control.Instance(), a <c>[StaticAddress]</c>. When
    /// either signature stops resolving they <b>throw</b> InvalidOperationException (InteropGenerator's
    /// ThrowHelper.ThrowNullAddress) rather than returning null - so a null check on Instance() was
    /// never a guard against a broken signature. This is reached from the RMIWalk detour, so a stale
    /// signature would mean a managed exception thrown inside a detour on every frame. Check the
    /// resolved addresses up front and skip the whole camera path instead.
    /// </summary>
    private static bool CameraApiResolved
        => FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Addresses.Instance.Value != 0
        && FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Addresses.GetActiveCamera.Value != 0;

    private static float GetCameraAzimuth()
    {
        var cameraManager = CameraApiResolved
            ? FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance()
            : null;
        var camera = cameraManager != null ? cameraManager->GetActiveCamera() : null;
        var renderCamera = camera != null ? camera->SceneCamera.RenderCamera : null;
        if (renderCamera == null)
        {
            return Player.Rotation;
        }

        var view = renderCamera->ViewMatrix;
        // Legacy mode's forward reference is the camera's facing rotated 180° (BossModReborn:
        // MovementOverride.cs:204, `CameraAzimuth.Radians() + 180f.Degrees()`).
        return MathF.Atan2(view.M13, view.M33) + MathF.PI;
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. The override
    // logic therefore runs inside a try, and the degraded behaviour is "don't override" - Original has
    // already run, so the player's own movement input passes through intact.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions - most importantly the
    // InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw
    // when their signature stops resolving after a game patch.
    private static long detourErrors;
    private static DateTime lastDetourErrorLog = DateTime.MinValue;

    public static long DetourErrors => detourErrors;

    private static void OnDetourError(Exception ex)
    {
        ++detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 2.
        var now = DateTime.UtcNow;
        if (now - lastDetourErrorLog < TimeSpan.FromSeconds(30))
        {
            return;
        }

        lastDetourErrorLog = now;
        Svc.Log.Information($"[Saucy] PreciseMovement 覆寫時發生例外，本次不覆寫、讓遊戲原本的移動輸入通過（累計 {detourErrors} 次）：{ex}");
    }

    /// <summary>Throttled note that the RMIWalk hook vanished mid-call. Uses the same 30 second
    /// throttle window as <see cref="OnDetourError"/> so a teardown race can never flood the log.</summary>
    private static void OnDetourHookGone()
    {
        var now = DateTime.UtcNow;
        if (now - lastDetourErrorLog < TimeSpan.FromSeconds(30))
        {
            return;
        }

        lastDetourErrorLog = now;
        Svc.Log.Information("[Saucy] PreciseMovement 的 RMIWalk hook 已在呼叫途中被卸載，本次不呼叫原始函式也不覆寫移動。");
    }

    private static void RmiWalkDetour(nint self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        // Shutdown() sets the hook field back to null while this detour may still be executing
        // (in-flight call). The `!` only silences the compiler - at run time it is still a bare
        // dereference, and a null field throws NullReferenceException straight back into native
        // game code with the original never called. Snapshot once and use only the local.
        var h = hook;
        if (h == null)
        {
            // Bail out completely rather than only skipping the original: without the original
            // call the game has not sampled movement input this frame, so sumLeft/sumForward
            // still hold stale values and ApplyOverride must not act on them.
            OnDetourHookGone();
            return;
        }

        h.OriginalDisposeSafe(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        try
        {
            ApplyOverride(self, sumLeft, sumForward, bAdditiveUnk);
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }

    private static void ApplyOverride(nint self, float* sumLeft, float* sumForward, byte bAdditiveUnk)
    {
        TotalCalls++;

        // BossModReborn only treats a call as the "real" input-gathering one when bAdditiveUnk==0
        // (its movementAllowed check) — RMIWalk gets invoked multiple times per frame for different
        // purposes, and overriding an additive/secondary call's output can just get discarded or
        // overwritten again by the real call later the same frame, which would look exactly like
        // "hook is ready but movement still does nothing" ("移動沒有反應" despite IsReady=True).
        if (bAdditiveUnk != 0)
        {
            SkippedAdditive++;
            return;
        }

        // Mirrors BossModReborn's movementAllowed check — if either says input sampling isn't
        // enabled this frame, the game has already decided not to read/apply movement input at
        // all, so writing here would be silently discarded (or worse, per its own TODO comment
        // about "skips reading input... returning something non-zero breaks stuff").
        if (isInputEnabled1 != null && isInputEnabled2 != null && (!isInputEnabled1(self) || !isInputEnabled2(self)))
        {
            SkippedInputDisabled++;
            return;
        }

        if (desiredDirection is not { } dir || !Player.Available)
        {
            SkippedNoDirection++;
            return;
        }

        // Never override real user input — if the player is actually pressing something this
        // frame, leave it alone entirely. Small epsilon rather than an exact !=0 check, in case
        // deceleration/momentum leaves a tiny nonzero residual for a frame or two after real input
        // actually stops.
        if (MathF.Abs(*sumLeft) > 0.01f || MathF.Abs(*sumForward) > 0.01f)
        {
            SkippedRealInput++;
            return;
        }

        var flat = new Vector3(dir.X, 0, dir.Z);
        var flatLength = flat.Length();
        if (flatLength < 0.0001f)
        {
            return;
        }

        // Previously always normalized to a unit vector — full running speed all the way to the
        // target, with no slow-down on approach, which reliably overshot past the arrival radius
        // before the next tick's distance check could catch it ("移動位置不精準 跑過頭"). Callers
        // can now pass a shorter vector to request slower movement (e.g. scaled down near the
        // target); only clamp the magnitude down to at most 1 (full speed), never scale it back up.
        var speedScale = MathF.Min(flatLength, 1f);
        var direction = flat / flatLength;

        // "立即移動會亂跑 繞地圖繞一圈後慢慢修正回原點" — always using Player.Rotation as the
        // reference frame is only correct if the player's client is in FFXIV's "Standard" movement
        // mode. In "Legacy" mode, W/A/D are relative to the CAMERA's facing instead of the
        // character's body facing (BossModReborn's ForwardMovementDirection() switches between the
        // two based on this exact same client setting — see MovementOverride.cs:204). Using the
        // wrong reference produces a systematically-rotated direction every tick — bounded but wrong
        // enough to send the character wandering in a wide loop before things happen to reconverge.
        var rotation = IsLegacyMoveMode() ? GetCameraAzimuth() : Player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation));
        var right = new Vector3(forward.Z, 0, -forward.X);

        *sumForward = Vector3.Dot(forward, direction) * speedScale;
        *sumLeft = Vector3.Dot(right, direction) * speedScale;
        OverriddenCalls++;

        // Diagnostic only — lets callers compare "what we actually commanded this frame" against
        // the character's real measured displacement, to tell apart "our own code is throttling the
        // command" from "the game engine itself is slowing the character down despite a full-
        // magnitude command" (e.g. resolving a commanded direction as mostly strafe rather than
        // forward run, which is slower in FFXIV regardless of what we send).
        LastCommandedForward = *sumForward;
        LastCommandedLeft = *sumLeft;
    }

    public static float LastCommandedForward { get; private set; }
    public static float LastCommandedLeft { get; private set; }
    public static float LastCommandedMagnitude => MathF.Sqrt((LastCommandedForward * LastCommandedForward) + (LastCommandedLeft * LastCommandedLeft));
}
