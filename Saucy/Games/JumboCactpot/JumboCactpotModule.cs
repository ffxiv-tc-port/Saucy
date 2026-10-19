using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Text;
using static ECommons.GenericHelpers;
using GoldSaucerTalk = Lumina.Excel.Sheets.GoldSaucerTalk;
using LuminaAddon = Lumina.Excel.Sheets.Addon;

namespace Saucy.JumboCactpot;

/// <summary>
/// 仙人仙彩（Jumbo Cactpot，金碟遊樂園每週彩券）購票輔助。
/// <para>只做一件事：<c>LotteryWeeklyInput</c> 購票面板開著時，把號碼填進去並推進到購買確認框，然後**停手**。互動序列參考 DailyRoutines <c>AutoJumboCactpot</c>的 addon callback 寫法，全程零 hook、零封包。</para>
/// <para>🔴 **花費金碟幣的那一次確認永遠由玩家自己按。** 這是本模組與 DR 版本最大的差異：DR 在送出號碼後緊接著呼叫 <c>ClickSelectYesnoYes()</c> 自動確認扣款，本模組**刻意不做**，連帶也不呼叫 <see cref="SelectYesnoHelper.PressYes"/>。台服的確認框是 Addon 9276「確定要以 N 金碟幣的價格購買 NNNN 號仙人仙彩嗎？［所持金碟幣：N］」——錢包動作留給人，模組只負責把號碼打好、把確認框叫出來。</para>
/// <para>觸發方式是「面板開著 + 模組啟用」，不是事件驅動接手鏈：模組不會自己去找 NPC、不會自己點對話選單、也不會在購票完成後自己開下一張。「這扇要不要填、填第幾張」由 <see cref="JumboPanelFlow"/> 判斷。</para>
/// <para>模組未啟用時不註冊任何監聽——手動購票不會被搶操作。</para>
/// </summary>
public unsafe class JumboCactpotModule : Module
{
    /// <summary>addon 內部名，跨語言用戶端一致（非在地化字串）。</summary>
    private const string AddonName = "LotteryWeeklyInput";

    /// <summary>每週可購買的張數。純粹用於狀態顯示，不當成閘門——真正的上限由伺服器決定。</summary>
    private const int WeeklyTicketCount = Configuration.JumboCactpotTicketsPerWeek;

    /// <summary>GoldSaucerTalk：「要不要再買一次？／價格是 N 金碟幣。」（上一張買成之後才會出現）。</summary>
    private const uint BuyAgainTalkRow = 490;

    /// <summary>Addon：購買確認框「確定要以 N 金碟幣的價格購買 NNNN 號仙人仙彩嗎？／［所持金碟幣：N］」。</summary>
    private const uint PurchaseConfirmAddonRow = 9276;

    /// <summary>每次請 YesAlready 暫停仙人仙彩自動填號的長度；每 <see cref="ClaimRenewMs"/> 續一次，中間不會斷。</summary>
    private const int ClaimMs = 15000;

    private const long ClaimRenewMs = 5000;

    private JumboPanelFlow flow = new();
    private string? buyAgainPrefix;
    private string? purchaseTemplate;
    private bool promptTextsMissingLogged;
    private long claimFrom;
    private long claimUntil;
    private long lastClaimAttempt;
    private bool claimFailLogged;

    public override string Name => "Jumbo Cactpot";

    /// <summary>給設定面板顯示的最近動作說明。</summary>
    public string LastAction { get; private set; } = "等待開啟仙人仙彩購票面板";

    /// <summary>下一張會用第幾格號碼（0 起算）。給設定面板顯示「下一張會用哪個號碼」用。</summary>
    public int NextTicketIndex => flow.ResolveTicketIndex(ReadWeekly(), Environment.TickCount64);

    public override void Enable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonEvent);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonEvent);
        Svc.Framework.Update += OnUpdate;
        GoldSaucerEventTracker.Acquire(InternalName);
    }

    public override void Disable()
    {
        GoldSaucerEventTracker.Release(InternalName);
        Svc.Framework.Update -= OnUpdate;
        Svc.AddonLifecycle.UnregisterListener(OnAddonEvent);
        TaskManager.Abort();
        flow = new();
        claimFrom = 0;
        claimUntil = 0;
        LastAction = "等待開啟仙人仙彩購票面板";
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        // 開窗與關窗都把「這次開窗已送出」的閂解除：下一次開窗是新的一張彩券。
        var now = Environment.TickCount64;
        if (type == AddonEvent.PostSetup)
        {
            flow.OnPanelSetup(args.Addon.Address, now);
        }
        else
        {
            flow.OnPanelFinalize(args.Addon.Address, now);
        }
    }

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        var inSaucer = InSaucer;
        AtkUnitBase* panel = null;
        var panelIndex = 0;
        var visibleNotReady = false;
        var yesno = JumboPanelFlow.YesnoKind.None;
        if (inSaucer)
        {
            panel = FindPanel(out panelIndex, out visibleNotReady);
            var talkingToBroker = Svc.Targets.Target?.BaseId == GoldSaucerVenue.JumboCactpotBrokerId &&
                                  (Svc.Condition[ConditionFlag.OccupiedInQuestEvent] || Svc.Condition[ConditionFlag.OccupiedInEvent]);
            if (panel != null || visibleNotReady || talkingToBroker)
            {
                // 跟發放員一開始對話就讓 YesAlready 讓開：它在面板 PostSetup 當下就填，等面板出來才問已經太晚。
                yesno = ClassifyYesno();
                TickClaim(now);
            }
        }

        var frame = new JumboPanelFlow.Frame(
            now, inSaucer, (nint)panel, panelIndex, visibleNotReady, yesno,
            panel != null ? ReadWeekly() : default,
            panel != null && YesAlready.IsLoaded,
            claimFrom != 0, claimFrom, claimUntil);
        var ticketIndex = flow.Update(frame);
        FlushDiagnostics();
        if (ticketIndex < 0)
        {
            return;
        }

        var number = ResolveNumber(ticketIndex);

        // 同一扇購票面板只送一次：判斷層已經是閂，守衛是同一件事的位址版，被擋下就下一幀再來。
        if (!AddonPressGuard.TryBeginPress(AddonName, panel))
        {
            flow.NoteGuardBlocked(now);
            FlushDiagnostics();
            return;
        }

        // DR 驗證過的送出方式：帶號碼的單一 int callback 等同「把號碼填進欄位並按下購買」，
        // 之後遊戲自己跳出扣款確認框。我們到此為止。
        Callback.Fire(panel, true, number);

        flow.MarkSubmitted((nint)panel, now);
        LastAction =
            $"已填入 {number:D4} 號並叫出購買確認框（本週第 {ticketIndex + 1}/{WeeklyTicketCount} 張）" +
            "，請自己按下確認";
        Log($"Submitted number {number:D4} (ticket {ticketIndex + 1})");
    }

    /// <summary>號碼來源：隨機、單一固定號碼、或三張各自指定。
    /// <para>⚠️ 隨機的上界是 <c>10000</c>（exclusive），也就是真的能開出 9999。
    /// DR 原版寫的是 <c>new Random().Next(0, 9999)</c>，永遠開不到 9999——那是差一，不是刻意的。</para>
    /// <para>🔴 每一層讀不到設定都往「隨機」退：買彩券是真的在花使用者的金碟幣，
    /// 寧可退回既有行為，也不要拿一個猜出來的號碼去買。</para></summary>
    /// <param name="ticketIndex">本週的第幾張（0 起算）。</param>
    private static int ResolveNumber(int ticketIndex)
    {
        if (!C.JumboCactpotUseFixedNumber)
        {
            return RandomNumber();
        }

        if (!C.JumboCactpotPerTicketNumbers)
        {
            return Math.Clamp(C.JumboCactpotFixedNumber, 0, Configuration.JumboCactpotMaxNumber);
        }

        return JumboCactpotNumberPlan.TryGetNumber(C, ticketIndex, out var number)
            ? number
            : RandomNumber();
    }

    private static int RandomNumber() => Random.Shared.Next(0, Configuration.JumboCactpotMaxNumber + 1);

    /// <summary>請 YesAlready 暫停它的仙人仙彩自動填號，而且讓暫停期間連續不斷（它的暫停取 max）。</summary>
    /// <remarks>它只在面板 PostSetup 當下送號碼；判斷層要求那一刻落在這段期間內，Saucy 才填。</remarks>
    private void TickClaim(long now)
    {
        if (lastClaimAttempt != 0 && now - lastClaimAttempt < ClaimRenewMs)
        {
            return;
        }

        lastClaimAttempt = now;
        if (!YesAlready.TryPauseLotteryWeeklyInput(ClaimMs))
        {
            if (!claimFailLogged)
            {
                claimFailLogged = true;
                Log("無法請 YesAlready 暫停它的仙人仙彩自動填號；確認它不會同時填之前，Saucy 不填這一扇");
            }

            return;
        }

        if (claimUntil <= now)
        {
            claimFrom = now;
        }

        claimUntil = now + ClaimMs;
    }

    private JumboPanelFlow.Weekly ReadWeekly() =>
        GoldSaucerEventTracker.TryGetJumboTicketsBought(out var bought, out var changed, out var captured)
            ? new JumboPanelFlow.Weekly(true, bought, changed, captured)
            : default;

    private void FlushDiagnostics()
    {
        foreach (var line in flow.Diagnostics)
        {
            Log(line);
            LastAction = line;
        }

        flow.Diagnostics.Clear();
        if (flow.TakeOtherFillNotice())
        {
            Svc.Chat.Print("[Saucy] " + "This Jumbo Cactpot number was not filled in by Saucy (you, or another plugin such as YesAlready's Jumbo Cactpot auto-fill). Saucy counts tickets actually bought, so the next ticket still gets its own number.".Loc());
        }
    }

    /// <summary>確認框是哪一種：比對的是表上的文字（數字與換行先拿掉），讀不到表就一律當成「其他」。</summary>
    private JumboPanelFlow.YesnoKind ClassifyYesno()
    {
        if (!SelectYesnoHelper.TryGetVisible(out var yesno))
        {
            return JumboPanelFlow.YesnoKind.None;
        }

        LoadPromptTexts();
        var prompt = SelectYesnoHelper.GetPrompt(yesno);
        if (buyAgainPrefix!.Length > 0 && Strip(prompt, false).StartsWith(buyAgainPrefix, StringComparison.Ordinal))
        {
            return JumboPanelFlow.YesnoKind.BuyAgain;
        }

        if (purchaseTemplate!.Length > 0 && Strip(prompt, true) == purchaseTemplate)
        {
            return JumboPanelFlow.YesnoKind.PurchaseConfirm;
        }

        return JumboPanelFlow.YesnoKind.Other;
    }

    private void LoadPromptTexts()
    {
        if (buyAgainPrefix != null && purchaseTemplate != null)
        {
            return;
        }

        var buyAgain = Svc.Data.GetExcelSheet<GoldSaucerTalk>()?.GetRowOrDefault(BuyAgainTalkRow)?.Unknown0.ExtractText() ?? string.Empty;
        var lineEnd = buyAgain.IndexOfAny(['\r', '\n']);
        buyAgainPrefix = Strip(lineEnd < 0 ? buyAgain : buyAgain[..lineEnd], false);
        purchaseTemplate = Strip(Svc.Data.GetExcelSheet<LuminaAddon>()?.GetRowOrDefault(PurchaseConfirmAddonRow)?.Text.ExtractText() ?? string.Empty, true);
        if ((buyAgainPrefix.Length == 0 || purchaseTemplate.Length == 0) && !promptTextsMissingLogged)
        {
            promptTextsMissingLogged = true;
            Log("讀不到仙人仙彩確認框的表文字；第幾張退回「Saucy 自己送過幾次」的舊算法");
        }
    }

    /// <summary>拿掉空白與換行；<paramref name="dropNumbers"/> 時連數字與千分位逗號一起拿掉（確認框裡的金額與號碼）。</summary>
    private static string Strip(string text, bool dropNumbers)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || (dropNumbers && (char.IsAsciiDigit(c) || c == ',')))
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>可見且就緒的那一扇（同名可能有好幾個實例，舊的還沒收掉時新的排在後面）。🔴 每幀重新解析，絕不跨幀保存原生指標。</summary>
    private static AtkUnitBase* FindPanel(out int index, out bool visibleNotReady)
    {
        index = 0;
        visibleNotReady = false;
        for (var i = 1; i < 100; i++)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName, i).Address;
            if (addon == null)
            {
                return null;
            }

            if (!addon->IsVisible)
            {
                continue;
            }

            if (!IsAddonReady(addon))
            {
                visibleNotReady = true;
                continue;
            }

            index = i;
            return addon;
        }

        return null;
    }
}
