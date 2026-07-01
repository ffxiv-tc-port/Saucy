using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
namespace Saucy.LeapOfFaith;

/// <summary>
/// Infers likely platform positions from OTHER players nearby who currently look stable (not
/// falling), rather than trying to race or follow them directly. A player whose Y position holds
/// steady for a short window is almost certainly standing on a platform, which is a decent
/// low-cost hint about where solid ground is even without real collision data. Observed points
/// persist to a JSON file across sessions in case the same course layout recurs (Leap of Faith
/// only has a small number of known layout variants).
/// </summary>
internal static class LeapOfFaithPlatformObserver
{
    private const float StableYToleranceUnits = 0.05f;
    private const int StableSampleCount = 4;
    private const int SampleIntervalMs = 250;
    private const float DedupeRadius = 2.5f;

    private static readonly Dictionary<uint, Queue<float>> yHistory = [];
    private static readonly List<Vector3> observedPlatforms = [];
    private static string? loadedFilePath;
    private static DateTime lastSampleUtc;

    public static IReadOnlyList<Vector3> ObservedPlatforms => observedPlatforms;

    public static void EnsureLoaded()
    {
        if (loadedFilePath != null)
        {
            return;
        }

        loadedFilePath = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "LeapOfFaithPlatforms.json");
        try
        {
            if (File.Exists(loadedFilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<Vector3>>(
                    File.ReadAllText(loadedFilePath), new JsonSerializerOptions { IncludeFields = true });
                if (loaded != null)
                {
                    observedPlatforms.AddRange(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[Saucy] Failed to load Leap of Faith platform observations");
        }
    }

    public static void Observe()
    {
        EnsureLoaded();

        if ((DateTime.UtcNow - lastSampleUtc).TotalMilliseconds < SampleIntervalMs)
        {
            return;
        }
        lastSampleUtc = DateTime.UtcNow;

        var selfId = Player.Available ? Player.Object?.GameObjectId : null;

        foreach (var obj in Svc.Objects)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.Player || obj.GameObjectId == selfId)
            {
                continue;
            }

            if (!yHistory.TryGetValue((uint)obj.GameObjectId, out var history))
            {
                history = new Queue<float>();
                yHistory[(uint)obj.GameObjectId] = history;
            }

            history.Enqueue(obj.Position.Y);
            while (history.Count > StableSampleCount)
            {
                history.Dequeue();
            }

            if (history.Count < StableSampleCount)
            {
                continue;
            }

            var min = history.Min();
            var max = history.Max();
            if (max - min > StableYToleranceUnits)
            {
                continue; // still moving vertically (jumping/falling) — not a reliable platform hint.
            }

            RecordIfNew(obj.Position);
        }
    }

    private static void RecordIfNew(Vector3 pos)
    {
        foreach (var existing in observedPlatforms)
        {
            if (Vector3.Distance(existing, pos) < DedupeRadius)
            {
                return;
            }
        }

        observedPlatforms.Add(pos);
    }

    public static void Save()
    {
        if (loadedFilePath == null || observedPlatforms.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(loadedFilePath)!);
            File.WriteAllText(loadedFilePath, JsonSerializer.Serialize(
                observedPlatforms, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[Saucy] Failed to save Leap of Faith platform observations");
        }
    }
}
