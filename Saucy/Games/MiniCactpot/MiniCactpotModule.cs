using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using System;
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
    private const int ClickThrottleMs = 800;

    /// <summary>全部翻開後先讓開獎動畫/派彩數字跑一下再關窗（DR 是立刻關；這裡放緩，
    /// 玩家至少看得到中了多少）。</summary>
    private const int CloseDelayMs = 1600;

    private const uint CellNodeIdFirst = 30;
    private const uint CellNodeIdLast = 38;
    private const uint LaneNodeIdFirst = 21;
    private const uint LaneNodeIdLast = 28;

    private readonly MiniCactpotSolver solver = new();

    private DateTime? boardReadyUtc;
    private DateTime? closeArmedUtc;
    private int pendingCell = -1;
    private int pendingCellRevealedCount = -1;

    public override string Name => "Mini Cactpot";

    /// <summary>給設定面板顯示的最近動作說明。</summary>
    public string LastAction { get; private set; } = "等待開啟仙人微彩面板";

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
            return;
        }

        // 開面板後先暖機一小段（同其他小遊戲模組的節奏），不跟面板初始化搶時序。
        if (!MinigameInputPacing.TryMarkWarmup(ref boardReadyUtc))
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
                TickCloseStage(addon);
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
        else
        {
            cell = solver.SuggestCell(board);
            if (cell < 0)
            {
                return;
            }
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
            LogDebug($"Reveal cell {cell} (revealed {revealed})");
        }
    }

    private void TickLaneStage(AddonLotteryDaily* addon, ReadOnlySpan<int> board)
    {
        if (!EzThrottler.Throttle(ClickThrottleKey, ClickThrottleMs))
        {
            return;
        }

        var lane = solver.SuggestLane(board);
        if (lane < 0)
        {
            return;
        }

        if (TryClickLane(addon, lane))
        {
            LastAction = $"選線並確認（UI 線 {lane}）";
            LogDebug($"Confirm lane {lane}");
        }
    }

    private void TickCloseStage(AddonLotteryDaily* addon)
    {
        closeArmedUtc ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - closeArmedUtc.Value).TotalMilliseconds < CloseDelayMs)
        {
            return;
        }

        if (!EzThrottler.Throttle(ClickThrottleKey, ClickThrottleMs))
        {
            return;
        }

        LastAction = "領獎並關閉面板";
        Callback.Fire(&addon->AtkUnitBase, true, -1);
        addon->AtkUnitBase.Close(true);
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
