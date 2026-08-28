using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Threading.Tasks;
using static ECommons.GenericHelpers;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;

namespace Saucy.MiniCactpot;

/// <summary>
/// 仙人微彩（Mini Cactpot，金蝶遊樂園每日刮刮樂）自動完成。互動序列依 DailyRoutines
/// AutoMiniCactpot 的設計重寫：LotteryDaily 面板開啟時，依期望值自動翻 3 格
/// （Callback(1, 格節點值)）、選線並確認（回寫選線欄位後 Callback(2, 線節點值)）、
/// 全部翻開後領獎關窗（Callback(-1) + Close），關窗後（可選）自動按下「購買下一張」
/// 確認框，把當日彩券一次完成。
/// 全程零 hook、零封包：只用 AddonLifecycle 事件 + addon callback 合成事件。
/// 階段判定不依賴 AgentLotteryDaily（API13 世代 FFXIVClientStructs 沒有這個 struct），
/// 改讀 addon 自己的 GameNumbers 盤面（0=未翻開）由已翻開格數推導：
/// 1..3 格=翻格階段、4 格=選線階段、9 格=領獎收尾（與 DR 讀 agent Status 1/2/4 等價）。
/// 模組未啟用時不註冊任何監聽——手動開面板玩不會被搶操作。
/// </summary>
public unsafe class MiniCactpotModule : Module
{
    private const string AddonName = "LotteryDaily"; // addon 內部名，跨語言用戶端一致（非在地化字串）

    private const string ClickThrottleKey = "Saucy.MiniCactpot.Click";

    /// <summary>面板消失超過這麼久就當成「重新進場」，下一張彩券恢復完整暖機。</summary>
    private const int VisitResetMs = 10000;

    /// <summary>選線確認送出後，隔多久沒有進展才允許重送一次。正常情況下一張彩券只會送出
    /// 一次 <c>Callback(2, lane)</c>；這個窗口只是為了在確認真的被吃掉時不要永久卡住。</summary>
    private const int LaneReconfirmMs = 5000;

    private const uint CellNodeIdFirst = 30;
    private const uint CellNodeIdLast = 38;
    private const uint LaneNodeIdFirst = 21;
    private const uint LaneNodeIdLast = 28;

    private readonly MiniCactpotSolver solver = new();

    /// <summary>保護底下所有背景求解狀態。</summary>
    private readonly object solveLock = new();

    private DateTime? boardReadyUtc;
    private DateTime? closeArmedUtc;
    private DateTime? addonGoneSinceUtc;
    private int pendingCell = -1;
    private int pendingCellRevealedCount = -1;
    private int confirmedLane = -1;
    private DateTime? confirmedLaneUtc;
    private bool boardLayoutSeen;

    /// <summary>這一張已經處理過開獎通知（不論有沒有真的念出來），避免每幀重試。</summary>
    private bool jackpotHandled;

    private bool solveInFlight;
    private bool hasSolvedCell;
    private ulong solvedBoardKey;
    private int solvedCell = -1;

    /// <summary>點擊間隔。做成設定，但夾在下限之上——理由見 Configuration 上的說明。</summary>
    private static int ClickThrottleMs => Math.Clamp(
        C.MiniCactpotClickIntervalMs,
        Configuration.MiniCactpotMinClickIntervalMs,
        Configuration.MiniCactpotMaxClickIntervalMs);

    /// <summary>全部翻開後先讓開獎動畫/派彩數字跑一下再關窗，玩家至少看得到中了多少。</summary>
    private static int CloseDelayMs => Math.Clamp(C.MiniCactpotCloseDelayMs, 0, Configuration.MiniCactpotMaxCloseDelayMs);

    /// <summary>請塔塔露提醒的派彩門檻（金碟幣），夾在派彩表的真實值域內。</summary>
    private static int JackpotThresholdMgp => Math.Clamp(
        C.MiniCactpotJackpotThresholdMgp,
        Configuration.MiniCactpotJackpotMinThresholdMgp,
        Configuration.MiniCactpotJackpotMaxThresholdMgp);

    public override string Name => "Mini Cactpot";

    /// <summary>給設定面板顯示的最近動作說明。</summary>
    public string LastAction { get; private set; } = "等待開啟仙人微彩面板";

    /// <summary>這一張開獎後的實際派彩（金碟幣）。還沒開獎、或這張不是模組選的線就是 0。</summary>
    public int LastPayoutMgp { get; private set; }

    public override void Enable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonEvent);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonEvent);
        Svc.Framework.Update += OnUpdate;
    }

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.AddonLifecycle.UnregisterListener(OnAddonEvent);
        TaskManager.Abort();
        ResetTicketState();
        boardLayoutSeen = false;
        addonGoneSinceUtc = null;
        LastAction = "等待開啟仙人微彩面板";
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                ResetTicketState();
                TaskManager.Abort();
                break;
            case AddonEvent.PreFinalize:
                ResetTicketState();
                if (C.MiniCactpotAutoPlayAgain)
                {
                    // 面板關閉後遊戲會自己跳「要購買下一張彩票嗎」確認框（購票長按鈕版面）。
                    // 用 TaskManager 重試等它出現（基底設定 5 秒逾時，逾時代表當日已完賽或
                    // 玩家先走人，靜默放棄）。
                    TaskManager.Abort();
                    TaskManager.Enqueue(TryPressPlayAgainYes, "MiniCactpot.PlayAgain");
                }

                break;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        var addon = GetLotteryDailyAddon();
        if (addon == null)
        {
            MinigameInputPacing.Reset(ref boardReadyUtc);
            addonGoneSinceUtc ??= DateTime.UtcNow;
            if (boardLayoutSeen &&
                (DateTime.UtcNow - addonGoneSinceUtc.Value).TotalMilliseconds >= VisitResetMs)
            {
                // 離開夠久＝下次是重新進場，恢復完整暖機。
                boardLayoutSeen = false;
            }

            return;
        }

        addonGoneSinceUtc = null;

        // 開面板後先暖機一小段，不跟面板初始化搶時序。但「自動購買下一張」會在同一次進場
        // 連開好幾次面板，版面早就建好了——第二張之後只留短緩衝，省下純粹的等待。
        var warmupMs = boardLayoutSeen
            ? MinigameInputPacing.RepeatBoardWarmupMs
            : MinigameInputPacing.BoardWarmupMs;
        if (!MinigameInputPacing.TryMarkWarmup(ref boardReadyUtc, warmupMs))
        {
            return;
        }

        Span<int> board = stackalloc int[MiniCactpotSolver.TotalCells];
        var revealed = 0;
        for (var i = 0; i < MiniCactpotSolver.TotalCells; i++)
        {
            var value = addon->GameNumbers[i];
            if (value is >= 1 and <= 9)
            {
                board[i] = value;
                revealed++;
            }
        }

        if (revealed != MiniCactpotSolver.TotalCells)
        {
            closeArmedUtc = null;
        }

        switch (revealed)
        {
            // 0 = 盤面資料還沒到，等。
            case >= 1 and < MiniCactpotSolver.RevealTarget:
                TickCellStage(addon, board, revealed);
                break;
            case MiniCactpotSolver.RevealTarget:
                TickLaneStage(addon, board);
                break;
            case MiniCactpotSolver.TotalCells:
                TickCloseStage(addon, board);
                break;
            // 5..8 = 選線後開獎資料翻開中，等它到齊。
        }
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
    /// <para>🔴 原本的寫法是在 <c>EzThrottler.Throttle</c> **之前**直接呼叫
    /// <c>solver.SuggestCell(board)</c>。首步求解實測約 30 ms 而且跑在 framework 執行緒上，
    /// 又因為只有點擊成功才會設 <c>pendingCell</c>，只要點擊一直沒成功、或是正處在兩次點擊之間的
    /// 節流窗內，就會**每一幀都重算一次** —— 800 ms 的節流窗等於連續數十幀各掉一次影格。</para>
    /// <para>🔑 結果的版本號用**盤面本身**（9 格各 4 bit 編碼成 ulong），不是已翻開的格數：
    /// 同一次進場會連續玩好幾張彩券，而每張新彩券都從「翻開 1 格」重新開始，
    /// 光用格數當版本號會把上一張彩券的答案誤認成這一張的。盤面編碼是精確身分，
    /// 背景結果回來時只要盤面已經變了就對不上，自動被忽略。</para>
    /// <para>⚠️ 執行緒安全：同一時間只允許一個背景求解（<c>solveInFlight</c> 閘門），所以
    /// <c>SuggestCell</c> 內部的記憶化字典不會被並行存取。選線階段的 <c>SuggestLane</c> 仍在
    /// framework 執行緒上同步呼叫，兩者可能同時發生——目前安全，因為 <c>SuggestLane</c> 完全
    /// 不碰那個字典（只用 static 的賠付表與線表）。**若日後要為 SuggestLane 加記憶化，
    /// 必須連同這裡的執行緒模型一起改。**</para></summary>
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
        _ = Task.Run(() => RunSolve(key, snapshot));
        return false;
    }

    private void RunSolve(ulong key, int[] snapshot)
    {
        var result = -1;
        try
        {
            result = solver.SuggestCell(snapshot);
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

    private void TickLaneStage(AddonLotteryDaily* addon, ReadOnlySpan<int> board)
    {
        // 🔴 已確認就不再重送。原本沒有這道閂：只要盤面停在「翻開 4 格」——伺服器還沒回應、
        // 或玩家自己把面板放著不動——每 800 ms 就會再送一次 Callback(2, lane)。
        // 這個窗口只是為了在確認真的被吃掉時能救回來，正常一張彩券只會送出一次。
        if (confirmedLane >= 0 &&
            confirmedLaneUtc != null &&
            (DateTime.UtcNow - confirmedLaneUtc.Value).TotalMilliseconds < LaneReconfirmMs)
        {
            return;
        }

        if (!EzThrottler.Throttle(ClickThrottleKey, ClickThrottleMs))
        {
            return;
        }

        // 重送時沿用同一條線，絕不改變已經送出去的選擇（同 pendingCell 的原則）。
        var lane = confirmedLane >= 0 ? confirmedLane : solver.SuggestLane(board);
        if (lane < 0)
        {
            return;
        }

        if (TryClickLane(addon, lane))
        {
            var resend = confirmedLane >= 0;
            confirmedLane = lane;
            confirmedLaneUtc = DateTime.UtcNow;
            LastAction = $"選線並確認（UI 線 {lane}）";
            Log($"Confirm lane {lane}{(resend ? " (re-send)" : string.Empty)}");
        }
    }

    private void TickCloseStage(AddonLotteryDaily* addon, ReadOnlySpan<int> board)
    {
        closeArmedUtc ??= DateTime.UtcNow;

        // 九格全翻開＝已開獎。派彩在關窗延遲之前就先算，免得延遲設成 0 時整個跳過。
        HandleJackpot(board);

        if ((DateTime.UtcNow - closeArmedUtc.Value).TotalMilliseconds < CloseDelayMs)
        {
            return;
        }

        if (!EzThrottler.Throttle(ClickThrottleKey, ClickThrottleMs))
        {
            return;
        }

        // 這次進場已經完整跑過一張彩券＝版面確定建好了，下一張只需要短暖機。
        boardLayoutSeen = true;
        LastAction = LastPayoutMgp > 0
            ? $"領獎並關閉面板（本張派彩 {LastPayoutMgp} 金碟幣）"
            : "領獎並關閉面板";
        Callback.Fire(&addon->AtkUnitBase, true, -1);
        addon->AtkUnitBase.Close(true);
    }

    /// <summary>
    /// 開獎後的「中獎」通知：算出這一張的實際派彩，達門檻就請 TataruPraise 念一句。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>純讀取、零副作用。</b> 不碰盤面、不影響翻格/選線/關窗任何一步；IPC 失敗一律忽略。
    /// <para>📌 派彩是<b>查表</b>算出來的（線和 → 派彩表），不解析面板文字，所以與在地化無關。
    /// 只在模組自己送出過選線（<c>confirmedLane &gt;= 0</c>）時才算——玩家自己手動選線的話
    /// 模組不知道他選了哪一條，寧可不念也不猜。</para>
    /// <para>⚠️ 一張只處理一次，成功與否都設旗標：這個階段每幀都會進來，重試等於洗 IPC。</para>
    /// </remarks>
    private void HandleJackpot(ReadOnlySpan<int> board)
    {
        if (jackpotHandled)
        {
            return;
        }

        jackpotHandled = true;

        if (confirmedLane < 0)
        {
            return;
        }

        var payout = MiniCactpotSolver.PayoutFor(board, confirmedLane);
        LastPayoutMgp = payout;
        if (payout <= 0 || !C.MiniCactpotJackpotTataruPraise)
        {
            return;
        }

        var threshold = JackpotThresholdMgp;
        if (payout < threshold)
        {
            return;
        }

        // 使用者跑 LogLevel 2，這行要看得到才有診斷價值。
        Log($"Payout {payout} MGP >= threshold {threshold}; asking TataruPraise to celebrate.");
        TataruPraise.TryPraiseJackpot();
    }

    /// <summary>翻格：格子的 callback 值取自其元件節點 NodeId（30..38 → 0..8，與陣列索引
    /// 是兩套編號，所以照 DR 的做法從節點反推，不假設同序）。</summary>
    private bool TryClickCell(AddonLotteryDaily* addon, int cellIndex)
    {
        var checkbox = addon->GameBoard[cellIndex];
        if (checkbox == null)
        {
            return false;
        }

        var owner = checkbox->AtkComponentButton.AtkComponentBase.OwnerNode;
        if (owner == null)
        {
            return false;
        }

        var nodeId = owner->AtkResNode.NodeId;
        if (nodeId is < CellNodeIdFirst or > CellNodeIdLast)
        {
            return false;
        }

        Callback.Fire(&addon->AtkUnitBase, true, 1, (int)(nodeId - CellNodeIdFirst));
        return true;
    }

    /// <summary>選線＋確認：先把選線值回寫 addon 的目前選線欄位（0x3EC，DR 以 +1004 硬指標
    /// 寫的就是這一欄；我們的 FFXIVClientStructs 已具名為 UnkNumber3D4），再發 Callback(2, 線值)。</summary>
    private bool TryClickLane(AddonLotteryDaily* addon, int laneIndex)
    {
        var radio = addon->LaneSelector[laneIndex];
        if (radio == null)
        {
            return false;
        }

        var owner = radio->AtkComponentButton.AtkComponentBase.OwnerNode;
        if (owner == null)
        {
            return false;
        }

        var nodeId = owner->AtkResNode.NodeId;
        if (nodeId is < LaneNodeIdFirst or > LaneNodeIdLast)
        {
            return false;
        }

        var laneValue = (int)(nodeId - LaneNodeIdFirst);
        addon->UnkNumber3D4 = laneValue;
        Callback.Fire(&addon->AtkUnitBase, true, 2, laneValue);
        return true;
    }

    /// <summary>「購買下一張」確認：只按仙人微彩自己的購票確認框——同時要求
    /// (a) 購票長按鈕版面（排除同 agent 的「中止遊玩」等一般是/否框）、
    /// (b) LotteryDaily agent 歸屬或作用中、(c) 不是系統性提示。全部用 agent/版面判定，
    /// 零文字比對，台服字串無關。</summary>
    private bool? TryPressPlayAgainYes()
    {
        if (!IsEnabled || !C.MiniCactpotAutoPlayAgain)
        {
            return true; // 中途被關掉：安靜結束
        }

        if (!SelectYesnoHelper.TryGetVisible(out var yesno))
        {
            return false; // 還沒出現，繼續等（TaskManager 逾時自動放棄）
        }

        if (!SelectYesnoHelper.IsTicketPurchaseLayout(yesno) ||
            !SelectYesnoHelper.ShouldPressLotteryYesno(yesno, AgentId.LotteryDaily))
        {
            return false;
        }

        if (!SelectYesnoHelper.PressYes(yesno))
        {
            return false;
        }

        LastAction = "已確認購買下一張彩券";
        return true;
    }

    private void ResetTicketState()
    {
        MinigameInputPacing.Reset(ref boardReadyUtc);
        closeArmedUtc = null;
        pendingCell = -1;
        pendingCellRevealedCount = -1;
        confirmedLane = -1;
        confirmedLaneUtc = null;
        jackpotHandled = false;
        LastPayoutMgp = 0;

        lock (solveLock)
        {
            // 進行中的求解不取消（沒有副作用）——它的結果會因為盤面 key 對不上而被忽略。
            hasSolvedCell = false;
            solvedCell = -1;
            solvedBoardKey = 0;
        }
    }

    private static AddonLotteryDaily* GetLotteryDailyAddon()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !addon->IsVisible || !IsAddonReady(addon))
        {
            return null;
        }

        return (AddonLotteryDaily*)addon;
    }
}
