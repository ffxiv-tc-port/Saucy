using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
namespace Saucy.Cliffhanger;

/// <summary>
/// Records the player's position and nearby GameObjects (bombs, chocobo chicks, etc.) while
/// manually playing Cliffhanger (搶救小鳥大作戰). Unlike Leap of Faith, the route here is fixed
/// per the user's description, so a single recorded run should be directly replayable — this tool
/// captures that reference run so the real route/obstacle DataIds can be identified from real data
/// instead of guessed. Not wired into automation yet — purely a data-collection tool from the
/// Debug tab, mirroring LeapOfFaithRecorder.
/// </summary>
internal static class CliffhangerRecorder
{
    private const int PlayerSampleIntervalMs = 150;
    private const int ObjectSampleIntervalMs = 300;
    private const float ObjectScanRadius = 60f;
    private const float RespawnJumpDistance = 15f;

    public readonly record struct RecordedPoint(
        float ElapsedSeconds, Vector3 Position, float Rotation, bool InGate, bool LikelyRespawn, int AttemptIndex, bool WasJumping);

    public readonly record struct RecordedObject(
        float ElapsedSeconds, uint DataId, ObjectKind Kind, string Name, Vector3 Position, float DistanceToPlayer);

    public static bool IsRecording { get; private set; }

    public static IReadOnlyList<RecordedPoint> Points => points;

    public static IReadOnlyList<RecordedObject> Objects => objects;

    private static readonly List<RecordedPoint> points = [];
    private static readonly List<RecordedObject> objects = [];
    private static DateTime recordingStartUtc;
    private static Vector3? lastPosition;
    private static int attemptIndex;

    public static void StartRecording()
    {
        if (IsRecording)
        {
            return;
        }

        points.Clear();
        objects.Clear();
        lastPosition = null;
        attemptIndex = 0;
        recordingStartUtc = DateTime.UtcNow;
        IsRecording = true;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    public static void StopRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        Svc.Framework.Update -= OnFrameworkUpdate;
    }

    public static void Clear()
    {
        points.Clear();
        objects.Clear();
    }

    private static void OnFrameworkUpdate(IFramework _)
    {
        // "其實是在大廣場裡/測試模式下跑的" — gating recording purely on the real GATE state meant
        // a manual test-mode run (TestRunActive, used everywhere else in this session for testing
        // outside the actual :00/:20/:40 window) silently recorded nothing at all, with no visible
        // error — it just produced an empty export. Accept the same "real gate OR test mode" OR
        // condition CliffhangerAutomation itself uses everywhere else.
        var inGate = GateDirector.IsInGate(Module.GateType.Cliffhanger) || CliffhangerAutomation.TestRunActive;
        if (!Player.Available || !inGate)
        {
            return;
        }

        var elapsed = (float)(DateTime.UtcNow - recordingStartUtc).TotalSeconds;

        if (EzThrottler.Throttle("Saucy.Cliffhanger.RecordPlayer", PlayerSampleIntervalMs))
        {
            var pos = Player.Position;
            var likelyRespawn = lastPosition.HasValue && Vector3.Distance(pos, lastPosition.Value) > RespawnJumpDistance;
            if (likelyRespawn)
            {
                attemptIndex++;
            }
            lastPosition = pos;

            points.Add(new RecordedPoint(elapsed, pos, Player.Rotation,
                GateDirector.IsInGate(Module.GateType.Cliffhanger), likelyRespawn, attemptIndex,
                Svc.Condition[ConditionFlag.Jumping]));
        }

        if (EzThrottler.Throttle("Saucy.Cliffhanger.RecordObjects", ObjectSampleIntervalMs))
        {
            var playerPos = Player.Position;
            foreach (var obj in Svc.Objects)
            {
                if (obj == null)
                {
                    continue;
                }

                var dist = Vector3.Distance(obj.Position, playerPos);
                if (dist > ObjectScanRadius)
                {
                    continue;
                }

                objects.Add(new RecordedObject(elapsed, obj.BaseId, obj.ObjectKind, obj.Name.TextValue, obj.Position, dist));
            }
        }
    }

    public readonly record struct ReplayWaypoint(Vector3 Position, bool JumpHere);

    /// <summary>
    /// Builds a walkable/jumpable waypoint list from the most recently recorded attempt (highest
    /// AttemptIndex — i.e. whatever the user played last before stopping, per "我手動跑一次(包含
    /// 跳躍) 你照路徑試試"). Since the course is fixed per-run, replaying a real successful manual
    /// run is far more reliable than any live heuristic (vnavmesh can't cross the gap at all here,
    /// and there's no navmesh-independent floor data to steer by otherwise).
    /// </summary>
    // Spacing must stay comfortably larger than CliffhangerAutomation's arrival radii — with the
    // old 1.5m spacing, waypoints could sit closer together than a 2m arrival radius, letting
    // walking speed alone "arrive" at several waypoints in one frame and constantly snap the
    // steering direction to whatever's next, which read as a heavy stutter/interruption while
    // moving ("移動時的中斷感還是很重"). Wider spacing means each waypoint gets an actual straight
    // run before the next direction change.
    public static List<ReplayWaypoint>? BuildReplayRoute(float minWaypointSpacing = 3f)
    {
        if (points.Count < 2)
        {
            // Nothing recorded this session (e.g. plugin reloaded after recording/exporting) —
            // fall back to the most recently exported route file on disk instead of giving up.
            TryLoadLatestExportedRoute();
        }

        if (points.Count < 2)
        {
            return null;
        }

        var bestAttempt = points.Max(p => p.AttemptIndex);
        var attemptPoints = points.Where(p => p.AttemptIndex == bestAttempt).OrderBy(p => p.ElapsedSeconds).ToList();
        if (attemptPoints.Count < 2)
        {
            return null;
        }

        // A real jump-heavy run can have several jumps only a fraction of a meter apart in quick
        // succession ("前三跳的間隔很短" — confirmed live: 3 consecutive jumps only 0.6-0.8m apart).
        // The normal minWaypointSpacing downsampling (3m) would let a single "jumpPending" flag
        // silently absorb ALL of them into just one eventual waypoint, losing 2 of the 3 real
        // takeoffs entirely. Force a waypoint at the exact moment each jump BEGINS (the
        // WasJumping false→true transition — the last real ground contact before liftoff),
        // bypassing the normal spacing gate, so every distinct jump keeps its own waypoint no
        // matter how close together they are. Ordinary running still downsamples normally.
        const float MinJumpWaypointSpacing = 0.3f;
        var waypoints = new List<ReplayWaypoint> { new(attemptPoints[0].Position, false) };
        var lastAdded = attemptPoints[0].Position;
        var wasJumpingPrev = attemptPoints[0].WasJumping;

        for (var i = 1; i < attemptPoints.Count; i++)
        {
            var curr = attemptPoints[i];
            var isJumpRisingEdge = curr.WasJumping && !wasJumpingPrev;
            wasJumpingPrev = curr.WasJumping;

            if (isJumpRisingEdge && Vector3.Distance(lastAdded, curr.Position) >= MinJumpWaypointSpacing)
            {
                waypoints.Add(new ReplayWaypoint(curr.Position, true));
                lastAdded = curr.Position;
                continue;
            }

            if (Vector3.Distance(lastAdded, curr.Position) < minWaypointSpacing)
            {
                continue;
            }

            waypoints.Add(new ReplayWaypoint(curr.Position, false));
            lastAdded = curr.Position;
        }

        return waypoints;
    }

    /// <summary>Public so the Debug tab can recover the last exported route into Points after a
    /// plugin reload — otherwise "匯出路線 JSON" stays disabled (Points empty) even though a real
    /// recording from earlier this play session already exists on disk ("匯出按鈕不可用").</summary>
    public static void TryLoadLatestExportedRoute()
    {
        try
        {
            var dir = Svc.PluginInterface.GetPluginConfigDirectory();
            var latest = Directory.GetFiles(dir, "CliffhangerRoute_*.json")
                .OrderByDescending(f => f)
                .FirstOrDefault();
            if (latest == null)
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<RecordedPoint>>(
                File.ReadAllText(latest), new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
            if (loaded is { Count: > 1 })
            {
                points.Clear();
                points.AddRange(loaded);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[Saucy] Failed to load exported Cliffhanger route");
        }
    }

    public static string Export()
    {
        var dir = Svc.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var routePath = Path.Combine(dir, $"CliffhangerRoute_{stamp}.json");
        File.WriteAllText(routePath, JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

        var objectsPath = Path.Combine(dir, $"CliffhangerObjects_{stamp}.json");
        var distinctSummary = objects
            .GroupBy(o => (o.DataId, o.Kind, o.Name))
            .Select(g => new
            {
                g.Key.DataId, g.Key.Kind, g.Key.Name, Samples = g.Count(), FirstSeen = g.Min(o => o.ElapsedSeconds),
                FirstPosition = g.OrderBy(o => o.ElapsedSeconds).First().Position
            })
            .OrderBy(o => o.Kind).ThenBy(o => o.DataId)
            .ToList();
        File.WriteAllText(objectsPath, JsonSerializer.Serialize(
            new { Summary = distinctSummary, Raw = objects },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

        return $"{routePath}\n{objectsPath}";
    }
}
