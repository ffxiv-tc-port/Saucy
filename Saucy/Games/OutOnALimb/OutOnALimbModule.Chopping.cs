using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using static ECommons.GenericHelpers;
namespace Saucy.OutOnALimb;

public unsafe partial class OutOnALimbModule
{
    /// <summary>第二階段：砍伐。輪到玩家時算出目標刻度，等指針掃過去再按。</summary>
    private void RunChoppingPhase()
    {
        var addon = LimbBoard.TryGetAddon(LimbBoard.BotanistAddon);
        if (addon == null)
        {
            lastBotanistCursor = null;
            return;
        }

        HandleDoubleDownPrompt(addon);

        // 🔴 順序有意義：換樹判定必須跑在回饋收集之前。
        // 換樹時還在等回饋的那一刀屬於**上一棵樹**，記到新盤面上就是把雜訊當成學習。
        TrackTreeBoundary(addon);
        CollectGaugeFeedback(addon);

        var state = LimbBoard.ReadState(addon);
        if (state == null)
        {
            // 讀不到階段就什麼都不做，也**不要更新 lastState** ——
            // 「不知道」不是一個階段，把它當成階段會製造假的階段轉換。
            if (EzThrottler.Throttle(StateDiagThrottleKey, StateDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] board state (AtkValue[0]) unreadable this frame; standing by");
            }

            return;
        }

        if (state.Value == LimbBoard.StatePlayerTurn)
        {
            if (lastState != LimbBoard.StatePlayerTurn)
            {
                HandleTurnStart(addon);
            }

            TrySwingAtTarget(addon);
        }

        lastState = state;
    }

    /// <summary>
    /// 每一幀判斷「是不是換了一棵新的樹」。
    /// 🔴 **這裡刻意完全不看階段值。**一刀的循環是 <c>3 →(4)→ 5 → 7 → 3</c>——5 與 7 每一刀都會出現，拿階段值判換樹的話**每一刀都會被判成換樹**，解題器永遠回到初始狀態。
    /// 只採信兩個單調性訊號：剩餘揮擊次數**回升**、以及量表上限改變。兩者都不需要知道任何階段值的語意，改版動了階段機也不會壞。
    /// </summary>
    private void TrackTreeBoundary(AtkUnitBase* addon)
    {
        var swingsLeft = LimbBoard.ReadSwingsLeft(addon);
        var gaugeMax = LimbBoard.ReadGaugeMax(addon);
        var gauge = LimbBoard.ReadGauge(addon);

        if (swingsLeft != null && swingsLeft.Value > maxSwingsSeen)
        {
            maxSwingsSeen = swingsLeft.Value;
        }

        string? reason = null;
        if (swingsLeft != null && lastSwingsLeft != null && swingsLeft.Value > lastSwingsLeft.Value)
        {
            reason = $"揮擊計數器回升 {lastSwingsLeft.Value}→{swingsLeft.Value}";
        }
        else if (gaugeMax != null && lastGaugeMax != null && gaugeMax.Value != lastGaugeMax.Value)
        {
            reason = $"量表上限改變 {lastGaugeMax.Value}→{gaugeMax.Value}";
        }

        if (reason != null)
        {
            StartNewTree(reason, swingsLeft, gauge, gaugeMax);
        }

        // 兩個訊號都讀不到就等於沒有樹邊界判據——這件事使用者必須看得到，不能靜默降級。
        if (swingsLeft == null && gaugeMax == null &&
            EzThrottler.Throttle(SwingsDiagThrottleKey, SwingsDiagThrottleMs))
        {
            Svc.Log.Information("[OutOnALimb] neither the swing counter (AtkValue[11]) nor the gauge max " +
                                "(AtkValue[13]) is readable; tree boundaries cannot be detected, " +
                                "the solver will keep learning across trees");
            LastAction = "讀不到揮擊計數器，無法判斷換樹";
        }

        lastSwingsLeft = swingsLeft ?? lastSwingsLeft;
        lastGaugeMax = gaugeMax ?? lastGaugeMax;
    }

    /// <summary>輪到玩家的那一幀：補一個只有在這個時機才成立的換樹判據，再算出這一刀要瞄哪裡。</summary>
    private void HandleTurnStart(AtkUnitBase* addon)
    {
        var gaugeMax = LimbBoard.ReadGaugeMax(addon);
        var gauge = LimbBoard.ReadGauge(addon);
        var swingsLeft = LimbBoard.ReadSwingsLeft(addon);

        // 🔑 「計數器全滿，但這棵樹我們已經砍過了」＝中間換過樹而我們沒看到計數器回頭。
        // ⚠️ 這條**只能在輪到玩家的那一幀判**：計數器是在結果階段才遞減的，
        //    剛揮完的那幾幀它還停在舊值，每幀判會把同一棵樹的第一刀誤判成換樹。
        if (swingsLeft != null && maxSwingsSeen > 0 &&
            swingsLeft.Value == maxSwingsSeen && swingsRecordedThisTree > 0)
        {
            StartNewTree($"計數器已滿（{swingsLeft.Value}）但這棵樹已經砍過 {swingsRecordedThisTree} 刀",
                         swingsLeft, gauge, gaugeMax);
        }

        lastGauge = gauge ?? lastGauge;

        nextTarget = solver.GetNextTargetCursorPos();
        lastBotanistCursor = null;

        var best = solver.Best;
        Svc.Log.Information($"[OutOnALimb] turn start: swings={swingsLeft?.ToString() ?? "?"}/{maxSwingsSeen}, " +
                            $"tried={solver.ObservedCount} contact={solver.ContactCount} " +
                            $"wasted={wastedSwingsThisTree}, " +
                            $"best={(best == null ? "-" : $"{best.Position}({HitPowerText.Of(best.Power)})")}, " +
                            $"target={nextTarget?.ToString() ?? "-"}");
    }

    /// <summary>
    /// **補強**回饋：樹的量表在砍完之後掉了多少。
    /// 只當額外訊號——它有資料就採信，沒資料也不影響收斂；手感仍是主判據，因為盲掃階段砍不中就沒有傷害可量。
    /// </summary>
    private void CollectGaugeFeedback(AtkUnitBase* addon)
    {
        if (pendingCursor == null)
        {
            WatchIdleGauge(addon);
            return;
        }

        if (!gaugeLooksPerSwing)
        {
            return;
        }

        if (pendingSinceUtc != null && DateTime.UtcNow - pendingSinceUtc.Value > GaugeWaitTimeout)
        {
            Svc.Log.Information($"[OutOnALimb] no gauge change {GaugeWaitTimeout.TotalSeconds:0}s after swinging " +
                                $"at {pendingCursor.Value}; dropping the sample");
            pendingCursor = null;
            gaugeAtSwing = null;
            pendingSinceUtc = null;
            return;
        }

        var gauge = LimbBoard.ReadGauge(addon);
        if (gauge == null || gaugeAtSwing == null || gauge.Value == gaugeAtSwing.Value)
        {
            return;
        }

        var cursor = pendingCursor.Value;
        var delta = gaugeAtSwing.Value - gauge.Value;
        pendingCursor = null;
        gaugeAtSwing = null;
        pendingSinceUtc = null;
        lastGauge = gauge;
        idleGauge = gauge;
        gaugeMovedThisTree = true;

        // 📌 方向是實測的：實機看到砍倒那一刀量表 10→0，
        // 也就是**變小＝這一刀有傷害**。變大只可能是換了一棵樹（重新填滿），那不是成績。
        if (delta <= 0)
        {
            Svc.Log.Information($"[OutOnALimb] gauge went up by {-delta} after swinging at {cursor} " +
                                $"(now {gauge.Value}); treating it as a board change, not a result");
            return;
        }

        solver.Record(cursor, HitPower.Unobserved, delta);
        LastAction = $"刻度 {cursor} 的量表落差：{delta}";
        Svc.Log.Information($"[OutOnALimb] swing at {cursor} moved the gauge by -{delta} (now {gauge.Value})");
    }

    /// <summary>沒有等待中的刀時盯著量表。它應該是靜止的——如果每幀都在跳，
    /// 那它就不是「這一刀砍得多準」的訊號，這條路徑要整條關掉，改用聊天備援。</summary>
    private void WatchIdleGauge(AtkUnitBase* addon)
    {
        if (!gaugeLooksPerSwing)
        {
            return;
        }

        var gauge = LimbBoard.ReadGauge(addon);
        if (gauge == null)
        {
            return;
        }

        if (idleGauge != null && idleGauge.Value != gauge.Value)
        {
            idleGaugeChanges++;
            if (idleGaugeChanges >= IdleGaugeChangeLimit)
            {
                gaugeLooksPerSwing = false;
                Svc.Log.Information($"[OutOnALimb] AtkValue[12] changed {idleGaugeChanges} times while idle " +
                                    "within one tree; it is not a per-swing value on this client. " +
                                    "Falling back to the chat feedback path for the rest of this session.");
                LastAction = "量表欄位不像是每刀變化，已改用系統訊息回饋";
            }
        }

        idleGauge = gauge;
    }

    private void TrySwingAtTarget(AtkUnitBase* addon)
    {
        if (nextTarget == null)
        {
            return;
        }

        var cursor = LimbBoard.ReadCursor();
        if (cursor == null)
        {
            if (EzThrottler.Throttle(CursorDiagThrottleKey, CursorDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] cursor position unavailable; not swinging");
                LastAction = "沒有出手（讀不到指針位置）";
            }

            return;
        }

        var targetRaw = LimbBoard.ToRawScale(nextTarget.Value);

        // 容許誤差夾在 1–4（0–100 顯示刻度），換算成 0–10000 的原始刻度。
        var toleranceRaw = Math.Clamp(Cfg.Tolerance, 1, 4) * (LimbBoard.CursorScale / 100);
        var inside = Math.Abs(cursor.Value - targetRaw) < toleranceRaw;
        var crossed = HasCrossed(lastBotanistCursor, cursor.Value, targetRaw);
        lastBotanistCursor = cursor.Value;

        if (!inside && !crossed)
        {
            return;
        }

        if (!TryClickButton(LimbBoard.BotanistAddon, addon, LimbBoard.BotanistSwingButtonId, SwingThrottleKey, SwingThrottleMs))
        {
            return;
        }

        var display = LimbBoard.ToDisplayScale(cursor.Value);
        var requested = nextTarget.Value;

        // 🔴 落點與目標不是同一件事，而且偏差是**系統性的**：
        // 落到隔壁格本身可以接受（資訊差不多），但如果那個隔壁格**已經試過了**，這一刀就是零收穫。真正的對策在解題器那邊（挑候選時優先選左右鄰居也沒試過的格子，見 <see cref="LimbSolver"/> 的 NearestUntried）；這裡只負責**量**，不做決策。
        // 🔴 這裡刻意**不**去把打不到的目標標成「以後不要挑」。離線量測（tools/limbsim 閘門⑥與變體比較）顯示那樣做只會讓重複要求變少、命中率**反而略降**，在感應半徑最小（只有正中才算命中）的假設下降得最明顯——因為那等於把一個可能就是最佳點的格子丟掉。**症狀改善、結果變差的典型。**
        // ⚠️ 這個判斷必須在 solver.Record 之前做，否則問到的是這一刀自己剛記進去的結果。
        var wasted = display != requested && solver.IsObserved(display);
        if (wasted)
        {
            wastedSwingsThisTree++;
        }

        pendingCursor = display;
        gaugeAtSwing = LimbBoard.ReadGauge(addon);
        pendingSinceUtc = DateTime.UtcNow;
        swingsRecordedThisTree++;
        LastAction = $"揮斧於刻度 {display}（目標 {requested}）";

        // 📌 `aimOffset` 是下一次實跑要拿來裁決「要不要動出手時機」的唯一依據：
        //    現在的出手條件是「第一個落進容許窗的影格」，所以落點會偏向掃描方向的後方。
        //    要判斷值不值得改，需要的是這個偏差在不同機台速度（AtkValue[2]＝375／750）下的分布，
        //    那件事離線推不出來。⇒ 先把它印出來，不要憑推論改出手時機。
        Svc.Log.Information($"[OutOnALimb] swing at raw {cursor.Value} (display {display}, " +
                            $"target {requested}, aimOffset {display - requested:+0;-0;0} ticks " +
                            $"/ {cursor.Value - LimbBoard.ToRawScale(requested):+0;-0;0} raw, " +
                            $"{(crossed ? "crossing" : "inside")}, " +
                            $"gauge {gaugeAtSwing?.ToString() ?? "?"}, " +
                            $"swing #{swingsRecordedThisTree} of this tree" +
                            $"{(wasted ? $"; NO NEW INFO (cell {display} was already known; {wastedSwingsThisTree} such swings this tree)" : string.Empty)})");
        nextTarget = null;
    }

    /// <summary>「挑戰翻倍」確認框。預設不碰——由玩家自己決定要不要續戰。</summary>
    private void HandleDoubleDownPrompt(AtkUnitBase* addon)
    {
        if (!Cfg.AutoContinue)
        {
            return;
        }

        if (!SelectYesnoHelper.TryGetVisible(out var yesno) || !SelectYesnoHelper.IsArcadeYesno(yesno))
        {
            return;
        }

        if (!EzThrottler.Throttle(YesnoThrottleKey, YesnoThrottleMs))
        {
            return;
        }

        // 讀不到剩餘時間就一律收手：寧可少玩一輪，也不要在時間不夠時押下去。
        var seconds = LimbBoard.ReadSecondsRemaining(addon);
        var keepPlaying = seconds != null && seconds.Value > Cfg.StopAtSecondsRemaining;
        Svc.Log.Information(
            $"[OutOnALimb] double-down prompt: remaining={seconds?.ToString() ?? "unknown"}s, " +
            $"threshold={Cfg.StopAtSecondsRemaining}s -> {(keepPlaying ? "yes" : "no")}");

        if (keepPlaying)
        {
            SelectYesnoHelper.PressYes(yesno);
            LastAction = $"續戰（剩餘 {seconds}秒）";
        }
        else
        {
            SelectYesnoHelper.PressNo(yesno);
            LastAction = "時間不足，本輪收工";
        }
    }
}
