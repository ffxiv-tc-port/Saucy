using System;
using System.Collections.Generic;

namespace Saucy.JumboCactpot;

/// <summary>
/// 仙人仙彩購票面板的「這一扇要不要填、填第幾張」判斷。純邏輯、不碰遊戲、不配置以外的東西；只在 framework 執行緒用。
/// 呼叫端每幀把看到的狀態（可見且就緒的面板位址、確認框是哪一種、本週已買張數、YesAlready 讓開的期間）餵進來，
/// 拿到「送第幾張」就自己去填號碼，填成功再呼叫 <see cref="MarkSubmitted"/>。
/// </summary>
/// <remarks>
/// 「第幾張」＝實際買了幾張：與發放員對話時伺服器送的本週已買張數，加上之後看到幾次「要不要再買一次？」（＝上一張買成了）；
/// 兩者取與「Saucy 自己送過幾次」的較大值，所以提示框認不出來時退回舊算法、不會變差。本週張數不知道或太舊時，改用這次進場的計數。
/// 「這扇已送過」只在看到「要不要再買一次？」、面板位址換了、或面板重建／銷毀時才解除：在確認框按「否」回到同一扇面板不會重送。
/// </remarks>
internal sealed class JumboPanelFlow
{
    public enum YesnoKind
    {
        None,
        PurchaseConfirm,
        BuyAgain,
        Other
    }

    public enum Block
    {
        None,
        AlreadySubmitted,
        ConfirmVisible,
        OtherFilled,
        YesAlreadyNotClear,
        Warmup
    }

    /// <summary>開窗暖機（毫秒）。</summary>
    public const long WarmupMs = 1400;

    /// <summary>過了暖機還這麼久沒填，就寫一行原因。</summary>
    public const long StuckReportMs = 1500;

    /// <summary>本週已買張數超過這麼久就不採信（這段期間一定又跟發放員說過話，會有新的一筆）。</summary>
    public const long WeeklyFreshMs = 15 * 60 * 1000;

    /// <summary>重建事件與第一次看到面板之間最多差這麼久，才把重建時間當成這扇的建立時間。</summary>
    private const long SetupMatchMs = 3000;

    private const long NotReadyReportMs = 2000;

    private readonly List<long> buyAgainTimes = [];
    private readonly List<long> submitTimes = [];
    private Sighting? sighting;
    private Sighting? lastSighting;
    private nint latchAddress;
    private nint lastSetupAddress;
    private long lastSetupTime;
    private bool hasSetup;
    private YesnoKind lastYesno;
    private int visitBuyAgain;
    private int visitSubmitted;
    private bool otherFillNotice;
    private bool otherFillNoticedThisVisit;
    private long notReadySince;
    private bool notReadyReported;

    /// <summary>給呼叫端寫進 log 的診斷（Information），由呼叫端每幀取走。</summary>
    public List<string> Diagnostics { get; } = [];

    /// <summary>這一扇目前被擋在哪一關（給狀態列與診斷用）。</summary>
    public Block LastBlock { get; private set; }

    /// <param name="ChangedMs">本週張數內容最後一次變動的時刻；從這之後看到的購買才往上加。</param>
    /// <param name="CapturedMs">最後一次收到的時刻；太舊就不採信。</param>
    public readonly record struct Weekly(bool Known, int Bought, long ChangedMs, long CapturedMs);

    public readonly record struct Frame(
        long Now,
        bool InSaucer,
        nint Panel,
        int PanelIndex,
        bool PanelVisibleNotReady,
        YesnoKind Yesno,
        Weekly Week,
        bool ClaimRequired,
        bool HasClaim,
        long ClaimFrom,
        long ClaimUntil);

    public void OnPanelSetup(nint address, long now)
    {
        lastSetupAddress = address;
        lastSetupTime = now;
        hasSetup = true;
        ResetWindow(address);
    }

    public void OnPanelFinalize(nint address, long now) => ResetWindow(address);

    /// <summary>回傳 -1＝這一幀不送；否則是要送的「第幾張」（0 起算）。</summary>
    public int Update(in Frame f)
    {
        if (!f.InSaucer)
        {
            visitBuyAgain = 0;
            visitSubmitted = 0;
            buyAgainTimes.Clear();
            submitTimes.Clear();
            otherFillNoticedThisVisit = false;
            latchAddress = 0;
            sighting = null;
            lastSighting = null;
            lastYesno = YesnoKind.None;
            LastBlock = Block.None;
            return -1;
        }

        TrackYesno(f);

        if (f.Panel == 0)
        {
            sighting = null;
            TrackNotReady(f);
            LastBlock = Block.None;
            return -1;
        }

        notReadySince = 0;
        notReadyReported = false;
        if (sighting is not { } s || s.Address != f.Panel)
        {
            s = new Sighting(f.Panel, f.Now, hasSetup && lastSetupAddress == f.Panel && Math.Abs(f.Now - lastSetupTime) <= SetupMatchMs
                ? lastSetupTime
                : f.Now);
            sighting = s;
            lastSighting = s;
            if (latchAddress != 0 && latchAddress != f.Panel)
            {
                latchAddress = 0;
                Diagnostics.Add($"面板換了一扇（0x{f.Panel:X}），解除「這扇已送過」");
            }

            if (f.PanelIndex > 1)
            {
                Diagnostics.Add($"這扇購票面板是同名的第 {f.PanelIndex} 個實例（0x{f.Panel:X}）");
            }
        }

        if (s.Submitted)
        {
            LastBlock = Block.AlreadySubmitted;
            return -1;
        }

        var block = latchAddress == f.Panel ? Block.AlreadySubmitted
            : f.Yesno != YesnoKind.None ? Block.ConfirmVisible
            : s.OtherFilled ? Block.OtherFilled
            : f.ClaimRequired && !(f.HasClaim && f.ClaimFrom <= s.SetupTime && s.SetupTime <= f.ClaimUntil) ? Block.YesAlreadyNotClear
            : Block.None;

        // 暖機從第一次所有關卡都放行的那一幀起算，之後被擋也不重算。
        if (block == Block.None)
        {
            s.WarmupStart ??= f.Now;
            if (f.Now - s.WarmupStart.Value < WarmupMs)
            {
                block = Block.Warmup;
            }
        }

        LastBlock = block;
        if (block != Block.None)
        {
            if (block != Block.Warmup && !s.Reported && f.Now - s.Since >= WarmupMs + StuckReportMs)
            {
                s.Reported = true;
                Diagnostics.Add($"購票面板 0x{f.Panel:X} 開了 {(f.Now - s.Since) / 1000.0:0.0} 秒還沒填：{Describe(block)}");
            }

            return -1;
        }

        return ResolveTicketIndex(f.Week, f.Now);
    }

    /// <summary>填號碼成功之後呼叫。</summary>
    public void MarkSubmitted(nint address, long now)
    {
        latchAddress = address;
        if (sighting is { } s && s.Address == address)
        {
            s.Submitted = true;
        }

        submitTimes.Add(now);
        visitSubmitted++;
    }

    /// <summary>呼叫端的防重按守衛擋下這一次（只記一行原因，下一幀再試）。</summary>
    public void NoteGuardBlocked(long now)
    {
        if (sighting is { } s && !s.Reported && now - s.Since >= WarmupMs + StuckReportMs)
        {
            s.Reported = true;
            Diagnostics.Add($"購票面板 0x{s.Address:X} 開了 {(now - s.Since) / 1000.0:0.0} 秒還沒填：防重按守衛擋下");
        }
    }

    /// <summary>這次進場第一次看到「號碼不是 Saucy 填的」時回 true 一次。</summary>
    public bool TakeOtherFillNotice()
    {
        var notice = otherFillNotice;
        otherFillNotice = false;
        return notice;
    }

    public int ResolveTicketIndex(Weekly week, long now)
    {
        if (week.Known && week.Bought >= 0 && now - week.CapturedMs <= WeeklyFreshMs)
        {
            return week.Bought + Math.Max(CountSince(buyAgainTimes, week.ChangedMs), CountSince(submitTimes, week.ChangedMs));
        }

        return Math.Max(visitBuyAgain, visitSubmitted);
    }

    private void TrackYesno(in Frame f)
    {
        if (f.Yesno == YesnoKind.BuyAgain && lastYesno != YesnoKind.BuyAgain)
        {
            buyAgainTimes.Add(f.Now);
            visitBuyAgain++;
            sighting = null;
            if (latchAddress != 0)
            {
                latchAddress = 0;
                Diagnostics.Add("看到「要不要再買一次？」：上一張已買成，解除「這扇已送過」");
            }
        }
        else if (f.Yesno == YesnoKind.PurchaseConfirm && lastYesno != YesnoKind.PurchaseConfirm &&
                 lastSighting is { Submitted: false } other)
        {
            other.OtherFilled = true;
            otherFillNotice |= !otherFillNoticedThisVisit;
            otherFillNoticedThisVisit = true;
            Diagnostics.Add($"購票面板 0x{other.Address:X} 跳出購買確認，但號碼不是 Saucy 填的（使用者自己填，或別的插件）");
        }

        lastYesno = f.Yesno;
    }

    private void TrackNotReady(in Frame f)
    {
        if (!f.PanelVisibleNotReady)
        {
            notReadySince = 0;
            notReadyReported = false;
            return;
        }

        notReadySince = notReadySince == 0 ? f.Now : notReadySince;
        if (!notReadyReported && f.Now - notReadySince >= NotReadyReportMs)
        {
            notReadyReported = true;
            Diagnostics.Add("購票面板看得到，但一直沒有就緒，找不到可以填的那一扇");
        }
    }

    // 只動事件講的那一個位址：舊實例的 PreFinalize 不能解除新實例的「已送過」。
    private void ResetWindow(nint address)
    {
        if (latchAddress == address)
        {
            latchAddress = 0;
        }

        if (sighting?.Address == address)
        {
            sighting = null;
        }
    }

    private static int CountSince(List<long> times, long since)
    {
        var n = 0;
        foreach (var t in times)
        {
            if (t >= since)
            {
                n++;
            }
        }

        return n;
    }

    private static string Describe(Block block) => block switch
    {
        Block.AlreadySubmitted => "這扇已經送過號碼（在確認框按了「否」回到面板時刻意不重送）",
        Block.ConfirmVisible => "確認框開著",
        Block.OtherFilled => "這扇的號碼已經由別人填了",
        Block.YesAlreadyNotClear => "無法確認 YesAlready 的仙人仙彩自動填號不會同時填這一扇",
        _ => block.ToString()
    };

    private sealed class Sighting(nint address, long since, long setupTime)
    {
        public nint Address { get; } = address;
        public long Since { get; } = since;
        public long SetupTime { get; } = setupTime;
        public long? WarmupStart { get; set; }
        public bool Submitted { get; set; }
        public bool OtherFilled { get; set; }
        public bool Reported { get; set; }
    }
}
