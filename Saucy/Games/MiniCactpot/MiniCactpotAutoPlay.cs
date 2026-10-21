using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using Saucy.JumboCactpot;
using System;
using System.Numerics;
using GoldSaucerTextData = Lumina.Excel.Sheets.GoldSaucerTextData;

namespace Saucy.MiniCactpot;

/// <summary>
/// 抵達仙人微彩發放員之後的接手：跟發放員對話、在發放員選單選「參加」，其餘只看不按——
/// 對話與購票確認交給使用者的 YesAlready 或使用者自己，彩券面板交給 <see cref="MiniCactpotModule"/>，
/// 之後的每一張照舊走模組的「自動購買下一張」。由 <see cref="GoldSaucerNavigator"/> 抵達時啟動，
/// 或不論怎麼走到發放員旁、站定之後啟動（<see cref="MiniCactpotArrivalPolicy"/>，這種一趟不會自己去追發放員）。
/// </summary>
/// <remarks>
/// 契約：只在 framework 執行緒呼叫；物件與視窗每幀重查，跨幀只留位址做等值比較，從不解參。
/// 🔴 唯一送出的視窗輸入是發放員選單的「參加」，而且只按「Saucy 請 YesAlready 暫停清單選項之後才出現」的那一扇：
/// YesAlready 只在選單 setup 當下判斷要不要選，TextAdvance 不處理這種選單，Questionable 只在跑任務時選任務名。
/// 確定只有 Saucy 會按，才不會兩個外掛同時按到同一扇正在關的窗。
/// </remarks>
internal static unsafe class MiniCactpotAutoPlay
{
    private const string LotteryDailyAddonName = "LotteryDaily";

    /// <summary>GoldSaucerTextData：發放員選單裡的「參加仙人微彩有獎競猜」。</summary>
    private const uint JoinOptionTextRow = 101;

    private const string InteractThrottleKey = "Saucy.MiniCactpotAutoPlay.Interact";
    private const int MaxInteractAttempts = 3;
    private const int MaxApproachMoves = 3;
    private const float ApproachRange = 2f;

    /// <summary>問不到遊戲的互動範圍判定時，退回用這個水平距離判斷。</summary>
    private const float FallbackInteractRange = 3f;

    private const float BrokerSearchRadius = 15f;

    /// <summary>導航在啟動這一趟的同一幀才停下路徑；先站穩再互動。</summary>
    private static readonly TimeSpan ArrivalSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>選單出現後先等它填好再選。</summary>
    private static readonly TimeSpan MenuSettle = TimeSpan.FromMilliseconds(600);

    /// <summary>每次互動前請 YesAlready 暫停清單選項的長度；只要涵蓋「互動→選單出現」那一段。</summary>
    private static readonly TimeSpan MenuClaim = TimeSpan.FromSeconds(5);

    /// <summary>選過之後同一扇選單在這段時間內不再碰；過了還在才重試。</summary>
    private static readonly TimeSpan MenuRetry = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan InteractResponseWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FindBrokerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EngageTimeout = TimeSpan.FromSeconds(30);

    /// <summary>發放員有回應之後（或上一張收掉之後）這麼久都沒開出彩券面板，就當成對話或購票確認沒人處理。</summary>
    private static readonly TimeSpan NoTicketTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan IdleEnd = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>「走到發放員旁」的取樣間隔（毫秒）：要掃物件表、問互動距離，不必每幀做。</summary>
    private const long ArrivalCheckIntervalMs = 200;

    private const long ArrivalErrorBackoffMs = 30_000;

    private static readonly ConditionFlag[] ArrivalBusyFlags =
    [
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied33, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
        ConditionFlag.OccupiedSummoningBell, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.InCombat
    ];

    private static Session? session;
    private static string? joinOptionText;
    private static readonly MiniCactpotArrivalPolicy Arrival = new();
    private static long nextArrivalCheckMs;
    private static bool arrivalWatchErrorLogged;

    public static bool IsActive => session != null;

    /// <summary>已在地化的一行狀態；閒置時是空字串。</summary>
    public static string StatusText => session?.Status ?? string.Empty;

    /// <summary>導航抵達仙人微彩發放員時呼叫。設定沒開就什麼都不做（維持「只移動、選中 NPC」）。</summary>
    public static void OnArrived()
    {
        if (!C.MiniCactpotAutoTalkOnArrival)
        {
            return;
        }

        if (!IsModuleActive())
        {
            Svc.Chat.Print("[Saucy] " + "The Mini Cactpot module is not enabled, so this trip only walked there without talking.".Loc());
            return;
        }

        var known = GoldSaucerEventTracker.TryGetMiniCactpotRemainingToday(out var remaining);
        if (known && remaining == 0)
        {
            Log("今天的仙人微彩已知用完（剩 0 張），這一趟不對話");
            Svc.Chat.Print("[Saucy] " + "Today's Mini Cactpot tickets are already used up, so this trip does not talk to the broker.".Loc());
            return;
        }

        Begin(passive: false);
        Log($"抵達發放員，開始自動對話（今日剩餘：{(known ? remaining.ToString() : "未知")}）");
    }

    private static void Begin(bool passive)
    {
        var now = DateTime.UtcNow;
        session = new Session
        {
            Passive = passive,
            StartedUtc = now,
            LastActivityUtc = now,
            WaitingSinceUtc = now,
            Status = "Mini Cactpot: talking to the broker...".Loc()
        };
    }

    public static void Cancel(bool announce = true)
    {
        Arrival.Disarm();
        if (session is not { } s)
        {
            return;
        }

        Release(s);
        Log($"取消（這一趟開了 {s.TicketsOpened} 張）");
        if (announce)
        {
            Svc.Chat.Print("[Saucy] " + "Mini Cactpot auto-play cancelled.".Loc());
        }
    }

    public static void Tick()
    {
        if (session is not { } s)
        {
            TickArrivalWatch();
            return;
        }

        Arrival.Disarm();
        try
        {
            TickSession(s, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // 只接得到受管理例外；停在這裡，免得每幀重擲、連帶跳過同一個 RunBot 裡後面的自動化。
            Svc.Log.Error(ex, "[MiniCactpotAutoPlay] 例外，已停止");
            Stop(s, "an unexpected error occurred".Loc());
        }
    }

    /// <summary>不經導航、自己（或別的插件）走到發放員旁站定時接手；條件見 <see cref="MiniCactpotArrivalPolicy"/>。</summary>
    private static void TickArrivalWatch()
    {
        var nowMs = Environment.TickCount64;
        if (nowMs < nextArrivalCheckMs)
        {
            return;
        }

        nextArrivalCheckMs = nowMs + ArrivalCheckIntervalMs;
        try
        {
            ObserveArrival(nowMs);
        }
        catch (Exception ex)
        {
            // 別的插件的 IPC 剛好在卸載也會走到這裡：退避重試，錯誤只記一次。
            nextArrivalCheckMs = nowMs + ArrivalErrorBackoffMs;
            if (!arrivalWatchErrorLogged)
            {
                arrivalWatchErrorLogged = true;
                Svc.Log.Error(ex, "[MiniCactpotAutoPlay] 判斷是否走到發放員旁時發生例外，30 秒後重試（之後的同類錯誤不再記）");
            }
        }
    }

    private static void ObserveArrival(long nowMs)
    {
        if (!C.MiniCactpotAutoTalkOnArrival || !GoldSaucerVenue.InSaucer || !IsModuleActive())
        {
            Arrival.Reset();
            return;
        }

        // 城內傳送的讀取畫面：保留狀態，傳送前後的位置差才看得出「離開過」。
        if (!Player.Available || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        var player = Player.Position;
        var broker = ObjectHelper.FindNearestByBaseId(GoldSaucerVenue.MiniCactpotBrokerId);
        var distance = broker == null ? float.NaN : GoldSaucerVenue.HorizontalDistance(player, broker.Position);
        var near = broker != null && distance <= MiniCactpotArrivalPolicy.NearDistance;
        var busy = near && IsArrivalBusy();
        var moving = near && IsArrivalMoving();
        var inRange = near && !busy && !moving &&
                      (InteractRangeCheck.IsWithinRange(broker!, "MiniCactpotAutoPlay") ?? distance <= FallbackInteractRange);
        var known = GoldSaucerEventTracker.TryGetMiniCactpotRemainingToday(out var remaining);

        var decision = Arrival.Decide(nowMs, player, distance, inRange, busy, moving, known && remaining == 0, out var note);
        switch (note)
        {
            case MiniCactpotArrivalPolicy.Note.OutOfInteractRange:
                Log($"站定在離發放員 {distance:0.0} 米處，不在互動距離內，不自動對話（走近一點站定就會開始）");
                break;
            case MiniCactpotArrivalPolicy.Note.KnownZero:
                Log("站定在發放員旁，但今天的仙人微彩已知用完（剩 0 張），不自動對話");
                break;
            case MiniCactpotArrivalPolicy.Note.Busy:
                Log("在發放員旁時正在對話、事件或選單中（多半是你自己先開始了），這次靠近不自動對話");
                break;
        }

        if (decision != MiniCactpotArrivalPolicy.Decision.Fire)
        {
            return;
        }

        Begin(passive: true);
        Log($"走到發放員旁站定（{distance:0.0} 米，不是經由導航），開始自動對話（今日剩餘：{(known ? remaining.ToString() : "未知")}）");
        Svc.Chat.Print("[Saucy] " + "Mini Cactpot: you are standing next to the broker, so Saucy is talking to them. Move to cancel.".Loc());
    }

    private static bool IsArrivalBusy()
    {
        foreach (var flag in ArrivalBusyFlags)
        {
            if (Svc.Condition[flag])
            {
                return true;
            }
        }

        return TalkHelper.IsVisible() || SelectYesnoHelper.IsVisible() || SelectStringHelper.IsNpcListMenuVisible() ||
               IsLotteryDailyVisible();
    }

    /// <summary>角色還在被帶著走、你按著移動鍵，或現在不能互動：都還不算站定。</summary>
    private static bool IsArrivalMoving() =>
        GoldSaucerNavigator.IsActive || JumboCactpotAutoTalk.IsActive || Vnavmesh.IsMoving() || Lifestream.IsBusyNow() ||
        GoldSaucerNavigator.IsManualMovementRequested() || !Player.Interactable || Player.IsAnimationLocked;

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
            Stop(s, "the Mini Cactpot module was disabled".Loc());
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

        if (IsLotteryDailyVisible())
        {
            if (!s.LotteryWasVisible)
            {
                s.LotteryWasVisible = true;
                s.TicketsOpened++;
                Log($"彩券面板開啟，這一趟第 {s.TicketsOpened} 張");
            }

            s.Engaged = true;
            s.LastActivityUtc = now;
            s.WaitingSinceUtc = now;
            s.Status = "Mini Cactpot: playing ticket ?? of this trip...".Loc(s.TicketsOpened);
            return;
        }

        s.LotteryWasVisible = false;

        var targetingBroker = Svc.Targets.Target?.BaseId == GoldSaucerVenue.MiniCactpotBrokerId;
        var menuVisible = TickMenu(s, targetingBroker, now);
        if (session != s)
        {
            return;
        }

        // Talk 與確認框只看有沒有開著，從不按：那是使用者的 YesAlready 或使用者自己的事。
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
            Log("發放員有回應；之後的對話與購票確認交給 YesAlready 或使用者，Saucy 只等待");
        }

        if (menuVisible || talkVisible || yesnoVisible || occupied)
        {
            s.LastActivityUtc = now;
            if (now - s.WaitingSinceUtc >= NoTicketTimeout)
            {
                StopUnanswered(s, menuVisible, talkVisible, yesnoVisible);
                return;
            }

            s.Status = menuVisible
                ? "Mini Cactpot: talking to the broker...".Loc()
                : "Mini Cactpot: waiting for YesAlready or you to confirm the purchase...".Loc();
            return;
        }

        if (now - s.LastActivityUtc >= IdleEnd)
        {
            Finish(s);
        }
    }

    private static void TickApproachAndInteract(Session s, DateTime now)
    {
        // 使用者自己動了就讓位：不重送靠近路徑，停手 2 秒再接續；讓位太久就停下這一趟。
        switch (s.Steer.Tick(ownsPath: s.Approaching, busy: false, out var paused))
        {
            case ManualSteerYield.Result.Hold when s.Passive && s.Steer.Yielding:
                Stop(s, "manual movement detected".Loc());
                return;
            case ManualSteerYield.Result.Hold:
                s.Status = "Mini Cactpot: paused while you move on your own...".Loc();
                return;
            case ManualSteerYield.Result.Resumed:
                s.StartedUtc += paused;
                s.ApproachMoves = Math.Min(s.ApproachMoves, MaxApproachMoves - 1);
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

        var broker = ObjectHelper.FindNearestByBaseId(GoldSaucerVenue.MiniCactpotBrokerId, BrokerSearchRadius);
        if (broker == null)
        {
            if (now - s.StartedUtc > FindBrokerTimeout)
            {
                Stop(s, "the broker could not be found nearby".Loc());
            }

            return;
        }

        var inRange = InteractRangeCheck.IsWithinRange(broker, "MiniCactpotAutoPlay") ??
                      GoldSaucerVenue.HorizontalDistance(Player.Position, broker.Position) <= FallbackInteractRange;
        if (!inRange && s.Passive)
        {
            // 不是你按導航叫它去的：不追著發放員走。
            Stop(s, "no longer within talking range of the broker".Loc());
            return;
        }

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

        s.Status = "Mini Cactpot: talking to the broker...".Loc();

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

    private static void TickApproach(Session s, Vector3 brokerPosition)
    {
        s.Status = "Mini Cactpot: moving closer to the broker...".Loc();
        if (Vnavmesh.IsMoving())
        {
            return;
        }

        if (!Vnavmesh.IsInstalled || s.ApproachMoves >= MaxApproachMoves)
        {
            Stop(s, "could not get within talking range of the broker".Loc());
            return;
        }

        s.ApproachMoves++;
        s.Approaching = true;
        Log($"不在互動範圍內，往發放員靠近（第 {s.ApproachMoves} 次）");
        Vnavmesh.TryMoveTo(brokerPosition, false, ApproachRange);
    }

    /// <summary>互動前請 YesAlready 暫停清單選項；確認它不會選，才記下「這段時間出現的選單歸 Saucy」。</summary>
    /// <remarks>每半個認領長度最多問一次：問不到時不會每幀重打 IPC，連續認領也不會中斷。</remarks>
    private static void ClaimMenu(Session s, DateTime now)
    {
        if (s.LastClaimAttemptUtc is { } tried && now - tried < MenuClaim / 2)
        {
            return;
        }

        s.LastClaimAttemptUtc = now;
        if (!YesAlready.TryPauseListSelection((int)MenuClaim.TotalMilliseconds))
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

    /// <summary>發放員的選單（參加／說明／取消）：只在 Saucy 認領過的那一扇上選「參加」。回傳選單是否可見。</summary>
    private static bool TickMenu(Session s, bool targetingBroker, DateTime now)
    {
        if (!SelectStringHelper.TryGetVisibleSelectIconString(out var menu))
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

        if (!targetingBroker || now - seen.SinceUtc < MenuSettle ||
            (s.MenuPicked is { } picked && picked.Address == address && now - picked.SinceUtc < MenuRetry))
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
        switch (SelectStringHelper.TrySelectIconStringEntryByText(menu, joinText, out var entries))
        {
            case SelectStringHelper.MenuPick.Selected:
                s.MenuPicked = new Sighting(address, now);
                Log($"在發放員選單選「{joinText}」");
                break;
            case SelectStringHelper.MenuPick.NotFound:
                Log($"發放員選單裡找不到「{joinText}」，選項：{entries}");
                Stop(s, "the join option is not in the broker's menu, please choose it yourself".Loc());
                break;
        }

        return true;
    }

    /// <summary>第一次看到這扇選單的時間落在認領期間內＝它 setup 時 YesAlready 的清單選項正暫停著，之後也不會再判斷它。</summary>
    private static bool IsClaimed(Session s, DateTime menuSeenUtc) =>
        s.MenuClaimedFromUtc is { } from && s.MenuClaimedUntilUtc is { } until &&
        menuSeenUtc >= from && menuSeenUtc <= until;

    private static bool IsLotteryDailyVisible()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(LotteryDailyAddonName).Address;
        return addon != null && addon->IsVisible;
    }

    private static bool IsModuleActive() =>
        C.IsModuleEnabled(ModuleNames.MiniCactpot) &&
        global::Saucy.Saucy.ModuleManager?.GetModule<MiniCactpotModule>()?.IsEnabled == true;

    /// <summary>選單裡「參加」那一項的文字；讀不到是空字串，那就永遠比不中（不猜索引）。</summary>
    private static string GetJoinOptionText() => joinOptionText ??=
        Svc.Data.GetExcelSheet<GoldSaucerTextData>()?.GetRowOrDefault(JoinOptionTextRow)?.Text.ExtractText().Trim() ?? string.Empty;

    /// <summary>等太久都沒開出彩券面板：說清楚是哪一扇沒人處理。今天已知玩完時，剩下的只是發放員的道別對話。</summary>
    private static void StopUnanswered(Session s, bool menuVisible, bool talkVisible, bool yesnoVisible)
    {
        if (!menuVisible && GoldSaucerEventTracker.TryGetMiniCactpotRemainingToday(out var remaining) && remaining == 0)
        {
            Finish(s);
            return;
        }

        var prompt = yesnoVisible && SelectYesnoHelper.TryGetVisible(out var yesno)
            ? Shorten(SelectYesnoHelper.GetPrompt(yesno))
            : "（無）";
        Log($"{NoTicketTimeout.TotalSeconds:0} 秒內沒有開出彩券：選單={menuVisible}、對話={talkVisible}、確認框={yesnoVisible}、確認框內文={prompt}。" +
            (menuVisible ? "發放員的選單沒有被選擇，請自己選「參加」。" : "購票確認沒有被處理，請確認 YesAlready 的規則或自己按。"));
        Stop(s, menuVisible
            ? "the broker's menu was not answered, please pick the join option yourself".Loc()
            : "the broker's dialog or purchase confirmation was not handled; check your YesAlready rules or press it yourself".Loc());
    }

    private static void Finish(Session s)
    {
        Release(s);
        var known = GoldSaucerEventTracker.TryGetMiniCactpotRemainingToday(out var remaining);
        Log($"發放員的對話結束：這一趟開了 {s.TicketsOpened} 張、今日剩餘 {(known ? remaining.ToString() : "未知")}");

        var message = known && remaining == 0
            ? "Mini Cactpot: today's tickets are all used up (?? played this trip).".Loc(s.TicketsOpened)
            : s.TicketsOpened > 0
                ? "Mini Cactpot: the broker's dialog has ended (?? played this trip).".Loc(s.TicketsOpened)
                : "Mini Cactpot: the dialog ended without a ticket (today's may be used up, or not enough MGP).".Loc();
        Svc.Chat.Print("[Saucy] " + message);
    }

    private static void Stop(Session s, string reason)
    {
        Release(s);
        Log($"停止：{reason}（這一趟開了 {s.TicketsOpened} 張）");
        Svc.Chat.PrintError("[Saucy] " + "Mini Cactpot auto-play stopped: ??.".Loc(reason));
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

    private static string Shorten(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 60 ? flat : flat[..60] + "…";
    }

    private static void Log(string message) => Svc.Log.Information($"[MiniCactpotAutoPlay] {message}");

    /// <param name="Address">只做等值比較的視窗位址，從不解參。</param>
    private readonly record struct Sighting(nint Address, DateTime SinceUtc);

    private sealed class Session
    {
        /// <summary>true＝站定觸發（不是導航抵達）：你一動或離開互動距離就停，不自己靠近。</summary>
        public bool Passive;

        public bool Approaching;
        public int ApproachMoves;
        public readonly ManualSteerYield Steer = new("仙人微彩接手靠近發放員");
        public bool ClaimFailLogged;
        public bool Engaged;
        public int InteractAttempts;
        public DateTime? LastClaimAttemptUtc;
        public DateTime LastActivityUtc;
        public DateTime? LastInteractUtc;
        public bool LotteryWasVisible;
        public Sighting? Menu;
        public DateTime? MenuClaimedFromUtc;
        public DateTime? MenuClaimedUntilUtc;
        public Sighting? MenuPicked;
        public bool MenuYieldLogged;
        public DateTime StartedUtc;
        public string Status = string.Empty;
        public int TicketsOpened;
        public DateTime WaitingSinceUtc;
    }
}
