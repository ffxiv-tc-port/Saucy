using System;
using System.Collections.Generic;
using System.Linq;
namespace Saucy.OutOnALimb;

/// <summary>
/// 孤樹無援的「找最佳砍伐位置」解題器——純資料運算，不碰任何原生記憶體。
///
/// 玩法：刻度盤 0–100 上有一個隱藏的最佳位置，每次砍完遊戲會用系統訊息回報手感
/// （沒感覺／接觸到／很接近／正中），據此逐步收斂。演算法沿用 PunishXIV/Saucy：
/// 1. 已經量到 Strong 的位置就一直打它（那附近就是甜蜜點）。
/// 2. 量到 Weak 的位置，往它左右還沒試過的鄰居探。
/// 3. 都沒有線索時，先掃 20/50/80 三個起始點。
/// 4. 再不然就從沒試過的位置裡隨機挑一個。
/// </summary>
internal class LimbSolver
{
    /// <summary>粗掃用的三個起始點。</summary>
    private static readonly int[] StartingPoints = [20, 50, 80];

    private readonly List<HitResult> results = [];

    private int minIndex;
    private bool recordMinIndex;

    internal IReadOnlyList<HitResult> Results => results;

    internal int MinIndex => minIndex;

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

    /// <summary>記錄一次砍伐結果。<paramref name="cursor"/> 是**當時實際按下去的刻度**，
    /// 不是回報訊息抵達當下的指針位置（指針還在轉）。</summary>
    internal void Record(HitPower power, int cursor)
    {
        var item = GetClosestResultPoint(cursor);
        if (item == null)
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
        var unobserved = results.Where(x => x.Power == HitPower.Unobserved).ToArray();
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
        return item == null || item.Power != HitPower.Unobserved;
    }
}
