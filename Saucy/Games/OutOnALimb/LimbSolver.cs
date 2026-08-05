using System;
using System.Collections.Generic;
using System.Linq;
namespace Saucy.OutOnALimb;

/// <summary>
/// 孤樹無援的「找最佳砍伐位置」解題器——純資料運算，不碰任何原生記憶體。
///
/// 玩法：刻度盤上有一個隱藏的最佳位置，砍得越準、樹的量表掉得越多。
/// 收斂用的訊號有兩種，優先順序如下：
/// <list type="number">
/// <item><b>量表落差</b>（<c>AtkValue[12]</c> 砍前砍後的差）——連續值，直接拿來爬山。
///   這是主要來源。</item>
/// <item><b>系統訊息的手感</b>（沒感覺／接觸到／很接近／正中）——只有四級，而且要靠比對
///   聊天欄文字才拿得到。量表讀不到時的備援。</item>
/// </list>
/// 兩種都沒有時就退化成盲掃，行為與舊版相同（不會亂按，只是收斂得慢）。
/// </summary>
internal class LimbSolver
{
    /// <summary>粗掃用的三個起始點（0–100 顯示刻度）。</summary>
    private static readonly int[] StartingPoints = [20, 50, 80];

    private readonly List<HitResult> results = [];

    private int minIndex;
    private bool recordMinIndex;

    internal IReadOnlyList<HitResult> Results => results;

    internal int MinIndex => minIndex;

    /// <summary>已經量到量表落差的位置數。UI 用來顯示「解題器到底有沒有在學東西」。</summary>
    internal int DamageSampleCount => results.Count(x => x.Damage != null);

    /// <summary>目前量到的最大落差與它的位置；一次都沒量到就回 null。</summary>
    internal (int Position, int Damage)? BestSample
    {
        get
        {
            HitResult? best = null;
            foreach (var item in results)
            {
                if (item.Damage == null)
                {
                    continue;
                }

                if (best == null || item.Damage.Value > best.Damage!.Value)
                {
                    best = item;
                }
            }

            return best == null ? null : (best.Position, best.Damage!.Value);
        }
    }

    /// <summary>新的一棵樹：依步進值重建整個刻度盤。</summary>
    internal void Reset(int step)
    {
        // step 由設定來，夾住下限避免 0 或負數造成無限迴圈／空清單。
        var safeStep = Math.Clamp(step, 1, 100);
        results.Clear();
        for (var i = 0; i <= 100; i += safeStep)
        {
            results.Add(new(i, HitPower.Unobserved));
        }

        minIndex = 0;
        recordMinIndex = false;
    }

    /// <summary>記錄一次砍伐結果。<paramref name="cursor"/> 是**當時實際按下去的刻度**
    /// （0–100 顯示刻度），不是回報抵達當下的指針位置（指針還在轉）。</summary>
    /// <param name="damage">樹的量表掉了多少。null＝這次沒量到（會退回用 <paramref name="power"/>）。</param>
    internal void Record(int cursor, HitPower power, int? damage = null)
    {
        var item = GetClosestResultPoint(cursor);
        if (item == null)
        {
            return;
        }

        if (damage is > 0)
        {
            // 同一個位置量到第二次就取較大值：量表落差本身沒有隨機性，
            // 但「換樹瞬間剛好結算」之類的干擾只會讓它偏小。
            item.Damage = item.Damage == null ? damage : Math.Max(item.Damage.Value, damage.Value);
        }

        if (power == HitPower.Unobserved)
        {
            return;
        }

        if (recordMinIndex)
        {
            recordMinIndex = false;
            minIndex = Math.Max(0, results.IndexOf(item));
        }

        // 比上一次記錄還差，代表方向走反了：解除收斂範圍，重新全域搜尋。
        if (power < item.Power)
        {
            minIndex = 0;
            recordMinIndex = false;
        }

        item.Power = power;
    }

    /// <summary>算出下一次要瞄準的刻度；沒有任何可選位置時回 null（呼叫端就不出手）。</summary>
    internal int? GetNextTargetCursorPos()
    {
        if (results.Count == 0)
        {
            return null;
        }

        var byDamage = GetNextTargetFromDamage();
        if (byDamage != null)
        {
            return byDamage;
        }

        return GetNextTargetFromChatFeedback();
    }

    /// <summary>量表落差路徑：對最好的那一點做局部爬山，鄰居都量過了就一直打它。</summary>
    private int? GetNextTargetFromDamage()
    {
        var bestIndex = -1;
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Damage == null)
            {
                continue;
            }

            if (bestIndex < 0 || results[i].Damage!.Value > results[bestIndex].Damage!.Value)
            {
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return null;
        }

        // 先把最佳點的左右鄰居補齊——爬山要有梯度才知道往哪走。
        foreach (var neighbour in new[] { bestIndex - 1, bestIndex + 1 })
        {
            var candidate = SafeAt(neighbour);
            if (candidate is { Damage: null })
            {
                return candidate.Position;
            }
        }

        // 只量到一兩點就直接固定下來太容易卡在局部極大值；先把三個粗掃點掃完。
        if (DamageSampleCount < StartingPoints.Length)
        {
            foreach (var start in StartingPoints)
            {
                var candidate = GetClosestResultPoint(start);
                if (candidate is { Damage: null })
                {
                    return candidate.Position;
                }
            }
        }

        return results[bestIndex].Position;
    }

    /// <summary>舊的四級手感路徑。量表讀不到時的備援，行為與改版前相同。</summary>
    private int? GetNextTargetFromChatFeedback()
    {
        var start = Math.Clamp(minIndex, 0, results.Count - 1);

        for (var i = start; i < results.Count; i++)
        {
            if (results[i].Power == HitPower.Strong)
            {
                return results[i].Position;
            }
        }

        for (var i = start; i < results.Count; i++)
        {
            if (results[i].Power != HitPower.Weak)
            {
                continue;
            }

            var prev = SafeAt(i - 1);
            var next = SafeAt(i + 1);
            if (prev != null && prev.Power == HitPower.Unobserved && i - 1 >= start)
            {
                return prev.Position;
            }

            if (next != null && next.Power == HitPower.Unobserved)
            {
                return next.Position;
            }
        }

        var pendingStartingPoints = StartingPoints.Where(z => !IsStartingPointChecked(z)).ToArray();
        if (pendingStartingPoints.Length > 0)
        {
            var target = GetClosestResultPoint(pendingStartingPoints[0]);
            if (target != null)
            {
                if (pendingStartingPoints.Length != StartingPoints.Length)
                {
                    recordMinIndex = true;
                }

                return target.Position;
            }
        }

        minIndex = 0;
        var unobserved = results.Where(x => !x.IsObserved).ToArray();
        return unobserved.Length == 0 ? null : unobserved[Random.Shared.Next(unobserved.Length)].Position;
    }

    /// <summary>索引兩軸都驗（下界與上界），越界回 null。</summary>
    private HitResult? SafeAt(int index) =>
        index < 0 || index >= results.Count ? null : results[index];

    private HitResult? GetClosestResultPoint(int point) =>
        results.Count == 0 ? null : results.OrderBy(x => Math.Abs(point - x.Position)).First();

    private bool IsStartingPointChecked(int position)
    {
        var item = GetClosestResultPoint(position);
        return item == null || item.IsObserved;
    }
}
