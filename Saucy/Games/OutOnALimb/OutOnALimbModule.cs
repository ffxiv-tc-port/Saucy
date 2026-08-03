using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using ECommons.Throttlers;
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
/// 【資料來源：完全不碰封包】本模組移植自 PunishXIV/Saucy 在 API13 世代（對應全球 7.3.0，
/// 也就是台服 7.20 的用戶端世代）的實作，資料全部來自玩家本來就看得到的東西：
///   ・階段／剩餘揮擊次數／剩餘時間 → <c>MiniGameBotanist</c> 的 AtkValue
///   ・指針位置                     → 指針節點的 Rotation
///   ・砍伐手感回饋                 → 遊戲自己送到聊天欄的系統訊息（Addon 表 9710–9713）
/// 沒有封包 detour、沒有 hook、沒有特徵碼、沒有寫入遊戲記憶體，出手只有「在對的時機按下
/// 畫面上本來就有的按鈕」。
///
/// 【動作範圍】只在機台畫面已經開著時才動作。不會自己去找機台、不會自己互動、不會自己
/// 開始新的一局——玩家自己走過去按開始，剩下的才交給模組。
/// </summary>
public unsafe class OutOnALimbModule : Module
{
    /// <summary>Addon 表列號 → 手感等級。這四列在台服 7.20 實測都有內容且彼此相異。</summary>
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
    private const int MachineDiagThrottleMs = 5000;
    private const string SwingsDiagThrottleKey = "Saucy.OutOnALimb.SwingsDiag";
    private const int SwingsDiagThrottleMs = 10000;

    private const int SwingThrottleMs = 2000;
    private const int AimgThrottleMs = 1000;
    private const int YesnoThrottleMs = 2000;
    private const int ChatDiagThrottleMs = 3000;

    private readonly LimbSolver solver = new();

    /// <summary>手感訊息文字 → 等級。第一次要用時才從 Excel 建，
    /// 避免外掛載入當下（ModuleManager 建構所有模組）就去碰資料表。</summary>
    private Dictionary<string, HitPower>? feedbackText;

    /// <summary>按下揮斧當下指針所在的刻度。手感訊息是稍後才到的，
    /// 那時指針早就轉走了，所以一定要記住「按的時候在哪」而不是回頭再讀一次。</summary>
    private int? pendingCursor;

    private int? nextTarget;
    private uint lastState;
    private bool powerMeterClicked;

    public override string Name => "Out on a Limb";

    /// <summary>給設定面板顯示的最近動作。</summary>
    public string LastAction { get; private set; } = "等待孤樹無援機台畫面";

    private static LimbSettings Cfg => C.OutOnALimb;

    public override void Enable()
    {
        Svc.Framework.Update += OnUpdate;
        Svc.Chat.ChatMessageHandled += OnChatMessage;
        Svc.Chat.ChatMessageUnhandled += OnChatMessage;
        ResetRoundState();
        LastAction = "等待孤樹無援機台畫面";
    }

    public override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.ChatMessageHandled -= OnChatMessage;
        Svc.Chat.ChatMessageUnhandled -= OnChatMessage;
        ResetRoundState();
        LastAction = "未啟用";
    }

    private void ResetRoundState()
    {
        pendingCursor = null;
        nextTarget = null;
        lastState = 0;
        powerMeterClicked = false;

        // 離開機台畫面就把解題盤面清空。樹與樹之間靠「剩餘揮擊次數回到滿值」判斷，
        // 但那個欄位萬一在未來改版被挪走，至少每次重新走到機台前都是乾淨的起點。
        solver.Reset(Cfg.Step);
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (!C.IsModuleEnabled(ModuleNames.OutOnALimb) || !Player.Available || !IsScreenReady())
            {
                return;
            }

            if (!LimbBoard.IsPlaying)
            {
                if (lastState != 0 || pendingCursor != null || powerMeterClicked)
                {
                    ResetRoundState();
                    LastAction = "等待孤樹無援機台畫面";
                }

                return;
            }

            RunPowerMeterPhase();
            RunChoppingPhase();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[OutOnALimb] update failed");
        }
    }

    /// <summary>第一階段：力量表。指針掃進所選難度的格子時按下停止鈕，一輪只按一次。
    ///
    /// 🔴 力量表 addon 與礦脈探索共用，所以動它之前要先確認面前那台真的是孤樹無援；
    /// 認不出來就完全不碰（玩家自己停表，砍伐階段照樣會接手）。</summary>
    private void RunPowerMeterPhase()
    {
        var addon = LimbBoard.TryGetAddon(LimbBoard.AimgAddon);
        if (addon == null)
        {
            powerMeterClicked = false;
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

        if (!LimbBoard.IsAimgCursorOnTarget(addon, Cfg.Difficulty))
        {
            return;
        }

        if (TryClickButton(addon, LimbBoard.AimgStopButtonId, AimgThrottleKey, AimgThrottleMs))
        {
            powerMeterClicked = true;
            LastAction = $"力量表已停在 {Cfg.Difficulty}";
            Svc.Log.Information($"[OutOnALimb] power meter stopped on {Cfg.Difficulty}");
        }
    }

    /// <summary>第二階段：砍伐。輪到玩家時算出目標刻度，等指針掃過去再按。</summary>
    private void RunChoppingPhase()
    {
        var addon = LimbBoard.TryGetAddon(LimbBoard.BotanistAddon);
        if (addon == null)
        {
            return;
        }

        HandleDoubleDownPrompt(addon);

        var state = LimbBoard.ReadState(addon) ?? 0;
        if (state == LimbBoard.StatePlayerTurn)
        {
            if (lastState != LimbBoard.StatePlayerTurn)
            {
                // 新的一輪。剩餘揮擊次數回到滿值＝換了一棵新的樹，之前量到的手感全部作廢。
                var swingsLeft = LimbBoard.ReadSwingsLeft(addon);
                if (swingsLeft == null && EzThrottler.Throttle(SwingsDiagThrottleKey, SwingsDiagThrottleMs))
                {
                    Svc.Log.Information("[OutOnALimb] swings-left field unreadable; " +
                                        "tree boundaries cannot be detected on this client build");
                }

                if (swingsLeft == LimbBoard.SwingsPerTree)
                {
                    solver.Reset(Cfg.Step);
                    pendingCursor = null;
                    Svc.Log.Information("[OutOnALimb] new tree, solver reset");
                }

                nextTarget = solver.GetNextTargetCursorPos();
            }

            TrySwingAtTarget(addon);
        }

        lastState = state;
    }

    private void TrySwingAtTarget(AtkUnitBase* addon)
    {
        if (nextTarget == null)
        {
            return;
        }

        var cursor = LimbBoard.ReadCursor(addon);
        if (cursor == null)
        {
            return;
        }

        // 容許誤差夾在 1–4：太小會因為畫面更新率追不上指針而永遠按不到。
        var tolerance = Math.Clamp(Cfg.Tolerance, 1, 4);
        if (Math.Abs(cursor.Value - nextTarget.Value) >= tolerance)
        {
            return;
        }

        if (!TryClickButton(addon, LimbBoard.BotanistSwingButtonId, SwingThrottleKey, SwingThrottleMs))
        {
            return;
        }

        pendingCursor = cursor.Value;
        LastAction = $"揮斧於刻度 {cursor.Value}（目標 {nextTarget.Value}）";
        Svc.Log.Information($"[OutOnALimb] swing at {cursor.Value} (target {nextTarget.Value})");
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

    /// <summary>每一層都先驗指標再解參考；任何一層取不到就整個不動作。</summary>
    private static bool TryClickButton(AtkUnitBase* addon, uint nodeId, string throttleKey, int throttleMs)
    {
        if (addon == null)
        {
            return false;
        }

        var button = addon->GetComponentButtonById(nodeId);
        if (button == null || button->AtkResNode == null || !button->AtkResNode->IsVisible() || !button->IsEnabled)
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
    /// 砍伐手感回饋。遊戲把它當系統訊息送進聊天欄，所以這裡只是被動聽——不 hook、不攔截。
    ///
    /// 刻意**不比對聊天頻道代碼**：上游寫死 2105，那個常數在台服沒有離線驗證過。改成
    /// 「機台畫面開著 ＋ 剛剛才揮過斧 ＋ 內容正好是那四句手感文字」三個條件同時成立才採信，
    /// 比寫死代碼可靠，也不會誤收其他訊息。實際看到的頻道代碼會記進 log 供日後查核。
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
            solver.Record(power, cursor);
            LastAction = $"刻度 {cursor} 的手感：{power}";
            Svc.Log.Information($"[OutOnALimb] hit result at {cursor}: {power} (chat type {(int)type})");
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
