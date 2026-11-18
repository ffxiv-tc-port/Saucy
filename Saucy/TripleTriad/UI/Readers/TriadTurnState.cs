using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace Saucy.TripleTriad.UI;

internal enum TurnState : byte
{
    Waiting = 0,
    NormalMove = 1,
    MaskedMove = 2
}

internal static class TriadTurnState
{
    public const int PlayerTurnAtkValueIndex = 23;

    private static readonly string[] TurnBannerMarkers =
    [
        " TURN",
        " TURN'",
        " TOUR ",
        " TOUR DE ",
        " ZUG",
        " AM ZUG",
        " TURNO",
        " TURNO DE ",
        " VEZ DE ",
        "のターン",
        "回合",
        "턴",
        " ход",
        " хід"
    ];

    /// <remarks>
    /// 🔴 <c>AtkValuesCount</c> 與 <c>AtkValues</c> 是兩個獨立欄位:長度對得上**不代表**指標已經配置好(setup 與拆解途中兩者的更新沒有原子性)。只驗長度是半套守衛,
    /// 指標為 null 時 <c>AtkValues[23]</c>＝ AccessViolationException,而 AVE 在 .NET Core 是 corrupted-state exception,<c>try</c>/<c>catch</c> 攔不到。
    /// 讀不到一律回 <see langword="false"/>(＝「還不是我的回合」),與既有失敗語意相同。
    /// </remarks>
    public static unsafe bool ReadIsPlayerTurn(AtkUnitBase* unit)
    {
        if (unit == null || unit->AtkValues == null || unit->AtkValuesCount <= PlayerTurnAtkValueIndex)
        {
            return false;
        }

        ref var value = ref unit->AtkValues[PlayerTurnAtkValueIndex];
        return value.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int && value.Int == 1;
    }

    public static bool IsBoardPickPhase(byte turnState) => turnState == (byte)TurnState.NormalMove;

    public static bool IsForcedCardPickPhase(byte turnState) => turnState == (byte)TurnState.MaskedMove;

    public static bool CanBlueAct(byte turnState, bool isPlayerTurn) =>
        IsForcedCardPickPhase(turnState) ||
        (IsBoardPickPhase(turnState) && isPlayerTurn);

    public static unsafe bool IsTurnBannerVisible(AtkUnitBase* unit) =>
        unit != null && HasTurnBannerText(unit->RootNode);

    private static unsafe bool HasTurnBannerText(AtkResNode* node)
    {
        if (node == null || !node->IsVisible())
        {
            return false;
        }

        if ((int)node->Type == (int)NodeType.Text && IsTurnBannerNodeText(node))
        {
            return true;
        }

        using var children = GUINodeUtils.BorrowImmediateChildNodes(node);
        for (var idx = 0; idx < children.Count; idx++)
        {
            if (HasTurnBannerText(children[idx]))
            {
                return true;
            }
        }

        return false;
    }

    // Decodes the text exactly like GUINodeUtils.GetNodeText (UTF-8, same replacement rules) but into a stack
    // buffer: a node text longer than the 96-char limit can never be a banner, so it is never materialized.
    private static unsafe bool IsTurnBannerNodeText(AtkResNode* node)
    {
        var textPtr = (byte*)new nint(((AtkTextNode*)node)->NodeText.StringPtr);
        if (textPtr == null)
        {
            return false;
        }

        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(textPtr);
        if (Encoding.UTF8.GetCharCount(bytes) > MaxTurnBannerLength)
        {
            return false;
        }

        Span<char> chars = stackalloc char[MaxTurnBannerLength];
        return IsTurnBannerText(chars[..Encoding.UTF8.GetChars(bytes, chars)]);
    }

    private const int MaxTurnBannerLength = 96;

    private static bool IsTurnBannerText(ReadOnlySpan<char> text)
    {
        if (text.IsWhiteSpace())
        {
            return false;
        }

        foreach (var marker in TurnBannerMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
