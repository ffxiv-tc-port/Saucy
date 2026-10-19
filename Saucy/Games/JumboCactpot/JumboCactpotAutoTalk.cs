using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Numerics;
using GoldSaucerTalk = Lumina.Excel.Sheets.GoldSaucerTalk;

namespace Saucy.JumboCactpot;

/// <summary>
/// 從「導航」抵達仙人仙彩發放員之後的接手：跟發放員對話、在「要做什麼？」選單選「參加仙人仙彩有獎競猜」，購票面板一開就結束。
/// 號碼由 <see cref="JumboCactpotModule"/> 填；對話、購票確認與「要不要再買一次？」交給使用者的 YesAlready 或使用者自己。
/// 只由 <see cref="GoldSaucerNavigator"/> 抵達時啟動。
/// </summary>
/// <remarks>
/// 契約：只在 framework 執行緒呼叫；物件與視窗每幀重查，跨幀只留位址做等值比較，從不解參。
/// 🔴 唯一送出的視窗輸入是那扇 SelectString 的「參加」，而且只按「Saucy 請 YesAlready 暫停 SelectString 選項之後才出現」的那一扇：
/// YesAlready 只在選單 setup 當下判斷要不要選，TextAdvance 只碰跳過過場的確認框，Questionable 只在跑任務時選任務名。
/// 問不到 YesAlready 就交給使用者自己選；一趟只選一次，之後再出現的選單一律不碰。
/// </remarks>
internal static unsafe class JumboCactpotAutoTalk
{
    private const string InputAddonName = "LotteryWeeklyInput";

    /// <summary>GoldSaucerTalk：發放員的「要做什麼？」選單，Unknown1 是第一個選項「參加仙人仙彩有獎競猜」。</summary>
    private const uint BrokerMenuTalkRow = 105;

    private const string InteractThrottleKey = "Saucy.JumboCactpotAutoTalk.Interact";
    private const string LogTag = "JumboCactpotAutoTalk";
    private const int MaxInteractAttempts = 3;
    private const float ApproachRange = 2f;

    /// <summary>問不到遊戲的互動範圍判定時，退回用這個水平距離判斷。</summary>
    private const float FallbackInteractRange = 3f;

    private const float BrokerSearchRadius = 15f;

    /// <summary>導航在啟動這一趟的同一幀才停下路徑；先站穩再互動。</summary>
    private static readonly TimeSpan ArrivalSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>選單出現後先等它填好再選。</summary>
    private static readonly TimeSpan MenuSettle = TimeSpan.FromMilliseconds(600);

    /// <summary>每次請 YesAlready 暫停 SelectString 選項的長度。</summary>
    private static readonly TimeSpan MenuClaim = TimeSpan.FromSeconds(5);

    /// <summary>選過之後同一扇選單在這段時間內不再碰；過了還在才重試。</summary>
    private static readonly TimeSpan MenuRetry = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan InteractResponseWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FindBrokerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EngageTimeout = TimeSpan.FromSeconds(30);

    /// <summary>發放員有回應之後這麼久都沒開出購票面板，就當成對話或選單沒人處理。</summary>
    private static readonly TimeSpan NoPanelTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan IdleEnd = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(5);

    private static Session? session;
    private static string? joinOptionText;

    public static bool IsActive => session != null;

    /// <summary>已在地化的一行狀態；閒置時是空字串。</summary>
    public static string StatusText => session?.Status ?? string.Empty;

    /// <summary>導航抵達仙人仙彩發放員時呼叫。設定沒開就什麼都不做（維持「只移動、選中 NPC」）。</summary>
    public static void OnArrived()
    {
        if (!C.JumboCactpotAutoTalkOnArrival)
        {
            return;
        }

        if (!IsModuleActive())
        {
            Svc.Chat.Print("[Saucy] " + "The Jumbo Cactpot module is not enabled, so this trip only walked there without talking.".Loc());
            return;
        }

        var now = DateTime.UtcNow;
        session = new Session
        {
            StartedUtc = now,
            LastActivityUtc = now,
            WaitingSinceUtc = now,
            Status = "Jumbo Cactpot: talking to the broker...".Loc()
        };
        Log($"抵達發放員，開始自動對話（{GoldSaucerEventTracker.DescribeWeeklyTickets()}）");
    }

    public static void Cancel(bool announce = true)
    {
        if (session is not { } s)
        {
            return;
        }

        Release(s);
        Log("取消");
        if (announce)
        {
            Svc.Chat.Print("[Saucy] " + "Jumbo Cactpot auto-talk cancelled.".Loc());
        }
    }

    public static void Tick()
    {
        if (session is not { } s)
        {
            return;
        }

        try
        {
            TickSession(s, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // 只接得到受管理例外；停在這裡，免得每幀重擲、連帶跳過同一個 RunBot 裡後面的自動化。
            Svc.Log.Error(ex, $"[{LogTag}] 例外，已停止");
            Stop(s, "an unexpected error occurred".Loc());
        }
    }

    private static void TickSession(Session s, DateTime now)
    {
        if (Svc.Condition[ConditionFlag.BetweenAreas] || !Player.Available)
        {
            return;
        }

        if (!GoldSaucerVenue.InSaucer)
        {
            Stop(s, "you left the Gold Saucer".Loc());
            return;
        }

        if (!IsModuleActive())
        {
            Stop(s, "the Jumbo Cactpot module was disabled".Loc());
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            Stop(s, "you are in combat".Loc());
            return;
        }

        if (now - s.StartedUtc > OverallTimeout)
        {
            Stop(s, "it timed out".Loc());
            return;
        }

        if (IsInputVisible())
        {
            Finish(s);
            return;
        }

        var targetingBroker = Svc.Targets.Target?.BaseId == GoldSaucerVenue.JumboCactpotBrokerId;
        var menuVisible = TickMenu(s, targetingBroker, now);
        if (session != s)
        {
            return;
        }

        // 對話與確認框只看有沒有開著，從不按：那是使用者的 YesAlready 或使用者自己的事。
        var talkVisible = TalkHelper.IsVisible();
        var yesnoVisible = SelectYesnoHelper.IsVisible();
        var occupied = Svc.Condition[ConditionFlag.OccupiedInQuestEvent];

        if (!s.Engaged)
        {
            // 還沒送出互動時的 OccupiedInQuestEvent 是玩家自己的別件事，不算發放員有回應。
            var brokerWindow = targetingBroker && (menuVisible || talkVisible || yesnoVisible);
            if (!brokerWindow && !(s.InteractAttempts > 0 && occupied))
            {
                TickApproachAndInteract(s, now);
                return;
            }

            s.Engaged = true;
            s.WaitingSinceUtc = now;
            Log("發放員有回應；對話交給 YesAlready 或使用者，Saucy 只在選單出現時選「參加」");
        }

        // 選單在發放員的開場對話之後才出現，對話可能由使用者自己慢慢按：
        // 還沒選到之前，對話開著就持續認領，選單 setup 時才仍在 YesAlready 讓開的期間內。
        if (!s.JoinPicked && targetingBroker && talkVisible)
        {
            ClaimMenu(s, now);
        }

        if (menuVisible || talkVisible || yesnoVisible || occupied)
        {
            s.LastActivityUtc = now;
            if (now - s.WaitingSinceUtc >= NoPanelTimeout)
            {
                StopUnanswered(s, menuVisible, talkVisible, yesnoVisible);
                return;
            }

            s.Status = "Jumbo Cactpot: talking to the broker...".Loc();
            return;
        }

        if (now - s.LastActivityUtc >= IdleEnd)
        {
            EndWithoutPanel(s);
        }
    }

    private static void TickApproachAndInteract(Session s, DateTime now)
    {
        // 使用者自己動了就讓位：不重送靠近路徑，停手 2 秒再接續一次；讓位太久就停下這一趟。
        switch (s.Steer.Tick(ownsPath: s.Approaching, busy: false, out var paused))
        {
            case ManualSteerYield.Result.Hold:
                s.Status = "Jumbo Cactpot: paused while you move on your own...".Loc();
                return;
            case ManualSteerYield.Result.Resumed:
                s.StartedUtc += paused;
                s.Approaching = false;
                break;
            case ManualSteerYield.Result.GaveUp:
                Stop(s, "manual movement detected".Loc());
                return;
        }

        if (now - s.StartedUtc > EngageTimeout)
        {
            Stop(s, "the broker did not respond".Loc());
            return;
        }

        var broker = ObjectHelper.FindNearestByBaseId(GoldSaucerVenue.JumboCactpotBrokerId, BrokerSearchRadius);
        if (broker == null)
        {
            if (now - s.StartedUtc > FindBrokerTimeout)
            {
                Stop(s, "the Jumbo Cactpot broker could not be found nearby".Loc());
            }

            return;
        }

        var inRange = InteractRangeCheck.IsWithinRange(broker, LogTag) ??
                      GoldSaucerVenue.HorizontalDistance(Player.Position, broker.Position) <= FallbackInteractRange;
        if (!inRange)
        {
            TickApproach(s, broker.Position);
            return;
        }

        if (s.Approaching)
        {
            s.Approaching = false;
            if (Vnavmesh.IsMoving())
            {
                Vnavmesh.StopPath();
            }
        }

        if (now - s.StartedUtc < ArrivalSettle ||
            !Player.Interactable || Player.IsAnimationLocked || Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return;
        }

        if (s.LastInteractUtc is { } last && now - last < InteractResponseWait)
        {
            return;
        }

        if (s.InteractAttempts >= MaxInteractAttempts)
        {
            Stop(s, "the broker did not respond".Loc());
            return;
        }

        s.Status = "Jumbo Cactpot: talking to the broker...".Loc();

        // 先選中發放員（這一步不互動），選中之後才認領選單、真的送出互動。
        if (Svc.Targets.Target?.Address != broker.Address)
        {
            ObjectHelper.TryInteractWithObject(broker, InteractThrottleKey);
            return;
        }

        ClaimMenu(s, now);

        // 回 true 只代表「這一幀送出了互動」；InteractWithObject 的回傳值沒有語意，成敗看有沒有視窗出來。
        if (ObjectHelper.TryInteractWithObject(broker, InteractThrottleKey))
        {
            s.InteractAttempts++;
            s.LastInteractUtc = now;
            Log($"對發放員送出互動（第 {s.InteractAttempts} 次）");
        }
    }

    /// <summary>只靠近一次：路徑自己停了還搆不到就停；使用者接手造成的停下由讓位處理，停手後才再靠近一次。</summary>
    private static void TickApproach(Session s, Vector3 brokerPosition)
    {
        s.Status = "Jumbo Cactpot: moving closer to the broker...".Loc();
        if (Vnavmesh.IsMoving())
        {
            return;
        }

        if (!Vnavmesh.IsInstalled || s.Approaching)
        {
            Stop(s, "could not get within talking range of the broker".Loc());
            return;
        }

        s.Approaching = true;
        Log("不在互動範圍內，往發放員靠近");
        Vnavmesh.TryMoveTo(brokerPosition, false, ApproachRange);
    }

    /// <summary>請 YesAlready 暫停 SelectString 選項；確認它不會選，才記下「這段時間出現的選單歸 Saucy」。</summary>
    /// <remarks>每半個認領長度最多問一次：問不到時不會每幀重打 IPC，連續認領也不會中斷。</remarks>
    private static void ClaimMenu(Session s, DateTime now)
    {
        if (s.LastClaimAttemptUtc is { } tried && now - tried < MenuClaim / 2)
        {
            return;
        }

        s.LastClaimAttemptUtc = now;
        if (!YesAlready.TryPauseSelectString((int)MenuClaim.TotalMilliseconds))
        {
            if (!s.ClaimFailLogged)
            {
                s.ClaimFailLogged = true;
                Log("無法確認 YesAlready 不會選發放員的選單，這一扇交給你自己選");
            }

            return;
        }

        // 連續認領時保留最早的起點：YesAlready 的暫停取 max，中間沒有斷過。
        if (s.MenuClaimedUntilUtc is not { } until || until <= now)
        {
            s.MenuClaimedFromUtc = now;
        }

        s.MenuClaimedUntilUtc = now + MenuClaim;
    }

    /// <summary>發放員的「要做什麼？」選單：只在 Saucy 認領過的那一扇上選「參加」，一趟只選一次。回傳選單是否可見。</summary>
    private static bool TickMenu(Session s, bool targetingBroker, DateTime now)
    {
        if (!SelectStringHelper.TryGetVisibleSelectString(out var menu))
        {
            s.Menu = null;
            s.MenuPicked = null;
            return false;
        }

        var address = (nint)menu;
        if (s.Menu is not { } seen || seen.Address != address)
        {
            s.Menu = new Sighting(address, now);
            return true;
        }

        if (!targetingBroker || now - seen.SinceUtc < MenuSettle)
        {
            return true;
        }

        // 選過之後只對同一扇、而且它一直沒關掉才重試；關掉後再出現的選單一律交給使用者。
        if (s.JoinPicked &&
            (s.MenuPicked is not { } picked || picked.Address != address || now - picked.SinceUtc < MenuRetry))
        {
            return true;
        }

        if (!IsClaimed(s, seen.SinceUtc))
        {
            if (!s.MenuYieldLogged)
            {
                s.MenuYieldLogged = true;
                Log("這扇選單不是在 YesAlready 讓開之後才出現的，不能確定只有 Saucy 會選，交給你自己選");
            }

            return true;
        }

        var joinText = GetJoinOptionText();
        switch (SelectStringHelper.TrySelectStringEntryByText(menu, joinText, out var entries))
        {
            case SelectStringHelper.MenuPick.Selected:
                s.MenuPicked = new Sighting(address, now);
                s.JoinPicked = true;
                Log($"在發放員選單選「{joinText}」");
                break;
            case SelectStringHelper.MenuPick.NotFound:
                Log($"發放員選單裡找不到「{joinText}」，選項：{entries}");
                Stop(s, "the join option is not in the broker's menu, please choose it yourself".Loc());
                break;
        }

        return true;
    }

    /// <summary>第一次看到這扇選單的時間落在認領期間內＝它 setup 時 YesAlready 的選項功能正暫停著，之後也不會再判斷它。</summary>
    private static bool IsClaimed(Session s, DateTime menuSeenUtc) =>
        s.MenuClaimedFromUtc is { } from && s.MenuClaimedUntilUtc is { } until &&
        menuSeenUtc >= from && menuSeenUtc <= until;

    private static bool IsInputVisible()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(InputAddonName).Address;
        return addon != null && addon->IsVisible;
    }

    private static bool IsModuleActive() =>
        C.IsModuleEnabled(ModuleNames.JumboCactpot) &&
        global::Saucy.Saucy.ModuleManager?.GetModule<JumboCactpotModule>()?.IsEnabled == true;

    /// <summary>選單裡「參加」那一項的文字；讀不到是空字串，那就永遠比不中（不猜索引）。</summary>
    private static string GetJoinOptionText() => joinOptionText ??=
        Svc.Data.GetExcelSheet<GoldSaucerTalk>()?.GetRowOrDefault(BrokerMenuTalkRow)?.Unknown1.ExtractText().Trim() ?? string.Empty;

    private static void StopUnanswered(Session s, bool menuVisible, bool talkVisible, bool yesnoVisible)
    {
        Log($"{NoPanelTimeout.TotalSeconds:0} 秒內沒有開出購票面板：選單={menuVisible}、對話={talkVisible}、確認框={yesnoVisible}、已選參加={s.JoinPicked}");
        Stop(s, menuVisible && !s.JoinPicked
            ? "the broker's menu was not answered, please pick the join option yourself".Loc()
            : "the broker's dialog was not advanced; check your YesAlready / TextAdvance rules or press it yourself".Loc());
    }

    private static void Finish(Session s)
    {
        Release(s);
        Log("購票面板已開啟，號碼交給仙人仙彩模組，這一趟結束");
        Svc.Chat.Print("[Saucy] " + "Jumbo Cactpot: the ticket panel is open; the module fills in the number and you confirm the purchase.".Loc());
    }

    private static void EndWithoutPanel(Session s)
    {
        Release(s);
        Log($"對話結束但沒有開出購票面板（已選參加={s.JoinPicked}；{GoldSaucerEventTracker.DescribeWeeklyTickets()}）");
        Svc.Chat.Print("[Saucy] " + (s.JoinPicked
            ? "Jumbo Cactpot: the dialog ended without a ticket panel (this week's three may already be bought, or not enough MGP).".Loc()
            : "Jumbo Cactpot: the dialog ended before the join option was picked.".Loc()));
    }

    private static void Stop(Session s, string reason)
    {
        Release(s);
        Log($"停止：{reason}");
        Svc.Chat.PrintError("[Saucy] " + "Jumbo Cactpot auto-talk stopped: ??.".Loc(reason));
    }

    /// <summary>結束這一趟；只收回自己發起的移動。</summary>
    private static void Release(Session s)
    {
        if (session == s)
        {
            session = null;
        }

        if (s.Approaching && Vnavmesh.IsMoving())
        {
            Vnavmesh.StopPath();
        }
    }

    private static void Log(string message) => Svc.Log.Information($"[{LogTag}] {message}");

    /// <param name="Address">只做等值比較的視窗位址，從不解參。</param>
    private readonly record struct Sighting(nint Address, DateTime SinceUtc);

    private sealed class Session
    {
        public bool Approaching;
        public readonly ManualSteerYield Steer = new("仙人仙彩接手靠近發放員");
        public bool ClaimFailLogged;
        public bool Engaged;
        public int InteractAttempts;
        public bool JoinPicked;
        public DateTime? LastClaimAttemptUtc;
        public DateTime LastActivityUtc;
        public DateTime? LastInteractUtc;
        public Sighting? Menu;
        public DateTime? MenuClaimedFromUtc;
        public DateTime? MenuClaimedUntilUtc;
        public Sighting? MenuPicked;
        public bool MenuYieldLogged;
        public DateTime StartedUtc;
        public string Status = string.Empty;
        public DateTime WaitingSinceUtc;
    }
}
