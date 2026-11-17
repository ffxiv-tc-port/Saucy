using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.LanguageHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static ECommons.GenericHelpers;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
using ConditionFlag = Dalamud.Game.ClientState.Conditions.ConditionFlag;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Saucy.MiniCactpot;

public unsafe partial class MiniCactpotModule
{
    /// <summary>取這一幀的 AtkValues。契約：只在 framework 執行緒呼叫，回傳的 span 不可跨幀保存。</summary>
    /// <remarks>數量與離線鑑識到的版面不符就回空——面板還沒填好、或版面變了，兩種都不要猜。</remarks>
    private static Span<AtkValue> GetLotteryAtkValues(AddonLotteryDaily* addon)
    {
        var unit = &addon->AtkUnitBase;
        if (unit->AtkValues == null || unit->AtkValuesCount != AtkValueCount)
        {
            return default;
        }

        return unit->AtkValuesSpan;
    }

    /// <summary>把面板上的派彩表（19 列，線和 6..24）讀進 <c>runtimePayouts</c>。</summary>
    /// <remarks>
    /// 🔴 fail-closed：任何一列不是 Int 型別、或值落在派彩值域外，整張表都不採用（這一幀退回硬編表）。
    /// ⚠️ <c>AtkValue.Int</c> 是 union 欄位，型別是 <c>Undefined</c> 時照樣讀得出 0——「沒設」與「0」分不出來，所以型別檢查不能省。
    /// </remarks>
    private bool TryReadPayoutTable(AddonLotteryDaily* addon)
    {
        var values = GetLotteryAtkValues(addon);
        if (values.IsEmpty)
        {
            return false;
        }

        var differs = false;
        for (var k = 0; k < MiniCactpotSolver.PayoutRowCount; k++)
        {
            ref var value = ref values[PayoutValueFirst + k];
            if (value.Type != ValueType.Int)
            {
                return false;
            }

            var mgp = value.Int;
            if (mgp is < MiniCactpotSolver.MinPayout or > MiniCactpotSolver.MaxPayout)
            {
                return false;
            }

            var sum = MiniCactpotSolver.MinLaneSum + k;
            runtimePayouts[sum] = mgp;
            differs |= mgp != fallbackPayouts[sum];
        }

        if (differs && !payoutTableDiffLogged)
        {
            payoutTableDiffLogged = true;
            var table = string.Join(',', runtimePayouts[MiniCactpotSolver.MinLaneSum..]);
            Log($"面板派彩表與硬編表不同，改用面板的值：{table}");
        }

        return true;
    }

    /// <summary>直接讀這一張真正拿到的金碟幣。</summary>
    /// <remarks>
    /// <c>UnkNumber3FC</c> ＝ 開獎後的中獎列索引（線和 − 6，−1 ＝ 未開獎），對應的 <c>AtkValues</c> 那一列就是伺服器給的派彩值。
    /// 🔑 這條不依賴「玩家選的是模組送出的那條線」，玩家自己手動選線也讀得到。
    /// </remarks>
    private bool TryReadActualPayout(AddonLotteryDaily* addon, out int mgp)
    {
        mgp = 0;
        var row = addon->UnkNumber3FC;
        if (row is < 0 or >= MiniCactpotSolver.PayoutRowCount)
        {
            return false;
        }

        var values = GetLotteryAtkValues(addon);
        if (values.IsEmpty)
        {
            return false;
        }

        ref var value = ref values[PayoutValueFirst + row];
        if (value.Type != ValueType.Int ||
            value.Int is < MiniCactpotSolver.MinPayout or > MiniCactpotSolver.MaxPayout)
        {
            return false;
        }

        mgp = value.Int;
        return true;
    }

    private void TickCellStage(AddonLotteryDaily* addon, ReadOnlySpan<int> board, int revealed)
    {
        int cell;
        if (pendingCell >= 0 && revealed == pendingCellRevealedCount)
        {
            // 上一次點的格子還沒翻開（伺服器回應中或點擊被吃掉）：只重試同一格，
            // 絕不在回應揭曉前改點別格。
            cell = pendingCell;
        }
        else if (!TryGetSuggestedCell(board, out cell))
        {
            // 求解還沒回來。求解跑在背景執行緒，這一幀就什麼都不做。
            return;
        }

        if (!EzThrottler.Throttle(ClickThrottleKey, ClickThrottleMs))
        {
            return;
        }

        if (TryClickCell(addon, cell))
        {
            pendingCell = cell;
            pendingCellRevealedCount = revealed;
            LastAction = $"翻開第 {cell + 1} 格（已翻 {revealed}/{MiniCactpotSolver.RevealTarget}）";
            Log($"Reveal cell {cell} (revealed {revealed})");
        }
    }

    /// <summary>取得建議翻開的格子；沒有現成答案就開一次背景求解並回 false。
    /// <para>🔴 不可以在 <c>EzThrottler.Throttle</c> **之前**直接呼叫<c>solver.SuggestCell(board)</c>：首步求解約 30 ms 而且跑在 framework 執行緒上，又因為只有點擊成功才會設 <c>pendingCell</c>，只要點擊一直沒成功、或是正處在兩次點擊之間的節流窗內，就會**每一幀都重算一次** —— 800 ms 的節流窗等於連續數十幀各掉一次影格。</para>
    /// <para>🔑 結果的版本號用**盤面本身**（9 格各 4 bit 編碼成 ulong），不是已翻開的格數：同一次進場會連續玩好幾張彩券，而每張新彩券都從「翻開 1 格」重新開始，光用格數當版本號會把上一張彩券的答案誤認成這一張的。盤面編碼是精確身分，背景結果回來時只要盤面已經變了就對不上，自動被忽略。</para>
    /// <para>⚠️ 執行緒安全：同一時間只允許一個背景求解（<c>solveInFlight</c> 閘門），所以<c>SuggestCell</c> 內部的記憶化字典不會被並行存取。選線階段的 <c>SuggestLane</c> 仍在framework 執行緒上同步呼叫，兩者可能同時發生——目前安全，因為 <c>SuggestLane</c> 完全不碰那個字典（只用 static 的賠付表與線表）。**若日後要為 SuggestLane 加記憶化，必須連同這裡的執行緒模型一起改。**</para></summary>
    private bool TryGetSuggestedCell(ReadOnlySpan<int> board, out int cell)
    {
        cell = -1;
        var key = EncodeBoard(board);

        lock (solveLock)
        {
            if (hasSolvedCell && solvedBoardKey == key)
            {
                cell = solvedCell;
                return cell >= 0;
            }

            if (solveInFlight)
            {
                // 已經有一個求解在跑。就算它算的是別的盤面也不必取消——求解沒有副作用，
                // 結果回來時會因為 key 對不上而被忽略，下一幀再開新的。
                return false;
            }

            solveInFlight = true;
        }

        // Span 不能被 lambda 捕獲，先複製一份再送進背景執行緒。
        var snapshot = board.ToArray();

        // 派彩表也要拍快照：背景求解期間 framework 執行緒還在每幀重填 runtimePayouts。
        var payouts = (int[])ActivePayouts.Clone();
        _ = Task.Run(() => RunSolve(key, snapshot, payouts));
        return false;
    }

    private void RunSolve(ulong key, int[] snapshot, int[] payouts)
    {
        var result = -1;
        try
        {
            result = solver.SuggestCell(snapshot, payouts);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[MiniCactpot] Background cell solve failed");
        }
        finally
        {
            lock (solveLock)
            {
                // 一律照 key 記錄。盤面若在求解期間變了，這筆結果就永遠對不上、等同被丟棄。
                solvedBoardKey = key;
                solvedCell = result;
                hasSolvedCell = true;
                solveInFlight = false;
            }
        }
    }

    /// <summary>把盤面編成 ulong 當版本號（每格 0..9 佔 4 bit）。</summary>
    private static ulong EncodeBoard(ReadOnlySpan<int> board)
    {
        var key = 0ul;
        for (var i = 0; i < MiniCactpotSolver.TotalCells; i++)
        {
            key |= (ulong)(uint)board[i] << (i * 4);
        }

        return key;
    }
}
