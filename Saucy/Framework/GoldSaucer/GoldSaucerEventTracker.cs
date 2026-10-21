using Dalamud.Hooking;
using ECommons.LanguageHelpers;
using System;
using System.Collections.Generic;

namespace Saucy.Framework.GoldSaucer;

/// <summary>
/// 金碟遊樂園事件回呼的共用解讀層。跟發放員對話當下伺服器送來的那筆事件，是仙人微彩「今天還剩
/// 幾張」與仙人仙彩「本週已買哪三組號碼」的唯一權威來源——兩邊的面板自己都沒有帶這些數字。
/// </summary>
/// <remarks>
/// 契約：<see cref="Detour"/> 由遊戲自己的事件處理路徑呼叫，只抄整數出來、不留任何原生指標；
/// 快照是不可變物件、整份換參考（參考指派是原子的），寫在事件執行緒與開彩券面板時、讀在繪製與框架執行緒。
/// 這個回呼是所有金碟遊戲共用的，每個遊戲的 data 版面都不一樣，只能靠「現在對話的是誰」區分。
/// 除了顯示，流程上的讀取端都只採信「確定今天剩 0 張」：導航抵達時不對話、面板關掉後不等「購買下一張」；
/// 不知道一律照常。
/// </remarks>
internal static unsafe class GoldSaucerEventTracker
{
    // DailyDuty 的 GoldSaucerMessageController 用的同一條。
    private const string GoldSaucerUpdateSignature = "E8 ?? ?? ?? ?? EB 07 48 8D 9F";

    /// <summary>仙人微彩發放員（ENpcResident 1010445）。</summary>
    private const uint MiniCactpotBrokerBaseId = 1010445;

    /// <summary>仙人仙彩發放員（ENpcResident 1010446）。</summary>
    private const uint JumboCactpotBrokerBaseId = 1010446;

    /// <summary>跟仙人微彩發放員說話當下伺服器回的「今日資訊」事件，<c>data[4]</c> 即剩餘張數。</summary>
    private const byte AllowanceEventId = 5;

    /// <summary>每日上限三張，用來擋掉「解讀錯欄位」的離譜值。</summary>
    private const int MaxAllowances = 3;

    /// <summary>本週三張仙人仙彩的號碼在 <c>data[2]</c> 起算的三格，值域 0000-9999。
    /// 索引與哨兵值照 DailyDuty 仙人仙彩模組的實際讀法，不是從仙人微彩的版面推的；
    /// 那邊也沒有對這筆事件的 eventId 設限，所以這裡同樣只認對話對象。</summary>
    private const int WeeklyTicketFirstIndex = 2;

    private const int WeeklyTicketSlots = 3;

    /// <summary>那一格還沒買時伺服器送的哨兵值。</summary>
    private const int EmptyTicketValue = 10000;

    private delegate void* GoldSaucerUpdateDelegate(void* a1, byte* a2, uint a3, ushort a4, void* a5, int* data, byte eventId);

    /// <summary>還需要這支 hook 的模組。兩個消費端共用同一支 detour，最後一個放手才拆掉。</summary>
    private static readonly HashSet<string> Consumers = [];

    private static Hook<GoldSaucerUpdateDelegate>? hook;

    private static volatile AllowanceSnapshot? allowances;

    private static volatile WeeklyTicketSnapshot? weeklyTickets;

    public static void Acquire(string consumer)
    {
        lock (Consumers)
        {
            Consumers.Add(consumer);
            if (hook != null)
            {
                return;
            }

            try
            {
                hook = Svc.Hook.HookFromSignature<GoldSaucerUpdateDelegate>(GoldSaucerUpdateSignature, Detour);
                hook.Enable();
            }
            catch (Exception ex)
            {
                // 特徵碼對不上不是故障：模組照常運作，只是這兩個數字永遠顯示「未知」。
                hook = null;
                Svc.Log.Warning(ex, "[Saucy] 金碟遊樂園事件特徵碼掛載失敗，仙人微彩剩餘張數與仙人仙彩本週號碼會一直顯示「未知」");
            }
        }
    }

    public static void Release(string consumer)
    {
        lock (Consumers)
        {
            Consumers.Remove(consumer);
            if (Consumers.Count > 0)
            {
                return;
            }

            hook?.Disable();
            hook?.Dispose();
            hook = null;
        }
    }

    /// <summary>給設定面板的一句話。收不到、換角色、或跨過今日重置一律回「未知」——
    /// 把不知道畫成 0 會直接誤導。</summary>
    public static string DescribeRemaining()
    {
        var snap = allowances;
        if (snap == null || !IsCurrent(snap))
        {
            return "今天還剩：未知（跟仙人微彩發放員說過話之後才知道）";
        }

        return snap.Inferred
            ? $"今天還剩 {snap.Remaining} 張（金碟遊樂園事件的張數扣掉 {snap.UpdatedUtc.ToLocalTime():HH:mm} 開的那張）"
            : $"今天還剩 {snap.Remaining} 張（來自金碟遊樂園事件，{snap.UpdatedUtc.ToLocalTime():HH:mm} 更新）";
    }

    /// <summary>今天還剩幾張。沒收到過、換了角色、或已經跨過每日重置都回 false（＝不知道）。</summary>
    public static bool TryGetMiniCactpotRemainingToday(out int remaining)
    {
        var snap = allowances;
        remaining = snap?.Remaining ?? 0;
        return snap != null && IsCurrent(snap);
    }

    /// <summary>仙人微彩開了一張彩券面板：把「今天還剩」扣一。框架執行緒呼叫。</summary>
    /// <remarks>
    /// 伺服器在每次購票確認之前都會送一次最新張數，所以一筆事件只扣一次：同一張面板重複 setup 不會多扣，
    /// 沒收到事件就不扣（寧可高估，讓導航照常對話）。當天最後一張之後不會再有購票，0 只能靠這裡扣出來。
    /// </remarks>
    public static void NoteMiniCactpotTicketOpened()
    {
        var snap = allowances;
        if (snap == null || snap.Inferred || snap.Remaining <= 0 || !IsCurrent(snap))
        {
            return;
        }

        var remaining = snap.Remaining - 1;
        allowances = snap with { Remaining = remaining, UpdatedUtc = DateTime.UtcNow, Inferred = true };
        Svc.Log.Information($"[Saucy] 仙人微彩今日剩餘張數：{remaining}（開彩券面板後由事件的張數扣一）");
    }

    private static bool IsCurrent(AllowanceSnapshot snap) =>
        snap.ContentId == Svc.PlayerState.ContentId && snap.UpdatedUtc >= LastDailyResetUtc();

    /// <summary>給設定面板的一句話。收不到、換角色、或跨過本週開獎一律回「未知」——
    /// 把不知道畫成「都沒買」會直接誤導。</summary>
    public static string DescribeWeeklyTickets()
    {
        var snap = weeklyTickets;
        if (snap == null || snap.ContentId != Svc.PlayerState.ContentId || snap.UpdatedUtc < LastWeeklyDrawingUtc())
        {
            return "This week's numbers: unknown (only known after talking to the Jumbo Cactpot broker).".Loc();
        }

        return "This week's numbers: ?? (from Gold Saucer event, updated ??)".Loc(snap.Numbers, snap.UpdatedUtc.ToLocalTime().ToString("MM/dd HH:mm"));
    }

    /// <summary>本週已買幾張（對話當下伺服器給的數字）。時刻都是 <see cref="Environment.TickCount64"/>：
    /// <paramref name="changedTick"/>＝內容最後一次變動、<paramref name="capturedTick"/>＝最後一次收到。
    /// 收不到、換角色、或跨過本週開獎回 false。</summary>
    public static bool TryGetJumboTicketsBought(out int bought, out long changedTick, out long capturedTick)
    {
        var snap = weeklyTickets;
        if (snap == null || snap.ContentId != Svc.PlayerState.ContentId || snap.UpdatedUtc < LastWeeklyDrawingUtc())
        {
            bought = -1;
            changedTick = 0;
            capturedTick = 0;
            return false;
        }

        bought = snap.Bought;
        changedTick = snap.ChangedTick;
        capturedTick = snap.CapturedTick;
        return true;
    }

    /// <summary>每日 15:00 UTC 重置（＝台北時間 23:00）。比這個時間早的快照已經過期。</summary>
    private static DateTime LastDailyResetUtc()
    {
        var now = DateTime.UtcNow;
        var todayReset = new DateTime(now.Year, now.Month, now.Day, 15, 0, 0, DateTimeKind.Utc);
        return now >= todayReset ? todayReset : todayReset.AddDays(-1);
    }

    /// <summary>開獎後本週號碼歸零。台服的開獎時刻沒有可離線查證的來源，這裡取其他區服共通的
    /// 「當地週六晚上」換算成台北時間，也就是週六 12:00 UTC 當界線。
    /// 界線猜錯只影響「何時改顯示未知」，抄到的號碼本身不受影響。</summary>
    private static DateTime LastWeeklyDrawingUtc()
    {
        var now = DateTime.UtcNow;
        var daysSinceSaturday = ((int)now.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        var thisWeek = now.Date.AddDays(-daysSinceSaturday).AddHours(12);
        return now >= thisWeek ? thisWeek : thisWeek.AddDays(-7);
    }

    private static void* Detour(void* a1, byte* a2, uint a3, ushort a4, void* a5, int* data, byte eventId)
    {
        try
        {
            Capture(data, eventId);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[Saucy] 解讀金碟遊樂園事件時發生例外");
        }

        return hook!.OriginalDisposeSafe(a1, a2, a3, a4, a5, data, eventId);
    }

    /// <summary>只讀不寫，而且在 <c>Original</c> 之前解參考，所以這裡的判空擋得到東西。</summary>
    private static void Capture(int* data, byte eventId)
    {
        if (data == null)
        {
            return;
        }

        var brokerId = Svc.Targets.Target?.BaseId;
        if (brokerId == MiniCactpotBrokerBaseId)
        {
            CaptureAllowances(data, eventId);
        }
        else if (brokerId == JumboCactpotBrokerBaseId)
        {
            CaptureWeeklyTickets(data);
        }
    }

    private static void CaptureAllowances(int* data, byte eventId)
    {
        if (eventId != AllowanceEventId)
        {
            return;
        }

        var remaining = data[4];
        if (remaining < 0 || remaining > MaxAllowances)
        {
            Svc.Log.Warning($"[Saucy] 金碟遊樂園事件回報的仙人微彩剩餘張數超出值域（{remaining}），已忽略");
            return;
        }

        var contentId = Svc.PlayerState.ContentId;
        var previous = allowances;
        allowances = new AllowanceSnapshot(remaining, DateTime.UtcNow, contentId);

        if (previous == null || previous.Remaining != remaining || previous.ContentId != contentId)
        {
            Svc.Log.Information($"[Saucy] 仙人微彩今日剩餘張數：{remaining}（來自金碟遊樂園事件）");
        }
    }

    /// <summary>伺服器每次對話送的是三格的完整現況，所以整份換掉、不做累加。</summary>
    private static void CaptureWeeklyTickets(int* data)
    {
        var slots = new string[WeeklyTicketSlots];
        var bought = 0;
        for (var i = 0; i < WeeklyTicketSlots; i++)
        {
            var value = data[WeeklyTicketFirstIndex + i];
            if (value < 0 || value > EmptyTicketValue)
            {
                // 值域外＝這筆事件不是彩券現況（回呼是所有金碟遊戲共用的），整筆丟掉比顯示垃圾好。
                Svc.Log.Warning($"[Saucy] 金碟遊樂園事件回報的仙人仙彩號碼超出值域（{value}），已忽略");
                return;
            }

            slots[i] = value == EmptyTicketValue ? "Not bought yet".Loc() : value.ToString("D4");
            if (value != EmptyTicketValue)
            {
                bought++;
            }
        }

        var numbers = string.Join("／", slots);
        var contentId = Svc.PlayerState.ContentId;
        var previous = weeklyTickets;
        var changed = previous == null || previous.Numbers != numbers || previous.ContentId != contentId;
        var now = Environment.TickCount64;
        weeklyTickets = new WeeklyTicketSnapshot(numbers, DateTime.UtcNow, contentId, bought,
            changed ? now : previous!.ChangedTick, now);

        if (changed)
        {
            Svc.Log.Information($"[Saucy] 仙人仙彩本週已買 {bought} 張：{numbers}（來自金碟遊樂園事件）");
        }
    }

    /// <param name="Inferred">true＝由 <see cref="NoteMiniCactpotTicketOpened"/> 扣出來的，不是伺服器直接給的數字。</param>
    private sealed record AllowanceSnapshot(int Remaining, DateTime UpdatedUtc, ulong ContentId, bool Inferred = false);

    /// <param name="ChangedTick">內容最後一次變動的時刻（<see cref="Environment.TickCount64"/>）；同樣內容再收一次不動它。</param>
    /// <param name="CapturedTick">最後一次收到的時刻。</param>
    private sealed record WeeklyTicketSnapshot(string Numbers, DateTime UpdatedUtc, ulong ContentId, int Bought, long ChangedTick, long CapturedTick);
}
