using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using System;
using static ECommons.GenericHelpers;

namespace Saucy.JumboCactpot;

/// <summary>
/// 仙人仙彩（Jumbo Cactpot，金碟遊樂園每週彩券）購票輔助。
/// <para>只做一件事：<c>LotteryWeeklyInput</c> 購票面板開著時，把號碼填進去並推進到購買確認框，然後**停手**。互動序列參考 DailyRoutines <c>AutoJumboCactpot</c>（<c>D:/ffxiv-tc-port/_dr-src/DailyRoutines.ModulesPublic/</c>）的 addon callback 寫法，全程零 hook、零封包。</para>
/// <para>🔴 **花費金碟幣的那一次確認永遠由玩家自己按。** 這是本模組與 DR 版本最大的差異：DR 在送出號碼後緊接著呼叫 <c>ClickSelectYesnoYes()</c> 自動確認扣款，本模組**刻意不做**，連帶也不呼叫 <see cref="SelectYesnoHelper.PressYes"/>。台服的確認框是 Addon 9276「確定要以 N 金碟幣的價格購買 NNNN 號仙人仙彩嗎？［所持金碟幣：N］」——錢包動作留給人，模組只負責把號碼打好、把確認框叫出來。</para>
/// <para>觸發方式是「面板開著 + 模組啟用」，不是事件驅動接手鏈：模組不會自己去找 NPC、不會自己點對話選單、也不會在購票完成後自己開下一張。每週三張的做法是玩家照常在 NPC選單選「購買彩券」，每開一次面板模組就幫忙填一次號碼，玩家按三次確認即可。</para>
/// <para>模組未啟用時不註冊任何監聽——手動購票不會被搶操作。</para>
/// </summary>
public unsafe class JumboCactpotModule : Module
{
    /// <summary>addon 內部名，跨語言用戶端一致（非在地化字串）。</summary>
    private const string AddonName = "LotteryWeeklyInput";

    /// <summary>每週可購買的張數。純粹用於狀態顯示，不當成閘門——真正的上限由伺服器決定，
    /// 本模組不做「還剩幾張」的推測。</summary>
    private const int WeeklyTicketCount = Configuration.JumboCactpotTicketsPerWeek;

    private DateTime? panelReadyUtc;

    /// <summary>這一次開窗是否已經送出過號碼。只在面板關閉（或重開）時解除，
    /// 所以玩家若在確認框按「否」，模組不會立刻再送一次跟他搶。</summary>
    private bool submittedForThisWindow;

    private int ticketsSubmitted;

    public override string Name => "Jumbo Cactpot";

    /// <summary>給設定面板顯示的最近動作說明。</summary>
    public string LastAction { get; private set; } = "等待開啟仙人仙彩購票面板";

    /// <summary>這次進場已經送出過幾張的號碼（不代表已成交——成交與否取決於玩家按不按確認）。</summary>
    public int TicketsSubmitted => ticketsSubmitted;

    /// <summary>下一張會用第幾格號碼（0 起算）。給設定面板顯示「下一張會用哪個號碼」用。</summary>
    public int NextTicketIndex => ticketsSubmitted;

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
        ResetWindowState();
        ticketsSubmitted = 0;
        LastAction = "等待開啟仙人仙彩購票面板";
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        // 開窗與關窗都把「這次開窗已送出」的閂解除：下一次開窗是新的一張彩券。
        ResetWindowState();
    }

    private void OnUpdate(IFramework framework)
    {
        var addon = GetInputAddon();
        if (addon == null)
        {
            MinigameInputPacing.Reset(ref panelReadyUtc);

            // 離開金碟遊樂園＝這次進場結束，張數計數重來。
            // 🔴 舊版的判準是「面板消失超過 10 秒」。在「三張各自指定號碼」上線之前那只影響狀態列
            // 顯示的張數，上線之後它會決定用哪一格號碼——玩家站在購票 NPC 前面發呆超過 10 秒，
            // 第二張就會被當成第一張，於是買到兩張一模一樣的號碼。改用地圖判準之後，
            // 只要人還在金碟遊樂園（TerritoryType 144）裡，第幾張就一直算得對。
            if (ticketsSubmitted > 0 && !InSaucer)
            {
                ticketsSubmitted = 0;
            }

            return;
        }

        if (submittedForThisWindow)
        {
            return;
        }

        // 🔴 確認框已經在畫面上時什麼都不做。這是「絕不自動確認」的第二道保險：
        // 就算判斷失誤重跑到這裡，也不會在扣款提示存在時再送一次購買。
        if (SelectYesnoHelper.IsVisible())
        {
            return;
        }

        // 開窗後先暖機，不跟面板初始化搶時序。
        if (!MinigameInputPacing.TryMarkWarmup(ref panelReadyUtc))
        {
            return;
        }

        // 第幾張決定用哪一格號碼：ticketsSubmitted 是「已送出幾張」，
        // 所以它同時就是這一張的 0 起算索引。
        var ticketIndex = ticketsSubmitted;
        var number = ResolveNumber(ticketIndex);

        // 同一扇購票面板只送一次：submittedForThisWindow 已經是閂，守衛是同一件事的位址版
        // （PostSetup／PreFinalize 都會解除），被擋下就下一幀再來，行為不變。
        if (!AddonPressGuard.TryBeginPress(AddonName, addon))
        {
            return;
        }

        // DR 驗證過的送出方式：帶號碼的單一 int callback 等同「把號碼填進欄位並按下購買」，
        // 之後遊戲自己跳出扣款確認框。我們到此為止。
        Callback.Fire(addon, true, number);

        submittedForThisWindow = true;
        ticketsSubmitted++;
        LastAction =
            $"已填入 {number:D4} 號並叫出購買確認框（本次進場第 {ticketsSubmitted}/{WeeklyTicketCount} 張）" +
            "，請自己按下確認";
        Log($"Submitted number {number:D4} (ticket {ticketsSubmitted})");
    }

    /// <summary>號碼來源：隨機、單一固定號碼、或三張各自指定。
    /// <para>⚠️ 隨機的上界是 <c>10000</c>（exclusive），也就是真的能開出 9999。
    /// DR 原版寫的是 <c>new Random().Next(0, 9999)</c>，永遠開不到 9999——那是差一，不是刻意的。</para>
    /// <para>🔴 每一層讀不到設定都往「隨機」退：買彩券是真的在花使用者的金碟幣，
    /// 寧可退回既有行為，也不要拿一個猜出來的號碼去買。</para></summary>
    /// <param name="ticketIndex">本次進場的第幾張（0 起算）。</param>
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

    private void ResetWindowState()
    {
        MinigameInputPacing.Reset(ref panelReadyUtc);
        submittedForThisWindow = false;
    }

    /// <summary>🔴 每幀重新解析，絕不跨幀保存原生指標。</summary>
    private static AtkUnitBase* GetInputAddon()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !addon->IsVisible || !IsAddonReady(addon))
        {
            return null;
        }

        return addon;
    }
}
