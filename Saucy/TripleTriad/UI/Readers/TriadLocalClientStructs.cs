using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ECommons.GenericHelpers;

namespace Saucy.TripleTriad.UI;

internal static unsafe class TriadLocalClientStructs
{
    public static bool TryGetRequest(out AddonRequest* addon, bool requireVisible = true) =>
        TryGetVisible("TripleTriadRequest", out addon, requireVisible);

    public static bool TryGetSelDeck(out AddonTripleTriadSelDeck* addon, bool requireVisible = true) =>
        TryGetVisible("TripleTriadSelDeck", out addon, requireVisible);

    public static bool TryGetResult(out AddonTripleTriadResult* addon, bool requireVisible = true) =>
        TryGetVisible("TripleTriadResult", out addon, requireVisible);

    public static bool TryGetBoard(out AddonTripleTriad* addon, bool requireVisible = true)
    {
        if (!TryGetAddonByName("TripleTriad", out addon))
        {
            return false;
        }

        return !requireVisible || addon->AtkUnitBase.IsVisible;
    }

    private static bool TryGetVisible<T>(string addonName, out T* addon, bool requireVisible)
    where T : unmanaged
    {
        if (!TryGetAddonByName(addonName, out addon))
        {
            return false;
        }

        if (!requireVisible)
        {
            return true;
        }

        return ((AtkUnitBase*)addon)->IsVisible;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 0x1D0)]
internal unsafe struct AgentTripleTriad
{
    [FieldOffset(0x00)] public AgentInterface AgentInterface;
    [FieldOffset(0x1C8)] public uint RewardItemId;

    internal static AgentTripleTriad* TryGet()
    {
        var module = AgentModule.Instance();
        if (module == null)
        {
            return null;
        }

        return (AgentTripleTriad*)module->GetAgentByInternalId(AgentId.TrippleTriad);
    }
}

[StructLayout(LayoutKind.Explicit)]
internal struct AddonTripleTriadSelDeck
{
    [FieldOffset(0)] public AtkUnitBase AtkUnitBase;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AddonTripleTriadResult
{
    [FieldOffset(0)] public AtkUnitBase AtkUnitBase;
}

[StructLayout(LayoutKind.Explicit, Size = 0x1000)] // no idea what size, last entries seems to be around +0xfc0?
internal unsafe struct AddonTripleTriad
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct TripleTriadCard
    {
        public AtkComponentBase* CardDropControl;
        public byte CardRarity;  // 1..5
        public byte CardType;    // 0: no type, 1: primal, 2: scion, 3: beastman, 4: garland
        public byte CardOwner;   // 0: empty, 1: blue, 2: red
        public byte NumSideU;
        public byte NumSideD;
        public byte NumSideR;
        public byte NumSideL;
        public bool HasCard;
    }

    [InlineArray(5)]
    internal struct DeckArray
    {
        private TripleTriadCard element0;
    }

    [InlineArray(9)]
    internal struct BoardArray
    {
        private TripleTriadCard element0;
    }

    [FieldOffset(0x0)] public AtkUnitBase AtkUnitBase;
    [FieldOffset(0x238)] public byte TurnState; // 0: waiting, 1: normal move, 2: masked move (order/chaos)

    [FieldOffset(0x240)] public DeckArray BlueDeck; // 2be = end of numbers
    [FieldOffset(0x588)] public DeckArray RedDeck;
    [FieldOffset(0x8d0)] public BoardArray Board;
}

/// <summary>
/// Old FFXIVClientStructs' AddonGSInfoCardList is missing the detail-pane stat fields
/// (CardIconId, NumSideU/L/D/R, CardRarity, CardType) that newer versions expose. Those fields
/// only feed secondary verification/fallback heuristics in TriadCardListSelectionReader and
/// UIReaderTriadCardList (name/display-label/description-text/grid based detection remain
/// fully intact), so report neutral "unknown" values here instead of guessing an unverifiable
/// memory offset that could misbehave or crash against the live client.
/// </summary>
internal static unsafe class AddonGSInfoCardListExtensions
{
    public static int CardIconId(AddonGSInfoCardList* addon) => 0;
    public static byte NumSideU(AddonGSInfoCardList* addon) => 0;
    public static byte NumSideL(AddonGSInfoCardList* addon) => 0;
    public static byte NumSideD(AddonGSInfoCardList* addon) => 0;
    public static byte NumSideR(AddonGSInfoCardList* addon) => 0;
    public static byte CardRarity(AddonGSInfoCardList* addon) => 0;
    public static byte CardType(AddonGSInfoCardList* addon) => 0;
}
