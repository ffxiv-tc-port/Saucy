using System;
namespace Saucy.OutOnALimb;

/// <summary>
/// 孤樹無援的「找最佳砍伐位置」解題器——純資料運算，不碰任何原生記憶體。
///
/// 玩法：刻度盤上有一個隱藏的最佳位置，砍得越準，系統訊息回報的「手感」越好。
///
/// 【這一版為什麼重做】2026-08-06 實機 log（21:14–21:20，共 21 刀）顯示舊版有三個問題：
/// <list type="number">
/// <item>盤面每一刀都被重設，所以永遠停在初始猜測，目標從頭到尾固定在 20。</item>
/// <item>舊版把「量表落差」當主要回饋，但實測 <c>AtkValue[12]</c> 全程不動
///   （見 <see cref="LimbBoard.ReadGauge"/> 的實測註記），那條路徑等於沒有資料。
///   真正每一刀都拿得到的是**四級手感**（系統訊息）。</item>
/// <item>沒有手感時舊版是**隨機**挑一個沒試過的位置，等於把有限的 10 刀丟掉。</item>
/// </list>
///
/// 【機制參考】判定的形狀參考了 DailyRoutines 的 <c>AutoOutOnALimb</c> 對外可見的行為與資料結構
/// （它維護一個 <c>int[100]</c> 的逐位置嘗試表、並以四級結果 Fail／Normal／Great／Perfect 收斂），
/// 但**程式碼是我們自己寫的**：DR 未公開原始碼，我們只採用「怎麼做」的知識，沒有沿用它的實作。
/// 對應關係：DR 的四級結果＝我們的 <see cref="HitPower"/>
/// （Addon#9710–9713 的四句手感文字），DR 的逐位置表＝這裡的 <see cref="board"/>。
///
/// 【與舊版的差別】盤面改成**整個 0–100 逐格**（跟 DR 的 <c>int[100]</c> 同解析度），
/// 設定裡的「步進值」不再是盤面格數，而是**粗掃時的最小間距**——
/// 一旦有手感就可以在比步進值更細的刻度上收斂。
/// </summary>
internal class LimbSolver
{
    internal const int MinPosition = 0;
    internal const int MaxPosition = 100;
    private const int PositionCount = MaxPosition - MinPosition + 1;

    /// <summary>逐格盤面。<c>null</c>＝這一格還沒試過。</summary>
    private readonly HitResult?[] board = new HitResult?[PositionCount];

    /// <summary>粗掃時兩個取樣點之間的最小間距（0–100 顯示刻度）。由設定的「步進值」來。</summary>
    private int coarseStep = 10;

    /// <summary>粗掃候選範圍往內縮的上限（0–100 顯示刻度）。
    ///
    /// 🔴 為什麼要內縮：**端點的資訊效率只有一半。** 最佳點的感應範圍是雙側的——
    /// 探刻度 50 能感應到 50 兩側的最佳點，探刻度 100 卻只能感應到左半邊，右半邊在界外。
    /// 舊版的最大間隙法第二刀時 0 與 100 並列最遠，於是把第二、第三刀都花在端點上，
    /// 序列是 <c>50 → 0 → 100 → 25 → 75</c>（使用者實測回報「50 100 1 這種策略不太對」）。
    ///
    /// ⚠️ 但**純二分（完全避開端點）會漏球**：最佳點落在極端位置時整局碰不到，
    /// 離線量測顯示感應半徑 5 時漏 12 個位置、半徑 8 時漏 5 個，比舊版還糟。
    /// 正解是「保留最大間隙法，但把候選範圍內縮一個感應半徑」——
    /// 端點區域改由**內側那一點的感應範圍**覆蓋，所以一個都不會漏。
    ///
    /// 📌 真實感應半徑是未知的遊戲常數（離線推不出來），所以內縮量取
    /// <c>coarseStep / 2</c>——粗掃間距本來就隱含「半徑約等於間距的一半」這個假設，
    /// 兩者一致才自洽。再夾一個上限 5，免得使用者把步進值調很大時
    /// 反而讓兩端一大片在刀數用完之前都掃不到（那才是真的回退）。
    /// 預設步進值 10 ⇒ 內縮 5，正好是離線量測出來最穩的那個值。</summary>
    private const int CoarseInsetLimit = 5;

    internal int ObservedCount { get; private set; }

    /// <summary>已經量到量表落差的位置數。UI 用來顯示「量表這條路徑到底有沒有資料」。</summary>
    internal int DamageSampleCount { get; private set; }

    /// <summary>有手感（Weak 以上，或量表真的掉了）的位置數。0＝還在盲掃階段。</summary>
    internal int ContactCount { get; private set; }

    /// <summary>目前分數最高的那一格；一刀都還沒記錄就回 null。</summary>
    internal HitResult? Best
    {
        get
        {
            HitResult? best = null;
            foreach (var item in board)
            {
                if (item != null && (best == null || Compare(item, best) > 0))
                {
                    best = item;
                }
            }

            return best;
        }
    }

    /// <summary>新的一棵樹：整個盤面清空。</summary>
    internal void Reset(int step)
    {
        // step 由設定來，夾住上下限：0／負數會讓粗掃永遠選同一格，過大則整個盤面只剩兩個候選。
        coarseStep = Math.Clamp(step, 1, 50);
        Array.Clear(board, 0, board.Length);
        ObservedCount = 0;
        DamageSampleCount = 0;
        ContactCount = 0;
    }

    /// <summary>記錄一次砍伐結果。<paramref name="cursor"/> 是**當時實際按下去的刻度**
    /// （0–100 顯示刻度），不是回報抵達當下的指針位置（指針還在轉）。</summary>
    /// <param name="power">系統訊息的四級手感。<see cref="HitPower.Unobserved"/>＝這次沒讀到手感。</param>
    /// <param name="damage">樹的量表掉了多少。null＝這次沒量到（實測台服 7.20 幾乎總是 null）。</param>
    internal void Record(int cursor, HitPower power, int? damage = null)
    {
        var index = ToIndex(cursor);
        if (index < 0)
        {
            return;
        }

        var item = board[index];
        if (item == null)
        {
            item = new(index, HitPower.Unobserved);
            board[index] = item;
            ObservedCount++;
        }

        var hadDamage = item.Damage != null;
        var hadContact = HasContact(item);

        if (damage is > 0)
        {
            // 同一格量到第二次就取較大值：量表落差本身沒有隨機性，
            // 會讓它偏小的只有「換樹瞬間剛好結算」這類干擾。
            item.Damage = item.Damage == null ? damage : Math.Max(item.Damage.Value, damage.Value);
            if (!hadDamage)
            {
                DamageSampleCount++;
            }
        }

        if (power != HitPower.Unobserved && power > item.Power)
        {
            item.Power = power;
        }

        if (!hadContact && HasContact(item))
        {
            ContactCount++;
        }
    }

    /// <summary>算出下一次要瞄準的刻度；盤面全滿且毫無資訊時回目前最佳（呼叫端照樣出手）。</summary>
    internal int? GetNextTargetCursorPos()
    {
        var best = Best;

        // 還沒有任何觀測 → 從正中央開始，之後靠最大間隙往兩邊二分。
        if (best == null)
        {
            return ScanProbe() ?? ((MinPosition + MaxPosition) / 2);
        }

        // 正中目標：就是這一格，繼續砍它。
        if (best.Power == HitPower.Maximum)
        {
            return best.Position;
        }

        // 已經有手感 → 在最佳點附近細修。先在一個粗掃格內找，找不到再放寬。
        if (HasContact(best))
        {
            foreach (var reach in new[] { coarseStep, coarseStep * 2, PositionCount })
            {
                var refine = RefineAround(best.Position, reach);
                if (refine != null)
                {
                    return refine;
                }
            }

            // 附近全試過了，最佳點就是目前所知最好的位置。
            return best.Position;
        }

        // 全部都是「沒手感」→ 繼續粗掃。
        // 🔴 粗掃間距用完之後**不可以退回「重砍目前最佳點」**：那一點也是「沒手感」，
        // 再砍一次得不到任何新資訊，等於把剩下的刀數丟掉（離線模擬抓到 555 次這種空轉）。
        // 正解是把最小間距縮到 1 繼續補洞——隱藏的最佳點一定就在還沒問過的縫隙裡。
        return ScanProbe() ?? best.Position;
    }

    /// <summary>粗掃的完整順序：先在內縮過的範圍裡撒開、撒不下去了在內縮範圍裡補洞，
    /// **內縮範圍整個問完之後才去碰兩端**。
    ///
    /// 🔴 最後兩層（<c>inset 0</c>）不是裝飾：少了它們，內縮範圍問完時就會回 null，
    /// 呼叫端只好退回「重砍目前最佳點」＝空轉，而且 <c>[0, inset)</c> 與
    /// <c>(100-inset, 100]</c> 會變成**永遠打不到的死區**。內縮是「先問資訊量大的地方」，
    /// 不是「把兩端劃掉」。</summary>
    private int? ScanProbe()
    {
        var inset = Math.Clamp(coarseStep / 2, 0, CoarseInsetLimit);
        return NextProbe(coarseStep, inset)
               ?? NextProbe(1, inset)
               ?? NextProbe(coarseStep, 0)
               ?? NextProbe(1, 0);
    }

    /// <summary>
    /// 在 <paramref name="centre"/> 附近挑一個還沒試過的刻度。
    ///
    /// 收斂假設：分數是「與隱藏最佳點的距離」的遞減函數，所以**分數比 centre 差的位置就是邊界**，
    /// 最佳點一定落在左右兩個邊界之間。做法是取「較寬的那一側未探索區間」的中點——
    /// 每問一次區間就折半，而且完全不需要知道各級手感對應多少刻度的半徑
    /// （那是遊戲內部常數，離線推不出來，寫死就是下次改版靜默失準）。
    /// </summary>
    private int? RefineAround(int centre, int reach)
    {
        var centreScore = Score(board[centre]);

        // 邊界＝離 centre 最近、分數比它差的已試位置；沒有的話就用 reach 夾住。
        var low = Math.Max(MinPosition - 1, centre - reach - 1);
        for (var i = centre - 1; i >= MinPosition; i--)
        {
            if (board[i] != null && Score(board[i]) < centreScore)
            {
                low = Math.Max(low, i);
                break;
            }
        }

        var high = Math.Min(MaxPosition + 1, centre + reach + 1);
        for (var i = centre + 1; i <= MaxPosition; i++)
        {
            if (board[i] != null && Score(board[i]) < centreScore)
            {
                high = Math.Min(high, i);
                break;
            }
        }

        var leftCandidate = NearestUntried(low + 1, centre - 1, (low + 1 + centre - 1) / 2);
        var rightCandidate = NearestUntried(centre + 1, high - 1, (centre + 1 + high - 1) / 2);

        if (leftCandidate == null)
        {
            return rightCandidate;
        }

        if (rightCandidate == null)
        {
            return leftCandidate;
        }

        // 兩側都還有空間時先問比較寬的那一側——資訊量比較大。
        var leftWidth = centre - low;
        var rightWidth = high - centre;
        return rightWidth > leftWidth ? rightCandidate : leftCandidate;
    }

    /// <summary>在 <c>[from, to]</c> 裡找一個沒試過的刻度，優先靠近 <paramref name="preferred"/>。</summary>
    private int? NearestUntried(int from, int to, int preferred)
    {
        if (from > to)
        {
            return null;
        }

        from = Math.Max(from, MinPosition);
        to = Math.Min(to, MaxPosition);
        if (from > to)
        {
            return null;
        }

        preferred = Math.Clamp(preferred, from, to);
        for (var offset = 0; offset <= to - from; offset++)
        {
            foreach (var candidate in new[] { preferred - offset, preferred + offset })
            {
                if (candidate >= from && candidate <= to && board[candidate] == null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 粗掃：挑「離所有已試位置最遠」的那一格（最大間隙），而不是舊版的**隨機**挑。
    ///
    /// 空盤面時最大間隙落在正中央，接著是兩端、再來是四等分點……
    /// 等於自動做出一輪二分掃描，用同樣的刀數換到更均勻的覆蓋。
    /// </summary>
    /// <param name="minSpacing">候選點離最近取樣點至少要多遠。
    /// 呼叫端先用設定的步進值撒開，撒不下去了再用 1 補洞——
    /// **絕不要在還有沒試過的格子時回 null 讓呼叫端去重砍舊位置**。</param>
    /// <param name="inset">候選範圍兩端各往內縮幾格（見 <see cref="CoarseInsetLimit"/>）。
    /// ⚠️ 只縮**候選**範圍；算距離時仍然要看整個盤面上所有已試過的點，
    /// 否則會把端點附近已經問過的資訊當成沒問過。</param>
    private int? NextProbe(int minSpacing, int inset)
    {
        int? bestCandidate = null;
        var bestDistance = -1;
        var anyObserved = ObservedCount > 0;

        var from = MinPosition + Math.Max(0, inset);
        var to = MaxPosition - Math.Max(0, inset);
        if (from > to)
        {
            from = MinPosition;
            to = MaxPosition;
        }

        for (var position = from; position <= to; position++)
        {
            if (board[position] != null)
            {
                continue;
            }

            int distance;
            if (anyObserved)
            {
                distance = int.MaxValue;
                for (var i = MinPosition; i <= MaxPosition; i++)
                {
                    if (board[i] == null)
                    {
                        continue;
                    }

                    distance = Math.Min(distance, Math.Abs(i - position));
                }
            }
            else
            {
                // 空盤面：離中央越近越好，這樣第一刀落在正中央。
                distance = MaxPosition - Math.Abs(position - ((MinPosition + MaxPosition) / 2));
            }

            if (distance > bestDistance)
            {
                bestDistance = distance;
                bestCandidate = position;
            }
        }

        // 已經有觀測、而且連「離最近取樣點 minSpacing 以上」的候選都沒有了 → 這個間距掃完了。
        if (anyObserved && bestDistance < Math.Max(1, minSpacing))
        {
            return null;
        }

        return bestCandidate;
    }

    private static int ToIndex(int cursor) =>
        cursor < MinPosition || cursor > MaxPosition ? -1 : cursor - MinPosition;

    /// <summary>一格的分數。手感是主軸；量表真的掉過就至少算「接觸到」——
    /// 量表會動就代表這一刀確實砍在樹上。</summary>
    private static int Score(HitResult? item)
    {
        if (item == null)
        {
            return -1;
        }

        var score = (int)item.Power;
        if (item.Damage is > 0 && score < (int)HitPower.Weak)
        {
            score = (int)HitPower.Weak;
        }

        return score;
    }

    private static bool HasContact(HitResult item) => Score(item) >= (int)HitPower.Weak;

    /// <summary>先比分數，同分再比量表落差。</summary>
    private static int Compare(HitResult a, HitResult b)
    {
        var byScore = Score(a).CompareTo(Score(b));
        return byScore != 0 ? byScore : (a.Damage ?? 0).CompareTo(b.Damage ?? 0);
    }
}
