using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using System;
using System.Numerics;

namespace Saucy.Framework;

/// <summary>
/// Follows one Lifestream aethernet hop until the character has really arrived. Lifestream goes idle
/// the moment it clicks the destination, before the loading screen starts, and through that screen the
/// player still reports its old position: a path planned on "Lifestream idle" starts where the
/// character left, and an in-zone hop never reloads the mesh, so vnavmesh keeps chasing it afterwards.
/// Framework thread only. Holds positions and ids, never game objects.
/// </summary>
internal sealed class AethernetHopWatch
{
    internal enum Result
    {
        Waiting,
        Landed,
        NotStarted,
        NoTravel,
        TimedOut
    }

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Minimum idle time before any arrival counts; the loading screen starts a moment after Lifestream goes idle.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan NoTravelTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    // Horizontal only: a shard position can come from a map marker whose Y is 0.
    private const float ArrivalRadius = 8f;
    private const float MinTravelDistance = 10f;

    private readonly string context;
    private readonly string destination;
    private readonly uint originTerritory;
    private readonly uint shardTerritory;
    private readonly Vector3? shardPosition;
    private readonly DateTime issuedUtc;

    private bool seenBusy;
    private bool transitionSeen;
    private DateTime lastActivityUtc;
    private DateTime? idleSinceUtc;
    private Vector3? departure;
    private uint departureTerritory;
    private bool resultLogged;

    /// <summary>Create right after Lifestream accepted the request.</summary>
    public AethernetHopWatch(string context, string destination, uint shardTerritory, Vector3? shardPosition)
    {
        this.context = context;
        this.destination = destination;
        this.shardTerritory = shardTerritory;
        this.shardPosition = shardPosition;
        originTerritory = Svc.ClientState.TerritoryType;
        issuedUtc = DateTime.UtcNow;
        lastActivityUtc = issuedUtc;
        Log($"已交給 Lifestream（目的地在區域 {shardTerritory} {FormatPosition(shardPosition)}）");
    }

    /// <summary>For frames the caller skips while zoning, so a loading screen it never passed on still counts.</summary>
    public void NoteLoading()
    {
        if (!seenBusy)
        {
            return;
        }

        transitionSeen = true;
        lastActivityUtc = DateTime.UtcNow;
    }

    /// <summary>Once per frame. <paramref name="busy"/>: Lifestream, or whatever it drives, is still working.</summary>
    public Result Update(bool busy)
    {
        var now = DateTime.UtcNow;
        uint territory = Svc.ClientState.TerritoryType;
        if (IsLoading() || territory != originTerritory)
        {
            NoteLoading();
        }

        if (busy)
        {
            seenBusy = true;
            lastActivityUtc = now;
            idleSinceUtc = null;
            return Result.Waiting;
        }

        if (!seenBusy)
        {
            return now - issuedUtc > StartTimeout
                ? Report(Result.NotStarted, $"Lifestream {StartTimeout.TotalSeconds:F0} 秒內都沒有開始")
                : Result.Waiting;
        }

        if (idleSinceUtc == null)
        {
            idleSinceUtc = now;
            departure = Player.Available ? Player.Position : null;
            departureTerritory = territory;
            Log($"Lifestream 已閒置，{(transitionSeen ? "已看過" : "還沒看到")}讀取畫面");
        }

        if (!IsSettled())
        {
            return now - lastActivityUtc > StallTimeout
                ? Report(Result.TimedOut, $"Lifestream 閒置後 {StallTimeout.TotalSeconds:F0} 秒角色仍無法操作")
                : Result.Waiting;
        }

        var idleFor = now - idleSinceUtc.Value;
        if (idleFor < SettleDelay)
        {
            return Result.Waiting;
        }

        if (FindArrival(territory) is { } how)
        {
            return Report(Result.Landed, $"{how}、讀取畫面已結束、角色可操作");
        }

        return idleFor > NoTravelTimeout
            ? Report(Result.NoTravel, $"Lifestream 閒置 {NoTravelTimeout.TotalSeconds:F0} 秒仍沒看到讀取畫面，角色也沒有移動")
            : Result.Waiting;
    }

    internal static string DescribeLocation() =>
        $"區域 {Svc.ClientState.TerritoryType}，座標 {FormatPosition(Player.Available ? Player.Position : null)}";

    internal static string FormatPosition(Vector3? position) =>
        position is { } p ? $"<{p.X:F1}, {p.Y:F1}, {p.Z:F1}>" : "?";

    private string? FindArrival(uint territory)
    {
        if (transitionSeen)
        {
            return territory != originTerritory ? "區域已改變" : "看過讀取畫面";
        }

        var position = Player.Position;
        if (shardPosition is { } shard && territory == shardTerritory)
        {
            var toShard = Horizontal(position, shard);
            if (toShard <= ArrivalRadius)
            {
                return $"已在目的地 {toShard:F1} 米內";
            }
        }

        if (departure is { } from && territory == departureTerritory)
        {
            var moved = Horizontal(position, from);
            if (moved >= MinTravelDistance)
            {
                return $"離出發點 {moved:F0} 米";
            }
        }

        return null;
    }

    private Result Report(Result result, string why)
    {
        if (!resultLogged)
        {
            resultLogged = true;
            var outcome = result switch
            {
                Result.Landed => "完成",
                Result.NotStarted => "沒有開始",
                Result.NoTravel => "沒有發生",
                _ => "逾時"
            };
            Log($"{outcome}：{why}，共 {(DateTime.UtcNow - issuedUtc).TotalSeconds:F1} 秒");
        }

        return result;
    }

    private void Log(string message) =>
        Svc.Log.Information($"[Saucy] {context}：都市傳送網前往「{destination}」，{message}；{DescribeLocation()}。");

    private static bool IsLoading() =>
        Svc.Condition[ConditionFlag.BetweenAreas] ||
        Svc.Condition[ConditionFlag.BetweenAreas51] ||
        !Player.Available;

    private static bool IsSettled() =>
        !Svc.Condition[ConditionFlag.BetweenAreas] &&
        !Svc.Condition[ConditionFlag.BetweenAreas51] &&
        Player.Interactable &&
        !Player.IsAnimationLocked;

    private static float Horizontal(Vector3 a, Vector3 b) =>
        Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
}
