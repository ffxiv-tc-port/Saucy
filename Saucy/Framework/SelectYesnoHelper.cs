using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static ECommons.GenericHelpers;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;

namespace Saucy.Framework;

public static unsafe class SelectYesnoHelper
{
    public const uint PromptTextNodeId = 2;

    /// <summary>Standard SelectYesno (Yes=8, No=11). Skip when ticket layout node 12 is visible.</summary>
    public const uint YesButtonNodeId = 8;

    public const uint NoButtonNodeId = 11;

    /// <summary>Lottery "buy another ticket?" (Yes=11, No=12 HoldButton).</summary>
    public const uint TicketPurchaseYesButtonNodeId = 11;

    public const uint TicketPurchaseNoButtonNodeId = 12;

    /// <summary>LotteryWeekly nested SelectString follow-up layout.</summary>
    public const uint AlternateYesButtonNodeId = 13;

    public const uint AlternateNoButtonNodeId = 10;

    private static readonly uint[] YesButtonNodeCandidates =
    [
        TicketPurchaseYesButtonNodeId,
        AlternateYesButtonNodeId,
        YesButtonNodeId,
    ];

    private static readonly (uint Yes, uint No)[] YesNoButtonNodeLayouts =
    [
        (TicketPurchaseYesButtonNodeId, TicketPurchaseNoButtonNodeId),
        (AlternateYesButtonNodeId, AlternateNoButtonNodeId),
        (YesButtonNodeId, NoButtonNodeId),
    ];

    private const uint MaxScannedPayoutTextNodeId = 32;

    private static DateTime? armedUntilUtc;

    private static readonly Regex DigitGroupRegex = new(@"[0-9][0-9,]*", RegexOptions.Compiled);

    private static readonly string[] BlockedSystemPromptMarkers =
    [
        "aetheryte",
        "aethernet",
        "ethérite",
        "étheryte",
        "ätheryt",
        "teleport",
        "téléport",
        "teleportieren",
        "テレポ",
        "summoning bell",
        "cloche d'invocation",
        "beschwörungsglocke",
        "discard",
        "jeter",
        "wegwerfen",
        "home point",
        "point de retour",
        "heimpunkt",
        "return home",
        "retour au foyer",
        "heimkehr",
        "party invitation",
        "party invite",
        "invitation dans un groupe",
        "gruppeneinladung",
        "upon release",
        "under release",
        "libération",
        "freigabe",

        // 台服（繁體中文）用戶端的對應詞。上面 29 個項目全是英/法/德/日文，唯一的非拉丁字元
        // 是片假名「テレポ」——在台服用戶端 IsBlockedSystemPrompt() 因此恆為 false，
        // IsSafeMinigameYesno()／IsRouteSafeYesno()／IsTriadYesno() 的系統提示防護整條靜默失效
        // （尤其 MultiAreaRouteExecutor 跨區移動時會自動按下確認框）。
        // 每個詞都對照 exd-tc/7.20 的官方文本，括號內為出處列號。
        "乙太之光",   // aetheryte：Addon 8511「乙太之光」、8507「沒有可以顯示的乙太之光。」、
                      // EObjName 2004968「簡易乙太之光」
        "傳送",       // teleport：Addon 108/109「確定要傳送嗎？」、111「要傳送到返回點嗎？」、
                      // 3217「確定要傳送到「」嗎？」、1800「要接受前往「」的傳送邀請嗎？」、
                      // 166「收到了發動的傳送，要隨同前往「」嗎？」。
                      // 同時涵蓋 aethernet：2720/2723「都市傳送網」、2735「傳送網」皆含「傳送」
                      // （台服沒有「乙太網」這個詞，全 EXD 零命中）。
        "傳喚鈴",     // summoning bell：Item 7064「傳喚鈴」、EObjName 2000072/2000401、
                      // Addon 8451「美容師傳喚鈴」
        "捨棄",       // discard：Addon 91「捨棄」、110「確定要捨棄×嗎？」、153、8346「捨棄任務道具。」
        "回歸點",     // home point / return home：Addon 194「即將返回「」的回歸點。」、3789「返回回歸點」
        "返回點",     // 同一概念的另一組官方用字（死亡返回）：Addon 111「目前返回點：」、608
        "邀請",       // party invitation / invite：Addon 170「入隊邀請」、121/787「組隊邀請」、
                      // 8542「團隊邀請」、172「通訊貝邀請」
        "回收"        // upon / under release：幻卡回收（把幻卡換成金碟幣，不可逆）
                      // Addon 9510「幻卡回收」、9513/9517/9518「回收…」、9520「回收」
    ];

    /// <summary>街機「挑戰翻倍」提示的關鍵字。只在確認 addon 屬於 GoldSaucerMiniGame agent 之後
    /// 才比對，所以可以用比較寬的詞。台服出處：Addon 9329/9333「挑戰翻倍…要嘗試挑戰一下嗎？」。</summary>
    private static readonly string[] ArcadeDoubleDownMarkers =
    [
        "double down",
        "double or nothing",
        "double your",
        "doubler",
        "verdoppeln",
        "ダブルアップ",
        "翻倍"
    ];

    public static bool IsArmed => armedUntilUtc != null && DateTime.UtcNow < armedUntilUtc;

    public static void ArmForYes(TimeSpan window) => armedUntilUtc = DateTime.UtcNow + window;

    public static void Disarm() => armedUntilUtc = null;

    public static bool IsVisible() => TryGetVisible(out var _);

    public static bool TryGetVisible(out AddonSelectYesno* yesno)
    {
        yesno = null;
        for (var i = 1; i < 100; i++)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
            if (addon == null)
            {
                return false;
            }

            if (!addon->IsVisible || !IsAddonReady(addon))
            {
                continue;
            }

            yesno = (AddonSelectYesno*)addon;
            return true;
        }

        return false;
    }

    public static bool PressYes(AddonSelectYesno* yesno = null)
    {
        if (!TryResolve(yesno, out yesno))
        {
            return false;
        }

        return PressCallback(yesno, 0, static master => master.Yes());
    }

    public static bool PressNo(AddonSelectYesno* yesno = null)
    {
        if (!TryResolve(yesno, out yesno))
        {
            return false;
        }

        return PressCallback(yesno, 1, static master => master.No());
    }

    public static bool IsBlockedSystemPrompt(AddonSelectYesno* yesno)
    {
        var prompt = GetPromptText(yesno);
        return !string.IsNullOrWhiteSpace(prompt) && PromptContainsAny(prompt, BlockedSystemPromptMarkers);
    }

    /// <summary>購票長按鈕版面（Yes=11、No=12 HoldButton）——彩券「花費 MGP 購買」類確認框
    /// 專用的版面；一般是/否框（8/11）不會有可見的節點 12。用來把「購買下一張彩券」跟同樣
    /// 掛在彩券 agent 底下的一般確認框（例如中止遊玩）區分開。</summary>
    public static bool IsTicketPurchaseLayout(AddonSelectYesno* yesno) =>
        yesno != null && IsComponentNodeVisible(yesno, TicketPurchaseNoButtonNodeId);

    public static bool IsArcadeYesno(AddonSelectYesno* yesno) =>
        yesno != null && IsArcadeAddon(&yesno->AtkUnitBase) && HasYesnoButtons(yesno);

    public static bool ShouldPressLotteryYesno(AddonSelectYesno* yesno, AgentId lotteryAgent) =>
        IsSafeMinigameYesno(yesno) &&
        (IsLotteryAgentAddon(&yesno->AtkUnitBase, lotteryAgent) ||
         IsArcadeYesno(yesno) ||
         AgentHelper.IsActive(lotteryAgent));

    public static bool ShouldPressTriadYesno(AddonSelectYesno* yesno) => IsTriadYesno(yesno);

    public static bool IsSafeMinigameYesno(AddonSelectYesno* yesno) =>
        yesno != null &&
        HasYesnoButtons(yesno) &&
        !IsBlockedSystemPrompt(yesno) &&
        !IsTriadYesno(yesno) &&
        !IsTriadYesNoPrompt(yesno) &&
        !IsCuffPlayRoundPrompt(yesno) &&
        !IsArcadeDoubleDownYesno(yesno);

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

    /// <summary>街機的「挑戰翻倍」確認框——把已經贏到的金碟幣再押一次的提示
    /// （Addon 9329/9333：「挑戰翻倍可以有機會獲得更多的金碟幣，但是失敗的話則會什麼都得不到。
    /// 要嘗試挑戰一下嗎？」）。這跟一般的「要不要玩一局」確認框性質完全不同，
    /// 絕不可以被當成「安全的小遊戲是/否框」自動按下：要不要續戰必須由各模組依自己的條件決定
    /// （孤樹無援模組就是自己讀剩餘秒數判斷後才呼叫 PressYes/PressNo）。
    /// <para>⚠️ 2026-07-01 的 cbfd349 移除街機模組時把這個函式砍成 <c>=&gt; false</c>，
    /// 但函式名與兩個呼叫端（IsSafeMinigameYesno、IsTriadYesNoPrompt）都留著 ——
    /// 名字宣稱一個判斷、實作卻是無條件常數，任何照名字信任它的人都不會得到徵兆。
    /// 這裡把它補回真正的判斷。</para></summary>
    public static bool IsArcadeDoubleDownYesno(AddonSelectYesno* yesno)
    {
        if (yesno == null || !IsArcadeAddon(&yesno->AtkUnitBase))
        {
            return false;
        }

        var text = GetPromptText(yesno);
        return !string.IsNullOrWhiteSpace(text) && PromptContainsAny(text, ArcadeDoubleDownMarkers);
    }

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

        var text = textNode->NodeText.ToString();
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

    public static bool TryPressArmedYes()
    {
        if (!IsArmed || !TryGetVisible(out var yesno) || IsBlockedSystemPrompt(yesno))
        {
            return false;
        }

        if (!PressYes(yesno))
        {
            return false;
        }

        Disarm();
        return true;
    }

    private static bool TryResolve(AddonSelectYesno* yesno, out AddonSelectYesno* resolved)
    {
        if (yesno != null && IsAddonReady(&yesno->AtkUnitBase))
        {
            resolved = yesno;
            return true;
        }

        return TryGetVisible(out resolved);
    }

    private static string NormalizePrompt(string text) =>
        text.Replace(" ", string.Empty).Replace("\u00A0", string.Empty);

    private static string GetPromptText(AddonSelectYesno* yesno)
    {
        if (yesno->PromptText != null)
        {
            return yesno->PromptText->NodeText.GetText();
        }

        var textNode = yesno->AtkUnitBase.GetTextNodeById(PromptTextNodeId);
        if (textNode == null)
        {
            return string.Empty;
        }

        return textNode->NodeText.ToString();
    }

    private static bool TryGetVisibleButtonByNodeId(AddonSelectYesno* yesno, uint nodeId, out AtkComponentButton* button)
    {
        button = null;
        if (yesno == null)
        {
            return false;
        }

        button = yesno->AtkUnitBase.GetComponentButtonById(nodeId);
        if (button == null || button->AtkResNode == null)
        {
            return false;
        }

        return button->AtkResNode->IsVisible();
    }

    private static bool PressCallback(AddonSelectYesno* yesno, int callbackId, Action<AddonMaster.SelectYesno> fallback)
    {
        var wantsYes = callbackId == 0;
        if (wantsYes && TryResolveYesButton(yesno, out var yesButton) &&
            TryClickStructButton(yesno, yesButton, forceEnable: true))
        {
            return true;
        }

        if (TryResolveYesNoButtons(yesno, out yesButton, out var noButton))
        {
            var targetButton = wantsYes ? yesButton : noButton;
            if (targetButton != null &&
                TryClickStructButton(yesno, targetButton, forceEnable: wantsYes))
            {
                return true;
            }
        }

        try
        {
            fallback(new(yesno));
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] AddonMaster fallback failed for callback {callbackId}");
        }

        if (wantsYes)
        {
            foreach (var nodeId in YesButtonNodeCandidates)
            {
                if (TryGetVisibleButtonByNodeId(yesno, nodeId, out var button) &&
                    TryClickButton(yesno, button, forceEnable: true))
                {
                    return true;
                }
            }
        }

        try
        {
            Callback.Fire(&yesno->AtkUnitBase, true, callbackId);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] Callback.Fire({callbackId}) failed");
        }

        try
        {
            yesno->FireCallbackInt(callbackId);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] FireCallbackInt({callbackId}) failed");
        }

        return false;
    }

    private static bool TryResolveYesButton(AddonSelectYesno* yesno, out AtkComponentButton* yesButton)
    {
        yesButton = null;
        if (yesno == null)
        {
            return false;
        }

        if (HasVisibleStructButton(yesno->YesButton, out yesButton))
        {
            return true;
        }

        foreach (var nodeId in YesButtonNodeCandidates)
        {
            if (nodeId == YesButtonNodeId && IsComponentNodeVisible(yesno, TicketPurchaseNoButtonNodeId))
            {
                continue;
            }

            if (TryGetVisibleButtonByNodeId(yesno, nodeId, out yesButton))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveYesNoButtons(
        AddonSelectYesno* yesno,
        out AtkComponentButton* yesButton,
        out AtkComponentButton* noButton)
    {
        yesButton = null;
        noButton = null;
        if (yesno == null)
        {
            return false;
        }

        if (HasVisibleStructButton(yesno->YesButton, out yesButton) &&
            HasVisibleStructButton(yesno->NoButton, out noButton))
        {
            return true;
        }

        if (TryGetVisibleButtonByNodeId(yesno, TicketPurchaseYesButtonNodeId, out yesButton) &&
            IsComponentNodeVisible(yesno, TicketPurchaseNoButtonNodeId))
        {
            TryGetVisibleButtonByNodeId(yesno, TicketPurchaseNoButtonNodeId, out noButton);
            return true;
        }

        foreach (var (yesNodeId, noNodeId) in YesNoButtonNodeLayouts)
        {
            if (yesNodeId == YesButtonNodeId &&
                noNodeId == NoButtonNodeId &&
                IsComponentNodeVisible(yesno, TicketPurchaseNoButtonNodeId))
            {
                continue;
            }

            if (!TryGetVisibleButtonByNodeId(yesno, yesNodeId, out yesButton))
            {
                continue;
            }

            if (noNodeId == TicketPurchaseNoButtonNodeId)
            {
                if (IsComponentNodeVisible(yesno, noNodeId))
                {
                    TryGetVisibleButtonByNodeId(yesno, noNodeId, out noButton);
                    return true;
                }

                continue;
            }

            if (TryGetVisibleButtonByNodeId(yesno, noNodeId, out noButton))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsComponentNodeVisible(AddonSelectYesno* yesno, uint nodeId)
    {
        var componentNode = yesno->AtkUnitBase.GetComponentNodeById(nodeId);
        if (componentNode == null)
        {
            return false;
        }

        return ((AtkResNode*)componentNode)->IsVisible();
    }

    private static bool HasVisibleStructButton(AtkComponentButton* button, out AtkComponentButton* resolved)
    {
        resolved = button;
        if (button == null || button->AtkResNode == null)
        {
            resolved = null;
            return false;
        }

        return button->AtkResNode->IsVisible();
    }

    private static bool TryClickStructButton(AddonSelectYesno* yesno, AtkComponentButton* button, bool forceEnable = false) =>
        HasVisibleStructButton(button, out var resolved) &&
        TryClickButton(yesno, resolved, forceEnable);

    private static bool TryClickButton(AddonSelectYesno* yesno, AtkComponentButton* button, bool forceEnable = false)
    {
        if (button == null)
        {
            return false;
        }

        if (forceEnable && !button->IsEnabled)
        {
            TryForceEnableButton(button);
        }

        return AddonButton.TryClick(&yesno->AtkUnitBase, button, requireEnabled: !forceEnable);
    }

    private static void TryForceEnableButton(AtkComponentButton* button)
    {
        try
        {
            var flagsPtr = (ushort*)&button->AtkComponentBase.OwnerNode->AtkResNode.NodeFlags;
            *flagsPtr ^= 1 << 5;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[SelectYesno] Force-enable button failed");
        }
    }
}
