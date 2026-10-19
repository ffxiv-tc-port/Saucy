using ECommons.Automation;
using ECommons.GameHelpers;
using Saucy.Framework;
using Saucy.IPC;
using System;
using static ECommons.GenericHelpers;
namespace Saucy.TripleTriad;

/// <summary>
/// 九宮幻卡跑完後的自動登出：等角色空下來才送 <c>/logout</c>，再等「回到標題畫面」那一扇確認框（用 Addon 表的句子認）才按「是」。
/// 契約：只在框架執行緒呼叫；<see cref="Cancel"/> 隨時可停（急停、手動停止都走它），各階段都有時間上限。
/// </summary>
internal static unsafe class TriadLogoutAfterRun
{
    private enum Stage
    {
        Idle,
        WaitingToSend,
        WaitingForPrompt,
    }

    private static readonly TimeSpan SendWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PromptWindow = TimeSpan.FromSeconds(15);

    private static Stage stage;

    private static DateTime deadlineUtc;

    /// <summary>YesAlready 確定不會按確認框的期限；null＝問不到，確認框交給 YesAlready 或使用者。</summary>
    private static DateTime? claimUntilUtc;

    public static bool IsPending => stage != Stage.Idle;

    public static void Schedule()
    {
        stage = Stage.WaitingToSend;
        deadlineUtc = DateTime.UtcNow + SendWindow;
        claimUntilUtc = null;
        Svc.Log.Information("[Saucy] 九宮幻卡跑完，等角色空下來後自動登出");
    }

    public static void Cancel(string reason)
    {
        if (stage == Stage.Idle)
        {
            return;
        }

        stage = Stage.Idle;
        claimUntilUtc = null;
        Svc.Log.Information($"[Saucy] 取消九宮幻卡跑完後的自動登出（{reason}）");
    }

    public static void Tick()
    {
        if (stage == Stage.Idle)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now >= deadlineUtc)
        {
            var what = stage == Stage.WaitingToSend ? "角色一直沒有空下來，沒有送出登出指令" : "等不到登出確認框或沒有按下";
            stage = Stage.Idle;
            Svc.Log.Information($"[Saucy] 九宮幻卡跑完後的自動登出放棄：{what}");
            return;
        }

        if (stage == Stage.WaitingToSend)
        {
            TickSend(now);
            return;
        }

        TickPrompt(now);
    }

    private static void TickSend(DateTime now)
    {
        if (!Player.Available || !Player.Interactable || IsOccupied())
        {
            return;
        }

        // YesAlready 在確認框 PostSetup 當下就按，所以要在送指令之前暫停它；問不到就只送指令、不跟它搶按。
        claimUntilUtc = YesAlready.TryPauseSelectYesno((int)PromptWindow.TotalMilliseconds) ? now + PromptWindow : null;
        Chat.SendMessage("/logout");
        if (claimUntilUtc == null)
        {
            stage = Stage.Idle;
            Svc.Log.Information("[Saucy] 已送出登出指令；無法確認 YesAlready 不會按確認框，這一扇交給它或你自己按");
            return;
        }

        stage = Stage.WaitingForPrompt;
        deadlineUtc = now + PromptWindow;
        Svc.Log.Information("[Saucy] 已送出登出指令，等登出確認框");
    }

    private static void TickPrompt(DateTime now)
    {
        if (claimUntilUtc is not { } claimUntil || now >= claimUntil)
        {
            return;
        }

        if (!SelectYesnoHelper.TryGetVisible(out var yesno) || !SelectYesnoHelper.IsLogoutPrompt(yesno))
        {
            return;
        }

        if (!SelectYesnoHelper.PressYes(yesno))
        {
            return;
        }

        stage = Stage.Idle;
        Svc.Log.Information("[Saucy] Saucy 已按下登出確認框的「是」");
    }
}
