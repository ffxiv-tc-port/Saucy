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

// Verified against the working FFTriadBuddyDalamud plugin (D:\FFTriadBuddyDalamud, confirmed by
// user to correctly read both players' board state live) — not guessed, not memory-scanned. Its
// offsets exactly cross-validate the earlier live-diff finding too: the diffed card's real stats
// address (addon-relative 0xAA0) equals Board start (0x8d0) + slot 2 (2*0xA8=0x150) + the real
// intra-slot stat offset (0x80) = 0x8d0+0x150+0x80 = 0xAA0. So Board really does start at 0x8d0
// as first assumed; the earlier mistake was assuming stats sit at the START of each 0xA8 slot
// (+0x0) instead of well inside it (+0x80).
[StructLayout(LayoutKind.Explicit, Size = 0x1000)]
internal unsafe struct AddonTripleTriad
{
    [StructLayout(LayoutKind.Explicit, Size = 0xA8)]
    internal unsafe struct TripleTriadCard
    {
        [FieldOffset(0x8)] public AtkComponentBase* CardDropControl;
        [FieldOffset(0x80)] public byte CardRarity;  // 1..5
        [FieldOffset(0x81)] public byte CardType;    // 0: no type, 1: primal, 2: scion, 3: beastman, 4: garland
        [FieldOffset(0x82)] public byte CardOwner;   // 0: empty, 1: blue, 2: red
        [FieldOffset(0x83)] public byte NumSideU;
        [FieldOffset(0x84)] public byte NumSideD;
        [FieldOffset(0x85)] public byte NumSideR;
        [FieldOffset(0x86)] public byte NumSideL;
        [FieldOffset(0xA4)] public bool HasCard;
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
