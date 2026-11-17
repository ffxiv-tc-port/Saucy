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
/// 【資料來源：完全不碰封包】資料全部來自玩家本來就看得到的東西：
/// 階段／量表／剩餘次數／剩餘時間 → <c>MiniGameBotanist</c> 的 AtkValue
/// 指針位置                       → <c>NumberArrayData[104].IntArray[0]</c>（遊戲自己讀的同一格）
/// 力量表的三段區間               → <c>MiniGameAimg</c> 的 <c>AtkValue[4]/[5]</c>
/// 砍伐回饋                       → 系統訊息的四級手感（主）／樹的量表落差（補強）沒有封包 detour、沒有 hook、沒有特徵碼、沒有寫入遊戲記憶體，出手只有「在對的時機按下畫面上本來就有的按鈕」。
/// 【動作範圍】預設只在機台畫面已經開著時才動作。「連續遊玩」是**明確的選擇加上明確的一次按鈕**：設定要打開、面板上要按下開始、而且跑滿設定的局數就自己停。沒按開始就絕不會自己去碰機台。</summary>
public unsafe partial class OutOnALimbModule : Module
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
    /// 這只是安全下限，不是節奏來源——真正「一輪只砍一刀」是靠階段值（<c>AtkValue[0]</c>＝3 才輪到玩家）
    /// 加上 <see cref="nextTarget"/> 只在進入階段 3 的那一幀才重算：同一輪裡不可能砍第二刀。</summary>
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
    /// 🔴 為什麼要有階梯：**單幀位移可以比目標格還寬**：這種情況下「某一幀剛好落在格子裡」是機率事件，不是必然，所以要允許放掉幾趟等下一趟。
    /// 🔴 但**絕不能等到卡死**：每一局都花掉一枚金碟幣，不停表等於錢丟掉。所以階梯的終點是「不管落在哪一格都按下去」——失敗形式是**打到隔壁格**，不是不揮斧。
    /// 0–<see cref="PreciseWindowMs"/>：只有落在目標格、而且離兩邊界都有餘裕才按。–<see cref="AnyInZoneMs"/>：落在目標格裡就按（不再要求餘裕）。
    /// –<see cref="WidenZoneMs"/>：放寬到「目標格或次寬的那一格」。之後：落在哪一格都按。
    /// ⚠️ 這幾個毫秒數是**取捨值不是實測常數**：訂太短會退化成舊行為（隨便按），訂太長則每局多站幾秒。實機 log 顯示一趟掃描約 200–300ms，所以 1500ms 大約是 5–7 趟。
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
    /// 只比「這一幀夠不夠近」的話，畫面更新率不夠時指針會整個掃過去而永遠按不到。</summary>
    private int? lastBotanistCursor;

    private int? lastAimgCursor;

    /// <summary>這一次力量表嘗試裡，指針**第一次真的動起來**的時刻（<see cref="Environment.TickCount64"/>）。
    /// null＝指針還停在待命位置（實機是 0），這一輪的計時還沒開始。
    ///
    /// 🔴 為什麼需要：待命值 0 落在最窄那一格 <c>(-1, 500]</c> 的**裡面**。少了這個閘門，
    /// 底下「等久了就放寬」的階梯會在指針根本還沒開始掃的時候就判定「在格子裡」而按下去。</summary>
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
    /// 🔴 **null 就是「不知道」，不可以代換成任何具體值。** 寫成 <c>ReadState(addon) ?? 0</c> 的話，
    /// 讀不到的那一幀會被當成一個真的階段值去比對。</summary>
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

    /// <summary>這一棵樹裡有幾刀是「零收穫」——落點偏掉、而且偏到的那一格早就試過了。
    /// 純計量，不參與任何判斷；它是「這台機器的指針對你的畫面更新率來說太快了」的直接徵兆。</summary>
    private int wastedSwingsThisTree;

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

            // 📌 「落點偏掉而且沒拿到新資訊」的刀數要在列上看得見：它是「這台機器的指針
            //    對你的畫面更新率來說太快了」的直接徵兆，使用者看到它變多就知道
            //    該把容許誤差放寬、或改停比較寬的那一格。
            var missed = wastedSwingsThisTree > 0 ? $"、{wastedSwingsThisTree} 刀落點偏掉沒拿到新資訊" : string.Empty;
            return $"目前最佳：刻度 {best.Position}（{HitPowerText.Of(best.Power)}{damage}）" +
                   $"，這棵樹已試 {solver.ObservedCount} 點、其中 {solver.ContactCount} 點有手感{missed}";
        }
    }

    /// <summary>回饋來源的健康狀況。
    /// 📌 這件事必須在列上看得見而不是藏在 tooltip，否則使用者會以為解題器有兩條資料在跑。
    /// ⚠️ 「這棵樹還沒動過」在盲掃階段是**正常的**（沒砍中就沒有傷害可量），不代表壞掉。</summary>
    public string FeedbackSummary =>
        $"回饋來源：系統訊息手感 {(feedbackText == null ? "尚未建表" : $"{feedbackText.Count}/{FeedbackRows.Length} 句")}" +
        $"；樹的量表 {(gaugeLooksPerSwing ? gaugeMovedThisTree ? "有在動" : "這棵樹還沒動過" : "已判定不是每刀變化，停用")}";

    private static LimbSettings Cfg => C.OutOnALimb;

    public override void Enable()
    {
        SubscribeUpdate(OnUpdate);
        Svc.Chat.ChatMessageHandled += OnChatMessage;
        Svc.Chat.ChatMessageUnhandled += OnChatMessage;
        gaugeLooksPerSwing = true;
        ResetRoundState();
        StopAutoReplay("模組啟用");
        LastAction = "等待孤樹無援機台畫面";
    }

    public override void Disable()
    {
        UnsubscribeUpdate();
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
        wastedSwingsThisTree = 0;
        idleGauge = null;
        idleGaugeChanges = 0;
        powerMeterClicked = false;
        ResetAimgAttempt();

        // 離開機台畫面就把解題盤面清空。
        solver.Reset(Cfg.Step);
    }

    /// <summary>換了一棵新的樹：盤面清空、丟掉還在等回饋的那一刀（它屬於上一棵樹）。
    /// <paramref name="reason"/> 會寫進 log——所以它必須一直留著。</summary>
    private void StartNewTree(string reason, uint? swingsLeft, int? gauge, int? gaugeMax)
    {
        solver.Reset(Cfg.Step);
        pendingCursor = null;
        gaugeAtSwing = null;
        pendingSinceUtc = null;
        nextTarget = null;
        swingsRecordedThisTree = 0;
        gaugeMovedThisTree = false;
        wastedSwingsThisTree = 0;
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

            // ⚠️ 這裡刻意只在**砍伐畫面關著**時才跑，而且 `allowInteract: false`：
            // ・砍伐畫面開著時的確認框是「挑戰翻倍」，那由 HandleDoubleDownPrompt 依剩餘時間決定，不能讓連續遊玩搶答（它只會一律按「是」）。用畫面狀態把兩者結構性地分開，比只靠文字判斷可靠。
            // ・機台畫面還開著就去互動＝在遊戲進行中重新戳機台，所以互動路徑整條關掉，這個時機只允許「回答已經跳出來的確認框」。
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
