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
/// Infers likely platform positions from OTHER players nearby, rather than trying to race or
/// follow them directly. Every non-falling sample of every nearby player is recorded and points
/// within a small radius are merged into one entry with a running observation count — count acts
/// as the confidence signal ("dense points = safer") since real platforms get walked/landed-on by
/// many players over many samples, while a spot only ever passed through briefly stays low-count.
/// The stability check only requires the player to not be actively falling on THIS sample (not a
/// sustained stillness window), so a jump that briefly lands on a new platform before jumping again
/// still gets recorded — a real landing, even a split-second one, is exactly the kind of point this
/// is meant to capture. Observed points persist to a JSON file across sessions in case the same
/// course layout recurs (Leap of Faith only has a small number of known layout variants).
/// </summary>
internal static class LeapOfFaithPlatformObserver
{
    // A real fall drops several units between 250ms samples; anything less is "not falling" for
    // this sample, whether that's a player standing still, walking, or having just landed a jump.
    private const float FallDropThresholdUnits = 0.3f;
    private const int SampleIntervalMs = 250;
    private const float DedupeRadius = 2.5f;

    // A single falling sample happens on the way down from EVERY normal jump too (that's how
    // jumping works), so it can't by itself mean "fell into the abyss" — some legitimate jumps
    // between platforms also cover a large Y difference in one hop. Only trust it as a real fall
    // once it's SUSTAINED: several consecutive falling samples (longer than any normal jump's
    // descent) covering a large cumulative drop. Confirmed via feedback ("有些路徑高低差很大
    // 確定掉落深淵才刪除") that the old single-sample check was too trigger-happy.
    private const int SustainedFallSampleCount = 3;
    private const float SustainedFallCumulativeDrop = 10f;

    // Some Leap of Faith map variants have no abyss to fall into — failing just drops the player
    // back onto the floor/start platform instead. That shows up as the player's position looping
    // back near an EARLIER point of their own current trail rather than as a sustained fall, so
    // it needs its own check ("掉回平面或是掉回前面的路徑 就刪除").
    private const int LoopBackMinPointsBack = 4;

    public sealed class ObservedPoint
    {
        public Vector3 Position { get; set; }
        public int ObservationCount { get; set; } = 1;
    }

    private static readonly Dictionary<uint, float> lastY = [];
    private static readonly List<ObservedPoint> observedPlatforms = [];
    private static string? loadedFilePath;
    private static DateTime lastSampleUtc;

    // A sample only fails the single-sample falling check (>0.3 units drop) once real fall speed
    // has built up — the 1-2 samples right as a player steps off an edge, or hovers near a jump's
    // apex, still read as "not falling" and were getting committed as platform points floating
    // over the abyss ("平台標記 會在深淵建立平台"). Hold each sample back for a few ticks instead
    // of committing it immediately; if a real fall streak starts before the holdback window
    // elapses, discard the held-back samples instead of stamping them as solid ground.
    private const int PreCommitHoldbackSamples = 3;
    private static readonly Dictionary<uint, Queue<Vector3>> pendingPoints = [];

    // Full paths of other nearby players, for the "其他玩家路徑" overlay — separate from the
    // deduped ObservedPlatforms points above, this keeps per-player ordered history so it can be
    // drawn as a line. A trail is discarded (not drawn) the moment its owner is seen falling —
    // "失足掉入深淵的路徑刪除" — since a path that ends in a fall was not a successful route and
    // would otherwise draw a misleading line straight off a platform into nothing.
    private const int MaxTrailPointsPerPlayer = 60;
    private const float TeleportJumpDistance = 15f;
    private static readonly Dictionary<uint, List<Vector3>> otherPlayerTrails = [];
    private static readonly Dictionary<uint, DateTime> otherPlayerTrailLastUpdateUtc = [];
    private static readonly Dictionary<uint, int> consecutiveFallSamples = [];
    private static readonly Dictionary<uint, float> fallStreakStartY = [];

    public static IReadOnlyList<ObservedPoint> ObservedPlatforms => observedPlatforms;

    public static IReadOnlyCollection<IReadOnlyList<Vector3>> OtherPlayerTrails => otherPlayerTrails.Values;

    // "不能沿著5~10秒前 有人安全走過的路線嗎" — a trail still being updated right now is proof
    // the player walking it hasn't fallen off it in the last few seconds, which is a much stronger
    // safety signal than the aggregated dot cloud (which mixes in old data from anywhere, any time).
    // Follows the trail step-by-step toward whichever recorded point is nearest the player, then the
    // next point further along the SAME trail in the direction of progress — not just "nearest point
    // overall" like the dot-cloud fallback, so the character actually shadows a real recent route
    // instead of possibly cutting across it.
    private const float GuideTrailMaxDistanceToPlayer = 10f;

    // Matches LeapOfFaithAutomation.JumpSegmentLengthThreshold — a gap this large between two
    // consecutive real samples of the SAME trail could only have been a real jump.
    private const float JumpSegmentLengthThreshold = 3.5f;

    public static Vector3? TryGetGuideWaypoint(Vector3 playerPos, Vector3 finalTarget, TimeSpan maxAge, out bool isLongHop)
    {
        isLongHop = false;
        var now = DateTime.UtcNow;
        var playerProgress = Vector3.Distance(playerPos, finalTarget);

        Vector3? best = null;
        var bestProgress = playerProgress;
        var bestHopLength = 0f;

        foreach (var (id, trail) in otherPlayerTrails)
        {
            if (trail.Count < 2 || !otherPlayerTrailLastUpdateUtc.TryGetValue(id, out var updated) || now - updated > maxAge)
            {
                continue;
            }

            // Find the trail point nearest the player, then look at the next few points along the
            // SAME trail (in recorded order — i.e. the direction that player actually walked) for
            // one that makes real progress toward the target.
            var nearestIndex = -1;
            var nearestDist = GuideTrailMaxDistanceToPlayer;
            for (var i = 0; i < trail.Count; i++)
            {
                var dist = Vector3.Distance(playerPos, trail[i]);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIndex = i;
                }
            }

            if (nearestIndex < 0)
            {
                continue;
            }

            for (var i = nearestIndex + 1; i < trail.Count && i <= nearestIndex + 5; i++)
            {
                var candidate = trail[i];
                var candidateProgress = Vector3.Distance(candidate, finalTarget);
                if (candidateProgress >= bestProgress)
                {
                    continue;
                }

                bestProgress = candidateProgress;
                best = candidate;
                bestHopLength = Vector3.Distance(trail[i - 1], candidate);
            }
        }

        if (best is { } waypoint)
        {
            isLongHop = bestHopLength >= JumpSegmentLengthThreshold;
            return waypoint;
        }

        return null;
    }

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
                var loaded = JsonSerializer.Deserialize<List<ObservedPoint>>(
                    File.ReadAllText(loadedFilePath), new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
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

            var id = (uint)obj.GameObjectId;
            var y = obj.Position.Y;

            var singleSampleFalling = lastY.TryGetValue(id, out var previousY) && previousY - y > FallDropThresholdUnits;
            lastY[id] = y;

            var confirmedFalling = TrackFallStreak(id, y, singleSampleFalling);

            UpdatePlayerTrail(id, obj.Position, confirmedFalling);

            if (singleSampleFalling)
            {
                // A fall is starting (or continuing) — the last few held-back samples from this
                // player may include the edge/void step that led into it, so discard rather than
                // commit them as platform points. Also break the line-segment chain here so the
                // next real commit doesn't get connected back across whatever gap they fell into.
                pendingPoints.Remove(id);
                lastCommittedPoint.Remove(id);
                continue;
            }

            if (!pendingPoints.TryGetValue(id, out var pending))
            {
                pending = new Queue<Vector3>();
                pendingPoints[id] = pending;
            }

            pending.Enqueue(obj.Position);
            if (pending.Count > PreCommitHoldbackSamples)
            {
                var committed = pending.Dequeue();
                RecordOrIncrement(committed);
                OnPointCommitted(id, committed);
            }
        }
    }

    /// <summary>Updates the per-player consecutive-fall streak and returns true only once that
    /// streak has been sustained long enough, and dropped far enough, to be confident this is a
    /// real fall into the abyss rather than the descent phase of a normal jump.</summary>
    private static bool TrackFallStreak(uint id, float y, bool singleSampleFalling)
    {
        if (!singleSampleFalling)
        {
            consecutiveFallSamples.Remove(id);
            fallStreakStartY.Remove(id);
            return false;
        }

        if (!consecutiveFallSamples.TryGetValue(id, out var streak))
        {
            fallStreakStartY[id] = y;
        }
        consecutiveFallSamples[id] = streak + 1;

        var cumulativeDrop = fallStreakStartY[id] - y;
        return consecutiveFallSamples[id] >= SustainedFallSampleCount && cumulativeDrop >= SustainedFallCumulativeDrop;
    }

    private static void UpdatePlayerTrail(uint id, Vector3 pos, bool confirmedFalling)
    {
        if (confirmedFalling)
        {
            // Confirmed falling — this run didn't land where it was headed, so the whole
            // in-progress trail was leading to a fall. Discard it rather than draw a line that
            // ends by walking off a platform into the abyss.
            otherPlayerTrails.Remove(id);
            otherPlayerTrailLastUpdateUtc.Remove(id);
            return;
        }

        if (!otherPlayerTrails.TryGetValue(id, out var trail))
        {
            trail = [];
            otherPlayerTrails[id] = trail;
        }

        // A teleport-sized jump (respawned at a checkpoint after falling elsewhere, or the object
        // just came into range) means this sample isn't a continuation of the existing trail —
        // start fresh instead of drawing a line across the gap.
        if (trail.Count > 0 && Vector3.Distance(trail[^1], pos) > TeleportJumpDistance)
        {
            trail.Clear();
        }

        // No-abyss map variant: failing drops the player back onto the floor/start instead of
        // off the map, which shows up as looping back near an earlier point of this same trail
        // rather than as a sustained fall. But some routes legitimately double back on themselves
        // — a safe detour to grab a cactuar trophy before continuing — so only discard the loop
        // if it ISN'T explained by a nearby cactuar/finish; otherwise it's a deliberate route, keep
        // it ("除非是跟仙人掌重疊，否則刪除").
        if (trail.Count > LoopBackMinPointsBack && !IsNearCactuarOrFinish(pos))
        {
            for (var i = 0; i < trail.Count - LoopBackMinPointsBack; i++)
            {
                if (Vector3.Distance(trail[i], pos) < DedupeRadius)
                {
                    trail.Clear();
                    break;
                }
            }
        }

        if (trail.Count == 0 || Vector3.Distance(trail[^1], pos) > 0.5f)
        {
            trail.Add(pos);
            otherPlayerTrailLastUpdateUtc[id] = DateTime.UtcNow;
            if (trail.Count > MaxTrailPointsPerPlayer)
            {
                trail.RemoveAt(0);
            }
        }
    }

    private const float CactuarOrFinishProximity = 4f;

    private static bool IsNearCactuarOrFinish(Vector3 pos)
    {
        foreach (var obj in Svc.Objects)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventObj)
            {
                continue;
            }

            if ((obj.BaseId == LeapOfFaithAutomation.FinishDataId || LeapOfFaithAutomation.CactuarDataIds.Contains(obj.BaseId)) &&
                Vector3.Distance(obj.Position, pos) < CactuarOrFinishProximity)
            {
                return true;
            }
        }

        return false;
    }

    private static void RecordOrIncrement(Vector3 pos)
    {
        foreach (var existing in observedPlatforms)
        {
            if (Vector3.Distance(existing.Position, pos) < DedupeRadius)
            {
                existing.ObservationCount++;
                return;
            }
        }

        observedPlatforms.Add(new ObservedPoint { Position = pos, ObservationCount = 1 });
    }

    // Filled "platform plane" polygons (convex-hull clustering, recomputed every draw call) were
    // removed entirely per user feedback ("不要畫綠色平台了 不准 又影響效能") — the clustering was
    // both a real performance cost every frame AND, since a cluster only needs points close to
    // ANY existing member, could span a real gap and paint over the abyss. Not bringing it back as
    // an option; only the (cheaper, temporally-chained — see below) thick-line segments remain.
    public const float PathSegmentWidth = 1f;

    // A segment only connects two points that were actually consecutive samples FROM THE SAME
    // PLAYER's walk (see lastCommittedPoint below) — never nearest-neighbor across the whole
    // accumulated point cloud. Nearest-neighbor linking was tried first and produced segments
    // straight across the abyss ("深淵中有許多藍色粗線"): two points on separate platforms can
    // easily be within a few meters of each other in the XZ plane with nothing real connecting
    // them. Chaining by actual footsteps can't make that mistake — if two samples are consecutive
    // for one real player, there was necessarily solid ground (or a real jump) between them.
    private const float MaxSegmentDistance = 6f;
    private const int MaxPathSegments = 1500;

    public sealed class PathSegment
    {
        public Vector3 A { get; set; }
        public Vector3 B { get; set; }
        public int ObservationCount { get; set; } = 1;
    }

    private static readonly Dictionary<uint, Vector3?> lastCommittedPoint = [];
    private static readonly List<PathSegment> pathSegments = [];

    public static IReadOnlyList<PathSegment> ComputeLinearSegments() => pathSegments;

    // "登高 顯示平台標記 改為整合其他玩家多次路線的優化路線繪製" — a segment walked by many
    // different players (or the same player across many runs) is much more likely to be a real,
    // safe route than one only ever seen once. Rather than drawing every raw segment as an
    // undifferentiated cloud, merge repeats of "essentially the same stretch" into a single entry
    // and bump its ObservationCount instead of adding a duplicate line — the draw side then only
    // renders segments that clear a minimum confirmation count, turning the noisy point/segment
    // cloud into one optimized, cross-player-confirmed route.
    private const float SegmentDedupeRadius = 1.5f;

    private static bool TryMergeIntoExisting(Vector3 a, Vector3 b)
    {
        foreach (var existing in pathSegments)
        {
            var sameDirection = Vector3.Distance(existing.A, a) < SegmentDedupeRadius && Vector3.Distance(existing.B, b) < SegmentDedupeRadius;
            var reverseDirection = Vector3.Distance(existing.A, b) < SegmentDedupeRadius && Vector3.Distance(existing.B, a) < SegmentDedupeRadius;
            if (sameDirection || reverseDirection)
            {
                existing.ObservationCount++;
                return true;
            }
        }

        return false;
    }

    private static void OnPointCommitted(uint playerId, Vector3 pos)
    {
        if (lastCommittedPoint.TryGetValue(playerId, out var last) && last is { } lastPos &&
            Vector3.Distance(lastPos, pos) <= MaxSegmentDistance && !TryMergeIntoExisting(lastPos, pos))
        {
            pathSegments.Add(new PathSegment { A = lastPos, B = pos });
            if (pathSegments.Count > MaxPathSegments)
            {
                pathSegments.RemoveAt(0);
            }
        }

        lastCommittedPoint[playerId] = pos;
    }

    /// <summary>Wipes both the persisted dot cloud and the in-memory line segments — for clearing
    /// out stale data recorded before a detection fix (e.g. points from before the fall-holdback
    /// or same-player-chain fixes), which otherwise lingers forever since dots only ever get added,
    /// never re-validated against newer logic.</summary>
    public static void Clear()
    {
        observedPlatforms.Clear();
        pathSegments.Clear();
        lastCommittedPoint.Clear();
        pendingPoints.Clear();

        if (loadedFilePath != null && File.Exists(loadedFilePath))
        {
            try
            {
                File.Delete(loadedFilePath);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[Saucy] Failed to delete Leap of Faith platform observations file");
            }
        }
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
