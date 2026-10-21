using System;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Saucy.LeapOfFaith;

/// <summary>
/// 副本選單 ContentsFinderMenu 能不能送「退出」的判斷。純邏輯、不碰原生記憶體、不配置（放行時）；呼叫端同一幀把 AtkValues 抄成值再交進來。
/// </summary>
/// <remarks>
/// 選單上沒有任何選項名稱可比（按鈕字寫在 ULD，AtkValues 唯一的字串是「詳細資訊」的說明），所以認的是型別指紋＋兩格旗標。
/// 改版日先跑 <c>~/.claude/tools/sigscan/cfmenu_layout.py</c>：<c>leave_value</c>／<c>typed_slots</c> 任一變了就先改這裡。
/// </remarks>
internal static class ContentsFinderMenuGate
{
    /// <summary>遊戲寫死的動作編號：0＝「退出」（一般副本與登高跳跳樂相同，之後一定經過確認框）。</summary>
    public const int LeaveCallbackValue = 0;

    // 遊戲開窗填的前 17 格型別；對不上＝排法變了，不按。比之前遮 TypeMask：[8] 實際是 ManagedString（0x28）。
    private static readonly int[] Fingerprint =
    [
        (int)AtkValueType.UInt,
        (int)AtkValueType.Bool, (int)AtkValueType.Bool, (int)AtkValueType.Bool, (int)AtkValueType.Bool,
        (int)AtkValueType.Bool, (int)AtkValueType.Bool, (int)AtkValueType.Bool,
        (int)AtkValueType.String,
        (int)AtkValueType.Bool, (int)AtkValueType.Bool, (int)AtkValueType.Bool, (int)AtkValueType.Bool,
        (int)AtkValueType.Bool, (int)AtkValueType.Bool,
        (int)AtkValueType.UInt, (int)AtkValueType.UInt,
    ];

    public static int FingerprintLength => Fingerprint.Length;

    /// <summary>回 <see langword="null"/>＝可以送 <see cref="LeaveCallbackValue"/>；否則是不按的理由。</summary>
    /// <param name="count">AtkValuesCount（實際格數，只要求至少涵蓋指紋，不要求等於 348）。</param>
    /// <param name="rawTypes">前幾格的原始 Type（未遮罩），呼叫端最多抄 <see cref="FingerprintLength"/> 格。</param>
    /// <param name="mode">[0].UInt：0＝一般模式。</param>
    /// <param name="leaveEnabled">[1].Byte：「退出」按鈕可不可按。callback 會繞過按鈕的灰掉狀態，所以一定要看這格。</param>
    public static string? Refusal(int count, ReadOnlySpan<int> rawTypes, uint mode, byte leaveEnabled)
    {
        if (count < Fingerprint.Length || rawTypes.Length < Fingerprint.Length)
        {
            return $"AtkValues 不足（count={count}）";
        }

        for (var i = 0; i < Fingerprint.Length; i++)
        {
            if ((rawTypes[i] & (int)AtkValueType.TypeMask) != Fingerprint[i])
            {
                return $"第 [{i}] 格型別 0x{rawTypes[i]:X} 不是 {TypeName(Fingerprint[i])}（選單排法和台服 7.25 不同）";
            }
        }

        if (mode != 0)
        {
            return $"選單不在一般模式（[0]={mode}）";
        }

        if (leaveEnabled == 0)
        {
            return "遊戲判定現在不能退出（「退出」按鈕是灰的）";
        }

        return null;
    }

    private static string TypeName(int type) => type switch
    {
        (int)AtkValueType.Bool => "Bool",
        (int)AtkValueType.UInt => "UInt",
        (int)AtkValueType.String => "String",
        _ => $"0x{type:X}"
    };
}
