using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Saucy.Framework;

internal static unsafe partial class GateScheduleAutomation
{
    private static bool ReturnsAfterGate(Module.GateType gate) => gate switch
    {
        Module.GateType.AnyWayTheWindBlows =>
            C.GoldSaucerGates.WindBlowsReturnToCoordinatorAfterGate && C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows),
        Module.GateType.SliceIsRight =>
            C.GoldSaucerGates.SliceIsRightReturnToCoordinatorAfterGate && C.IsModuleEnabled(ModuleNames.SliceIsRight),
        Module.GateType.Cliffhanger =>
            C.GoldSaucerGates.CliffhangerReturnToCoordinator && C.IsModuleEnabled(ModuleNames.Cliffhanger),
        _ => false
    };

    private static unsafe void ObserveGateEnd()
    {
        if (GateDirector.InSaucer && GateDirector.IsPlayerOnStage())
        {
            var gate = GateDirector.GetCurrentGate();
            afterGateWatch = !afterGateStoppedThisGate && ReturnsAfterGate(gate) ? gate : Module.GateType.None;
            afterGateOffStageSince = 0;
            afterGateEndedSince = 0;
            return;
        }

        afterGateStoppedThisGate = false;
        if (afterGateWatch == Module.GateType.None)
        {
            return;
        }

        if (!GateDirector.InSaucer)
        {
            ForgetGateEndWatch();
            return;
        }

        var now = Environment.TickCount64;
        var hasDirector = GateDirector.TryGetDirector(out var director);
        var flags = hasDirector ? director->Flags : default;
        var directorGate = hasDirector ? (Module.GateType)director->GateType : Module.GateType.None;
        var gateStopped = !hasDirector || flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer.GFateDirectorFlag.IsFinished) ||
                          directorGate != afterGateWatch;
        if (afterGateOffStageSince == 0)
        {
            afterGateOffStageSince = now;
            Svc.Log.Information($"[Saucy] GATE 結束後走回：{afterGateWatch} 已不在場上（導演{(hasDirector ? $"在，Flags={flags}，GateType={directorGate}" : "不在")}）；" +
                                (gateStopped ? $"GATE 已結束，{AfterGateSettleMs / 1000} 秒後走回最近的活動解說員" : "GATE 還在跑，等它結束"));
        }

        if (!gateStopped)
        {
            afterGateEndedSince = 0;
            return;
        }

        if (afterGateEndedSince == 0)
        {
            afterGateEndedSince = now;
        }

        if (now - afterGateEndedSince < AfterGateSettleMs)
        {
            return;
        }

        var ended = afterGateWatch;
        ForgetGateEndWatch();
        if (!ReturnsAfterGate(ended))
        {
            return;
        }

        if (GateEmergencyHold.IsHeld)
        {
            GateEmergencyHold.NoteSkipped("GATE 結束後走回活動解說員");
            return;
        }

        StartReturnToCoordinator("GATE 結束後走回", $"{ended} 結束，離開場地", now);
    }

    private static void StartReturnToCoordinator(string label, string why, long now)
    {
        returnLabel = label;
        var nearest = NearestRecordedCoordinator();
        if (nearest == null)
        {
            Svc.Log.Information($"[Saucy] {label}：沒有已記錄的活動解說員位置，不移動");
            return;
        }

        afterGateTarget = nearest;
        afterGateStartedAt = now;
        afterGateNextTryAt = 0;
        afterGateNavOwned = false;
        afterGateSteer.Reset();
        Svc.Log.Information($"[Saucy] {label}：{why}走向活動解說員（{nearest.X:F1}, {nearest.Y:F1}, {nearest.Z:F1}）");
    }

    private static bool IsCliffhangerWalkOffStage() =>
        !C.GoldSaucerGates.CliffhangerAutoMovement && GateDirector.GetCurrentGate() == Module.GateType.Cliffhanger &&
        (afterJoinArmedAt != 0 || (afterGateTarget != null && returnLabel == AfterJoinLabel));

    private static bool ReturnsAfterJoin(Module.GateType gate) =>
        gate == Module.GateType.Cliffhanger && C.GoldSaucerGates.CliffhangerReturnToCoordinator && C.IsModuleEnabled(ModuleNames.Cliffhanger);

    /// <summary>報名後每幀的決策（純函式）：確認框與對話要先結束；GATE 報名時段交給排程；太久沒空就不走。</summary>
    internal static AfterJoinDecision AfterJoinStep(long sinceArmedMs, bool occupied, bool joinWindow)
    {
        if (sinceArmedMs > AfterJoinGiveUpMs)
        {
            return AfterJoinDecision.GiveUp;
        }

        return sinceArmedMs < AfterJoinSettleMs || occupied || joinWindow ? AfterJoinDecision.Wait : AfterJoinDecision.Start;
    }

    private static void TickAfterJoinReturn()
    {
        if (afterJoinArmedAt == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!ReturnsAfterJoin(Module.GateType.Cliffhanger) || afterGateTarget != null)
        {
            afterJoinArmedAt = 0;
            return;
        }

        if (GateEmergencyHold.IsHeld)
        {
            afterJoinArmedAt = 0;
            GateEmergencyHold.NoteSkipped("報名後走回活動解說員");
            return;
        }

        switch (AfterJoinStep(now - afterJoinArmedAt, GateNpcNavigation.IsOccupied(), IsJoinWindow || manualJoinUntilUtc != null))
        {
            case AfterJoinDecision.GiveUp:
                afterJoinArmedAt = 0;
                Svc.Log.Information($"[Saucy] {AfterJoinLabel}：{AfterJoinGiveUpMs / 1000} 秒內一直在對話或報名時段中，不走回");
                return;
            case AfterJoinDecision.Start:
                afterJoinArmedAt = 0;
                StartReturnToCoordinator(AfterJoinLabel, "報名完成、對話結束，", now);
                return;
        }
    }

    private static void ForgetGateEndWatch()
    {
        afterGateWatch = Module.GateType.None;
        afterGateOffStageSince = 0;
        afterGateEndedSince = 0;
    }

    private static GateNpcSpot? NearestRecordedCoordinator()
    {
        if (!Player.Available)
        {
            return null;
        }

        var playerPos = Player.Position;
        return C.GoldSaucerGates.EventCoordinatorSpots
            .Where(s => s.Recorded)
            .OrderBy(s => Vector3.Distance(playerPos, new Vector3(s.X, s.Y, s.Z)))
            .FirstOrDefault();
    }

    private static void TickAfterGateReturn()
    {
        if (afterGateTarget is not { } spot)
        {
            return;
        }

        if (GateEmergencyHold.IsHeld)
        {
            CancelAfterGateReturn("急停");
            return;
        }

        if (IsJoinWindow || manualJoinUntilUtc != null)
        {
            CancelAfterGateReturn("GATE 報名時段開始，交給排程");
            return;
        }

        if (!Vnavmesh.IsInstalled || !Player.Available)
        {
            return;
        }

        var now = Environment.TickCount64;
        var destination = new Vector3(spot.X, spot.Y, spot.Z);
        if (Vnavmesh.IsWithinHorizontalRange(destination, GateNpcNavigation.CloseRange))
        {
            CancelAfterGateReturn("已走到活動解說員旁，交回排程");
            return;
        }

        switch (afterGateSteer.Tick(afterGateNavOwned, busy: false, out var pausedFor))
        {
            case ManualSteerYield.Result.Hold:
                return;
            case ManualSteerYield.Result.GaveUp:
                // 使用者還在自己走：不停他的移動，只結束這一趟。
                afterGateNavOwned = false;
                CancelAfterGateReturn("你自己移動太久，不再接續");
                Svc.Chat.Print($"[Saucy] 已停止{returnLabel}活動解說員：你自己移動超過 {ManualSteerWatch.MaxYieldMs / 1000} 秒。");
                return;
            case ManualSteerYield.Result.Resumed:
                afterGateStartedAt += (long)pausedFor.TotalMilliseconds;
                break;
        }

        if (now - afterGateStartedAt > AfterGateWalkLimitMs)
        {
            CancelAfterGateReturn($"{AfterGateWalkLimitMs / 1000} 秒內沒走到");
            return;
        }

        if (Vnavmesh.IsMoving() || now < afterGateNextTryAt)
        {
            return;
        }

        afterGateNextTryAt = now + AfterGateRetryMs;
        if (Vnavmesh.TryMoveTo(destination, false, GateNpcNavigation.CloseRange))
        {
            afterGateNavOwned = true;
        }
    }

    /// <summary>取消「GATE 結束後走回」（含還在等結束的那一段），停下自己送的路徑；<paramref name="reason"/> 為 null 時不寫 log。回傳原本有沒有在跑。只在 framework 執行緒呼叫。</summary>
    public static bool CancelAfterGateReturn(string? reason)
    {
        var onStageWatch = afterGateWatch != Module.GateType.None && afterGateOffStageSince == 0;
        var active = IsReturningAfterGate || (reason != null && (onStageWatch || afterJoinArmedAt != 0));
        if (reason != null && onStageWatch)
        {
            afterGateStoppedThisGate = true;
        }

        if (reason != null)
        {
            ForgetGateEndWatch();
            afterJoinArmedAt = 0;
        }

        if (afterGateTarget == null)
        {
            if (active && reason != null)
            {
                Svc.Log.Information($"[Saucy] GATE 結束後走回：取消等待（{reason}）");
            }

            return active;
        }

        if (afterGateNavOwned && Vnavmesh.IsInstalled && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopOwnPath();
        }

        afterGateTarget = null;
        afterGateNavOwned = false;
        afterGateSteer.Reset();
        if (reason != null)
        {
            Svc.Log.Information($"[Saucy] {returnLabel}：結束（{reason}）");
        }

        return true;
    }
}
