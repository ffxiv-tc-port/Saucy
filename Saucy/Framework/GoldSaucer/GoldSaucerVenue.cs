using Lumina.Excel.Sheets;
using Saucy.TripleTriad;
using System;
using System.Collections.Generic;
using System.Numerics;
using LuminaLevel = Lumina.Excel.Sheets.Level;
namespace Saucy.Framework.GoldSaucer;

/// <summary>
/// Sheet-derived map of the Gold Saucer: every *static* activity acceptance point plus the six
/// internal aethernet shards.
///
/// Nothing here hardcodes display text — names come from ENpcResident/EObjName and aethernet names
/// from Aetheryte/PlaceName at runtime, so they render correctly on the TC client (and any other)
/// without a translation step. Positions come from the Level sheet instead of user recordings, so a
/// fresh character/install can navigate immediately with zero setup.
///
/// ⚠️ This deliberately covers only objects that HAVE a Level row. The GATE registration NPCs
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

    /// <summary>Level.Type discriminators: 8 positions an ENpc, 45 positions an EObj. Both are
    /// needed — Doman Mahjong is entered at EObj game tables, not at an NPC.</summary>
    private const byte LevelTypeENpc = 8;

    private const byte LevelTypeEObj = 45;

    private static Dictionary<uint, Vector3>? objectPositions;
    private static List<AethernetStop>? aethernetStops;

    /// <summary>
    /// The activity "acceptance points" offered in the navigation panel.
    ///
    /// Every row id below was verified against the TC 7.20 EXD dump (`D:\ffxiv-tc-port\exd-tc\7.20\`)
    /// rather than guessed — the repo has been bitten twice by assumed DataIds, so the evidence is
    /// recorded per entry on purpose.
    ///
    /// The decisive evidence is the **internal name of the CustomTalk handler** each object carries
    /// (CustomTalk.Name, e.g. `CmnGscTripleTriadGuide_00238`). That is developer-authored English,
    /// independent of client language and of any guess about what an NPC's Chinese name implies, and
    /// it named every single entry below unambiguously. Where an entry is reached by a warp instead
    /// of a conversation, the evidence is the Warp row's own Question text and TerritoryType.
    /// Secondary corroboration used while narrowing things down: ENpcBase.ENpcData handler *types*
    /// (35 = TripleTriad, 37 = LotteryDaily/仙人微彩, 38 = LotteryWeekly/仙人彩) and
    /// ENpcResident.Title (G.A.T.E.事件 / 九宮幻卡 / 貿易人員).
    /// </summary>
    public static readonly GoldSaucerDestination[] Destinations =
    [
        // CustomTalk CmnGscGATENotice_00242. All three ids are exactly the three the user had
        // recorded by hand, which independently confirms both the ids and the sheet positions.
        new()
        {
            Key = "EventCoordinator",
            LabelKey = "Event Coordinator",
            ObjectIds = [1011080, 1011084, 1011093]
        },

        // CustomTalk CmnGscDailyLotDescription_00226 (+ ENpcBase handler type 37 = LotteryDaily).
        new() { Key = "MiniCactpot", LabelKey = "Mini Cactpot", ObjectIds = [1010445] },

        // ENpcBase handler type 38 = LotteryWeekly. 仙人仙彩發放員.
        new() { Key = "JumboCactpot", LabelKey = "Jumbo Cactpot", ObjectIds = [1010446] },

        // CustomTalk CmnGscEMJGameTable_00547 — "EMJ" is the game's own prefix for Doman Mahjong
        // (cf. the EmjAddon sheet). ⚠️ Doman Mahjong has NO reception NPC: it is entered at three
        // EObj game tables, 初級/中級/上級多瑪方城桌, all on the upper deck of Wonder Square East.
        // The nearest table is used. (An earlier guess that the type-0x29 attendant 溫金 was the
        // receptionist was WRONG — that handler type appears on three unrelated Saucer attendants
        // and could not be named from the sheets. Do not reintroduce it.)
        new()
        {
            Key = "DomanMahjong",
            LabelKey = "Doman Mahjong",
            ObjectIds = [2009669, 2009707, 2009708]
        },

        // CustomTalk CtsFckMaster_00453 (假面·羅斯) and CtsFckAttendant_00454 (霞). "Fck" is the
        // game's abbreviation for Fashion Check, i.e. the weekly fashion report. The two stand 2.4y
        // apart, so either lands the player correctly.
        new() { Key = "FashionReport", LabelKey = "Fashion report", ObjectIds = [1025176, 1025177] },

        // Warp row 131177, Question "要前往陸行鳥廣場嗎？", TerritoryType 388 = 陸行鳥廣場.
        // ⚠️ The actual registration NPCs (參賽登記員 1010464, 訓鳥師 1010465) live in territory 388,
        // not in the Saucer, so the in-Saucer acceptance point is the attendant who warps you across.
        new() { Key = "ChocoboRacing", LabelKey = "Chocobo racing", ObjectIds = [1011044] },

        // Warp row 131539, Question "要前往金碟巨豆中心廣場嗎？", TerritoryType 1197 =
        // 金碟巨豆中心廣場. Same pattern as Chocobo racing: a warp attendant standing in the Saucer.
        new() { Key = "BeanCenter", LabelKey = "Gold Saucer Bean Center", ObjectIds = [1046442] },

        // CustomTalk CmnGscTripleTriadGuide_00238 (+ CmnGscTripleTriadRoomMove_00371) — 幻卡大師.
        new() { Key = "TriadMaster", LabelKey = "Triple Triad Master", ObjectIds = [1011060] },

        // CustomTalk CmnGscTripleTriadLTDTournament_00703 — 官方錦標賽接待員.
        new() { Key = "TriadTournament", LabelKey = "Triple Triad tournament", ObjectIds = [1011061] },

        // CustomTalk CmnGscTripleTriadCup_00247 — 大賽接待員.
        new()
        {
            Key = "TriadOpenTournament",
            LabelKey = "Triple Triad open tournament",
            ObjectIds = [1010479]
        },

        // CustomTalk CmnGscTripleTriadCardToCoin_00239 — 卡片兌換員, at two separate counters.
        new() { Key = "TriadCardVendor", LabelKey = "Triple Triad card vendor", ObjectIds = [1010478, 1016294] },

        // CustomTalk CmnGscWeeklyLotUnlockTalk_00105 — 獎品兌換員.
        new() { Key = "PrizeExchange", LabelKey = "Prize exchange", ObjectIds = [1010451] },

        // CustomTalk CmnGscGilToCoin_00240 — 金碟幣兌換員.
        new() { Key = "MgpExchange", LabelKey = "MGP exchange", ObjectIds = [1011038] },

        // CustomTalk CmnGscEstablishmentGuidance_00241 — 來賓接待員, the general "what is there to do
        // here" desk.
        new() { Key = "Reception", LabelKey = "Gold Saucer reception", ObjectIds = [1010448] }
    ];

    public static bool InSaucer => Svc.ClientState.TerritoryType == TerritoryId;

    /// <summary>Object row id -> world position, built in a single pass over the Level sheet and then
    /// cached forever. One pass over ~58k rows costs a couple of milliseconds and only ever happens
    /// once, on first use — deliberately not done in the constructor/OnUpdate (this plugin has a
    /// history of main-thread stalls, see the NAudio note in the repo skill).
    ///
    /// ENpc and EObj row ids do not overlap (ENpc ids are ~1.0M, EObj ids ~2.0M), so one dictionary
    /// safely holds both.</summary>
    private static Dictionary<uint, Vector3> ObjectPositions
    {
        get
        {
            if (objectPositions != null)
            {
                return objectPositions;
            }

            var map = Scan(filterByType: true);

            // Belt and braces: the Type filter is the only part of this that could not be verified
            // offline (the CSV dump and Lumina share a schema, so the column *should* line up). If it
            // ever stops matching, an unfiltered scan still yields the right coordinates for the ids
            // we actually look up, which all have exactly one Level row in this territory. Better a
            // slightly looser scan than a silently empty map.
            if (map.Count == 0)
            {
                Svc.Log.Warning(
                    "No Gold Saucer Level rows matched Type {0}/{1}; retrying without the type filter.",
                    LevelTypeENpc,
                    LevelTypeEObj);
                map = Scan(filterByType: false);
            }

            objectPositions = map;
            return map;
        }
    }

    private static Dictionary<uint, Vector3> Scan(bool filterByType)
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

            if (filterByType && row.Type != LevelTypeENpc && row.Type != LevelTypeEObj)
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

    /// <summary>Display name straight from ENpcResident (NPCs) or EObjName (event objects), so it is
    /// always in the client's own language. Returns null when the row is missing/blank rather than
    /// inventing a label — a destination whose name cannot be resolved is dropped from the UI instead
    /// of shown as a mystery button.</summary>
    public static string? TryGetObjectName(uint objectId)
    {
        var npcName = Svc.Data.GetExcelSheet<ENpcResident>()?.GetRowOrDefault(objectId)?.Singular.ToString();
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            return npcName;
        }

        var objName = Svc.Data.GetExcelSheet<EObjName>()?.GetRowOrDefault(objectId)?.Singular.ToString();
        return string.IsNullOrWhiteSpace(objName) ? null : objName;
    }

    /// <summary>Position for a single instance: the live object table wins when the object is
    /// actually loaded (it is the ground truth, and covers NPCs that wander), otherwise the Level
    /// sheet. Returns null when neither knows about it.</summary>
    public static Vector3? TryGetObjectPosition(uint objectId)
    {
        var live = ObjectHelper.FindNearestByBaseId(objectId);
        if (live != null)
        {
            return live.Position;
        }

        return ObjectPositions.TryGetValue(objectId, out var position) ? position : null;
    }

    /// <summary>Of a destination's (possibly several) physical instances, the one closest to
    /// <paramref name="from"/> on the horizontal plane. Y is excluded because the Saucer is stacked
    /// vertically — the mahjong parlour sits 17y above the fashion report desk directly below it, and
    /// including Y made "nearest" pick things on the wrong deck.</summary>
    public static bool TryGetNearestInstance(
        GoldSaucerDestination destination,
        Vector3 from,
        out uint objectId,
        out Vector3 position)
    {
        objectId = 0;
        position = default;
        var bestDistance = float.MaxValue;

        foreach (var candidate in destination.ObjectIds)
        {
            var candidatePosition = TryGetObjectPosition(candidate);
            if (candidatePosition == null)
            {
                continue;
            }

            var distance = HorizontalDistance(from, candidatePosition.Value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                objectId = candidate;
                position = candidatePosition.Value;
            }
        }

        return objectId != 0;
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

    /// <summary>Every ENpcResident or EObj row this activity is reachable at. More than one means the
    /// same activity has several physical counters/tables and the nearest is used.</summary>
    public required uint[] ObjectIds { get; init; }
}
