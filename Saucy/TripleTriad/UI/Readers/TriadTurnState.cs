using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
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
    /// 🔴 <c>AtkValuesCount</c> 與 <c>AtkValues</c> 是兩個獨立欄位:長度對得上**不代表**指標
    /// 已經配置好(setup 與拆解途中兩者的更新沒有原子性)。只驗長度是半套守衛,
    /// 指標為 null 時 <c>AtkValues[23]</c> 是從位址 0x170 讀 ＝ AccessViolationException,
    /// 而 AVE 在 .NET Core 是 corrupted-state exception,<c>try</c>/<c>catch</c> 攔不到。
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

        if ((int)node->Type == (int)NodeType.Text && IsTurnBannerText(GUINodeUtils.GetNodeText(node)))
        {
            return true;
        }

        foreach (var child in GUINodeUtils.GetImmediateChildNodes(node) ?? [])
        {
            if (HasTurnBannerText(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTurnBannerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 96)
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
