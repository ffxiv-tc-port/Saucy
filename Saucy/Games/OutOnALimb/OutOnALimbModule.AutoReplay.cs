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
    /// <summary>
    /// 連續遊玩：一局結束後自己跟機台開下一局。
    /// 🔴 三層閘門，每一層都必須成立才會動作：設定要打開、玩家要按過「開始連續遊玩」、而且局數還沒到上限。任何一層不成立就完全不碰機台。這裡不做任何「偵測到某某狀況就自動接手」的事——沒按開始就永遠不會有第一個動作。
    /// </summary>
    /// <param name="allowInteract">可不可以主動去戳機台。機台畫面還開著時一律 false——那個時機只准回答已經跳出來的確認框，不准發起新的互動。</param>
    private void RunAutoReplayPhase(bool allowInteract)
    {
        if (!AutoReplayRunning)
        {
            return;
        }

        if (!Cfg.AutoReplay)
        {
            StopAutoReplay("設定已關閉");
            return;
        }

        var cap = Math.Max(1, Cfg.AutoReplayMaxGames);
        if (AutoReplayGamesDone >= cap)
        {
            StopAutoReplay($"已完成 {AutoReplayGamesDone}/{cap} 局");
            return;
        }

        // 🔴 全程都要求「附近認得出一台孤樹無援機台」。認不出來就停——
        // 不會在別的機台前面亂按確認框，也不會在玩家走開之後繼續動作。
        // ⚠️ machine 只在這一幀有效，用完就丟，不存起來。
        if (!LimbMachine.TryFindNearbyMachine(out var machine) || machine == null)
        {
            StopAutoReplay("附近找不到孤樹無援機台");
            return;
        }

        // 機台的「要挑戰一下嗎」確認框。挑戰翻倍那種提示不在這裡處理（它有自己的開關）。
        if (SelectYesnoHelper.TryGetVisible(out var yesno))
        {
            HandleAutoReplayPrompt(yesno, cap);
            return;
        }

        // 有些機台會先跳一層選單。
        if (SelectStringHelper.TryGetArcadeMenu(out var menu))
        {
            if (SelectStringHelper.IsArcadeYesnoMenu(menu) &&
                EzThrottler.Throttle(ReplayMenuThrottleKey, ReplayMenuThrottleMs))
            {
                SelectStringHelper.TrySelectYesEntry(menu);
                LastAction = "連續遊玩：選單已選「是」";
                Svc.Log.Information($"[OutOnALimb] auto-replay selected the arcade start menu entry " +
                                    $"({AutoReplayGamesDone}/{cap})");
            }

            return;
        }

        // 🔴 機台畫面還開著就絕不主動互動——那等於在遊戲進行中重新戳機台。
        if (!allowInteract)
        {
            if (EzThrottler.Throttle(ReplayWaitDiagThrottleKey, ReplayWaitDiagThrottleMs))
            {
                Svc.Log.Information($"[OutOnALimb] auto-replay standing by: the machine screen is still open and " +
                                    $"there is no prompt to answer ({AutoReplayGamesDone}/{cap})");
            }

            return;
        }

        // 什麼畫面都沒有 → 去跟機台互動。
        // ⚠️ Player.Interactable 是「發起互動」的前提，不是「按畫面上的按鈕」的前提，
        //    所以只擋這一條路徑（擋在整個函式最前面的話，連回答確認框都會被一起擋掉）。
        if (!Player.Interactable)
        {
            return;
        }

        if (ObjectHelper.TryInteractWithObject(machine, ReplayInteractThrottleKey))
        {
            LastAction = $"連續遊玩：正在跟機台互動（已完成 {AutoReplayGamesDone}/{cap} 局）";
        }
    }

    /// <summary>
    /// 連續遊玩看到確認框時的判定。
    /// 🔴 這是整條路徑上**唯一會花掉金碟幣**的動作（每局 1 枚），所以判定寫成**連言**：下面每一條都成立才按「是」，任何一條不成立就完全不碰。寧可不按，也不要按錯。
    /// 確認框文字裡有**機台名稱**——執行期從 EObjName#2005423／Item#30425 讀，不寫死字串；
    /// 確認框文字命中 <see cref="LimbPrompt"/> 從 Addon#9321 拆出來的固定句。台服 1138 張表全掃證實「要挑戰一下嗎？」只有 9321 這一列有，而翻倍提示（9329／9333）寫的是「要<b>嘗試</b>挑戰一下嗎？」，兩者不會互相包含；
    /// 附近認得出孤樹無援機台（呼叫端已經要求，這裡不重複）；不是翻倍提示（<see cref="SelectYesnoHelper.LooksLikeArcadeDoubleDownPrompt"/>）；不是被封鎖的系統提示（傳送、捨棄、組隊邀請……）。
    /// </summary>
    private void HandleAutoReplayPrompt(AddonSelectYesno* yesno, int cap)
    {
        var prompt = SelectYesnoHelper.GetPrompt(yesno);
        var hasButtons = SelectYesnoHelper.HasYesnoButtons(yesno);
        var blocked = SelectYesnoHelper.IsBlockedSystemPrompt(yesno);

        // ⚠️ 兩支都問：LooksLike… 是純文字版（放寬 agent 條件之後唯一還有效的那支），
        //    IsArcadeDoubleDownYesno 是原本的 agent＋文字版。任一成立就當成翻倍提示。
        var doubleDown = SelectYesnoHelper.LooksLikeArcadeDoubleDownPrompt(yesno) ||
                         SelectYesnoHelper.IsArcadeDoubleDownYesno(yesno);
        var mentionsMachine = LimbMachine.PromptMentionsMachine(prompt);
        var matchesTemplate = LimbPrompt.LooksLikePlayConfirm(prompt);

        var accept = hasButtons && !blocked && !doubleDown && mentionsMachine && matchesTemplate;
        if (!accept)
        {
            // 「為什麼沒按」必須看得見：要使用者回報的診斷，寫 Information。
            if (EzThrottler.Throttle(ReplayPromptDiagThrottleKey, ReplayPromptDiagThrottleMs))
            {
                Svc.Log.Information($"[OutOnALimb] auto-replay left a yes/no prompt alone: " +
                                    $"{DescribePromptGates(yesno, hasButtons, blocked, doubleDown, mentionsMachine, matchesTemplate)}");
                LastAction = $"連續遊玩：看到確認框但認不出來，沒有動作（{DescribeRefusal(hasButtons, blocked, doubleDown, mentionsMachine, matchesTemplate)}）";
            }

            return;
        }

        if (!EzThrottler.Throttle(ReplayYesThrottleKey, ReplayYesThrottleMs))
        {
            return;
        }

        var pressed = SelectYesnoHelper.PressYes(yesno);
        LastAction = pressed
            ? $"連續遊玩：開始第 {AutoReplayGamesDone + 1} 局"
            : "連續遊玩：認出確認框但按不下去";
        Svc.Log.Information($"[OutOnALimb] auto-replay {(pressed ? "accepted" : "FAILED TO PRESS")} the arcade prompt " +
                            $"({AutoReplayGamesDone + 1}/{cap}); " +
                            $"{DescribePromptGates(yesno, hasButtons, blocked, doubleDown, mentionsMachine, matchesTemplate)}");
    }

    /// <summary>把每一層判定的結果攤平成一行。下次再卡住時，這一行要能直接指出是哪一層擋的。</summary>
    private static string DescribePromptGates(
        AddonSelectYesno* yesno,
        bool hasButtons,
        bool blocked,
        bool doubleDown,
        bool mentionsMachine,
        bool matchesTemplate) =>
        $"owner={AgentHelper.DescribeOwner(&yesno->AtkUnitBase)}, " +
        $"arcadeAgent={SelectYesnoHelper.IsArcadeYesno(yesno)}, " +
        $"buttons={hasButtons}, blockedSystemPrompt={blocked}, doubleDown={doubleDown}, " +
        $"machineName={mentionsMachine}, template={matchesTemplate} " +
        $"(fragments={LimbPrompt.Fragments.Length}), " +
        $"prompt=\"{Flatten(SelectYesnoHelper.GetPrompt(yesno))}\"";

    private static string DescribeRefusal(
        bool hasButtons,
        bool blocked,
        bool doubleDown,
        bool mentionsMachine,
        bool matchesTemplate)
    {
        if (!hasButtons)
        {
            return "找不到是/否按鈕";
        }

        if (blocked)
        {
            return "是被封鎖的系統提示";
        }

        if (doubleDown)
        {
            return "是挑戰翻倍提示，交給續戰設定決定";
        }

        if (!mentionsMachine)
        {
            return "文字裡沒有孤樹無援的機台名稱";
        }

        return matchesTemplate ? "未知原因" : "文字不符合街機遊玩確認框的模板";
    }

    /// <summary>log 用：把多行文字壓成一行，免得一則診斷散成好幾行難以對齊時間戳。</summary>
    private static string Flatten(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r", string.Empty).Replace("\n", " ⏎ ");

    /// <summary>每一層都先驗指標再解參考；任何一層取不到就整個不動作。</summary>
    /// <remarks>
    /// 機台鈕按下不會關窗（機台板要到局末才由遊戲收掉），「同窗只按一次」不適用（一局要揮很多刀），
    /// 而且停表／揮擊都是看指針落點的時序動作，任何額外延遲都會改變落點。
    /// 所以這裡只接 <see cref="AddonPressGuard.TryTouch"/>：不登記、不改節奏，只擋
    /// 「已經看過 PreFinalize 的實例」——局末遊戲收板那幾幀剛好落一下就是它要擋的。
    /// 放在節流之前：被擋下不消耗節流，下一幀時機還在就照常按。
    /// </remarks>
    private static bool TryClickButton(string addonName, AtkUnitBase* addon, uint nodeId, string throttleKey, int throttleMs)
    {
        if (addon == null)
        {
            return false;
        }

        var button = addon->GetComponentButtonById(nodeId);
        // ⚠️ button->AtkResNode 與 IsEnabled 解的 OwnerNode 是兩個不同欄位，前者擋不到後者 → 用 IsEnabledSafe。
        if (button == null || button->AtkResNode == null || !button->AtkResNode->IsVisible() || !AddonButton.IsEnabledSafe(button))
        {
            return false;
        }

        if (!AddonPressGuard.TryTouch(addonName, addon))
        {
            return false;
        }

        if (!EzThrottler.Throttle(throttleKey, throttleMs))
        {
            return false;
        }

        try
        {
            button->ClickAddonButton(addon);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[OutOnALimb] button click failed");
            return false;
        }
    }
}
