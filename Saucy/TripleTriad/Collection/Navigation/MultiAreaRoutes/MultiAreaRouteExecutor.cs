using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetheryteSheet = Lumina.Excel.Sheets.Aetheryte;

namespace Saucy.TripleTriad;

internal static class AethernetShardLookup
{
#pragma warning disable IDE0028 // StringComparer cannot use collection expressions without losing comparer semantics
    private static readonly Dictionary<string, uint> Cache = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028

    public static uint Resolve(string shardName)
    {
        if (string.IsNullOrEmpty(shardName))
        {
            return 0;
        }

        if (Cache.TryGetValue(shardName, out var cached))
        {
            return cached;
        }

        var sheet = Svc.Data.GetExcelSheet<AetheryteSheet>();
        if (sheet == null)
        {
            return 0;
        }

        foreach (var row in sheet)
        {
            if (row.IsAetheryte)
            {
                continue;
            }

            var name = row.AethernetName.ValueNullable?.Name.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name.Equals(shardName, StringComparison.OrdinalIgnoreCase))
            {
                Cache[shardName] = row.RowId;
                return row.RowId;
            }
        }

        return 0;
    }
}

internal static unsafe class MultiAreaRouteExecutor
{
    private const uint GeneralActionDismount = 23;

    /// <summary>傳送確認框的認領長度：蓋過「等換區」那一步的 30 秒上限。</summary>
    private static readonly TimeSpan RouteYesnoClaim = TimeSpan.FromSeconds(30);

    public static bool TryBeginRoute(
        MultiAreaRoute route,
        MultiAreaRouteContext context,
        out RouteExecution execution,
        out string? beginMessage)
    {
        execution = null!;
        beginMessage = null;

        var startIndex = GetFirstIncompleteStepIndex(route, context);
        if (startIndex < 0)
        {
            return false;
        }

        execution = new()
        {
            Route = route, Context = context, StepIndex = startIndex, StepStartedUtc = DateTime.UtcNow
        };

        var step = route.Steps[startIndex];
        if (step.Kind == MultiAreaRouteStepKind.Teleport)
        {
            var aetheryteId = step.AetheryteId;
            if (route.Name == DomanEnclaveRoute.Route.Name)
            {
                aetheryteId = DomanEnclaveRoute.ResolveYanxiaEntryTeleportAetheryteId();
            }

            if (aetheryteId == 0)
            {
                return false;
            }

            if (AetheryteHelper.IsPlayerInAetheryteTerritory(aetheryteId))
            {
                beginMessage = "[Saucy] " + "Entering ??, then moving to the NPC.".Loc(route.Name);
                return true;
            }

            if (!Lifestream.TryTeleport(aetheryteId))
            {
                Svc.Chat.PrintError("[Saucy] " + "Lifestream could not teleport for ??.".Loc(route.Name));
                return false;
            }

            beginMessage =
                "[Saucy] " + "Teleporting for ??, then moving to the NPC.".Loc(route.Name);
            return true;
        }

        beginMessage =
            "[Saucy] " + "Entering ??, then moving to the NPC.".Loc(route.Name);
        return true;
    }

    public static bool Tick(RouteExecution execution)
    {
        if (execution.Failed || execution.StepIndex >= execution.Route.Steps.Count)
        {
            return !execution.Failed && execution.StepIndex >= execution.Route.Steps.Count;
        }

        var step = execution.Route.Steps[execution.StepIndex];
        var complete = step.Kind switch
        {
            MultiAreaRouteStepKind.Teleport => TickTeleport(execution.Route, step),
            MultiAreaRouteStepKind.Aethernet => TickAethernet(step, execution),
            MultiAreaRouteStepKind.Mount => TickMount(),
            MultiAreaRouteStepKind.MoveTo => TickMoveTo(step, execution),
            MultiAreaRouteStepKind.Interact => TickInteract(step, execution),
            MultiAreaRouteStepKind.SelectYesno => TickSelectYesno(execution),
            MultiAreaRouteStepKind.WaitForZone => TickWaitForZone(execution),
            var _ => false
        };

        if (execution.Failed)
        {
            LogStep(execution, step, "失敗，路線停止");
            return false;
        }

        if (!complete)
        {
            return false;
        }

        LogStep(execution, step, "完成");
        execution.StepIndex++;
        execution.StepActionStarted = false;
        execution.SteerResumed = false;
        execution.Steer?.Reset();
        execution.AethernetHop = null;
        execution.StepStartedUtc = DateTime.UtcNow;
        return execution.StepIndex >= execution.Route.Steps.Count;
    }

    public static bool IsTeleportStepComplete(RouteExecution execution, MultiAreaRouteStep teleportStep)
    {
        if (execution.StepIndex != 0 || teleportStep.Kind != MultiAreaRouteStepKind.Teleport)
        {
            return false;
        }

        return TickTeleport(execution.Route, teleportStep);
    }

    private static uint ResolveTeleportAetheryteId(MultiAreaRoute route, MultiAreaRouteStep step) =>
        route.Name == DomanEnclaveRoute.Route.Name
            ? DomanEnclaveRoute.ResolveYanxiaEntryTeleportAetheryteId()
            : step.AetheryteId;

    private static int GetFirstIncompleteStepIndex(MultiAreaRoute route, MultiAreaRouteContext context)
    {
        for (var i = 0; i < route.Steps.Count; i++)
        {
            var step = route.Steps[i];
            if (step.Kind == MultiAreaRouteStepKind.Teleport &&
                Svc.ClientState.TerritoryType == GetAetheryteTerritoryId(ResolveTeleportAetheryteId(route, step)))
            {
                continue;
            }

            if (step.Kind == MultiAreaRouteStepKind.WaitForZone &&
                route.IsInDestinationTerritory(Svc.ClientState.TerritoryType, context.TargetTerritoryId))
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool TickTeleport(MultiAreaRoute route, MultiAreaRouteStep step)
    {
        if (Lifestream.IsBusyNow())
        {
            return false;
        }

        if (!Player.Interactable || IsBetweenAreas() || Player.IsAnimationLocked)
        {
            return false;
        }

        var aetheryteId = ResolveTeleportAetheryteId(route, step);
        var territoryId = GetAetheryteTerritoryId(aetheryteId);
        return territoryId != 0 && Svc.ClientState.TerritoryType == territoryId;
    }

    private static bool TickAethernet(MultiAreaRouteStep step, RouteExecution execution)
    {
        var shardId = ResolveShardId(step);
        if (shardId == 0)
        {
            Svc.Chat.PrintError("[Saucy] " + "Could not resolve aethernet shard \"??\".".Loc(step.AethernetShardName));
            execution.Failed = true;
            return false;
        }

        if (!execution.StepActionStarted)
        {
            // The shard can sit in another zone (Foundation -> The Last Vigil), whose coordinates mean nothing here.
            var shardTerritory = GetAetheryteTerritoryId(shardId);
            var shardPos = AetheryteHelper.GetAethernetShardWorldPosition(shardId);
            if (shardPos != null &&
                Svc.ClientState.TerritoryType == shardTerritory &&
                Vector3.Distance(Player.Position, shardPos.Value) <= 10f)
            {
                return true;
            }

            if (Lifestream.IsBusyNow() || !Player.Interactable || IsBetweenAreas() || Player.IsAnimationLocked)
            {
                return false;
            }

            var shardName = step.AethernetShardName ?? AetheryteHelper.GetAethernetShardName(shardId);
            if (!Lifestream.TryAethernetViaLiCommand(shardName))
            {
                Svc.Chat.PrintError("[Saucy] " + "Lifestream could not aethernet for ??.".Loc(execution.Route.Name));
                execution.Failed = true;
                return false;
            }

            execution.StepActionStarted = true;
            execution.StepStartedUtc = DateTime.UtcNow;
            execution.AethernetHop = new AethernetHopWatch(
                $"路線「{execution.Route.Name}」", shardName ?? shardId.ToString(), shardTerritory, shardPos);
            return false;
        }

        if (execution.AethernetHop is not { } hop)
        {
            Svc.Chat.PrintError("[Saucy] " + "Lifestream could not aethernet for ??.".Loc(execution.Route.Name));
            execution.Failed = true;
            return false;
        }

        switch (hop.Update(Lifestream.IsBusyNow()))
        {
            case AethernetHopWatch.Result.Waiting:
                return false;

            case AethernetHopWatch.Result.Landed:
                // The old two-second floor stays as a minimum.
                return DateTime.UtcNow - execution.StepStartedUtc > TimeSpan.FromSeconds(2);

            case AethernetHopWatch.Result.TimedOut:
                Svc.Chat.PrintError("[Saucy] " + "Navigation timed out.".Loc());
                execution.Failed = true;
                return false;

            default:
                // Walking on would plan in the wrong zone or from the wrong spot, so stop instead.
                Svc.Chat.PrintError("[Saucy] " + "Lifestream could not aethernet for ??.".Loc(execution.Route.Name));
                execution.Failed = true;
                return false;
        }
    }

    private static uint ResolveShardId(MultiAreaRouteStep step)
    {
        if (!string.IsNullOrEmpty(step.AethernetShardName))
        {
            return AethernetShardLookup.Resolve(step.AethernetShardName);
        }

        return step.AetheryteId;
    }

    private static bool TickSelectYesno(RouteExecution execution)
    {
        if (!SelectYesnoHelper.TryGetVisible(out var yesno))
        {
            return false;
        }

        if (!IsExpectedRouteYesno(execution, yesno))
        {
            return false;
        }

        if (!EzThrottler.Throttle("SaucyRouteYesno"))
        {
            return false;
        }

        if (!SelectYesnoHelper.PressYes(yesno))
        {
            return false;
        }

        return true;
    }

    private static bool TickMount()
    {
        if (!TravelMountHelper.CanMountInCurrentTerritory())
        {
            return true;
        }

        if (!Player.Interactable || IsBetweenAreas())
        {
            return false;
        }

        // 騎不上去時不要卡在這一步(這一步沒有逾時,卡住就是永遠卡住):把它當作完成,
        // 後續的 MoveTo 會經由 ResolveUseFlying 的未騎乘降級自動改走地面路徑。
        return TravelMountHelper.TryMount() != MountAttemptResult.InProgress;
    }

    private static bool TickMoveTo(MultiAreaRouteStep step, RouteExecution execution)
    {
        if (!execution.StepActionStarted)
        {
            if (!Player.Interactable || IsBetweenAreas())
            {
                return false;
            }

            if (!Vnavmesh.IsNavReady())
            {
                Vnavmesh.TryEnsureNavMeshLoading();
                return false;
            }

            if (TickManualSteer(execution))
            {
                return false;
            }

            execution.SteerResumed = false;
            var approachPoint = TriadMapNavigation.ResolveNpcPathPoint(ResolveApproachPoint(step));
            // 已在範圍內時 TryMoveTo 不送路徑就回 true，不能拿後面「vnav 沒在動」判失敗；別人的路徑還在動就等它停。
            if (step.Range > 0f && Vnavmesh.IsWithinHorizontalRange(approachPoint, step.Range))
            {
                return !Vnavmesh.IsMoving() && TryCompleteMoveToStep(step);
            }

            var fly = TravelMountHelper.ResolveUseFlying(step.Fly);
            if (!Vnavmesh.TryMoveTo(approachPoint, fly, step.Range) &&
                fly &&
                !Vnavmesh.TryMoveTo(approachPoint, false, step.Range))
            {
                Svc.Chat.PrintError("[Saucy] " + "vnavmesh could not start movement for this route step.".Loc());
                execution.Failed = true;
                return false;
            }

            if (!Vnavmesh.IsMoving() && !Vnavmesh.IsPathfindInProgress())
            {
                Svc.Chat.PrintError("[Saucy] " + "vnavmesh could not start movement for this route step.".Loc());
                execution.Failed = true;
                return false;
            }

            execution.StepActionStarted = true;
            execution.NavOwned = true;
            return false;
        }

        if (!Player.Interactable || Player.IsAnimationLocked)
        {
            return false;
        }

        if (step.ArrivalObjectDataId != 0)
        {
            var target = FindObject(step.ArrivalObjectDataId);
            if (target != null && Vnavmesh.TickOwnArrival(target.Position, step.Range, ref execution.NavOwned))
            {
                return TryCompleteMoveToStep(step);
            }
        }
        else
        {
            var approachPoint = TriadMapNavigation.ResolveNpcPathPoint(ResolveApproachPoint(step));
            if (Vnavmesh.TickOwnArrival(approachPoint, step.Range, ref execution.NavOwned))
            {
                return TryCompleteMoveToStep(step);
            }
        }

        // 送過路徑之後只在使用者接手、停手之後重送一次；導航自己停在半路照舊不重送。
        if (TickManualSteer(execution) || execution.Failed)
        {
            return false;
        }

        if (execution.SteerResumed)
        {
            execution.SteerResumed = false;
            execution.StepActionStarted = false;
        }

        return false;
    }

    /// <summary>回 true＝讓位中（這一幀不要送路徑）；放棄時把整條路線標成失敗。</summary>
    private static bool TickManualSteer(RouteExecution execution)
    {
        execution.Steer ??= new ManualSteerYield($"路線「{execution.Route.Name}」");
        switch (execution.Steer.Tick(execution.NavOwned, busy: false, out var paused))
        {
            case ManualSteerYield.Result.Hold:
                return true;
            case ManualSteerYield.Result.Resumed:
                execution.SteerResumed = true;
                execution.StepStartedUtc += paused;
                return false;
            case ManualSteerYield.Result.GaveUp:
                Svc.Chat.PrintError("[Saucy] " + "Stopped navigation: you kept moving on your own for ?? seconds.".Loc(global::Saucy.Framework.GoldSaucer.ManualSteerWatch.MaxYieldMs / 1000));
                execution.Failed = true;
                return true;
            default:
                return false;
        }
    }

    private static bool TryCompleteMoveToStep(MultiAreaRouteStep step)
    {
        if (step.Fly && !TravelMountHelper.TryDismount())
        {
            return false;
        }

        return true;
    }

    private static Vector3 ResolveApproachPoint(MultiAreaRouteStep step)
    {
        if (step.ArrivalObjectDataId != 0)
        {
            var target = FindObject(step.ArrivalObjectDataId);
            if (target != null)
            {
                return target.Position;
            }
        }

        if (step.ApproachMapId != 0)
        {
            var fromMap = TriadMapNavigation.GetWorldPositionFromMap(
                step.ApproachMapId, step.ApproachMapX, step.ApproachMapY);
            if (fromMap != null)
            {
                return fromMap.Value;
            }
        }

        return step.Position;
    }

    private static bool TickInteract(MultiAreaRouteStep step, RouteExecution execution)
    {
        if (step.DismountFirst && !TryDismount())
        {
            return false;
        }

        var target = FindObject(step.ObjectDataId);
        if (target == null)
        {
            return false;
        }

        if (Vector3.Distance(Player.Position, target.Position) > step.Range)
        {
            return false;
        }

        if (Svc.Targets.Target?.BaseId != step.ObjectDataId)
        {
            Svc.Targets.Target = target;
            return false;
        }

        if (!EzThrottler.Throttle("SaucyRouteInteract"))
        {
            return false;
        }

        ClaimRouteYesno(execution);
        TargetSystem.Instance()->InteractWithObject((GameObject*)target.Address, false);
        return true;
    }

    /// <summary>互動叫出傳送確認框之前先請 YesAlready 暫停按是／否框（它在 PostSetup 當下就按）；問不到就把那一扇交給它或使用者。</summary>
    private static void ClaimRouteYesno(RouteExecution execution)
    {
        var now = DateTime.UtcNow;
        if (YesAlready.TryPauseSelectYesno((int)RouteYesnoClaim.TotalMilliseconds))
        {
            execution.YesnoClaimUntilUtc = now + RouteYesnoClaim;
            return;
        }

        execution.YesnoClaimUntilUtc = null;
        Svc.Log.Information($"[Saucy] 路線「{execution.Route.Name}」：無法確認 YesAlready 不會按傳送確認框，這一扇 Saucy 不按");
    }

    /// <summary>只按這條路線預期的傳送確認框：句子要等於 Warp 表裡「傳送進本路線目的地」的那句，而且 YesAlready 確定不會同時按。</summary>
    private static bool IsExpectedRouteYesno(RouteExecution execution, AddonSelectYesno* yesno)
    {
        if (execution.YesnoClaimUntilUtc is not { } claimUntil || DateTime.UtcNow >= claimUntil ||
            !SelectYesnoHelper.IsRouteSafeYesno(yesno))
        {
            return false;
        }

        if (SelectYesnoHelper.IsWarpPromptInto(yesno, execution.Route.ArrivalTerritoryIds))
        {
            return true;
        }

        if (!execution.UnexpectedYesnoLogged)
        {
            execution.UnexpectedYesnoLogged = true;
            Svc.Log.Information($"[Saucy] 路線「{execution.Route.Name}」：確認框不是預期的傳送確認，Saucy 不按：{SelectYesnoHelper.GetPrompt(yesno)}");
        }

        return false;
    }

    private static bool TickWaitForZone(RouteExecution execution)
    {
        if (!Player.Interactable || Lifestream.IsBusyNow())
        {
            return false;
        }

        if (execution.Route.IsInDestinationTerritory(
            Svc.ClientState.TerritoryType, execution.Context.TargetTerritoryId))
        {
            return true;
        }

        if (IsBetweenAreas())
        {
            return false;
        }

        if (SelectYesnoHelper.TryGetVisible(out var yesno) &&
            IsExpectedRouteYesno(execution, yesno) &&
            EzThrottler.Throttle("SaucyRouteYesno"))
        {
            SelectYesnoHelper.PressYes(yesno);
            return false;
        }

        if (TalkHelper.TryAdvance("SaucyRouteTalk"))
        {
            return false;
        }

        if (DateTime.UtcNow - execution.StepStartedUtc > TimeSpan.FromSeconds(30))
        {
            Svc.Chat.PrintError("[Saucy] " + "Did not arrive in ?? after zone transition.".Loc(execution.Route.Name));
            execution.Failed = true;
        }

        return false;
    }

    private static bool TryDismount()
    {
        if (!Svc.Condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (Player.IsAnimationLocked || !EzThrottler.Throttle("SaucyRouteDismount"))
        {
            return false;
        }

        Chat.ExecuteGeneralAction(GeneralActionDismount);
        return false;
    }

    private static IGameObject? FindObject(uint dataId) =>
        Svc.Objects
            .Where(o => o.IsTargetable && o.BaseId == dataId)
            .OrderBy(o => Vector3.Distance(Player.Position, o.Position))
            .FirstOrDefault();

    private static uint GetAetheryteTerritoryId(uint aetheryteId)
    {
        var row = Svc.Data.GetExcelSheet<AetheryteSheet>()?.GetRowOrDefault(aetheryteId);
        return row?.Territory.RowId ?? 0;
    }

    private static bool IsBetweenAreas() => Svc.Condition[ConditionFlag.BetweenAreas];

    /// <summary>One Information line per step change, never per frame.</summary>
    private static void LogStep(RouteExecution execution, MultiAreaRouteStep step, string outcome) =>
        Svc.Log.Information(
            $"[Saucy] 路線「{execution.Route.Name}」第 {execution.StepIndex + 1}/{execution.Route.Steps.Count} 步（{step.Kind}）{outcome}；{AethernetHopWatch.DescribeLocation()}。");

    internal sealed class RouteExecution
    {
        public AethernetHopWatch? AethernetHop;
        public required MultiAreaRouteContext Context;
        public bool Failed;
        public required MultiAreaRoute Route;

        /// <summary>vnavmesh 現在走的是路線移動步驟自己送的路徑：送路徑成功才設；vnavmesh 停了由 <see cref="TriadMapNavigation"/>.Tick 開頭清。</summary>
        public bool NavOwned;

        public ManualSteerYield? Steer;
        public bool SteerResumed;
        public bool StepActionStarted;
        public int StepIndex;
        public DateTime StepStartedUtc;

        /// <summary>YesAlready 確定不會按確認框的期限；null＝沒拿到，路線不按確認框。</summary>
        public DateTime? YesnoClaimUntilUtc;

        public bool UnexpectedYesnoLogged;
    }

    internal sealed class MultiAreaRouteContext
    {
        public required Vector3 Destination;
        public required MapLinkPayload Location;
        public required uint TargetTerritoryId;
    }
}
