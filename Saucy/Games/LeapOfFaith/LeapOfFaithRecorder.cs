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
namespace Saucy.LeapOfFaith;

/// <summary>
/// Records the player's position and nearby GameObjects while manually playing Leap of Faith.
/// The platform layout and cactuar trophy positions are randomized per run, so a single fixed
/// route can't be replayed — this instead collects raw samples (player path + nearby object
/// kind/DataId/name/position) so the actual platforms/trophies can be identified from real data
/// and detected live at runtime, rather than guessed. Not wired into the automation loop yet —
/// this is purely a data-collection tool exposed from the Debug tab.
/// </summary>
internal static class LeapOfFaithRecorder
{
    private const int PlayerSampleIntervalMs = 150;
    private const int ObjectSampleIntervalMs = 500;
    private const float ObjectScanRadius = 60f;

    // A teleport-sized jump between consecutive 150ms samples (normal movement/falling can't
    // cover this far) means the player fell off and got returned to a checkpoint/start — treat
    // that as the boundary between attempts.
    private const float RespawnJumpDistance = 15f;

    public readonly record struct RecordedPoint(
        float ElapsedSeconds, Vector3 Position, float Rotation, bool InGate, bool LikelyRespawn, int AttemptIndex);

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
        if (!Player.Available)
        {
            return;
        }

        var elapsed = (float)(DateTime.UtcNow - recordingStartUtc).TotalSeconds;

        if (EzThrottler.Throttle("Saucy.LeapOfFaith.RecordPlayer", PlayerSampleIntervalMs))
        {
            var pos = Player.Position;
            var likelyRespawn = lastPosition.HasValue && Vector3.Distance(pos, lastPosition.Value) > RespawnJumpDistance;
            if (likelyRespawn)
            {
                attemptIndex++;
            }
            lastPosition = pos;

            points.Add(new RecordedPoint(elapsed, pos, Player.Rotation, GateDirector.IsInGate(Module.GateType.LeapOfFaith),
                likelyRespawn, attemptIndex));
        }

        if (EzThrottler.Throttle("Saucy.LeapOfFaith.RecordObjects", ObjectSampleIntervalMs))
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

                objects.Add(new RecordedObject(elapsed, obj.DataId, obj.ObjectKind, obj.Name.TextValue, obj.Position, dist));
            }
        }
    }

    public static string Export()
    {
        var dir = Svc.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var routePath = Path.Combine(dir, $"LeapOfFaithRoute_{stamp}.json");
        File.WriteAllText(routePath, JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

        var objectsPath = Path.Combine(dir, $"LeapOfFaithObjects_{stamp}.json");
        // Dedupe by DataId/Kind/Name for a readable summary; full per-sample distances stay in the raw list.
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
