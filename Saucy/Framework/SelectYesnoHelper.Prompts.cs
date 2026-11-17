using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static ECommons.GenericHelpers;
using AddonSetupArgs = Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonSetupArgs;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Saucy.Framework;

public static unsafe partial class SelectYesnoHelper
{
    public static bool IsCuffPlayRoundPrompt(AddonSelectYesno* yesno)
    {
        if (yesno == null || IsBlockedSystemPrompt(yesno) || IsTriadYesNoPrompt(yesno))
        {
            return false;
        }

        var text = GetPromptText(yesno);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 機台名稱閘門。台服的街機確認框是 Addon 9321 的模板「**<機台名>** … 要挑戰一下嗎？
        // 需要金碟幣：N」，機台名在執行期由 EObjName 代入，所以要比對的是台服機台名。
        // 「重擊伽美什」＝EObjName 2005029；台服四台街機分別是 2004804「怪物投籃」(Monster Toss)、
        // 2005035「強襲水晶塔」(Crystal Tower Striker)、2005036「莫古抓球機」(The Moogle's Paw)、
        // 2005029「重擊伽美什」——以排除法即 Cuff-a-Cur（打擊型機台）。
        if (!text.Contains("Cuff", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("伽美什", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("payout", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("réussite", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Gewinn", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Contains("Play", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("round", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("jouer", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("spielen", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("プレイ", StringComparison.OrdinalIgnoreCase) ||
               // 台服：Addon 9321「要挑戰一下嗎？」為街機遊玩確認框的固定句式。
               text.Contains("挑戰", StringComparison.Ordinal);
    }

    public static bool IsTriadYesNoPrompt(AddonSelectYesno* yesno)
    {
        if (IsBlockedSystemPrompt(yesno) || IsArcadeDoubleDownYesno(yesno))
        {
            return false;
        }

        var text = NormalizePrompt(GetPromptText(yesno));
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return PromptContainsAny(text,
        [
            "triad",
            "triade",
            "triplo",
            "トリプル",
            // ⚠️「三重幻卡」在台服 EXD 是零命中（那是簡中服的譯名），這個項目一直是死碼。
            // 台服官方用字是「幻卡」／「九宮幻卡」：Addon 9160/9173/9179/9184「幻卡挑戰」、
            // 9529/9991「九宮幻卡」、9757「確定要用此卡組進行對局嗎？」。
            // 只影響「這是不是幻卡提示」的分類（IsTriadYesno 走的是 agent 歸屬，不受此處影響）。
            "幻卡"
        ]);
    }

    private static bool PromptContainsAny(string prompt, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (prompt.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsTriadYesno(AddonSelectYesno* yesno) =>
        yesno != null &&
        HasYesnoButtons(yesno) &&
        !IsBlockedSystemPrompt(yesno) &&
        !IsArcadeAddon(&yesno->AtkUnitBase) &&
        IsTriadAddon(&yesno->AtkUnitBase);

    public static bool TryGetTriadYesno(out AddonSelectYesno* yesno)
    {
        if (!TryGetVisible(out yesno) || !IsTriadYesno(yesno))
        {
            yesno = null;
            return false;
        }

        return true;
    }

    public static bool HasYesnoButtons(AddonSelectYesno* yesno) =>
        yesno != null && (TryResolveYesNoButtons(yesno, out _, out _) || TryResolveYesButton(yesno, out _));

    /// <summary>街機的「挑戰翻倍」確認框——把已經贏到的金碟幣再押一次的提示。這跟一般的「要不要玩一局」確認框性質完全不同，絕不可以被當成「安全的小遊戲是/否框」自動按下：要不要續戰必須由各模組依自己的條件決定（孤樹無援模組就是自己讀剩餘秒數判斷後才呼叫 PressYes/PressNo）。</summary>
    public static bool IsArcadeDoubleDownYesno(AddonSelectYesno* yesno)
    {
        if (yesno == null || !IsArcadeAddon(&yesno->AtkUnitBase))
        {
            return false;
        }

        var text = GetPromptText(yesno);
        return !string.IsNullOrWhiteSpace(text) && PromptContainsAny(text, ArcadeDoubleDownMarkers);
    }

    /// <summary>
    /// 純文字判定的「挑戰翻倍」提示——**刻意不看 agent 歸屬**。
    /// 🔴 為什麼要有第二支：<see cref="IsArcadeDoubleDownYesno"/> 的第一個條件是
    /// <see cref="IsArcadeAddon"/>，所以「addon 不屬於 GoldSaucerMiniGame agent」時它會回 **false**。
    /// 那在原本的用法裡沒問題（呼叫端本來就先要求 agent 歸屬），但只要有呼叫端放寬了 agent 條件，
    /// 這個排除項就會跟著失效——**防護會隨著它保護的那個條件一起消失**，而且完全沒有徵兆。
    /// 放寬 agent 條件的呼叫端一律改用這一支。
    /// </summary>
    public static bool LooksLikeArcadeDoubleDownPrompt(AddonSelectYesno* yesno)
    {
        if (yesno == null)
        {
            return false;
        }

        var text = GetPromptText(yesno);
        return !string.IsNullOrWhiteSpace(text) && PromptContainsAny(text, ArcadeDoubleDownMarkers);
    }

    /// <summary>確認框的內文。給診斷與「用資料表文字比對」的呼叫端用。</summary>
    public static string GetPrompt(AddonSelectYesno* yesno) =>
        yesno == null ? string.Empty : GetPromptText(yesno) ?? string.Empty;

    public static bool IsArcadeAddon(AtkUnitBase* addon) =>
        AgentHelper.IsAddonOwnedBy(addon, AgentId.GoldSaucerMiniGame);

    public static bool IsLotteryDailyAddon(AtkUnitBase* addon) =>
        AgentHelper.IsAddonOwnedBy(addon, AgentId.LotteryDaily);

    public static bool IsLotteryWeeklyAddon(AtkUnitBase* addon) =>
        AgentHelper.IsAddonOwnedBy(addon, AgentId.LotteryWeekly);

    private static bool IsLotteryAgentAddon(AtkUnitBase* addon, AgentId lotteryAgent) =>
        lotteryAgent switch
        {
            AgentId.LotteryDaily => IsLotteryDailyAddon(addon),
            AgentId.LotteryWeekly => IsLotteryWeeklyAddon(addon),
            var _ => false
        };

    public static bool IsTriadAddon(AtkUnitBase* addon) =>
        AgentHelper.IsAddonOwnedBy(addon, AgentId.TrippleTriad);

    private static bool IsModuleManagedYesno(AddonSelectYesno* yesno)
    {
        if (yesno == null || !HasYesnoButtons(yesno))
        {
            return false;
        }

        var addon = &yesno->AtkUnitBase;
        return IsTriadAddon(addon) ||
               IsArcadeAddon(addon) ||
               IsLotteryDailyAddon(addon) ||
               IsLotteryWeeklyAddon(addon);
    }

    public static bool IsRouteSafeYesno(AddonSelectYesno* yesno) =>
        yesno != null &&
        HasYesnoButtons(yesno) &&
        !IsBlockedSystemPrompt(yesno) &&
        !IsModuleManagedYesno(yesno);

    private static bool HasVisiblePayoutAmount(AddonSelectYesno* yesno)
    {
        var numericValues = new List<int>();
        TryCollectDigitGroupsFromTextNode(yesno->AtkTextNode298, numericValues);
        TryCollectDigitGroupsFromTextNode(yesno->PromptText, numericValues);

        for (uint nodeId = 1; nodeId <= MaxScannedPayoutTextNodeId; nodeId++)
        {
            TryCollectDigitGroupsFromTextNode(yesno->AtkUnitBase.GetTextNodeById(nodeId), numericValues);
        }

        foreach (var value in numericValues)
        {
            if (value is >= 10 and <= 9999)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryCollectDigitGroupsFromTextNode(AtkTextNode* textNode, List<int> values)
    {
        if (textNode == null || !((AtkResNode*)textNode)->IsVisible())
        {
            return;
        }

        // 🔴 NodeText.ToString() 就是 Encoding.UTF8.GetString(AsSpan())，完全不剝 SeString payload：道具連結 payload 會解出
        //    U+FFFD，內嵌圖示 payload 則留下可列印的雜字元（會被下面的數字正規式當成金額撿走）。所以走 GetText()
        //    （底下是 MemoryHelper.ReadSeString，只保留 TextPayload），拿到的是玩家實際看得見的那串字。
        // ⚠️ StringPtr 判空不能省：MemoryHelper.ReadSeString 只判 Utf8String* 本身非 null，StringPtr 為 null 而 Length 還留著殘值時，
        //    AsSpan() 會建出一個長度非零、指向位址 0 的 Span，讀下去就是攔不到的存取違規。
        if (!textNode->NodeText.StringPtr.HasValue)
        {
            return;
        }

        var text = textNode->NodeText.GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in DigitGroupRegex.Matches(text))
        {
            var digits = match.Value.Replace(",", string.Empty);
            if (int.TryParse(digits, out var value) && value > 0)
            {
                values.Add(value);
            }
        }
    }

    /// <summary>是不是 <c>/logout</c> 的「回到標題畫面」確認框：整句與 Addon 表那一列逐字相同（去掉換行與空白）。表讀不到就一律 false（不按）。</summary>
    public static bool IsLogoutPrompt(AddonSelectYesno* yesno)
    {
        logoutPrompt ??= NormalizeWholePrompt(
            Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(LogoutPromptAddonRowId)?.Text.ExtractText() ?? string.Empty);
        return yesno != null &&
               logoutPrompt.Length > 0 &&
               HasYesnoButtons(yesno) &&
               NormalizeWholePrompt(GetPromptText(yesno)) == logoutPrompt;
    }

    /// <summary>是不是仙人微彩發放員的購票確認框：整句與 GoldSaucerTalk 那一列的樣板相同（數字巨集只配數字），換行與空白不計。</summary>
    /// <remarks>表讀不到、或那一列有數字以外的巨集時一律 false（不按），並寫一次 Information。</remarks>
    public static bool IsMiniCactpotPurchasePrompt(string prompt)
    {
        if (!miniCactpotPurchasePromptBuilt)
        {
            miniCactpotPurchasePromptBuilt = true;
            var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.GoldSaucerTalk>()?.GetRowOrDefault(MiniCactpotPurchasePromptTalkRowId);
            miniCactpotPurchasePrompt = row is { } talk ? GameTextTemplate.Build(talk.Unknown0) : null;
            if (miniCactpotPurchasePrompt == null)
            {
                Svc.Log.Information(
                    $"[SelectYesno] 讀不到 GoldSaucerTalk#{MiniCactpotPurchasePromptTalkRowId} 的購票確認句，Saucy 認不出仙人微彩的購票確認框（交給 YesAlready 或你自己按）");
            }
        }

        return miniCactpotPurchasePrompt != null && miniCactpotPurchasePrompt.IsMatch(GameTextTemplate.Normalize(prompt));
    }

    /// <summary>確認框 PreSetup 當下的內文（setup 參數的 AtkValues[0]，讀不到再試視窗自己的）；讀不到回空字串。</summary>
    /// <remarks>契約：只在那一次 PreSetup 事件呼叫裡用，參數與指標都不留到事件之後。</remarks>
    public static string ReadSetupPrompt(AddonSetupArgs args)
    {
        if (args.AtkValues != nint.Zero && args.AtkValueCount > 0)
        {
            var value = (AtkValue*)args.AtkValues;
            if ((value->Type is ValueType.String or ValueType.String8 or ValueType.ManagedString) && value->String.Value != null)
            {
                return Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value->String.Value).GetText();
            }
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        return addon == null ? string.Empty : GetAtkValueTextOrNull(addon, 0) ?? string.Empty;
    }

    /// <summary>確認框 AtkValues[0] 的內文（與 PreSetup 讀的、YesAlready 比對的是同一份）；讀不到回空字串。</summary>
    public static string GetValuePrompt(AddonSelectYesno* yesno) =>
        yesno == null ? string.Empty : GetAtkValueTextOrNull(&yesno->AtkUnitBase, 0) ?? string.Empty;
}
