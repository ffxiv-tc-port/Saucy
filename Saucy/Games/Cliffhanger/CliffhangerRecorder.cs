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
        if (!Player.Available || !GateDirector.IsInGate(Module.GateType.Cliffhanger))
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
                GateDirector.IsInGate(Module.GateType.Cliffhanger), likelyRespawn, attemptIndex));
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

                objects.Add(new RecordedObject(elapsed, obj.DataId, obj.ObjectKind, obj.Name.TextValue, obj.Position, dist));
            }
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
