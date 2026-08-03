using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using static ECommons.GenericHelpers;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;
namespace Saucy.OutOnALimb;

/// <summary>
/// 唯讀讀取 <c>MiniGameBotanist</c>／<c>MiniGameAimg</c> 兩個 addon 的畫面狀態。
///
/// 🔴 這裡刻意**不用** ECommons 的 <c>AtkReader</c>：那個實作在索引越界或型別不符時會丟例外
/// （<c>ArgumentOutOfRangeException</c>／<c>InvalidCastException</c>），而我們每一幀都會讀，
/// 型別只要在某個改版換掉就會變成每幀丟例外。本檔一律回傳可空值、永不丟例外。
///
/// 所有讀取都是「當幀取得 addon 指標 → 立刻用完丟掉」，**不跨幀保存任何原生指標**。
/// AtkValue 索引沿用 PunishXIV/Saucy 在 API13 世代（對應全球 7.3.0＝台服 7.20）使用的那一版。
/// </summary>
internal static unsafe class LimbBoard
{
    internal const string BotanistAddon = "MiniGameBotanist";

    /// <summary>力量表 addon。⚠️ 這個 addon 是**孤樹無援與礦脈探索共用**的（兩者都是採集系機台），
    /// 光看它開著沒辦法分辨玩家在玩哪一台，所以動它之前一定要另外確認機台身分。</summary>
    internal const string AimgAddon = "MiniGameAimg";

    /// <summary>礦脈探索的砍伐畫面——看到它就代表玩家在玩另一台，我們一律不動作。</summary>
    internal const string MinerAddon = "MiniGameMiner";

    /// <summary>AtkValue 索引：目前階段。3 = 輪到玩家、可以揮斧。</summary>
    private const int StateIndex = 0;

    /// <summary>AtkValue 索引：本輪剩餘揮擊次數。滿值 10 代表新的一棵樹剛開始。</summary>
    private const int SwingsLeftIndex = 11;

    /// <summary>AtkValue 索引：機台剩餘時間字串（"分:秒"）。</summary>
    private const int TimeRemainingIndex = 15;

    /// <summary>指針節點；它的 <c>Rotation</c>（弧度）就是刻度盤位置。</summary>
    private const uint CursorNodeId = 17;

    /// <summary>揮斧按鈕。</summary>
    internal const uint BotanistSwingButtonId = 24;

    /// <summary>力量表的停止按鈕。</summary>
    internal const uint AimgStopButtonId = 37;

    /// <summary>力量表的指針節點。</summary>
    private const uint AimgCursorNodeId = 39;

    /// <summary>State 值：輪到玩家出手。</summary>
    internal const uint StatePlayerTurn = 3;

    /// <summary>一棵新樹開始時的揮擊次數。</summary>
    internal const uint SwingsPerTree = 10;

    /// <summary>指針旋轉角的兩端（弧度），對應刻度 0 與 100。</summary>
    private const float CursorRotationMin = -0.733f;
    private const float CursorRotationMax = 0.733f;

    /// <summary>力量表刻度的基準高度：節點 Height 由此反推指針位置。</summary>
    private const float AimgTrackHeight = 400f;

    /// <summary>各難度的目標區塊節點 ID（力量表上那三格）。</summary>
    internal static uint AimgReferenceNodeId(LimbDifficulty difficulty) => difficulty switch
    {
        LimbDifficulty.Titan => 41,
        LimbDifficulty.Morbol => 44,
        var _ => 47
    };

    /// <summary>各難度目標區塊的高度（像素）。</summary>
    internal static float AimgReferenceHeight(LimbDifficulty difficulty) => difficulty switch
    {
        LimbDifficulty.Titan => 20f,
        LimbDifficulty.Morbol => 40f,
        var _ => 340f
    };

    /// <summary>取得目前可用的 addon 指標；沒開或還沒初始化完成一律回 null。</summary>
    internal static AtkUnitBase* TryGetAddon(string name)
    {
        if (!TryGetAddonByName<AtkUnitBase>(name, out var addon))
        {
            return null;
        }

        return IsAddonReady(addon) ? addon : null;
    }

    internal static bool IsBotanistOpen => TryGetAddon(BotanistAddon) != null;

    internal static bool IsAimgOpen => TryGetAddon(AimgAddon) != null;

    internal static bool IsMinerOpen => TryGetAddon(MinerAddon) != null;

    /// <summary>任一階段的畫面在就算「正在玩」。</summary>
    internal static bool IsPlaying => IsBotanistOpen || IsAimgOpen;

    /// <summary>邊界檢查兩軸（負索引與上界）＋型別檢查的 AtkValue 讀取，永不丟例外。</summary>
    private static AtkValue* TryGetValue(AtkUnitBase* addon, int index)
    {
        if (addon == null || index < 0)
        {
            return null;
        }

        var values = addon->AtkValues;
        if (values == null || index >= addon->AtkValuesCount)
        {
            return null;
        }

        return &values[index];
    }

    private static uint? ReadUInt(AtkUnitBase* addon, int index)
    {
        var value = TryGetValue(addon, index);
        if (value == null)
        {
            return null;
        }

        return value->Type switch
        {
            AtkValueType.UInt => value->UInt,
            AtkValueType.Int => value->Int >= 0 ? (uint)value->Int : null,
            var _ => null
        };
    }

    internal static uint? ReadState(AtkUnitBase* addon) => ReadUInt(addon, StateIndex);

    internal static uint? ReadSwingsLeft(AtkUnitBase* addon) => ReadUInt(addon, SwingsLeftIndex);

    /// <summary>機台剩餘秒數。AtkValue[15] 是 "分:秒" 字串；
    /// 解析失敗（型別換掉、格式不符、欄位不存在）一律回 null，呼叫端要能接受「不知道」。</summary>
    internal static int? ReadSecondsRemaining(AtkUnitBase* addon)
    {
        var value = TryGetValue(addon, TimeRemainingIndex);
        if (value == null)
        {
            return null;
        }

        if (value->Type is not (AtkValueType.String or AtkValueType.String8 or AtkValueType.ManagedString))
        {
            return null;
        }

        var raw = value->String.Value;
        if (raw == null)
        {
            return null;
        }

        string text;
        try
        {
            text = Dalamud.Memory.MemoryHelper.ReadStringNullTerminated((nint)raw);
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[OutOnALimb] failed to read time string");
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 上游只取 Split(':')[1]，也就是把 "1:05" 讀成 5 秒。這裡完整換算成 65 秒——
        // 少算會讓自動續戰提早收手，多算則會讓它在時間不夠時還接受下一輪。
        var parts = text.Split(':');
        if (parts.Length >= 2 &&
            int.TryParse(parts[0].Trim(), out var minutes) &&
            int.TryParse(parts[1].Trim(), out var seconds))
        {
            return (minutes * 60) + seconds;
        }

        return int.TryParse(text.Trim(), out var only) ? only : null;
    }

    /// <summary>目前指針落在刻度盤的哪裡（0–100）。節點不存在時回 null。</summary>
    internal static int? ReadCursor(AtkUnitBase* addon)
    {
        if (addon == null)
        {
            return null;
        }

        var node = addon->GetNodeById(CursorNodeId);
        if (node == null)
        {
            return null;
        }

        var normalised = (node->Rotation - CursorRotationMin) / (CursorRotationMax - CursorRotationMin);
        return (int)Math.Round(normalised * 100f);
    }

    /// <summary>力量表的指針是否正落在該難度的目標區塊內。任何節點取不到就回 false（不動作）。</summary>
    internal static bool IsAimgCursorOnTarget(AtkUnitBase* addon, LimbDifficulty difficulty)
    {
        if (addon == null)
        {
            return false;
        }

        var reference = addon->GetNodeById(AimgReferenceNodeId(difficulty));
        var cursor = addon->GetNodeById(AimgCursorNodeId);
        if (reference == null || cursor == null)
        {
            return false;
        }

        var cursorPosition = AimgTrackHeight - cursor->Height;
        return cursorPosition > reference->Y &&
               cursorPosition < reference->Y + AimgReferenceHeight(difficulty);
    }
}
