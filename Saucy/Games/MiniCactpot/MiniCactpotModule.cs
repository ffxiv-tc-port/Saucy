using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.LanguageHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static ECommons.GenericHelpers;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
using ConditionFlag = Dalamud.Game.ClientState.Conditions.ConditionFlag;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Saucy.MiniCactpot;

/// <summary>
/// 仙人微彩（Mini Cactpot，金碟遊樂園每日刮刮樂）自動完成。互動序列依 DailyRoutinesAutoMiniCactpot 的設計重寫：LotteryDaily 面板開啟時，依期望值自動翻 3 格（Callback(1, 格節點值)）、選線並確認（回寫選線欄位後 Callback(2, 線節點值)）、全部翻開後領獎關窗（Callback(-1) + Close），關窗後（可選）自動按下「購買下一張」確認框，把當日彩券一次完成。
/// 全程零 hook、零封包：只用 AddonLifecycle 事件 + addon callback 合成事件。
/// 階段判定不依賴 AgentLotteryDaily（API13 世代 FFXIVClientStructs 沒有這個 struct），
/// 改讀 addon 自己的 GameNumbers 盤面（0=未翻開）由已翻開格數推導：
/// 1..3 格=翻格階段、4 格=選線階段、9 格=領獎收尾（與 DR 讀 agent Status 1/2/4 等價）。
/// 模組未啟用時不註冊任何監聽——手動開面板玩不會被搶操作。
/// </summary>
public unsafe partial class MiniCactpotModule : Module
{
    private const string AddonName = "LotteryDaily"; // addon 內部名，跨語言用戶端一致（非在地化字串）

    private const string ClickThrottleKey = "Saucy.MiniCactpot.Click";

    /// <summary>面板消失超過這麼久就當成「重新進場」，下一張彩券恢復完整暖機。</summary>
    private const int VisitResetMs = 10000;

    /// <summary>選線確認送出後，隔多久沒有進展才允許重送一次。正常情況下一張彩券只會送出
    /// 一次 <c>Callback(2, lane)</c>；這個窗口只是為了在確認真的被吃掉時不要永久卡住。</summary>
    private const int LaneReconfirmMs = 5000;

    private const uint CellNodeIdFirst = 30;
    private const uint CellNodeIdLast = 38;
    private const uint LaneNodeIdFirst = 21;
    private const uint LaneNodeIdLast = 28;

    /// <summary>開窗時 LotteryDaily 帶的 AtkValue 數（離線鑑識台服 7.20 用戶端得到 45）。</summary>
    private const int AtkValueCount = 45;

    /// <summary>派彩表第一列在 AtkValues 裡的索引：第 k 列對應線和 6+k。</summary>
    private const int PayoutValueFirst = 15;

    /// <summary>等面板填好中獎列的寬限上限。</summary>
    private const int JackpotReadGraceMs = 600;

    private readonly MiniCactpotSolver solver = new();

    /// <summary>派彩表，索引＝線和。硬編那份永不變動；執行期那份每幀從面板重填。</summary>
    private readonly int[] fallbackPayouts = MiniCactpotSolver.CreateFallbackPayoutTable();

    private readonly int[] runtimePayouts = MiniCactpotSolver.CreateFallbackPayoutTable();

    /// <summary>只在 framework 執行緒讀寫（背景求解拿到的是複本）。</summary>
    private bool runtimePayoutsValid;

    /// <summary>保護底下所有背景求解狀態。</summary>
    private readonly object solveLock = new();

    private DateTime? boardReadyUtc;
    private DateTime? closeArmedUtc;
    private DateTime? addonGoneSinceUtc;
    private int pendingCell = -1;
    private int pendingCellRevealedCount = -1;
    private int confirmedLane = -1;
    private DateTime? confirmedLaneUtc;
    private bool boardLayoutSeen;

    /// <summary>「購買下一張」專用的逾時處置：接管事件，不讓基底那條「佇列中止」Warning 把每天一次的
    /// 正常收尾報成故障。其餘欄位留 null ＝沿用基底設定（逾時中止佇列）。</summary>
    private TaskManagerConfiguration? playAgainTaskConfiguration;

    /// <summary>面板關掉之後等「購買下一張」確認框的時間（也是那個任務的逾時）。</summary>
    private const int PlayAgainWaitMs = 5000;

    /// <summary>請 YesAlready 暫停 SelectYesno 的長度：它只在確認框 PostSetup 當下判斷一次，只要蓋過同一次 Setup 呼叫。</summary>
    private const int PlayAgainYieldMs = 2000;

    /// <summary>接手之後 Saucy 按下去的期限：YesAlready 已經放過這一扇，Saucy 不按就沒人按，所以比讓開的時間長。</summary>
    private const int PlayAgainPressMs = 5000;

    /// <summary>在這之前出現的第一扇購票確認框才由 Saucy 判斷要不要接手；null＝沒在等。</summary>
    private DateTime? playAgainArmedUntilUtc;

    /// <summary>Saucy 接手的那一扇確認框（PreSetup 當下的位址，只做等值比較、從不解參）；0＝沒有。</summary>
    private nint playAgainClaimedYesno;

    private DateTime playAgainClaimedUntilUtc;

    /// <summary>這一輪已交還給 YesAlready 或你自己按；任務安靜結束。</summary>
    private bool playAgainHandedBack;

    private bool playAgainUnrecognizedLogged;

    /// <summary>領獎關窗級聯走到第幾招（0 = <c>Callback(-1)</c>、1 = <c>Close(true)</c>），以及那是對哪一個實例
    /// （<see cref="AddonPressGuard"/> 走逃生口放行時才前進一招）。</summary>
    private int closeChainStage;

    private nint closeChainAddress;

    /// <summary>這一張已經處理過開獎通知（不論有沒有真的念出來），避免每幀重試。</summary>
    private bool jackpotHandled;

    /// <summary>這一張已回報過「面板派彩表與硬編表不同」，避免每幀洗 log。</summary>
    private bool payoutTableDiffLogged;

    /// <summary>這一張九格都翻開過（不是玩到一半關掉）。</summary>
    private bool boardCompleted;

    /// <summary>排了「交棒給 Questionable」的時間（呼叫不到後重排時是未來的時刻）；null＝沒排。</summary>
    private DateTime? handOffArmedUtc;

    /// <summary>這一輪交棒第一次呼叫的時間；null＝還沒呼叫過。</summary>
    private DateTime? handOffFirstTryUtc;

    /// <summary>排交棒的角色；換了角色就不呼叫。</summary>
    private ulong handOffContentId;

    /// <summary>角色 ContentId → 上一次 Questionable 收到交棒（開始或拒絕）的時間。只放記憶體：剩 0 張之後當天開不了新面板，重載也不會再觸發。</summary>
    private static readonly Dictionary<ulong, DateTime> HandOffCalledUtc = [];

    private bool solveInFlight;
    private bool hasSolvedCell;
    private ulong solvedBoardKey;
    private int solvedCell = -1;

    /// <summary>點擊間隔。做成設定，但夾在下限之上——理由見 Configuration 上的說明。</summary>
    private static int ClickThrottleMs => Math.Clamp(
        C.MiniCactpotClickIntervalMs,
        Configuration.MiniCactpotMinClickIntervalMs,
        Configuration.MiniCactpotMaxClickIntervalMs);

    /// <summary>全部翻開後先讓開獎動畫/派彩數字跑一下再關窗，玩家至少看得到中了多少。</summary>
    private static int CloseDelayMs => Math.Clamp(C.MiniCactpotCloseDelayMs, 0, Configuration.MiniCactpotMaxCloseDelayMs);

    /// <summary>請塔塔露提醒的派彩門檻（金碟幣），夾在派彩表的真實值域內。</summary>
    private static int JackpotThresholdMgp => Math.Clamp(
        C.MiniCactpotJackpotThresholdMgp,
        Configuration.MiniCactpotJackpotMinThresholdMgp,
        Configuration.MiniCactpotJackpotMaxThresholdMgp);

    /// <summary>這一幀求解要用的派彩表：讀得到面板就用伺服器的值，否則退回硬編表。</summary>
    private int[] ActivePayouts => runtimePayoutsValid ? runtimePayouts : fallbackPayouts;

    public override string Name => "Mini Cactpot";

    /// <summary>給設定面板顯示的最近動作說明。</summary>
    public string LastAction { get; private set; } = "等待開啟仙人微彩面板";

    /// <summary>這一張開獎後的實際派彩（金碟幣）。還沒開獎、或這張不是模組選的線就是 0。</summary>
    public int LastPayoutMgp { get; private set; }

    public override void Enable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonEvent);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonEvent);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, SelectYesnoHelper.AddonName, OnYesnoPreSetup);
        SubscribeUpdate(OnUpdate);
        GoldSaucerEventTracker.Acquire(InternalName);
    }

    public override void Disable()
    {
        GoldSaucerEventTracker.Release(InternalName);
        UnsubscribeUpdate();
        Svc.AddonLifecycle.UnregisterListener(OnAddonEvent);
        Svc.AddonLifecycle.UnregisterListener(OnYesnoPreSetup);
        TaskManager.Abort();
        ResetTicketState();
        ResetPlayAgainState();
        handOffArmedUtc = null;
        boardLayoutSeen = false;
        addonGoneSinceUtc = null;
        LastAction = "等待開啟仙人微彩面板";
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                ResetTicketState();
                ResetPlayAgainState();
                handOffArmedUtc = null;
                handOffFirstTryUtc = null;
                TaskManager.Abort();
                GoldSaucerEventTracker.NoteMiniCactpotTicketOpened();
                break;
            case AddonEvent.PreFinalize:
                var ticketFinished = boardCompleted;
                ResetTicketState();
                ResetPlayAgainState();
                var known = GoldSaucerEventTracker.TryGetMiniCactpotRemainingToday(out var remaining);
                ArmQuestionableHandOff(ticketFinished, known, remaining);
                if (C.MiniCactpotAutoPlayAgain)
                {
                    // 台服關面板後是發放員再講一句話、接著跳購票確認框（事件開的，沒有 agent 歸屬）。
                    // 認框在 OnYesnoPreSetup；這個任務只負責按下接手的那一扇。逾時是正常終局
                    // （當日三張打完後就不再問），所以逾時處置換成自己的一份，見 OnPlayAgainTimedOut。
                    playAgainTaskConfiguration ??= new TaskManagerConfiguration
                    {
                        TimeLimitMS = PlayAgainWaitMs,
                        ExecuteDefaultConfigurationEvents = false,
                        OnTaskTimeout = OnPlayAgainTimedOut
                    };
                    TaskManager.Abort();
                    if (!ShouldWaitForPlayAgain(known, remaining))
                    {
                        LastAction = "今日彩券已打完，不等「購買下一張」";
                        Log("今日剩餘張數確定是 0，不等「購買下一張」確認框");
                        break;
                    }

                    playAgainArmedUntilUtc = DateTime.UtcNow.AddMilliseconds(PlayAgainWaitMs);
                    TaskManager.Enqueue(TryPressPlayAgainYes, "MiniCactpot.PlayAgain", playAgainTaskConfiguration);
                }

                break;
        }
    }

    /// <summary>
    /// 確認框 Setup 之前先認：是仙人微彩的購票確認才請 YesAlready 讓開並記下這一扇；認不出就什麼都不做。
    /// 🔴 一定要在 PreSetup：YesAlready 只在同一次 Setup 呼叫的 PostSetup 判斷一次，這時暫停它就保證它不會碰這一扇，
    /// 不會同幀兩邊一起按；反過來，認不出時不暫停，它照常判斷，不會卡成沒人按。
    /// </summary>
    private void OnYesnoPreSetup(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonSetupArgs setup)
        {
            return;
        }

        var address = setup.Addon.Address;
        if (address != nint.Zero && address == playAgainClaimedYesno)
        {
            playAgainClaimedYesno = nint.Zero; // 同一位址又開了一扇＝接手的那扇已經不在了
        }

        if (playAgainArmedUntilUtc is not { } armedUntil)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now >= armedUntil || !IsEnabled || !C.MiniCactpotAutoPlayAgain)
        {
            playAgainArmedUntilUtc = null;
            return;
        }

        var prompt = SelectYesnoHelper.ReadSetupPrompt(setup);
        if (!SelectYesnoHelper.IsMiniCactpotPurchasePrompt(prompt))
        {
            if (!playAgainUnrecognizedLogged)
            {
                playAgainUnrecognizedLogged = true;
                Log($"等「購買下一張」時出現的確認框不是仙人微彩的購票確認，Saucy 不接手（YesAlready 照常判斷）：{FlattenPrompt(prompt)}");
            }

            return;
        }

        playAgainArmedUntilUtc = null;
        if (!YesAlready.TryPauseSelectYesno(PlayAgainYieldMs))
        {
            playAgainHandedBack = true;
            LastAction = "無法確認 YesAlready 不會按「購買下一張」，交給它或你自己按";
            Log("認出「購買下一張」確認框，但無法確認 YesAlready 不會按，交還給 YesAlready 或你自己按");
            return;
        }

        playAgainClaimedYesno = address;
        playAgainClaimedUntilUtc = now.AddMilliseconds(PlayAgainPressMs);
        Log("認出「購買下一張」確認框，確定 YesAlready 不會按（已暫停、沒啟用或沒載入），由 Saucy 按「是」");
    }

    private void OnUpdate(IFramework framework)
    {
        TickQuestionableHandOff();
        var addon = GetLotteryDailyAddon();
        if (addon == null)
        {
            MinigameInputPacing.Reset(ref boardReadyUtc);
            addonGoneSinceUtc ??= DateTime.UtcNow;
            if (boardLayoutSeen &&
                (DateTime.UtcNow - addonGoneSinceUtc.Value).TotalMilliseconds >= VisitResetMs)
            {
                // 離開夠久＝下次是重新進場，恢復完整暖機。
                boardLayoutSeen = false;
            }

            return;
        }

        addonGoneSinceUtc = null;

        // 開面板後先暖機一小段，不跟面板初始化搶時序。但「自動購買下一張」會在同一次進場
        // 連開好幾次面板，版面早就建好了——第二張之後只留短緩衝，省下純粹的等待。
        var warmupMs = boardLayoutSeen
            ? MinigameInputPacing.RepeatBoardWarmupMs
            : MinigameInputPacing.BoardWarmupMs;
        if (!MinigameInputPacing.TryMarkWarmup(ref boardReadyUtc, warmupMs))
        {
            return;
        }

        runtimePayoutsValid = TryReadPayoutTable(addon);

        Span<int> board = stackalloc int[MiniCactpotSolver.TotalCells];
        var revealed = 0;
        for (var i = 0; i < MiniCactpotSolver.TotalCells; i++)
        {
            var value = addon->GameNumbers[i];
            if (value is >= 1 and <= 9)
            {
                board[i] = value;
                revealed++;
            }
        }

        if (revealed != MiniCactpotSolver.TotalCells)
        {
            closeArmedUtc = null;
        }

        switch (revealed)
        {
            // 0 = 盤面資料還沒到，等。
            case >= 1 and < MiniCactpotSolver.RevealTarget:
                TickCellStage(addon, board, revealed);
                break;
            case MiniCactpotSolver.RevealTarget:
                TickLaneStage(addon, board);
                break;
            case MiniCactpotSolver.TotalCells:
                TickCloseStage(addon, board);
                break;
            // 5..8 = 選線後開獎資料翻開中，等它到齊。
        }
    }

    private void ResetPlayAgainState()
    {
        playAgainArmedUntilUtc = null;
        playAgainClaimedYesno = nint.Zero;
        playAgainHandedBack = false;
        playAgainUnrecognizedLogged = false;
    }

    private static string FlattenPrompt(string prompt)
    {
        var flat = prompt.ReplaceLineEndings(" ");
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }

    private void ResetTicketState()
    {
        MinigameInputPacing.Reset(ref boardReadyUtc);
        closeArmedUtc = null;
        pendingCell = -1;
        pendingCellRevealedCount = -1;
        confirmedLane = -1;
        confirmedLaneUtc = null;
        jackpotHandled = false;
        payoutTableDiffLogged = false;
        runtimePayoutsValid = false;
        LastPayoutMgp = 0;
        closeChainStage = 0;
        closeChainAddress = nint.Zero;
        boardCompleted = false;

        lock (solveLock)
        {
            // 進行中的求解不取消（沒有副作用）——它的結果會因為盤面 key 對不上而被忽略。
            hasSolvedCell = false;
            solvedCell = -1;
            solvedBoardKey = 0;
        }
    }

    private static AddonLotteryDaily* GetLotteryDailyAddon()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !addon->IsVisible || !IsAddonReady(addon))
        {
            return null;
        }

        return (AddonLotteryDaily*)addon;
    }
}
