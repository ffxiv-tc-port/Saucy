using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using static ECommons.GenericHelpers;
namespace Saucy.OutOnALimb;

/// <summary>
/// 孤樹無援（Out on a Limb，金碟遊樂園陸行鳥廣場的伐木機台）自動遊玩。
///
/// 【資料來源：完全不碰封包】資料全部來自玩家本來就看得到的東西：
///   ・階段／量表／剩餘次數／剩餘時間 → <c>MiniGameBotanist</c> 的 AtkValue
///   ・指針位置                       → <c>NumberArrayData[104].IntArray[0]</c>（遊戲自己讀的同一格）
///   ・力量表的三段區間               → <c>MiniGameAimg</c> 的 <c>AtkValue[4]/[5]</c>
///   ・砍伐回饋                       → 系統訊息的四級手感（主）／樹的量表落差（補強）
/// 沒有封包 detour、沒有 hook、沒有特徵碼、沒有寫入遊戲記憶體，出手只有「在對的時機按下
/// 畫面上本來就有的按鈕」。
///
/// 【2026-08-06 重做】實機 log 顯示舊版每一刀都誤判成「換了新樹」而把解題盤面清空，
/// 目標從頭到尾固定在 20；真因是「砍伐階段只有 3 與 4」這個假設是錯的（見
/// <see cref="TrackTreeBoundary"/>）。同時把主回饋從實測不會動的量表改回四級手感。
/// 判定的**形狀**參考了 DailyRoutines 的 <c>AutoOutOnALimb</c> 對外可見的行為
/// （逐位置嘗試表＋四級結果收斂），實作是我們自己寫的——DR 未公開原始碼，只採用機制知識。
///
/// 【動作範圍】預設只在機台畫面已經開著時才動作。「連續遊玩」是**明確的選擇加上明確的一次按鈕**：
/// 設定要打開、面板上要按下開始、而且跑滿設定的局數就自己停。沒按開始就絕不會自己去碰機台。
/// </summary>
public unsafe class OutOnALimbModule : Module
{
    /// <summary>Addon 表列號 → 手感等級。備援路徑用。
    /// ⚠️ 9706–9709 與 9710–9713 兩組文字**逐字相同**（都是「沒什麼手感……／感覺接觸到了什麼東西。／
    /// 感覺離目標相當接近了。／感覺正中目標！」），所以文字只能判「多準」，判不出是哪一台機器。</summary>
    private static readonly (uint RowId, HitPower Power)[] FeedbackRows =
    [
        (9710, HitPower.Nothing),
        (9711, HitPower.Weak),
        (9712, HitPower.Strong),
        (9713, HitPower.Maximum)
    ];

    private const string SwingThrottleKey = "Saucy.OutOnALimb.Swing";
    private const string AimgThrottleKey = "Saucy.OutOnALimb.PowerMeter";
    private const string YesnoThrottleKey = "Saucy.OutOnALimb.Yesno";
    private const string ChatDiagThrottleKey = "Saucy.OutOnALimb.ChatDiag";
    private const string MachineDiagThrottleKey = "Saucy.OutOnALimb.MachineDiag";
    private const string ZoneDiagThrottleKey = "Saucy.OutOnALimb.ZoneDiag";
    private const string CursorDiagThrottleKey = "Saucy.OutOnALimb.CursorDiag";
    private const string BoardDumpThrottleKey = "Saucy.OutOnALimb.BoardDump";
    private const string ReplayYesThrottleKey = "Saucy.OutOnALimb.ReplayYes";
    private const string ReplayMenuThrottleKey = "Saucy.OutOnALimb.ReplayMenu";
    private const string ReplayInteractThrottleKey = "Saucy.OutOnALimb.ReplayInteract";
    private const string ReplayPromptDiagThrottleKey = "Saucy.OutOnALimb.ReplayPromptDiag";
    private const string ReplayWaitDiagThrottleKey = "Saucy.OutOnALimb.ReplayWaitDiag";
    private const string SwingsDiagThrottleKey = "Saucy.OutOnALimb.SwingsDiag";
    private const string StateDiagThrottleKey = "Saucy.OutOnALimb.StateDiag";

    /// <summary>揮斧的最低間隔。
    /// 這只是安全下限，不是節奏來源——真正「一輪只砍一刀」是靠階段機（<c>AtkValue[0]</c> 3↔4）
    /// 加上 <see cref="nextTarget"/> 只在進入階段 3 的那一幀才重算：同一輪裡不可能砍第二刀。
    /// 舊值 2000ms 等於替每棵樹硬加 20 秒，是「效率太差」的一部分。</summary>
    private const int SwingThrottleMs = 600;
    private const int AimgThrottleMs = 1000;
    private const int YesnoThrottleMs = 2000;
    private const int ChatDiagThrottleMs = 3000;
    private const int MachineDiagThrottleMs = 5000;
    private const int ZoneDiagThrottleMs = 5000;
    private const int CursorDiagThrottleMs = 5000;
    private const int BoardDumpThrottleMs = 1000;
    private const int SwingsDiagThrottleMs = 10000;
    private const int StateDiagThrottleMs = 10000;
    private const int ReplayYesThrottleMs = 1500;
    private const int ReplayMenuThrottleMs = 1500;
    private const int ReplayPromptDiagThrottleMs = 3000;
    private const int ReplayWaitDiagThrottleMs = 10000;

    /// <summary>
    /// 力量表「等到停得夠準」的放寬階梯（毫秒，從指針開始動算起）。
    ///
    /// 🔴 為什麼要有階梯：2026-08-06 實機 log 直接證明**單幀位移可以比目標格還寬**
    /// （22:11:36 那一刀，上一幀 ≤249、這一幀 1303，一幀跨了 1054 刻度以上，
    /// 而泰坦格只有 501 寬）。這種情況下「某一幀剛好落在格子裡」是機率事件，不是必然，
    /// 所以要允許放掉幾趟等下一趟。
    ///
    /// 🔴 但**絕不能等到卡死**：每一局都花掉一枚金碟幣，不停表等於錢丟掉。
    /// 所以階梯的終點是「不管落在哪一格都按下去」——失敗形式是**打到隔壁格**，不是不揮斧。
    /// <list type="number">
    /// <item>0–<see cref="PreciseWindowMs"/>：只有落在目標格、而且離兩邊界都有餘裕才按。</item>
    /// <item>–<see cref="AnyInZoneMs"/>：落在目標格裡就按（不再要求餘裕）。</item>
    /// <item>–<see cref="WidenZoneMs"/>：放寬到「目標格或次寬的那一格」。</item>
    /// <item>之後：落在哪一格都按。</item>
    /// </list>
    /// ⚠️ 這幾個毫秒數是**取捨值不是實測常數**：訂太短會退化成舊行為（隨便按），
    /// 訂太長則每局多站幾秒。實機 log 顯示一趟掃描約 200–300ms，所以 1500ms 大約是 5–7 趟。
    /// </summary>
    private const int PreciseWindowMs = 1500;

    private const int AnyInZoneMs = 3000;

    private const int WidenZoneMs = 4500;

    /// <summary>揮斧之後最多等多久量表變化。超過就放掉，讓聊天備援有機會補上。</summary>
    private static readonly TimeSpan GaugeWaitTimeout = TimeSpan.FromSeconds(6);

    private readonly LimbSolver solver = new();

    /// <summary>手感訊息文字 → 等級。第一次要用時才從 Excel 建，
    /// 避免外掛載入當下（ModuleManager 建構所有模組）就去碰資料表。</summary>
    private Dictionary<string, HitPower>? feedbackText;

    /// <summary>按下揮斧當下指針所在的刻度（0–100 顯示刻度）。回饋是稍後才到的，
    /// 那時指針早就轉走了，所以一定要記住「按的時候在哪」而不是回頭再讀一次。</summary>
    private int? pendingCursor;

    /// <summary>按下揮斧當下樹的量表值。砍完之後的落差就是這一刀的成績。</summary>
    private int? gaugeAtSwing;

    private DateTime? pendingSinceUtc;

    /// <summary>下一刀要瞄的位置（0–100 顯示刻度）。</summary>
    private int? nextTarget;

    /// <summary>上一幀的指針原始刻度（0–10000）。用來判斷「兩幀之間有沒有跨過目標」——
    /// 舊版只比「這一幀夠不夠近」，畫面更新率不夠時指針會整個掃過去而永遠按不到。</summary>
    private int? lastBotanistCursor;

    private int? lastAimgCursor;

    /// <summary>這一次力量表嘗試裡，指針**第一次真的動起來**的時刻（<see cref="Environment.TickCount64"/>）。
    /// null＝指針還停在待命位置（實機是 0），這一輪的計時還沒開始。
    ///
    /// 🔴 為什麼需要：待命值 0 落在最窄那一格 <c>(-1, 500]</c> 的**裡面**。少了這個閘門，
    /// 底下「等久了就放寬」的階梯會在指針根本還沒開始掃的時候就判定「在格子裡」而按下去。
    /// 舊版沒有這個問題純粹是因為它的邊界餘裕剛好把 0 排除掉，那是巧合不是防護。</summary>
    private long? aimgLiveSinceMs;

    /// <summary>這一次嘗試裡看過的**最大單幀位移**（0–10000 刻度）。
    /// 這是「為什麼停不準」的關鍵數字：它比目標格寬度大的時候，
    /// 一趟掃過去根本不保證有任何一幀落在格子裡。</summary>
    private int aimgMaxStep;

    /// <summary>這一次嘗試裡指針動起來之後看過幾幀。診斷用。</summary>
    private int aimgLiveFrames;

    /// <summary>這一次嘗試已經退到第幾階（0＝還在要求精準）。只用來讓 log 在階梯前進時說一次話。</summary>
    private int aimgRelaxStage;

    /// <summary>上一幀讀到的階段值。
    /// 🔴 **null 就是「不知道」，不可以代換成任何具體值。** 舊版寫 <c>ReadState(addon) ?? 0</c>，
    /// 而 0 不在當時假設的砍伐狀態集合裡 → 讀不到的那一幀直接被當成「換了一棵樹」。</summary>
    private uint? lastState;

    private int? lastGaugeMax;
    private int? lastGauge;

    /// <summary>上一幀的剩餘揮擊次數。
    /// 🔴 **每一幀都要更新**：樹邊界只在計數器回頭的那一瞬間看得見，
    /// 只在「輪到玩家」那一幀取樣會整個錯過——實機案例是一刀就把樹砍倒時，
    /// 前後兩次輪到玩家讀到的都是 10，中間那個 9 落在別的階段。</summary>
    private uint? lastSwingsLeft;

    /// <summary>本局看過的最大剩餘揮擊次數，也就是「計數器全滿」等於多少。
    /// 自我校準而不是寫死 10——改版把每棵樹的刀數改掉時不會靜默失準。</summary>
    private uint maxSwingsSeen;

    /// <summary>這一棵樹我們已經揮了幾刀。用來分辨「計數器全滿」是同一棵樹的第一刀，
    /// 還是我們漏掉了一次換樹。</summary>
    private int swingsRecordedThisTree;

    /// <summary>這一棵樹裡量表有沒有動過。只為了在 log 裡誠實回報「量表這條路徑到底有沒有資料」。</summary>
    private bool gaugeMovedThisTree;

    private bool powerMeterClicked;
    private bool botanistWasOpen;

    /// <summary>量表欄位到底是不是「每一刀才動一次」的東西。
    /// 🔴 這是**推論**（離線反組譯看到 <c>AtkValue[12]</c> ×100 之後餵給量表元件的目前值），
    /// 不是實機驗證過的事實。萬一它其實是每幀都在動的東西（例如計時器），
    /// 拿它的落差去餵解題器只會學到雜訊。所以這裡在「沒有等待中的刀」時盯著它：
    /// 一棵樹之內閒置時就跳動好幾次，就永久關掉這條路徑並改用聊天備援。</summary>
    private bool gaugeLooksPerSwing = true;

    private int? idleGauge;
    private int idleGaugeChanges;

    /// <summary>閒置時允許的量表跳動次數。換樹／結算尾聲本來就會動個一兩次，
    /// 但每幀都在動的東西幾秒內就會超過這個數。</summary>
    private const int IdleGaugeChangeLimit = 6;

    public override string Name => "Out on a Limb";

    /// <summary>給設定面板顯示的最近動作。</summary>
    public string LastAction { get; private set; } = "等待孤樹無援機台畫面";

    /// <summary>「連續遊玩」是不是正在跑。這是執行期狀態，不寫進設定檔——
    /// 重開遊戲、重載外掛一律回到停止。</summary>
    public bool AutoReplayRunning { get; private set; }

    /// <summary>這一輪連續遊玩已經打完幾局。</summary>
    public int AutoReplayGamesDone { get; private set; }

    /// <summary>解題器目前學到什麼（給面板顯示）。
    /// ⚠️ 「還不知道」本身要看得見，所以沒有資料時不會顯示成 0，而是講清楚是哪一種沒有。</summary>
    public string SolverSummary
    {
        get
        {
            var best = solver.Best;
            if (best == null)
            {
                return "這棵樹還沒有任何回饋（尚未揮出第一刀，或回饋讀不到）";
            }

            var damage = best.Damage is > 0 ? $"、量表落差 {best.Damage}" : string.Empty;
            return $"目前最佳：刻度 {best.Position}（{HitPowerText.Of(best.Power)}{damage}）" +
                   $"，這棵樹已試 {solver.ObservedCount} 點、其中 {solver.ContactCount} 點有手感";
        }
    }

    /// <summary>回饋來源的健康狀況。
    /// 📌 「量表沒資料」是常態（台服 7.20 實測），但那件事必須在列上看得見而不是藏在 tooltip，
    /// 否則使用者會以為解題器有兩條資料在跑。</summary>
    public string FeedbackSummary =>
        $"回饋來源：系統訊息手感 {(feedbackText == null ? "尚未建表" : $"{feedbackText.Count}/{FeedbackRows.Length} 句")}" +
        $"；樹的量表 {(gaugeLooksPerSwing ? gaugeMovedThisTree ? "有在動" : "這棵樹還沒動過" : "已判定不是每刀變化，停用")}";

    private static LimbSettings Cfg => C.OutOnALimb;

    public override void Enable()
    {
        Svc.Framework.Update += OnUpdate;
        Svc.Chat.ChatMessageHandled += OnChatMessage;
        Svc.Chat.ChatMessageUnhandled += OnChatMessage;
        gaugeLooksPerSwing = true;
        ResetRoundState();
        StopAutoReplay("模組啟用");
        LastAction = "等待孤樹無援機台畫面";
    }

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.ChatMessageHandled -= OnChatMessage;
        Svc.Chat.ChatMessageUnhandled -= OnChatMessage;
        ResetRoundState();
        StopAutoReplay("模組停用");
        LastAction = "未啟用";
    }

    /// <summary>面板上的「開始連續遊玩」。只有玩家自己按這個才會開始，
    /// 而且局數上限一到就自己停——不做任何事件驅動的自動接手。</summary>
    public void StartAutoReplay()
    {
        AutoReplayGamesDone = 0;
        AutoReplayRunning = true;
        LastAction = $"連續遊玩已開始（上限 {Math.Max(1, Cfg.AutoReplayMaxGames)} 局）";
        Svc.Log.Information($"[OutOnALimb] auto-replay started, cap {Cfg.AutoReplayMaxGames} games");
    }

    /// <summary>停止連續遊玩。面板上的停止鈕、模組停用、以及每一個結束條件都走這裡。</summary>
    public void StopAutoReplay(string reason)
    {
        if (AutoReplayRunning)
        {
            Svc.Log.Information($"[OutOnALimb] auto-replay stopped: {reason}");
            LastAction = $"連續遊玩已停止（{reason}）";
        }

        AutoReplayRunning = false;
    }

    private void ResetRoundState()
    {
        pendingCursor = null;
        gaugeAtSwing = null;
        pendingSinceUtc = null;
        nextTarget = null;
        lastBotanistCursor = null;
        lastAimgCursor = null;
        lastState = null;
        lastGaugeMax = null;
        lastGauge = null;
        lastSwingsLeft = null;
        maxSwingsSeen = 0;
        swingsRecordedThisTree = 0;
        gaugeMovedThisTree = false;
        idleGauge = null;
        idleGaugeChanges = 0;
        powerMeterClicked = false;
        ResetAimgAttempt();

        // 離開機台畫面就把解題盤面清空。
        solver.Reset(Cfg.Step);
    }

    /// <summary>換了一棵新的樹：盤面清空、丟掉還在等回饋的那一刀（它屬於上一棵樹）。
    /// <paramref name="reason"/> 會寫進 log —— 2026-08-06 那次就是靠「為什麼認定換樹」這個欄位
    /// 才發現每一刀都在誤判，所以它必須一直留著。</summary>
    private void StartNewTree(string reason, uint? swingsLeft, int? gauge, int? gaugeMax)
    {
        solver.Reset(Cfg.Step);
        pendingCursor = null;
        gaugeAtSwing = null;
        pendingSinceUtc = null;
        nextTarget = null;
        swingsRecordedThisTree = 0;
        gaugeMovedThisTree = false;
        idleGauge = gauge;
        idleGaugeChanges = 0;

        Svc.Log.Information($"[OutOnALimb] new tree ({reason}), solver reset " +
                            $"[swings={swingsLeft?.ToString() ?? "?"}/{maxSwingsSeen}, " +
                            $"gauge={gauge?.ToString() ?? "?"}/{gaugeMax?.ToString() ?? "?"}, " +
                            $"step={Cfg.Step}]");
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (!C.IsModuleEnabled(ModuleNames.OutOnALimb) || !Player.Available || !IsScreenReady())
            {
                return;
            }

            TrackGameBoundary();

            if (!LimbBoard.IsPlaying)
            {
                if (lastState != null || pendingCursor != null || powerMeterClicked)
                {
                    ResetRoundState();
                    LastAction = AutoReplayRunning
                        ? $"連續遊玩：等待下一局（已完成 {AutoReplayGamesDone} 局）"
                        : "等待孤樹無援機台畫面";
                }

                RunAutoReplayPhase(allowInteract: true);
                return;
            }

            DumpBoardDiagnostics();
            RunPowerMeterPhase();
            RunChoppingPhase();

            // 🔴🔴 2026-08-06 使用者實測回報的真因就在這裡。
            //
            // 舊版把連續遊玩整段掛在 `!IsPlaying` 底下，而 `IsPlaying = 砍伐畫面開著 || 力量表畫面開著`。
            // 實機 log（22:14:33 砍伐畫面關閉 → 22:14:34~22:14:43 力量表畫面持續回報
            // `AtkValue[0]=1`、指針 0）證明**一局結束後力量表畫面會留在原地待命**，
            // 機台的「要挑戰一下嗎」確認框就開在它上面。於是 `IsPlaying` 恆為 true，
            // `RunAutoReplayPhase()` **在那個時機根本不會被呼叫** —— 不是確認框認不出來，
            // 是整段程式碼從來沒有機會執行（整份 log 六次「開始連續遊玩」，
            // 一次都沒有出現接受確認框的那行 Information）。
            //
            // ⚠️ 這裡刻意只在**砍伐畫面關著**時才跑，而且 `allowInteract: false`：
            //   ・砍伐畫面開著時的確認框是「挑戰翻倍」，那由 HandleDoubleDownPrompt 依剩餘時間決定，
            //     不能讓連續遊玩搶答（它只會一律按「是」）。用畫面狀態把兩者結構性地分開，
            //     比只靠文字判斷可靠。
            //   ・機台畫面還開著就去互動＝在遊戲進行中重新戳機台，所以互動路徑整條關掉，
            //     這個時機只允許「回答已經跳出來的確認框」。
            if (!LimbBoard.IsBotanistOpen)
            {
                RunAutoReplayPhase(allowInteract: false);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[OutOnALimb] update failed");
        }
    }

    /// <summary>砍伐畫面從開著變成關掉＝一局結束。連續遊玩用它數局數。
    /// ⚠️ 樹與樹之間畫面若也會關掉，這裡會多數幾局——那是**偏保守**的方向（提早停），刻意如此。</summary>
    private void TrackGameBoundary()
    {
        var open = LimbBoard.IsBotanistOpen;
        if (botanistWasOpen && !open)
        {
            AutoReplayGamesDone++;
            Svc.Log.Information($"[OutOnALimb] chopping screen closed; games this run = {AutoReplayGamesDone}");
        }

        botanistWasOpen = open;
    }

    /// <summary>把面板的 AtkValue[0..15] 印出來一輪。純診斷、不參與判斷，預設關閉。</summary>
    private void DumpBoardDiagnostics()
    {
        if (!Cfg.LogBoardDiagnostics || !EzThrottler.Throttle(BoardDumpThrottleKey, BoardDumpThrottleMs))
        {
            return;
        }

        var cursor = LimbBoard.ReadCursor();
        var botanist = LimbBoard.TryGetAddon(LimbBoard.BotanistAddon);
        if (botanist != null)
        {
            Svc.Log.Information($"[OutOnALimb][diag] cursor={cursor?.ToString() ?? "?"} " +
                                $"botanist {LimbBoard.DumpAtkValues(botanist)}");
        }

        var aimg = LimbBoard.TryGetAddon(LimbBoard.AimgAddon);
        if (aimg != null)
        {
            Svc.Log.Information($"[OutOnALimb][diag] cursor={cursor?.ToString() ?? "?"} " +
                                $"aimg {LimbBoard.DumpAtkValues(aimg)}");
        }
    }

    /// <summary>第一階段：力量表。指針掃進所選難度的那一格時按下停止鈕，一輪只按一次。
    ///
    /// 🔴 力量表 addon 與礦脈探索共用，所以動它之前要先確認面前那台真的是孤樹無援；
    /// 認不出來就完全不碰（玩家自己停表，砍伐階段照樣會接手）。
    ///
    /// 【2026-08-06 修正：點不到泰坦】使用者實測「Saucy 點不到泰坦」。實機 log 直接指出真因：
    /// <code>
    /// 21:15:35 power meter stopped on Titan at  793 (zone slot 2 = (-1, 500], ..., crossing)
    /// 22:11:36 power meter stopped on Titan at 1303 (zone slot 2 = (-1, 500], ..., crossing)
    /// </code>
    /// 793 與 1303 **都不在** log 自己印出來的目標區間 <c>(-1, 500]</c> 裡。
    /// 舊版的出手條件是「落在格子裡（含餘裕）**或** 兩幀之間跨過格子中心」，而後面那個條件
    /// **完全沒有限制當幀已經衝過去多遠**——指針一幀能跨 1000 刻度以上，所以「跨過中心」
    /// 成立的那一幀往往人已經在隔壁格了。這不是延遲，是判斷式本身少了一個條件。
    ///
    /// 📌 佐證「按下去的就是我們讀到的那個刻度、沒有額外延遲」：同一份 log 裡，
    /// 唯一一次落在格子內的停表（22:13:27 at 401）之後，砍伐畫面的 <c>AtkValue[2]</c> 是 **375**；
    /// 而 1303／3373／7581 那三局都是 **750**。落在格子內外會產生不同的後續狀態，
    /// 代表遊戲採用的就是我們當幀讀到的位置。⇒ **不需要、也不應該加「提前量」**。
    ///
    /// 現在的出手條件改成連言：**當幀的指針必須真的落在目標格裡**（用遊戲自己的
    /// <c>pos &gt; low &amp;&amp; pos &lt;= high</c> 判定），再加上一個會隨時間放寬的精準度要求。
    /// </summary>
    private void RunPowerMeterPhase()
    {
        var addon = LimbBoard.TryGetAddon(LimbBoard.AimgAddon);
        if (addon == null)
        {
            powerMeterClicked = false;
            lastAimgCursor = null;
            ResetAimgAttempt();
            return;
        }

        if (powerMeterClicked || !Cfg.AutoPowerMeter)
        {
            return;
        }

        if (LimbBoard.IsMinerOpen || !LimbMachine.IsNearLimbMachine())
        {
            if (EzThrottler.Throttle(MachineDiagThrottleKey, MachineDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] power meter left alone: no Out on a Limb machine identified nearby " +
                                    "(the power meter screen is shared with the mining minigame)");
                LastAction = "力量表由你自己停（附近沒認出孤樹無援機台）";
            }

            return;
        }

        var cursor = LimbBoard.ReadCursor();
        if (cursor == null)
        {
            if (EzThrottler.Throttle(CursorDiagThrottleKey, CursorDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] cursor position unavailable " +
                                    "(GoldSaucerArcadeMachine number array not readable); leaving the power meter alone");
                LastAction = "力量表由你自己停（讀不到指針位置）";
            }

            return;
        }

        if (!TryGetZonesBySize(addon, out var zones, out var source))
        {
            if (EzThrottler.Throttle(ZoneDiagThrottleKey, ZoneDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] power meter zones unreadable " +
                                    "(neither AtkValue[4]/[5] nor the block nodes gave a usable layout); " +
                                    "leaving the power meter alone");
                LastAction = "力量表由你自己停（讀不到區塊配置）";
            }

            lastAimgCursor = cursor;
            return;
        }

        var wanted = Math.Clamp((int)Cfg.Difficulty, 0, zones.Length - 1);
        var zone = zones[wanted];

        // 追蹤指針速度。⚠️ 折返／重設造成的大跳躍不算位移（沿用 HasCrossed 的同一個門檻）。
        if (lastAimgCursor != null)
        {
            var delta = Math.Abs(cursor.Value - lastAimgCursor.Value);
            if (delta > 0 && delta <= LimbBoard.CursorScale / 4)
            {
                aimgMaxStep = Math.Max(aimgMaxStep, delta);
                aimgLiveSinceMs ??= Environment.TickCount64;
            }
        }

        lastAimgCursor = cursor.Value;

        // 🔴 指針還停在待命位置就完全不動作。待命值 0 落在最窄那一格裡面，
        // 少了這一條，「等久了就放寬」會在遊戲根本還沒開始的時候按下去。
        if (aimgLiveSinceMs == null)
        {
            return;
        }

        aimgLiveFrames++;
        var waited = Environment.TickCount64 - aimgLiveSinceMs.Value;

        // 放寬階梯。⚠️ 每前進一階都寫一行 Information，使用者才看得出「它在等，不是壞了」。
        var stage = waited >= WidenZoneMs ? 3 : waited >= AnyInZoneMs ? 2 : waited >= PreciseWindowMs ? 1 : 0;
        if (stage > aimgRelaxStage)
        {
            aimgRelaxStage = stage;
            Svc.Log.Information($"[OutOnALimb] power meter still hunting {Cfg.Difficulty} after {waited}ms " +
                                $"({aimgLiveFrames} frames, biggest one-frame step {aimgMaxStep} vs zone width " +
                                $"{zone.Width}); relaxing to stage {stage} " +
                                $"({DescribeRelaxStage(stage)})");
            LastAction = $"力量表：等不到準點，已放寬到第 {stage} 階（{DescribeRelaxStage(stage)}）";
        }

        // 🔴 出手條件的第一條、也是修掉「點不到泰坦」的那一條：
        //    **當幀的指針必須真的落在可接受的格子裡**，用的就是遊戲自己的判定式。
        //    舊版的「跨過中心」沒有這層限制，所以會在已經衝到 793／1303 的那一幀按下去。
        // 先看目標格，再往寬的方向放。⚠️ 順序有意義：命中目標格永遠優先。
        var acceptTo = stage >= 3 ? zones.Length - 1 : stage >= 2 ? Math.Min(wanted + 1, zones.Length - 1) : wanted;
        var landed = -1;
        for (var i = wanted; i <= acceptTo && landed < 0; i++)
        {
            if (zones[i].Contains(cursor.Value))
            {
                landed = i;
            }
        }

        // 最後一階是「落在哪一格都收」，所以也要收**比目標更窄**的那些格
        // （想要魔界花卻掃到泰坦格時就是這一段在收）。少了它，最後一階名不副實。
        if (landed < 0 && stage >= 3)
        {
            for (var i = 0; i < zones.Length && landed < 0; i++)
            {
                if (zones[i].Contains(cursor.Value))
                {
                    landed = i;
                }
            }
        }

        if (landed < 0)
        {
            return;
        }

        var hit = zones[landed];

        // 邊界留一點餘裕，免得剛好卡在兩格交界被判到隔壁那格。
        // 📌 這現在只是**偏好**：第 0 階要求它，之後就放掉——寧可停得靠邊，也不要一直不停表。
        if (stage == 0)
        {
            var margin = Math.Clamp(hit.Width / 6, 1, 400);
            if (cursor.Value <= hit.LowExclusive + margin || cursor.Value > hit.HighInclusive - margin)
            {
                return;
            }
        }

        if (TryClickButton(addon, LimbBoard.AimgStopButtonId(addon), AimgThrottleKey, AimgThrottleMs))
        {
            powerMeterClicked = true;
            var landedName = (LimbDifficulty)Math.Clamp(landed, 0, 2);
            var onTarget = landed == wanted;
            LastAction = onTarget
                ? $"力量表已停在 {Cfg.Difficulty}（刻度 {cursor.Value}）"
                : $"力量表停在 {landedName}（想要 {Cfg.Difficulty}，刻度 {cursor.Value}）";

            // 📌 這一行是使用者驗收「有沒有修好」的唯一依據，所以它必須說出
            //    **實際落在哪一格**，而不是只印我們想瞄哪一格。
            //    舊版印「stopped on Titan at 1303」是在講一件不成立的事。
            Svc.Log.Information($"[OutOnALimb] power meter stopped at {cursor.Value} -> landed in {landedName} " +
                                $"(zone slot {hit.Slot} = ({hit.LowExclusive}, {hit.HighInclusive}], " +
                                $"width {hit.Width}); wanted {Cfg.Difficulty}; " +
                                $"{(onTarget ? "ON TARGET" : "MISSED, took a wider zone")}; " +
                                $"source {source}, waited {waited}ms over {aimgLiveFrames} frames, " +
                                $"biggest one-frame step {aimgMaxStep}, relax stage {stage}");
        }
    }

    private static string DescribeRelaxStage(int stage) => stage switch
    {
        0 => "只收離邊界有餘裕的",
        1 => "落在目標格裡就收",
        2 => "目標格或次寬的那一格",
        var _ => "落在哪一格都收"
    };

    private void ResetAimgAttempt()
    {
        aimgLiveSinceMs = null;
        aimgMaxStep = 0;
        aimgLiveFrames = 0;
        aimgRelaxStage = 0;
    }

    /// <summary>取得力量表的三格，**依寬度由小到大**排序。
    /// 所以不管伺服器把區間切成什麼樣子、也不管畫面上的區塊順序怎麼洗牌，
    /// 索引 0（泰坦）永遠指最窄（最難停中）的那一格、索引 2（仙人掌怪）永遠指最寬的。
    ///
    /// 📌 2026-08-06 這個排序被實機資料**正面驗證**過一次：
    /// <c>AtkValue[4]=8500 / [5]=9500</c> 有兩種讀法（窄格在刻度低端 vs 高端），
    /// 而 log 顯示停在 401 的那一局後續狀態（<c>botanist AtkValue[2]=375</c>）
    /// 與停在 1303／3373／7581 的三局（都是 750）不同。
    /// 只有「窄格在低端」這個讀法能讓 401 和 1303 落在不同格；高端那個讀法會把四個都算成同一格。
    /// ⇒ 現行讀法成立，另一個讀法被推翻。</summary>
    private static bool TryGetZonesBySize(AtkUnitBase* addon, out LimbZone[] zones, out string source)
    {
        source = "none";
        if (LimbBoard.TryGetPowerZones(addon, out zones))
        {
            source = "AtkValue[4]/[5]";
        }
        else if (LimbBoard.TryGetPowerZonesFromNodes(addon, out zones))
        {
            source = "block nodes";
        }
        else
        {
            return false;
        }

        return zones.Length > 0 && zones[0].Width > 0;
    }

    /// <summary>兩幀之間指針有沒有跨過目標。低更新率下指針一幀可以跳很遠，
    /// 只比「這一幀夠不夠近」會整段掃過去都按不到。</summary>
    private static bool HasCrossed(int? previous, int current, int target)
    {
        if (previous == null)
        {
            return false;
        }

        var low = Math.Min(previous.Value, current);
        var high = Math.Max(previous.Value, current);

        // 指針折返或重設時的跳躍不算「掃過」，否則會在錯的位置出手。
        if (high - low > LimbBoard.CursorScale / 4)
        {
            return false;
        }

        return target >= low && target <= high;
    }

    /// <summary>第二階段：砍伐。輪到玩家時算出目標刻度，等指針掃過去再按。</summary>
    private void RunChoppingPhase()
    {
        var addon = LimbBoard.TryGetAddon(LimbBoard.BotanistAddon);
        if (addon == null)
        {
            lastBotanistCursor = null;
            return;
        }

        HandleDoubleDownPrompt(addon);

        // 🔴 順序有意義：換樹判定必須跑在回饋收集之前。
        // 換樹時還在等回饋的那一刀屬於**上一棵樹**，記到新盤面上就是把雜訊當成學習。
        TrackTreeBoundary(addon);
        CollectGaugeFeedback(addon);

        var state = LimbBoard.ReadState(addon);
        if (state == null)
        {
            // 讀不到階段就什麼都不做，也**不要更新 lastState** ——
            // 「不知道」不是一個階段，把它當成階段會製造假的階段轉換。
            if (EzThrottler.Throttle(StateDiagThrottleKey, StateDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] board state (AtkValue[0]) unreadable this frame; standing by");
            }

            return;
        }

        if (state.Value == LimbBoard.StatePlayerTurn)
        {
            if (lastState != LimbBoard.StatePlayerTurn)
            {
                HandleTurnStart(addon);
            }

            TrySwingAtTarget(addon);
        }

        lastState = state;
    }

    /// <summary>
    /// 每一幀判斷「是不是換了一棵新的樹」。
    ///
    /// 🔴 **這裡刻意完全不看階段值。** 舊版用「階段不在 {3,4} 裡」當判據，但實機面板傾印顯示
    /// 一刀的循環是 <c>3 →(4)→ 5 → 7 → 3</c>——5 與 7 每一刀都會出現，於是**每一刀都被判成換樹**，
    /// 解題器永遠回到初始狀態、目標從頭到尾固定在 20。那就是「比 DR 差太多」的主因。
    ///
    /// 現在只採信兩個單調性訊號：剩餘揮擊次數**回升**、以及量表上限改變。
    /// 兩者都不需要知道任何階段值的語意，改版動了階段機也不會壞。
    /// </summary>
    private void TrackTreeBoundary(AtkUnitBase* addon)
    {
        var swingsLeft = LimbBoard.ReadSwingsLeft(addon);
        var gaugeMax = LimbBoard.ReadGaugeMax(addon);
        var gauge = LimbBoard.ReadGauge(addon);

        if (swingsLeft != null && swingsLeft.Value > maxSwingsSeen)
        {
            maxSwingsSeen = swingsLeft.Value;
        }

        string? reason = null;
        if (swingsLeft != null && lastSwingsLeft != null && swingsLeft.Value > lastSwingsLeft.Value)
        {
            reason = $"揮擊計數器回升 {lastSwingsLeft.Value}→{swingsLeft.Value}";
        }
        else if (gaugeMax != null && lastGaugeMax != null && gaugeMax.Value != lastGaugeMax.Value)
        {
            reason = $"量表上限改變 {lastGaugeMax.Value}→{gaugeMax.Value}";
        }

        if (reason != null)
        {
            StartNewTree(reason, swingsLeft, gauge, gaugeMax);
        }

        // 兩個訊號都讀不到就等於沒有樹邊界判據——這件事使用者必須看得到，不能靜默降級。
        if (swingsLeft == null && gaugeMax == null &&
            EzThrottler.Throttle(SwingsDiagThrottleKey, SwingsDiagThrottleMs))
        {
            Svc.Log.Information("[OutOnALimb] neither the swing counter (AtkValue[11]) nor the gauge max " +
                                "(AtkValue[13]) is readable; tree boundaries cannot be detected, " +
                                "the solver will keep learning across trees");
            LastAction = "讀不到揮擊計數器，無法判斷換樹";
        }

        lastSwingsLeft = swingsLeft ?? lastSwingsLeft;
        lastGaugeMax = gaugeMax ?? lastGaugeMax;
    }

    /// <summary>輪到玩家的那一幀：補一個只有在這個時機才成立的換樹判據，再算出這一刀要瞄哪裡。</summary>
    private void HandleTurnStart(AtkUnitBase* addon)
    {
        var gaugeMax = LimbBoard.ReadGaugeMax(addon);
        var gauge = LimbBoard.ReadGauge(addon);
        var swingsLeft = LimbBoard.ReadSwingsLeft(addon);

        // 🔑 「計數器全滿，但這棵樹我們已經砍過了」＝中間換過樹而我們沒看到計數器回頭。
        // ⚠️ 這條**只能在輪到玩家的那一幀判**：計數器是在結果階段才遞減的，
        //    剛揮完的那幾幀它還停在舊值，每幀判會把同一棵樹的第一刀誤判成換樹。
        if (swingsLeft != null && maxSwingsSeen > 0 &&
            swingsLeft.Value == maxSwingsSeen && swingsRecordedThisTree > 0)
        {
            StartNewTree($"計數器已滿（{swingsLeft.Value}）但這棵樹已經砍過 {swingsRecordedThisTree} 刀",
                         swingsLeft, gauge, gaugeMax);
        }

        lastGauge = gauge ?? lastGauge;

        nextTarget = solver.GetNextTargetCursorPos();
        lastBotanistCursor = null;

        var best = solver.Best;
        Svc.Log.Information($"[OutOnALimb] turn start: swings={swingsLeft?.ToString() ?? "?"}/{maxSwingsSeen}, " +
                            $"tried={solver.ObservedCount} contact={solver.ContactCount}, " +
                            $"best={(best == null ? "-" : $"{best.Position}({HitPowerText.Of(best.Power)})")}, " +
                            $"target={nextTarget?.ToString() ?? "-"}");
    }

    /// <summary>
    /// **補強**回饋：樹的量表在砍完之後掉了多少。
    ///
    /// 📌 2026-08-06 之前這裡被寫成「主要回饋來源」，那是錯的：實機 21 刀裡 <c>AtkValue[12]</c>
    /// 只動過 1 次（樹倒下那一刻），其餘 20 刀全程 10 不變。真正每刀都拿得到的是系統訊息的四級手感。
    /// 所以這條路徑保留，但只當額外訊號——它有資料就採信，沒資料也不影響收斂。
    /// </summary>
    private void CollectGaugeFeedback(AtkUnitBase* addon)
    {
        if (pendingCursor == null)
        {
            WatchIdleGauge(addon);
            return;
        }

        if (!gaugeLooksPerSwing)
        {
            return;
        }

        if (pendingSinceUtc != null && DateTime.UtcNow - pendingSinceUtc.Value > GaugeWaitTimeout)
        {
            Svc.Log.Information($"[OutOnALimb] no gauge change {GaugeWaitTimeout.TotalSeconds:0}s after swinging " +
                                $"at {pendingCursor.Value}; dropping the sample");
            pendingCursor = null;
            gaugeAtSwing = null;
            pendingSinceUtc = null;
            return;
        }

        var gauge = LimbBoard.ReadGauge(addon);
        if (gauge == null || gaugeAtSwing == null || gauge.Value == gaugeAtSwing.Value)
        {
            return;
        }

        var cursor = pendingCursor.Value;
        var delta = gaugeAtSwing.Value - gauge.Value;
        pendingCursor = null;
        gaugeAtSwing = null;
        pendingSinceUtc = null;
        lastGauge = gauge;
        idleGauge = gauge;
        gaugeMovedThisTree = true;

        // 📌 方向現在是實測的，不再是猜的：實機看到砍倒那一刀量表 10→0，
        // 也就是**變小＝這一刀有傷害**。變大只可能是換了一棵樹（重新填滿），那不是成績。
        if (delta <= 0)
        {
            Svc.Log.Information($"[OutOnALimb] gauge went up by {-delta} after swinging at {cursor} " +
                                $"(now {gauge.Value}); treating it as a board change, not a result");
            return;
        }

        solver.Record(cursor, HitPower.Unobserved, delta);
        LastAction = $"刻度 {cursor} 的量表落差：{delta}";
        Svc.Log.Information($"[OutOnALimb] swing at {cursor} moved the gauge by -{delta} (now {gauge.Value})");
    }

    /// <summary>沒有等待中的刀時盯著量表。它應該是靜止的——如果每幀都在跳，
    /// 那它就不是「這一刀砍得多準」的訊號，這條路徑要整條關掉，改用聊天備援。</summary>
    private void WatchIdleGauge(AtkUnitBase* addon)
    {
        if (!gaugeLooksPerSwing)
        {
            return;
        }

        var gauge = LimbBoard.ReadGauge(addon);
        if (gauge == null)
        {
            return;
        }

        if (idleGauge != null && idleGauge.Value != gauge.Value)
        {
            idleGaugeChanges++;
            if (idleGaugeChanges >= IdleGaugeChangeLimit)
            {
                gaugeLooksPerSwing = false;
                Svc.Log.Information($"[OutOnALimb] AtkValue[12] changed {idleGaugeChanges} times while idle " +
                                    "within one tree; it is not a per-swing value on this client. " +
                                    "Falling back to the chat feedback path for the rest of this session.");
                LastAction = "量表欄位不像是每刀變化，已改用系統訊息回饋";
            }
        }

        idleGauge = gauge;
    }

    private void TrySwingAtTarget(AtkUnitBase* addon)
    {
        if (nextTarget == null)
        {
            return;
        }

        var cursor = LimbBoard.ReadCursor();
        if (cursor == null)
        {
            if (EzThrottler.Throttle(CursorDiagThrottleKey, CursorDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] cursor position unavailable; not swinging");
                LastAction = "沒有出手（讀不到指針位置）";
            }

            return;
        }

        var targetRaw = LimbBoard.ToRawScale(nextTarget.Value);

        // 容許誤差夾在 1–4（0–100 顯示刻度），換算成 0–10000 的原始刻度。
        var toleranceRaw = Math.Clamp(Cfg.Tolerance, 1, 4) * (LimbBoard.CursorScale / 100);
        var inside = Math.Abs(cursor.Value - targetRaw) < toleranceRaw;
        var crossed = HasCrossed(lastBotanistCursor, cursor.Value, targetRaw);
        lastBotanistCursor = cursor.Value;

        if (!inside && !crossed)
        {
            return;
        }

        if (!TryClickButton(addon, LimbBoard.BotanistSwingButtonId, SwingThrottleKey, SwingThrottleMs))
        {
            return;
        }

        var display = LimbBoard.ToDisplayScale(cursor.Value);
        pendingCursor = display;
        gaugeAtSwing = LimbBoard.ReadGauge(addon);
        pendingSinceUtc = DateTime.UtcNow;
        swingsRecordedThisTree++;
        LastAction = $"揮斧於刻度 {display}（目標 {nextTarget.Value}）";
        Svc.Log.Information($"[OutOnALimb] swing at raw {cursor.Value} (display {display}, " +
                            $"target {nextTarget.Value}, {(crossed ? "crossing" : "inside")}, " +
                            $"gauge {gaugeAtSwing?.ToString() ?? "?"}, " +
                            $"swing #{swingsRecordedThisTree} of this tree)");
        nextTarget = null;
    }

    /// <summary>「挑戰翻倍」確認框。預設不碰——由玩家自己決定要不要續戰。</summary>
    private void HandleDoubleDownPrompt(AtkUnitBase* addon)
    {
        if (!Cfg.AutoContinue)
        {
            return;
        }

        if (!SelectYesnoHelper.TryGetVisible(out var yesno) || !SelectYesnoHelper.IsArcadeYesno(yesno))
        {
            return;
        }

        if (!EzThrottler.Throttle(YesnoThrottleKey, YesnoThrottleMs))
        {
            return;
        }

        // 讀不到剩餘時間就一律收手：寧可少玩一輪，也不要在時間不夠時押下去。
        var seconds = LimbBoard.ReadSecondsRemaining(addon);
        var keepPlaying = seconds != null && seconds.Value > Cfg.StopAtSecondsRemaining;
        Svc.Log.Information(
            $"[OutOnALimb] double-down prompt: remaining={seconds?.ToString() ?? "unknown"}s, " +
            $"threshold={Cfg.StopAtSecondsRemaining}s -> {(keepPlaying ? "yes" : "no")}");

        if (keepPlaying)
        {
            SelectYesnoHelper.PressYes(yesno);
            LastAction = $"續戰（剩餘 {seconds}秒）";
        }
        else
        {
            SelectYesnoHelper.PressNo(yesno);
            LastAction = "時間不足，本輪收工";
        }
    }

    /// <summary>
    /// 連續遊玩：一局結束後自己跟機台開下一局。
    ///
    /// 🔴 三層閘門，每一層都必須成立才會動作：設定要打開、玩家要按過「開始連續遊玩」、
    /// 而且局數還沒到上限。任何一層不成立就完全不碰機台。
    /// 這裡不做任何「偵測到某某狀況就自動接手」的事——沒按開始就永遠不會有第一個動作。
    /// </summary>
    /// <param name="allowInteract">可不可以主動去戳機台。機台畫面還開著時一律 false——
    /// 那個時機只准回答已經跳出來的確認框，不准發起新的互動。</param>
    private void RunAutoReplayPhase(bool allowInteract)
    {
        if (!AutoReplayRunning)
        {
            return;
        }

        if (!Cfg.AutoReplay)
        {
            StopAutoReplay("設定已關閉");
            return;
        }

        var cap = Math.Max(1, Cfg.AutoReplayMaxGames);
        if (AutoReplayGamesDone >= cap)
        {
            StopAutoReplay($"已完成 {AutoReplayGamesDone}/{cap} 局");
            return;
        }

        // 🔴 全程都要求「附近認得出一台孤樹無援機台」。認不出來就停——
        // 不會在別的機台前面亂按確認框，也不會在玩家走開之後繼續動作。
        // ⚠️ machine 只在這一幀有效，用完就丟，不存起來。
        if (!LimbMachine.TryFindNearbyMachine(out var machine) || machine == null)
        {
            StopAutoReplay("附近找不到孤樹無援機台");
            return;
        }

        // 機台的「要挑戰一下嗎」確認框。挑戰翻倍那種提示不在這裡處理（它有自己的開關）。
        if (SelectYesnoHelper.TryGetVisible(out var yesno))
        {
            HandleAutoReplayPrompt(yesno, cap);
            return;
        }

        // 有些機台會先跳一層選單。
        if (SelectStringHelper.TryGetArcadeMenu(out var menu))
        {
            if (SelectStringHelper.IsArcadeYesnoMenu(menu) &&
                EzThrottler.Throttle(ReplayMenuThrottleKey, ReplayMenuThrottleMs))
            {
                SelectStringHelper.TrySelectYesEntry(menu);
                LastAction = "連續遊玩：選單已選「是」";
                Svc.Log.Information($"[OutOnALimb] auto-replay selected the arcade start menu entry " +
                                    $"({AutoReplayGamesDone}/{cap})");
            }

            return;
        }

        // 🔴 機台畫面還開著就絕不主動互動——那等於在遊戲進行中重新戳機台。
        if (!allowInteract)
        {
            if (EzThrottler.Throttle(ReplayWaitDiagThrottleKey, ReplayWaitDiagThrottleMs))
            {
                Svc.Log.Information($"[OutOnALimb] auto-replay standing by: the machine screen is still open and " +
                                    $"there is no prompt to answer ({AutoReplayGamesDone}/{cap})");
            }

            return;
        }

        // 什麼畫面都沒有 → 去跟機台互動。
        // ⚠️ Player.Interactable 是「發起互動」的前提，不是「按畫面上的按鈕」的前提，
        //    所以只擋這一條路徑（舊版擋在整個函式最前面，連回答確認框都被一起擋掉）。
        if (!Player.Interactable)
        {
            return;
        }

        if (ObjectHelper.TryInteractWithObject(machine, ReplayInteractThrottleKey))
        {
            LastAction = $"連續遊玩：正在跟機台互動（已完成 {AutoReplayGamesDone}/{cap} 局）";
        }
    }

    /// <summary>
    /// 連續遊玩看到確認框時的判定。
    ///
    /// 🔴 這是整條路徑上**唯一會花掉金碟幣**的動作（每局 1 枚），所以判定寫成**連言**：
    /// 下面每一條都成立才按「是」，任何一條不成立就完全不碰。寧可不按，也不要按錯。
    ///
    /// 【為什麼不能只看 agent 歸屬】舊版的唯一條件是 <c>IsArcadeYesno</c>，也就是
    /// 「這個 addon 的回呼登記在 GoldSaucerMiniGame agent 名下」。那個條件在**遊戲進行中**的
    /// 翻倍提示上確實成立（實機 log 證實 <see cref="HandleDoubleDownPrompt"/> 一直有作用），
    /// 但開場的遊玩確認框是不是同一個擁有者**沒有任何離線證據**——
    /// <c>AddonCallbackEntry</c> 偏移 0 是 <c>EventInterface</c>／<c>AgentInterface</c> 的 union，
    /// 事件腳本開的視窗登記的就不是 agent。所以這裡改成不依賴 agent 歸屬也能成立，
    /// agent 歸屬降級成**診斷欄位**寫進 log。
    ///
    /// 【換上來的閘門一樣緊，而且是可離線證明的】
    /// <list type="bullet">
    /// <item>附近認得出孤樹無援機台（呼叫端已經要求，這裡不重複）；</item>
    /// <item>確認框文字裡有**機台名稱**——執行期從 EObjName#2005423／Item#30425 讀，不寫死字串；</item>
    /// <item>確認框文字命中 <see cref="LimbPrompt"/> 從 Addon#9321 拆出來的固定句。
    ///   台服 1138 張表全掃證實「要挑戰一下嗎？」只有 9321 這一列有，
    ///   而翻倍提示（9329／9333）寫的是「要<b>嘗試</b>挑戰一下嗎？」，兩者不會互相包含；</item>
    /// <item>不是翻倍提示（<see cref="SelectYesnoHelper.LooksLikeArcadeDoubleDownPrompt"/>，
    ///   純文字判定，不會因為放寬 agent 條件而一起失效）；</item>
    /// <item>不是被封鎖的系統提示（傳送、捨棄、組隊邀請……）。</item>
    /// </list>
    /// </summary>
    private void HandleAutoReplayPrompt(AddonSelectYesno* yesno, int cap)
    {
        var prompt = SelectYesnoHelper.GetPrompt(yesno);
        var hasButtons = SelectYesnoHelper.HasYesnoButtons(yesno);
        var blocked = SelectYesnoHelper.IsBlockedSystemPrompt(yesno);

        // ⚠️ 兩支都問：LooksLike… 是純文字版（放寬 agent 條件之後唯一還有效的那支），
        //    IsArcadeDoubleDownYesno 是原本的 agent＋文字版。任一成立就當成翻倍提示。
        var doubleDown = SelectYesnoHelper.LooksLikeArcadeDoubleDownPrompt(yesno) ||
                         SelectYesnoHelper.IsArcadeDoubleDownYesno(yesno);
        var mentionsMachine = LimbMachine.PromptMentionsMachine(prompt);
        var matchesTemplate = LimbPrompt.LooksLikePlayConfirm(prompt);

        var accept = hasButtons && !blocked && !doubleDown && mentionsMachine && matchesTemplate;
        if (!accept)
        {
            // 「為什麼沒按」必須看得見。使用者跑 LogLevel 2，所以寫 Information 不是 Debug。
            if (EzThrottler.Throttle(ReplayPromptDiagThrottleKey, ReplayPromptDiagThrottleMs))
            {
                Svc.Log.Information($"[OutOnALimb] auto-replay left a yes/no prompt alone: " +
                                    $"{DescribePromptGates(yesno, hasButtons, blocked, doubleDown, mentionsMachine, matchesTemplate)}");
                LastAction = $"連續遊玩：看到確認框但認不出來，沒有動作（{DescribeRefusal(hasButtons, blocked, doubleDown, mentionsMachine, matchesTemplate)}）";
            }

            return;
        }

        if (!EzThrottler.Throttle(ReplayYesThrottleKey, ReplayYesThrottleMs))
        {
            return;
        }

        var pressed = SelectYesnoHelper.PressYes(yesno);
        LastAction = pressed
            ? $"連續遊玩：開始第 {AutoReplayGamesDone + 1} 局"
            : "連續遊玩：認出確認框但按不下去";
        Svc.Log.Information($"[OutOnALimb] auto-replay {(pressed ? "accepted" : "FAILED TO PRESS")} the arcade prompt " +
                            $"({AutoReplayGamesDone + 1}/{cap}); " +
                            $"{DescribePromptGates(yesno, hasButtons, blocked, doubleDown, mentionsMachine, matchesTemplate)}");
    }

    /// <summary>把每一層判定的結果攤平成一行。下次再卡住時，這一行要能直接指出是哪一層擋的。</summary>
    private static string DescribePromptGates(
        AddonSelectYesno* yesno,
        bool hasButtons,
        bool blocked,
        bool doubleDown,
        bool mentionsMachine,
        bool matchesTemplate) =>
        $"owner={AgentHelper.DescribeOwner(&yesno->AtkUnitBase)}, " +
        $"arcadeAgent={SelectYesnoHelper.IsArcadeYesno(yesno)}, " +
        $"buttons={hasButtons}, blockedSystemPrompt={blocked}, doubleDown={doubleDown}, " +
        $"machineName={mentionsMachine}, template={matchesTemplate} " +
        $"(fragments={LimbPrompt.Fragments.Length}), " +
        $"prompt=\"{Flatten(SelectYesnoHelper.GetPrompt(yesno))}\"";

    private static string DescribeRefusal(
        bool hasButtons,
        bool blocked,
        bool doubleDown,
        bool mentionsMachine,
        bool matchesTemplate)
    {
        if (!hasButtons)
        {
            return "找不到是/否按鈕";
        }

        if (blocked)
        {
            return "是被封鎖的系統提示";
        }

        if (doubleDown)
        {
            return "是挑戰翻倍提示，交給續戰設定決定";
        }

        if (!mentionsMachine)
        {
            return "文字裡沒有孤樹無援的機台名稱";
        }

        return matchesTemplate ? "未知原因" : "文字不符合街機遊玩確認框的模板";
    }

    /// <summary>log 用：把多行文字壓成一行，免得一則診斷散成好幾行難以對齊時間戳。</summary>
    private static string Flatten(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r", string.Empty).Replace("\n", " ⏎ ");

    /// <summary>每一層都先驗指標再解參考；任何一層取不到就整個不動作。</summary>
    private static bool TryClickButton(AtkUnitBase* addon, uint nodeId, string throttleKey, int throttleMs)
    {
        if (addon == null)
        {
            return false;
        }

        var button = addon->GetComponentButtonById(nodeId);
        // ⚠️ button->AtkResNode 與 IsEnabled 解的 OwnerNode 是兩個不同欄位，前者擋不到後者 → 用 IsEnabledSafe。
        if (button == null || button->AtkResNode == null || !button->AtkResNode->IsVisible() || !AddonButton.IsEnabledSafe(button))
        {
            return false;
        }

        if (!EzThrottler.Throttle(throttleKey, throttleMs))
        {
            return false;
        }

        try
        {
            button->ClickAddonButton(addon);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[OutOnALimb] button click failed");
            return false;
        }
    }

    /// <summary>
    /// 砍伐手感回饋的**備援**路徑。遊戲把它當系統訊息送進聊天欄，所以這裡只是被動聽——
    /// 不 hook、不攔截。量表落差先到的話這條就不會被用到（<see cref="pendingCursor"/> 已經清掉）。
    ///
    /// 刻意**不比對聊天頻道代碼**：上游寫死 2105，那個常數在台服沒有離線驗證過。改成
    /// 「機台畫面開著 ＋ 剛剛才揮過斧 ＋ 內容正好是那四句手感文字」三個條件同時成立才採信。
    /// </summary>
    private void OnChatMessage(XivChatType type, int timestamp, SeString sender, SeString message)
    {
        try
        {
            if (pendingCursor == null || !LimbBoard.IsBotanistOpen)
            {
                return;
            }

            var text = Normalise(message.TextValue);
            if (text.Length == 0)
            {
                return;
            }

            var table = feedbackText ??= BuildFeedbackTable();
            if (!table.TryGetValue(text, out var power))
            {
                if (EzThrottler.Throttle(ChatDiagThrottleKey, ChatDiagThrottleMs))
                {
                    Svc.Log.Information($"[OutOnALimb] unmatched chat while swinging: type={(int)type} text={text}");
                }

                return;
            }

            var cursor = pendingCursor.Value;
            pendingCursor = null;
            gaugeAtSwing = null;
            pendingSinceUtc = null;
            solver.Record(cursor, power);
            LastAction = $"刻度 {cursor} 的手感：{HitPowerText.Of(power)}";
            Svc.Log.Information($"[OutOnALimb] hit result at {cursor}: {power} (chat type {(int)type}); " +
                                $"board now tried={solver.ObservedCount} contact={solver.ContactCount}");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[OutOnALimb] chat handler failed");
        }
    }

    private static string Normalise(string? s) => string.IsNullOrEmpty(s) ? string.Empty : s.Replace(" ", string.Empty);

    /// <summary>從 Addon 表建手感文字對照。台服有「列存在但欄位空白＝未開放」的情形，
    /// 所以空字串一律跳過；重複字串也跳過，避免字典初始化丟例外。</summary>
    private static Dictionary<string, HitPower> BuildFeedbackTable()
    {
        var table = new Dictionary<string, HitPower>(StringComparer.OrdinalIgnoreCase);
        var sheet = Svc.Data.GetExcelSheet<Addon>();
        if (sheet == null)
        {
            Svc.Log.Warning("[OutOnALimb] Addon sheet unavailable; hit feedback will not be read");
            return table;
        }

        foreach (var (rowId, power) in FeedbackRows)
        {
            var text = Normalise(sheet.GetRowOrDefault(rowId)?.Text.ExtractText());
            if (text.Length == 0)
            {
                Svc.Log.Warning($"[OutOnALimb] Addon#{rowId} is empty on this client; {power} feedback unavailable");
                continue;
            }

            if (!table.TryAdd(text, power))
            {
                Svc.Log.Warning($"[OutOnALimb] Addon#{rowId} duplicates an earlier feedback string; skipped");
            }
        }

        Svc.Log.Information($"[OutOnALimb] hit feedback table built with {table.Count}/{FeedbackRows.Length} entries");
        return table;
    }
}
