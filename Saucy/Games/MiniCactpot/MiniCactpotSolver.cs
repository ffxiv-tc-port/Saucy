using System;
using System.Collections.Generic;

namespace Saucy.MiniCactpot;

/// <summary>
/// 仙人微彩（Mini Cactpot，每日刮刮樂）期望值求解器。
/// 演算法與 DailyRoutines AutoMiniCactpot 的選格/選線邏輯等價（依其設計重寫，非抄碼）：
/// 翻格階段對「之後每一步都走最佳解」的期望 MGP 取最大（expectimax + 記憶化）；
/// 選線階段對該線隱藏格的所有取值排列取平均 payout 後取最大。
/// 盤面索引：0..8 由左至右、由上至下（0=左上、4=中央、8=右下）；0 代表未翻開。
/// </summary>
internal sealed class MiniCactpotSolver
{
    public const int TotalCells = 9;
    public const int TotalLanes = 8;

    /// <summary>翻格階段的目標翻開數（1 格免費 + 玩家自選 3 格）。</summary>
    public const int RevealTarget = 4;

    // 線和(6..24)對應 MGP 派彩——遊戲常數，全球/台服同表（與 DR 的表逐值一致）。
    private static readonly int[] Payouts =
    [
        0, 0, 0, 0, 0, 0,
        10000, 36, 720, 360, 80, 252, 108, 72, 54, 180, 72, 180, 119, 36, 306, 1080, 144, 1800, 3600
    ];

    // 與 AddonLotteryDaily.LaneTileSelector 的 UI 順序一一對應：
    // 0=主對角線(左上→右下)、1..3=直行(左→右)、4=反對角線(右上→左下)、5..7=橫列(上→下)。
    // 因此 SuggestLane 的回傳值可直接當 LaneSelector 的索引使用，不需要像 DR 那樣再過一張
    // 「solver 線序 → UI 線序」的對照表（DR 的 map=[6,3,4,5,7,0,1,2] 就是在做這件事）。
    private static readonly int[][] Lanes =
    [
        [0, 4, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [2, 4, 6],
        [0, 1, 2], [3, 4, 5], [6, 7, 8]
    ];

    private readonly Dictionary<ulong, double> memo = [];

    /// <summary>派彩表裡最小的非零值（線和 7 = 36 MGP）。</summary>
    public const int MinPayout = 36;

    /// <summary>派彩表裡最大的值（線和 6 = 10000 MGP）。</summary>
    public const int MaxPayout = 10000;

    /// <summary>
    /// 已開獎（九格全翻開）的盤面上，某一條線實際派彩多少 MGP。
    /// </summary>
    /// <param name="board">盤面，索引 0..8，0 代表未翻開。</param>
    /// <param name="lane">線索引，與 <c>AddonLotteryDaily.LaneTileSelector</c> 的 UI 順序一致
    /// （也就是 <see cref="SuggestLane"/> 的回傳值）。</param>
    /// <returns>派彩 MGP。這條線有任何一格還沒翻開、或線索引越界，一律回 0。</returns>
    /// <remarks>
    /// 🔑 這是<b>純查表</b>：派彩只看線和，表是遊戲常數（與求解器自己用的是同一份），
    /// 所以不必去讀面板上的派彩文字——沒有在地化字串、沒有節點版面假設，也沒有「解析失敗」這回事。
    /// ⚠️ 前提是呼叫端傳進來的線索引真的是玩家選中的那一條。本模組是自己送出選線的所以拿得到；
    /// 玩家自己手動選線時模組不知道選了哪條，那種情況呼叫端不要叫這個函式。
    /// </remarks>
    public static int PayoutFor(ReadOnlySpan<int> board, int lane)
    {
        if (lane is < 0 or >= TotalLanes || board.Length < TotalCells)
        {
            return 0;
        }

        var sum = 0;
        foreach (var idx in Lanes[lane])
        {
            var value = board[idx];
            if (value is < 1 or > 9)
            {
                // 還沒全部翻開＝還沒開獎，不猜。
                return 0;
            }

            sum += value;
        }

        return sum < Payouts.Length ? Payouts[sum] : 0;
    }

    /// <summary>翻格階段：回傳期望值最高的隱藏格索引（0..8；無格可翻回 -1）。</summary>
    public int SuggestCell(ReadOnlySpan<int> board)
    {
        memo.Clear();
        var work = board.ToArray();
        var best = -1;
        var bestEv = double.NegativeInfinity;
        for (var i = 0; i < TotalCells; i++)
        {
            if (work[i] != 0)
            {
                continue;
            }

            var ev = RevealEv(work, i);
            if (ev > bestEv + 1e-9)
            {
                bestEv = ev;
                best = i;
            }
        }

        return best;
    }

    /// <summary>選線階段（已翻滿 4 格）：回傳期望值最高的線（LaneSelector UI 索引 0..7）。</summary>
    public int SuggestLane(ReadOnlySpan<int> board)
    {
        var work = board.ToArray();
        var best = -1;
        var bestEv = double.NegativeInfinity;
        for (var lane = 0; lane < TotalLanes; lane++)
        {
            var ev = LaneEv(work, lane);
            if (ev > bestEv + 1e-9)
            {
                bestEv = ev;
                best = lane;
            }
        }

        return best;
    }

    /// <summary>翻開 cell 的期望值：對所有尚未出現的數字取平均的「翻開後盤面價值」。</summary>
    private double RevealEv(int[] board, int cell)
    {
        var sum = 0d;
        var count = 0;
        for (var v = 1; v <= 9; v++)
        {
            if (Contains(board, v))
            {
                continue;
            }

            board[cell] = v;
            sum += BoardValue(board);
            board[cell] = 0;
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>盤面價值：已翻滿 4 格時 = 最佳線期望；否則 = 最佳「下一翻」期望（遞迴）。</summary>
    private double BoardValue(int[] board)
    {
        var revealed = 0;
        foreach (var v in board)
        {
            if (v != 0)
            {
                revealed++;
            }
        }

        if (revealed >= RevealTarget)
        {
            var bestLane = 0d;
            for (var lane = 0; lane < TotalLanes; lane++)
            {
                var ev = LaneEv(board, lane);
                if (ev > bestLane)
                {
                    bestLane = ev;
                }
            }

            return bestLane;
        }

        var key = Encode(board);
        if (memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var best = 0d;
        for (var i = 0; i < TotalCells; i++)
        {
            if (board[i] != 0)
            {
                continue;
            }

            var ev = RevealEv(board, i);
            if (ev > best)
            {
                best = ev;
            }
        }

        memo[key] = best;
        return best;
    }

    /// <summary>單一線的期望派彩：對隱藏格的所有取值排列取 Payouts 平均（payout 只看線和，
    /// 排列與組合平均值相同）。</summary>
    private static double LaneEv(int[] board, int lane)
    {
        var baseSum = 0;
        Span<int> hidden = stackalloc int[3];
        var hiddenCount = 0;
        foreach (var idx in Lanes[lane])
        {
            var v = board[idx];
            if (v > 0)
            {
                baseSum += v;
            }
            else
            {
                hidden[hiddenCount++] = idx;
            }
        }

        if (hiddenCount == 0)
        {
            return Payouts[baseSum];
        }

        Span<int> unused = stackalloc int[9];
        var unusedCount = 0;
        for (var v = 1; v <= 9; v++)
        {
            if (!Contains(board, v))
            {
                unused[unusedCount++] = v;
            }
        }

        long total = 0;
        var perms = 0;
        switch (hiddenCount)
        {
            case 1:
                for (var i = 0; i < unusedCount; i++)
                {
                    total += Payouts[baseSum + unused[i]];
                    perms++;
                }

                break;
            case 2:
                for (var i = 0; i < unusedCount; i++)
                {
                    for (var j = 0; j < unusedCount; j++)
                    {
                        if (j == i)
                        {
                            continue;
                        }

                        total += Payouts[baseSum + unused[i] + unused[j]];
                        perms++;
                    }
                }

                break;
            default:
                for (var i = 0; i < unusedCount; i++)
                {
                    for (var j = 0; j < unusedCount; j++)
                    {
                        if (j == i)
                        {
                            continue;
                        }

                        for (var k = 0; k < unusedCount; k++)
                        {
                            if (k == i || k == j)
                            {
                                continue;
                            }

                            total += Payouts[baseSum + unused[i] + unused[j] + unused[k]];
                            perms++;
                        }
                    }
                }

                break;
        }

        return perms == 0 ? 0 : (double)total / perms;
    }

    private static bool Contains(int[] board, int value)
    {
        foreach (var v in board)
        {
            if (v == value)
            {
                return true;
            }
        }

        return false;
    }

    private static ulong Encode(int[] board)
    {
        var key = 0ul;
        for (var i = 0; i < TotalCells; i++)
        {
            key |= (ulong)board[i] << (i * 4);
        }

        return key;
    }
}
