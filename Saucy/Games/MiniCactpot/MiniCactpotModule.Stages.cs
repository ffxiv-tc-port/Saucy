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
    private void TickLaneStage(AddonLotteryDaily* addon, ReadOnlySpan<int> board)
    {
        // 🔴 已確認就不再重送：否則只要盤面停在「翻開 4 格」——伺服器還沒回應、
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
        var lane = confirmedLane >= 0 ? confirmedLane : solver.SuggestLane(board, ActivePayouts);
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
        boardCompleted = true;

        // 九格全翻開＝已開獎。派彩在關窗延遲之前就先算，免得延遲設成 0 時整個跳過。
        HandleJackpot(addon, board);

        if ((DateTime.UtcNow - closeArmedUtc.Value).TotalMilliseconds < CloseDelayMs)
        {
            return;
        }

        if (!EzThrottler.Throttle(ClickThrottleKey, ClickThrottleMs))
        {
            return;
        }

        // 🔴🔴 不可以在同一次呼叫內「Callback(-1) → 緊接 Close(true)」：Callback(-1) 生效後那幾幀窗仍在、
        // IsVisible／IsAddonReady 三關照過（GetLotteryDailyAddon 擋不住），第二下就是打在正在關的窗上（攔不到的存取違規）；
        // 節流下限 400ms 在低 FPS 只有 10 幀，正落在危險窗口內。
        // 所以守衛以 LotteryDaily 位址為鍵，一個守衛窗口只送一招；守衛走逃生口放行（＝窗 90 幀都沒收掉，
        // 上一招真的沒生效）時才輪到下一招。
        var unit = &addon->AtkUnitBase;
        if (!AddonPressGuard.TryBeginPress(
                AddonName, unit, AddonPressGuard.WholeWindowKey, AddonPressGuard.ReleaseEscapeFrames,
                out var viaEscape))
        {
            return;
        }

        var address = (nint)unit;
        closeChainStage = viaEscape && closeChainAddress == address ? closeChainStage + 1 : 0;
        closeChainAddress = address;

        // 這次進場已經完整跑過一張彩券＝版面確定建好了，下一張只需要短暖機。
        boardLayoutSeen = true;
        LastAction = LastPayoutMgp > 0
            ? $"領獎並關閉面板（本張派彩 {LastPayoutMgp} 金碟幣）"
            : "領獎並關閉面板";
        if (closeChainStage % 2 == 0)
        {
            Callback.Fire(unit, true, -1);
        }
        else
        {
            unit->Close(true);
        }
    }

    /// <summary>開獎後的「中獎」通知：算出這一張的實際派彩，達門檻就請 TataruPraise 念一句。</summary>
    /// <remarks>
    /// 🔴 <b>純讀取、零副作用。</b> 不碰盤面、不影響翻格/選線/關窗任何一步；IPC 失敗一律忽略。
    /// <para>📌 優先直讀面板給的中獎列派彩（伺服器真值，手動選線也算得出來）；讀不到才退回「模組自己送出的那條線」查表，那條退路才需要 <c>confirmedLane &gt;= 0</c>。兩條都不解析面板文字，與在地化無關。</para>
    /// <para>⚠️ 一張只處理一次，成功與否都設旗標：這個階段每幀都會進來，重試等於洗 IPC。</para>
    /// </remarks>
    private void HandleJackpot(AddonLotteryDaily* addon, ReadOnlySpan<int> board)
    {
        if (jackpotHandled)
        {
            return;
        }

        if (!TryReadActualPayout(addon, out var payout))
        {
            if (WithinJackpotReadGrace())
            {
                // 面板填中獎列可能比盤面慢幾幀，寬限期內先不定案。
                return;
            }

            jackpotHandled = true;
            if (confirmedLane < 0)
            {
                return;
            }

            payout = MiniCactpotSolver.PayoutFor(board, confirmedLane, ActivePayouts);
            Log($"面板沒給中獎列（UnkNumber3FC={addon->UnkNumber3FC}），退回用模組選的線 {confirmedLane} 查表：{payout} 金碟幣");
        }

        jackpotHandled = true;
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

        // 要使用者回報的診斷：這行要看得到才有價值。
        Log($"Payout {payout} MGP >= threshold {threshold}; asking TataruPraise to celebrate.");
        TataruPraise.TryPraiseJackpot();
    }

    /// <summary>中獎列的等待寬限：夾在關窗延遲之內 ⇒ 延遲設 0 時等於不等。</summary>
    private bool WithinJackpotReadGrace() =>
        closeArmedUtc != null &&
        (DateTime.UtcNow - closeArmedUtc.Value).TotalMilliseconds < Math.Min(CloseDelayMs, JackpotReadGraceMs);

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

        // 翻格不關窗，「同窗只按一次」不適用（一張要翻三格、同一格還會重試）；
        // 能加的只有「已經看過 PreFinalize／剛送過終結動作的實例不要再碰」——玩家中途手動關板就是那一種。
        if (!AddonPressGuard.TryTouch(AddonName, &addon->AtkUnitBase))
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

        // 同 TryClickCell：選線不關窗，只擋正在銷毀／剛送過終結動作的實例。
        if (!AddonPressGuard.TryTouch(AddonName, &addon->AtkUnitBase))
        {
            return false;
        }

        var laneValue = (int)(nodeId - LaneNodeIdFirst);
        addon->UnkNumber3D4 = laneValue;
        Callback.Fire(&addon->AtkUnitBase, true, 2, laneValue);
        return true;
    }
}
