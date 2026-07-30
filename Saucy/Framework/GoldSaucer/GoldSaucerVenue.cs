using Lumina.Excel.Sheets;
using Saucy.TripleTriad;
using System;
using System.Collections.Generic;
using System.Numerics;
using LuminaLevel = Lumina.Excel.Sheets.Level;
namespace Saucy.Framework.GoldSaucer;

/// <summary>
/// Sheet-derived map of the Gold Saucer: every *static* activity NPC's position/name plus the six
/// internal aethernet shards.
///
/// Nothing here hardcodes display text — NPC names come from ENpcResident and aethernet names from
/// Aetheryte/PlaceName at runtime, so they render correctly on the TC client (and any other) without
/// a translation step. Positions come from the Level sheet instead of user recordings, so a fresh
/// character/install can navigate immediately with zero setup.
///
/// ⚠️ This deliberately covers only NPCs that HAVE a Level row. The GATE registration NPCs
/// (仙人掌怪導覽員 1016306, 傲慢的咒術士 1010476, 保鑣的小弟 1031796, 束手無策的女性/少女 1010473/
/// 1010447) exist in ENpcResident with title "G.A.T.E.事件" but have **no Level rows at all** —
/// verified against the TC 7.20 EXD dump — because they are spawned dynamically per GATE. Their
/// positions genuinely cannot be resolved from sheets, which is why GateNpcSpot's user-recorded
/// coordinates remain the right mechanism for those and are left untouched.
/// </summary>
internal static class GoldSaucerVenue
{
    public const uint TerritoryId = 144;

    /// <summary>Aetheryte row 62 = 金碟遊樂園, and it is also the hub of AethernetGroup 5 (the
    /// Saucer's internal network, rows 63-68).</summary>
    public const uint AetheryteId = 62;

    /// <summary>Level.Type discriminator for "this row positions an ENpc".</summary>
    private const byte LevelTypeENpc = 8;

    private static Dictionary<uint, Vector3>? npcPositions;
    private static List<AethernetStop>? aethernetStops;

    /// <summary>
    /// The activity "acceptance points" offered in the navigation panel. Every row id below was
    /// verified against the TC 7.20 EXD dump (`D:\ffxiv-tc-port\exd-tc\7.20\`) rather than guessed —
    /// see the per-entry evidence. The repo has been bitten twice by assumed DataIds, so the
    /// evidence is recorded here on purpose.
    ///
    /// Evidence used, in order of strength:
    ///  * ENpcBase.ENpcData event-handler *type* (the high 16 bits of each handler id) — this is
    ///    locale-independent and states what the NPC actually does:
    ///      type 35 = Triple Triad, type 37 = Mini Cactpot, type 38 = Jumbo Cactpot.
    ///  * ENpcResident.Title — "G.A.T.E.事件" for the Event Coordinators, "九宮幻卡" for the two
    ///    tournament receptionists, "貿易人員" for the card traders.
    /// Every one of these also has a Level row in territory 144, so its position is resolvable.
    /// </summary>
    public static readonly GoldSaucerDestination[] Destinations =
    [
        // Title "G.A.T.E.事件" + Level rows. These three ids are exactly the three the user had
        // recorded by hand, which independently confirms both the ids and the sheet positions.
        new()
        {
            Key = "EventCoordinator",
            LabelKey = "Event Coordinator",
            NpcIds = [1011080, 1011084, 1011093]
        },

        // ENpcBase handler type 37 (Mini Cactpot).
        new() { Key = "MiniCactpot", LabelKey = "Mini Cactpot", NpcIds = [1010445] },

        // ENpcBase handler type 38 (Jumbo Cactpot).
        new() { Key = "JumboCactpot", LabelKey = "Jumbo Cactpot", NpcIds = [1010446] },

        // ENpcBase handler type 35 (Triple Triad) — 幻卡大師, the rules/introduction NPC.
        new() { Key = "TriadMaster", LabelKey = "Triple Triad Master", NpcIds = [1011060] },

        // Title "九宮幻卡" — 官方錦標賽接待員.
        new() { Key = "TriadTournament", LabelKey = "Triple Triad tournament", NpcIds = [1011061] },

        // Title "九宮幻卡" — 大賽接待員.
        new()
        {
            Key = "TriadOpenTournament",
            LabelKey = "Triple Triad open tournament",
            NpcIds = [1010479]
        },

        // Title "貿易人員" — 卡片兌換員, present at two separate spots.
        new() { Key = "TriadCardVendor", LabelKey = "Triple Triad card vendor", NpcIds = [1010478, 1016294] },

        // ENpcBase handler type 38 (shares the Jumbo Cactpot handler) — 獎品兌換員.
        new() { Key = "PrizeExchange", LabelKey = "Prize exchange", NpcIds = [1010451] },

        // 金碟幣兌換員.
        new() { Key = "MgpExchange", LabelKey = "MGP exchange", NpcIds = [1011038] }
    ];

    public static bool InSaucer => Svc.ClientState.TerritoryType == TerritoryId;

    /// <summary>ENpc row id -> world position, built in a single pass over the Level sheet and then
    /// cached forever. One pass over ~58k rows costs a couple of milliseconds and only ever happens
    /// once, on first use — deliberately not done in the constructor/OnUpdate (this plugin has a
    /// history of main-thread stalls, see the NAudio note in the repo skill).</summary>
    private static Dictionary<uint, Vector3> NpcPositions
    {
        get
        {
            if (npcPositions != null)
            {
                return npcPositions;
            }

            var map = Scan(requireENpcType: true);

            // Belt and braces: the Type == 8 filter is the only part of this that could not be
            // verified offline (the CSV dump and Lumina share a schema, so the column *should* line
            // up). If it ever stops matching, an unfiltered scan still yields the right coordinates
            // for the ids we actually look up, which all have exactly one Level row in this
            // territory. Better a slightly looser scan than a silently empty map.
            if (map.Count == 0)
            {
                Svc.Log.Warning(
                    "No Gold Saucer ENpc Level rows matched Type == {0}; retrying without the type filter.",
                    LevelTypeENpc);
                map = Scan(requireENpcType: false);
            }

            npcPositions = map;
            return map;
        }
    }

    private static Dictionary<uint, Vector3> Scan(bool requireENpcType)
    {
        var map = new Dictionary<uint, Vector3>();
        var sheet = Svc.Data.GetExcelSheet<LuminaLevel>();
        if (sheet == null)
        {
            return map;
        }

        foreach (var row in sheet)
        {
            if (row.Territory.RowId != TerritoryId)
            {
                continue;
            }

            if (requireENpcType && row.Type != LevelTypeENpc)
            {
                continue;
            }

            var objectId = row.Object.RowId;
            if (objectId != 0)
            {
                map.TryAdd(objectId, new Vector3(row.X, row.Y, row.Z));
            }
        }

        return map;
    }

    /// <summary>The Saucer's own aethernet: the hub crystal plus its shards, sorted so the hub comes
    /// first. Derived from the Aetheryte sheet (Territory 144, AethernetGroup 5) — never a hardcoded
    /// list, so it stays correct if the game ever adds a stop.</summary>
    public static IReadOnlyList<AethernetStop> AethernetStops
    {
        get
        {
            if (aethernetStops != null)
            {
                return aethernetStops;
            }

            var stops = new List<AethernetStop>();
            var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    if (row.Territory.RowId != TerritoryId || row.AethernetGroup == 0)
                    {
                        continue;
                    }

                    var name = row.AethernetName.ValueNullable?.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var position = AetheryteHelper.GetAethernetShardWorldPosition(row.RowId);
                    if (position == null)
                    {
                        continue;
                    }

                    stops.Add(new AethernetStop(row.RowId, name, position.Value, row.IsAetheryte));
                }
            }

            stops.Sort((a, b) => a.IsHub == b.IsHub ? a.RowId.CompareTo(b.RowId) : a.IsHub ? -1 : 1);
            aethernetStops = stops;
            return stops;
        }
    }

    /// <summary>NPC display name straight from ENpcResident, so it is always in the client's own
    /// language. Returns null when the row is missing/blank rather than inventing a label — a
    /// destination whose name cannot be resolved is dropped from the UI instead of shown as a
    /// mystery button.</summary>
    public static string? TryGetNpcName(uint enpcId)
    {
        var name = Svc.Data.GetExcelSheet<ENpcResident>()?.GetRowOrDefault(enpcId)?.Singular.ToString();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Position for a single NPC instance: the live object table wins when the NPC is
    /// actually loaded (it is the ground truth, and covers NPCs that wander), otherwise the Level
    /// sheet. Returns null when neither knows about it.</summary>
    public static Vector3? TryGetNpcPosition(uint enpcId)
    {
        var live = ObjectHelper.FindNearestByBaseId(enpcId);
        if (live != null)
        {
            return live.Position;
        }

        return NpcPositions.TryGetValue(enpcId, out var position) ? position : null;
    }

    /// <summary>Of a destination's (possibly several) physical instances, the one closest to
    /// <paramref name="from"/> on the horizontal plane. Y is excluded because the Saucer is stacked
    /// vertically — the Jumbo Cactpot floor sits 18y above the entrance, and including Y made
    /// "nearest" pick things on the wrong deck.</summary>
    public static bool TryGetNearestInstance(
        GoldSaucerDestination destination,
        Vector3 from,
        out uint npcId,
        out Vector3 position)
    {
        npcId = 0;
        position = default;
        var bestDistance = float.MaxValue;

        foreach (var candidate in destination.NpcIds)
        {
            var candidatePosition = TryGetNpcPosition(candidate);
            if (candidatePosition == null)
            {
                continue;
            }

            var distance = HorizontalDistance(from, candidatePosition.Value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                npcId = candidate;
                position = candidatePosition.Value;
            }
        }

        return npcId != 0;
    }

    public static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    internal readonly record struct AethernetStop(uint RowId, string Name, Vector3 Position, bool IsHub);
}

internal sealed class GoldSaucerDestination
{
    /// <summary>Stable identifier used for ImGui ids and log lines — never shown to the user.</summary>
    public required string Key { get; init; }

    /// <summary>English source string fed through <c>.Loc()</c>, matching how the rest of Saucy
    /// localizes (see LanguageChineseTraditional.ini).</summary>
    public required string LabelKey { get; init; }

    /// <summary>Every ENpcResident row this activity is reachable at. More than one means the same
    /// activity has several physical counters and the nearest is used.</summary>
    public required uint[] NpcIds { get; init; }
}
