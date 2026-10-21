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
    public enum Verdict
    {
        Press,
        TooFew,
        WrongType,
        NotNormalMode,
        LeaveDisabled,
    }

    /// <summary>遊戲寫死的動作編號：0＝「退出」（一般副本與登高跳跳樂相同，之後一定經過確認框）。</summary>
    public const int LeaveCallbackValue = 0;

    /// <summary>關閉選單（agent Hide），不經確認框。</summary>
    public const int CloseCallbackValue = -2;

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
    public static string? Refusal(int count, ReadOnlySpan<int> rawTypes, uint mode, byte leaveEnabled)
    {
        Judge(count, rawTypes, mode, leaveEnabled, out var reason);
        return reason;
    }

    /// <summary>同 <see cref="Refusal"/>，另外分出是哪一種不按。</summary>
    /// <param name="count">AtkValuesCount（實際格數，只要求至少涵蓋指紋，不要求等於 348）。</param>
    /// <param name="rawTypes">前幾格的原始 Type（未遮罩），呼叫端最多抄 <see cref="FingerprintLength"/> 格。</param>
    /// <param name="mode">[0].UInt：0＝一般模式，非 0＝「詳細資訊」子窗開著。</param>
    /// <param name="leaveEnabled">[1].Byte：「退出」按鈕可不可按。callback 會繞過按鈕的灰掉狀態，所以一定要看這格。</param>
    public static Verdict Judge(int count, ReadOnlySpan<int> rawTypes, uint mode, byte leaveEnabled, out string? reason)
    {
        if (count < Fingerprint.Length || rawTypes.Length < Fingerprint.Length)
        {
            reason = $"AtkValues 不足（count={count}）";
            return Verdict.TooFew;
        }

        for (var i = 0; i < Fingerprint.Length; i++)
        {
            if ((rawTypes[i] & (int)AtkValueType.TypeMask) != Fingerprint[i])
            {
                reason = $"第 [{i}] 格型別 0x{rawTypes[i]:X} 不是 {TypeName(Fingerprint[i])}（選單排法和台服 7.25 不同）";
                return Verdict.WrongType;
            }
        }

        if (mode != 0)
        {
            reason = $"副本選單正在顯示詳細資訊（[0]={mode}）";
            return Verdict.NotNormalMode;
        }

        if (leaveEnabled == 0)
        {
            reason = "遊戲判定現在不能退出（「退出」按鈕是灰的）";
            return Verdict.LeaveDisabled;
        }

        reason = null;
        return Verdict.Press;
    }

    /// <summary>暫時性的不按：關掉自己叫出來的選單、冷卻後重叫，到總時限才放棄。排法不對（格數、型別）一律立即放棄。</summary>
    public static bool CanRetry(Verdict verdict) => verdict is Verdict.NotNormalMode or Verdict.LeaveDisabled;

    private static string TypeName(int type) => type switch
    {
        (int)AtkValueType.Bool => "Bool",
        (int)AtkValueType.UInt => "UInt",
        (int)AtkValueType.String => "String",
        _ => $"0x{type:X}"
    };
}
