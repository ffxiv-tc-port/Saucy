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
    /// <summary>確認框的 addon 名稱。也是它在 <see cref="AddonPressGuard"/> 裡的鍵。</summary>
    public const string AddonName = "SelectYesno";

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

    /// <summary><c>/logout</c> 確認框的句子（台服「確定要退出並回到標題畫面嗎？」）。</summary>
    private const uint LogoutPromptAddonRowId = 115;

    private static string? logoutPrompt;

    /// <summary>GoldSaucerTalk：仙人微彩發放員的購票確認（「參加…需要＜數字＞金碟幣。確定要參加嗎？[所持金碟幣：＜數字＞]」）。</summary>
    private const uint MiniCactpotPurchasePromptTalkRowId = 16;

    /// <summary>購票確認的整句樣板；第一次用到才建。null＝表讀不到或那一列不能當樣板（永遠比不中）。</summary>
    private static Regex? miniCactpotPurchasePrompt;

    private static bool miniCactpotPurchasePromptBuilt;

    /// <summary>Warp 表：目的地區域 → 該區傳送確認框的句子（正規化後）。第一次用到才建。</summary>
    private static Dictionary<uint, HashSet<string>>? warpQuestions;

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

    public static bool IsVisible() => TryGetVisible(out var _);

    public static bool TryGetVisible(out AddonSelectYesno* yesno)
    {
        yesno = null;
        for (var i = 1; i < 100; i++)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName, i).Address;
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

    /// <summary>找出位址等於 <paramref name="address"/>、而且可見並就緒的那一扇確認框。位址只做等值比較，從不解參。</summary>
    public static bool TryGetVisibleAt(nint address, out AddonSelectYesno* yesno)
    {
        yesno = null;
        if (address == nint.Zero)
        {
            return false;
        }

        for (var i = 1; i < 100; i++)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName, i).Address;
            if (addon == null)
            {
                return false;
            }

            if ((nint)addon != address)
            {
                continue;
            }

            if (!addon->IsVisible || !IsAddonReady(addon))
            {
                return false;
            }

            yesno = (AddonSelectYesno*)addon;
            return true;
        }

        return false;
    }

    /// <summary>是不是「傳送進 <paramref name="territoryIds"/> 其中之一」的確認框：整句與 Warp 表該區的 Question 逐字相同（去掉換行與空白）。</summary>
    public static bool IsWarpPromptInto(AddonSelectYesno* yesno, IReadOnlyList<uint>? territoryIds)
    {
        if (yesno == null || territoryIds is not { Count: > 0 } || !HasYesnoButtons(yesno))
        {
            return false;
        }

        var prompt = NormalizeWholePrompt(GetPromptText(yesno));
        if (prompt.Length == 0)
        {
            return false;
        }

        warpQuestions ??= BuildWarpQuestions();
        foreach (var territoryId in territoryIds)
        {
            if (warpQuestions.TryGetValue(territoryId, out var questions) && questions.Contains(prompt))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<uint, HashSet<string>> BuildWarpQuestions()
    {
        var map = new Dictionary<uint, HashSet<string>>();
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Warp>();
        if (sheet == null)
        {
            return map;
        }

        foreach (var row in sheet)
        {
            var question = NormalizeWholePrompt(row.Question.ExtractText());
            if (question.Length == 0)
            {
                continue;
            }

            var territoryId = row.TerritoryType.RowId;
            if (!map.TryGetValue(territoryId, out var questions))
            {
                map[territoryId] = questions = new HashSet<string>(StringComparer.Ordinal);
            }

            questions.Add(question);
        }

        return map;
    }

    private static string NormalizeWholePrompt(string text) => GameTextTemplate.Normalize(text);

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
        // ⚠️ StringPtr 判空不能省，理由同 TryCollectDigitGroupsFromTextNode。
        if (textNode == null || !textNode->NodeText.StringPtr.HasValue)
        {
            return string.Empty;
        }

        // 🔴 與上面主路徑（PromptText->NodeText.GetText()）同基準，一律剝掉 SeString payload。
        //    用 ToString() 的話：SelectYesno 的提示常含道具連結，payload 必定解出
        //    U+FFFD ⇒ 餵進 AddonPressGuard.LooksCorrupted 之後守衛「永遠」成立，
        //    等於這條備援路徑一次都按不下去，而且長得跟「視窗真的在變動」一模一樣。
        return textNode->NodeText.GetText();
    }
}
