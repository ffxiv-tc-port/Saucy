using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Text;
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
///
/// 【欄位來源】以下四點是 2026-08-06 對台服 <c>ffxiv_dx11.exe</c> 離線反組譯得到的，
/// 不是從別的外掛抄來的猜測：
/// <list type="bullet">
/// <item>指針位置＝<c>NumberArrayData[104].IntArray[0]</c>，值域 0–10000。
///   <c>AddonGoldSaucerGatheringMiniGameBase::OnRequestedUpdate</c> 取 <c>numberArrays[104]</c>
///   的 <c>IntArray[0]</c>，再以 <c>pos / 10000.0f</c> 算指針旋轉角
///   （除數 10000.0f 就在該函式的 rip 相對常數裡）。</item>
/// <item><c>AtkValue[0]</c>＝階段。3＝輪到玩家（輸入解鎖）。
///   🔴 **舊註解寫「砍伐迴圈是 3 ↔ 4」是錯的，而且是 2026-08-06「一直失敗」的主因。**
///   台服 7.20 實機面板傾印顯示一刀的完整循環是 <c>3 →(4)→ 5 → 7 → 3</c>：
///   5＝揮擊／結算，7＝結果顯示（<c>AtkValue[11]</c> 就是在這一格遞減），4 只在部分刀出現。
///   ⇒ **不可以用「階段不在某個集合裡」推論「換了一棵樹」**——5 與 7 每一刀都會出現。</item>
/// <item><c>AtkValue[11]</c>＝本輪剩餘揮擊次數（實機 10→1 單調遞減，換樹回到 10）。
///   <c>AtkValue[12]</c>／<c>AtkValue[13]</c> ×100 後餵給量表元件的「目前值」與「最大值」，
///   但**實機 21 刀全程都是 10**，見 <see cref="ReadGauge"/>。</item>
/// <item>力量表的三段寬度＝<c>[AtkValue[4], AtkValue[5]-AtkValue[4], 10000-AtkValue[5]]</c>，
///   命中判定見 <see cref="TryGetPowerZones"/>。</item>
/// </list>
/// </summary>
internal static unsafe class LimbBoard
{
    internal const string BotanistAddon = "MiniGameBotanist";

    /// <summary>力量表 addon。⚠️ 這個 addon 是**孤樹無援與礦脈探索共用**的（兩者都是採集系機台），
    /// 光看它開著沒辦法分辨玩家在玩哪一台，所以動它之前一定要另外確認機台身分。</summary>
    internal const string AimgAddon = "MiniGameAimg";

    /// <summary>礦脈探索的砍伐畫面——看到它就代表玩家在玩另一台，我們一律不動作。</summary>
    internal const string MinerAddon = "MiniGameMiner";

    /// <summary>指針刻度的滿值。遊戲自己就是用 0–10000 這個尺度
    /// （<c>NumberArrayData[104].IntArray[0]</c>，再除以 10000.0f 算角度）。</summary>
    internal const int CursorScale = 10000;

    /// <summary>AtkValue 索引：目前階段。3 = 輪到玩家、可以揮斧。</summary>
    private const int StateIndex = 0;

    /// <summary>AtkValue 索引：力量表三段的兩個分界（0–10000 刻度）。</summary>
    private const int PowerBoundLowIndex = 4;

    private const int PowerBoundHighIndex = 5;

    /// <summary>AtkValue 索引：本輪剩餘揮擊次數（用戶端把它寫進計數文字節點）。</summary>
    private const int SwingsLeftIndex = 11;

    /// <summary>AtkValue 索引：樹的量表目前值。用戶端 ×100 後餵給量表元件。</summary>
    private const int GaugeIndex = 12;

    /// <summary>AtkValue 索引：樹的量表最大值（開場時設定一次）。</summary>
    private const int GaugeMaxIndex = 13;

    /// <summary>AtkValue 索引：機台剩餘時間字串（"分:秒"）。</summary>
    private const int TimeRemainingIndex = 15;

    /// <summary>AtkValue 索引：力量表的節點組選擇（==4 走低編號那組）。只有備援路徑會用到。</summary>
    private const int AimgNodeGroupIndex = 1;

    /// <summary>揮斧按鈕。</summary>
    internal const uint BotanistSwingButtonId = 24;

    /// <summary>力量表的停止按鈕——高編號節點組。</summary>
    internal const uint AimgStopButtonIdHigh = 37;

    /// <summary>力量表的停止按鈕——低編號節點組（<c>AtkValue[1] == 4</c>）。</summary>
    internal const uint AimgStopButtonIdLow = 9;

    /// <summary>State 值：輪到玩家出手。這是唯一一個我們真的依賴的階段值——
    /// 其餘階段的語意沒有離線證實過，所以判斷邏輯一律不靠它們。</summary>
    internal const uint StatePlayerTurn = 3;

    /// <summary>一棵新樹開始時的揮擊次數。⚠️ 只當**取不到實測值時**的後備：
    /// 真正在用的是「本局看過的最大 <see cref="ReadSwingsLeft"/>」，這樣就算改版把 10 改掉也不會壞。</summary>
    internal const uint SwingsPerTree = 10;

    /// <summary>備援路徑用的區塊節點組：<c>{容器節點, 填色節點}</c>×3。
    /// 容器節點的 <c>Y</c> 是該段在軌道上的位置，填色節點的 <c>Height</c> 是該段的長度
    /// （用戶端 <c>OnSetup</c> 就是這樣寫進去的）。</summary>
    private static readonly (uint Container, uint Fill)[] AimgBlockNodesHigh =
        [(47, 49), (44, 46), (41, 43)];

    private static readonly (uint Container, uint Fill)[] AimgBlockNodesLow =
        [(19, 21), (16, 18), (13, 15)];

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

    /// <summary>照遊戲自己的作法讀一個數值欄位：型別只要不是 Undefined 就直接取 +8 的 int。
    /// （用戶端的三個取值函式都只做 <c>Type &amp; 0x0F != 0</c> 這一個檢查。）
    /// 這比只認 Int／UInt 寬鬆，是刻意的——欄位在改版換成別的數值型別時不會整條靜默失效。</summary>
    internal static int? ReadRawInt(AtkUnitBase* addon, int index)
    {
        var value = TryGetValue(addon, index);
        if (value == null)
        {
            return null;
        }

        return value->Type switch
        {
            AtkValueType.Int => value->Int,
            AtkValueType.UInt => unchecked((int)value->UInt),
            AtkValueType.Bool => value->Byte,
            var _ => null
        };
    }

    private static uint? ReadUInt(AtkUnitBase* addon, int index)
    {
        var raw = ReadRawInt(addon, index);
        return raw is >= 0 ? (uint)raw.Value : null;
    }

    internal static uint? ReadState(AtkUnitBase* addon) => ReadUInt(addon, StateIndex);

    internal static uint? ReadSwingsLeft(AtkUnitBase* addon) => ReadUInt(addon, SwingsLeftIndex);

    /// <summary>樹的量表目前值（<c>AtkValue[12]</c>）。
    ///
    /// 🔴 **不要把它當主要回饋來源。** 2026-08-06 台服 7.20 實機面板傾印（21 刀）顯示
    /// <c>[12]</c> 與 <c>[13]</c> **全程都是 10**，只有樹倒下那一刻掉到 0；
    /// 同一段 log 裡 <c>[11]</c>（剩餘刀數）10→1 正常遞減。
    /// ⇒ 它要嘛是「樹的血量，而沒手感的刀就是 0 傷害」，要嘛根本不是每刀變動的欄位；
    /// 兩種解讀都指向同一個結論：**它一整棵樹都可能不動，不能拿來收斂。**
    /// 現在只當補強訊號用（有動就採信、沒動也不影響），主要判據是四級手感。</summary>
    internal static int? ReadGauge(AtkUnitBase* addon) => ReadRawInt(addon, GaugeIndex);

    /// <summary>樹的量表最大值（<c>AtkValue[13]</c>）。實機量到的是固定 10。
    /// 只在「它真的變了」時當成換樹的補充訊號，主判據是揮擊計數器。</summary>
    internal static int? ReadGaugeMax(AtkUnitBase* addon) => ReadRawInt(addon, GaugeMaxIndex);

    /// <summary>機台剩餘秒數。AtkValue[15] 是 "分:秒" 字串；
    /// 解析失敗（型別換掉、格式不符、欄位不存在）一律回 null，呼叫端要能接受「不知道」。</summary>
    internal static int? ReadSecondsRemaining(AtkUnitBase* addon)
    {
        var text = ReadString(addon, TimeRemainingIndex);
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

    private static string? ReadString(AtkUnitBase* addon, int index)
    {
        var value = TryGetValue(addon, index);
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

        try
        {
            return Dalamud.Memory.MemoryHelper.ReadStringNullTerminated((nint)raw);
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[OutOnALimb] failed to read AtkValue string");
            return null;
        }
    }

    /// <summary>目前指針落在 0–10000 刻度盤的哪裡。
    ///
    /// 這是遊戲自己讀的同一個欄位（<c>NumberArrayData[104].IntArray[0]</c>，
    /// <c>NumberArrayType.GoldSaucerArcadeMachine</c>），砍伐畫面與力量表共用。
    /// 舊版是拿指針節點的 <c>Rotation</c> 反推，並且把兩端角度寫死成 ±0.733——
    /// 那個值其實是用戶端執行期從 uld 讀出來的節點原始角度，寫死等於改版一動就靜默失準，
    /// 而且把解析度從 10000 階砍成 100 階。
    ///
    /// 取不到（陣列還沒建、還沒進遊戲）一律回 null，呼叫端就不出手。</summary>
    internal static int? ReadCursor()
    {
        var stage = AtkStage.Instance();
        if (stage == null)
        {
            return null;
        }

        // 刻意走欄位路徑而不是 AtkStage.GetNumberArrayData()：後者是帶特徵碼的 MemberFunction，
        // 特徵碼在台服失效時會在呼叫當下丟例外；欄位路徑只依賴結構偏移，取不到就是 null。
        var holder = stage->AtkArrayDataHolder;
        if (holder == null || holder->NumberArrays == null)
        {
            return null;
        }

        const int index = (int)NumberArrayType.GoldSaucerArcadeMachine;
        if (holder->NumberArrayCount <= index)
        {
            return null;
        }

        var array = holder->NumberArrays[index];
        if (array == null || array->IntArray == null || array->AtkArrayData.Size <= 0)
        {
            return null;
        }

        var raw = array->IntArray[0];
        return raw is < 0 or > CursorScale ? null : raw;
    }

    /// <summary>把 0–10000 的指針刻度換成使用者看到的 0–100 顯示刻度。</summary>
    internal static int ToDisplayScale(int rawCursor) =>
        (int)Math.Round(rawCursor * 100.0 / CursorScale);

    /// <summary>把 0–100 的顯示刻度換回 0–10000。</summary>
    internal static int ToRawScale(int displayCursor) =>
        Math.Clamp(displayCursor, 0, 100) * (CursorScale / 100);

    /// <summary>
    /// 力量表的三段區間，用**遊戲自己的判定式**算出來。
    ///
    /// <c>AddonMiniGameAimg::OnRequestedUpdate</c> 的判定是：
    /// <code>
    /// cum = 0;
    /// for (i = 2; i &gt;= 0; i--) { cum += width[i]; if (pos &lt;= cum) return slot i; }
    /// </code>
    /// 其中 <c>width = [AtkValue[4], AtkValue[5] - AtkValue[4], 10000 - AtkValue[5]]</c>
    /// （<c>OnSetup</c> 寫進去的）。所以指針低端那一段是 <c>width[2]</c>，高端那一段是 <c>width[0]</c>。
    ///
    /// 回傳的三格**依寬度由小到大排序**：最窄的＝最難停中＝泰坦，最寬的＝仙人掌怪。
    /// 這比寫死節點編號可靠：段落寬度是伺服器在開場時給的，不是常數。
    ///
    /// 任何一個欄位不合理（不是 <c>0 &lt; v4 &lt; v5 &lt; 10000</c>）就回 false，呼叫端不動作。
    /// </summary>
    internal static bool TryGetPowerZones(AtkUnitBase* addon, out LimbZone[] zonesBySize)
    {
        zonesBySize = [];
        if (addon == null)
        {
            return false;
        }

        var low = ReadRawInt(addon, PowerBoundLowIndex);
        var high = ReadRawInt(addon, PowerBoundHighIndex);
        if (low is not > 0 || high is not > 0 || low.Value >= high.Value || high.Value >= CursorScale)
        {
            return false;
        }

        var widths = new[] { low.Value, high.Value - low.Value, CursorScale - high.Value };

        // 遊戲從 slot 2 開始累加，所以低端那一段是 slot 2。
        var zones = new LimbZone[3];
        var cumulative = 0;
        for (var slot = 2; slot >= 0; slot--)
        {
            var lowerExclusive = slot == 2 ? -1 : cumulative;
            cumulative += widths[slot];
            zones[2 - slot] = new(slot, lowerExclusive, cumulative);
        }

        Array.Sort(zones, static (a, b) => a.Width.CompareTo(b.Width));
        zonesBySize = zones;
        return true;
    }

    /// <summary>
    /// 備援路徑：直接量畫面上三個區塊節點的幾何，換算回 0–10000 刻度。
    ///
    /// 只有在 <see cref="TryGetPowerZones"/> 讀不到 <c>AtkValue[4]/[5]</c> 時才會走這裡。
    /// 節點組由 <c>AtkValue[1]</c> 決定（用戶端 <c>OnSetup</c> 的 <c>== 4</c> 分支）；
    /// 兩組都取不到節點就回 false，整幀放棄——不會拿錯的節點去比。
    /// </summary>
    internal static bool TryGetPowerZonesFromNodes(AtkUnitBase* addon, out LimbZone[] zonesBySize)
    {
        zonesBySize = [];
        if (addon == null)
        {
            return false;
        }

        var preferLow = ReadRawInt(addon, AimgNodeGroupIndex) == 4;
        if (TryMeasureBlocks(addon, preferLow ? AimgBlockNodesLow : AimgBlockNodesHigh, out zonesBySize))
        {
            return true;
        }

        return TryMeasureBlocks(addon, preferLow ? AimgBlockNodesHigh : AimgBlockNodesLow, out zonesBySize);
    }

    private static bool TryMeasureBlocks(
        AtkUnitBase* addon,
        (uint Container, uint Fill)[] nodes,
        out LimbZone[] zonesBySize)
    {
        zonesBySize = [];

        var tops = new float[nodes.Length];
        var heights = new float[nodes.Length];
        var total = 0f;
        for (var i = 0; i < nodes.Length; i++)
        {
            var container = addon->GetNodeById(nodes[i].Container);
            var fill = addon->GetNodeById(nodes[i].Fill);
            if (container == null || fill == null || fill->Height == 0)
            {
                return false;
            }

            tops[i] = container->Y;
            heights[i] = fill->Height;
            total += heights[i];
        }

        if (total <= 0f)
        {
            return false;
        }

        // 指針刻度與畫面 Y 反向：pos = 10000 在 Y = 0 那端。
        var zones = new LimbZone[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var upper = (int)Math.Round((total - tops[i]) * CursorScale / total);
            var lower = (int)Math.Round((total - tops[i] - heights[i]) * CursorScale / total);
            zones[i] = new(i, Math.Clamp(Math.Min(lower, upper), -1, CursorScale),
                              Math.Clamp(Math.Max(lower, upper), 0, CursorScale));
            if (zones[i].Width <= 0)
            {
                return false;
            }
        }

        Array.Sort(zones, static (a, b) => a.Width.CompareTo(b.Width));
        zonesBySize = zones;
        return true;
    }

    /// <summary>力量表停止鈕的節點編號。節點組由 <c>AtkValue[1]</c> 決定；
    /// 選錯的話 <c>GetComponentButtonById</c> 只會回 null（不動作），不會誤按到別的東西。</summary>
    internal static uint AimgStopButtonId(AtkUnitBase* addon) =>
        ReadRawInt(addon, AimgNodeGroupIndex) == 4 ? AimgStopButtonIdLow : AimgStopButtonIdHigh;

    /// <summary>把 AtkValue[0..15] 傾印成一行字串。診斷用，不參與任何判斷。</summary>
    internal static string DumpAtkValues(AtkUnitBase* addon, int count = 16)
    {
        if (addon == null)
        {
            return "<null addon>";
        }

        var builder = new StringBuilder();
        builder.Append("count=").Append(addon->AtkValuesCount);
        for (var i = 0; i < count; i++)
        {
            var value = TryGetValue(addon, i);
            builder.Append(" [").Append(i).Append(']');
            if (value == null)
            {
                builder.Append("=-");
                continue;
            }

            builder.Append(value->Type).Append(':');
            switch (value->Type)
            {
                case AtkValueType.Int:
                    builder.Append(value->Int);
                    break;
                case AtkValueType.UInt:
                    builder.Append(value->UInt);
                    break;
                case AtkValueType.Bool:
                    builder.Append(value->Byte);
                    break;
                case AtkValueType.Float:
                    builder.Append(value->Float.ToString("0.###"));
                    break;
                case AtkValueType.String:
                case AtkValueType.String8:
                case AtkValueType.ManagedString:
                    builder.Append('"').Append(ReadString(addon, i) ?? string.Empty).Append('"');
                    break;
                default:
                    builder.Append('?');
                    break;
            }
        }

        return builder.ToString();
    }
}
