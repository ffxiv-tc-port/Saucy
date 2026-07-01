using ECommons.GameHelpers;
using ECommons.Throttlers;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
namespace Saucy.LeapOfFaith;

/// <summary>
/// Records the player's position while manually playing Leap of Faith, so a route file can
/// later be built for automated jumping. Not wired into the automation loop yet — this is
/// purely a data-collection tool exposed from the Debug tab.
/// </summary>
internal static class LeapOfFaithRecorder
{
    private const int SampleIntervalMs = 150;

    public readonly record struct RecordedPoint(float ElapsedSeconds, Vector3 Position, float Rotation, bool InGate);

    public static bool IsRecording { get; private set; }

    public static IReadOnlyList<RecordedPoint> Points => points;

    private static readonly List<RecordedPoint> points = [];
    private static DateTime recordingStartUtc;

    public static void StartRecording()
    {
        if (IsRecording)
        {
            return;
        }

        points.Clear();
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

    public static void Clear() => points.Clear();

    private static void OnFrameworkUpdate(IFramework _)
    {
        if (!EzThrottler.Throttle("Saucy.LeapOfFaith.Record", SampleIntervalMs))
        {
            return;
        }

        if (!Player.Available)
        {
            return;
        }

        var elapsed = (float)(DateTime.UtcNow - recordingStartUtc).TotalSeconds;
        points.Add(new RecordedPoint(elapsed, Player.Position, Player.Rotation, GateDirector.IsInGate(Module.GateType.LeapOfFaith)));
    }

    public static string Export()
    {
        var dir = Svc.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"LeapOfFaithRoute_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        var json = JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }
}
