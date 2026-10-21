using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;
using System.Text;
using static ECommons.GenericHelpers;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;
namespace Saucy.LeapOfFaith;

// 本檔契約：狀態只在 framework 執行緒讀寫（模組的 Framework.Update、SelectYesno 的 PreSetup、停止指令）。
// 只在「這次啟用後親眼看到入場」時才開始倒數：插件載入、模組啟用、選項打開時人已經在裡面都不算。
// 退出只走介面：副本選單同一幀過 ContentsFinderMenuGate（排法指紋＋「退出」可按）才送；確認框在 PreSetup 比中「確定要退出任務嗎？」才請 YesAlready 暫停並記下那一扇，
// 之後只按同一位址、同一句；暫停失敗就不按。視窗位址只做等值比較、永不解參，要碰視窗都當幀以名稱重取。

/// <summary>登高跳跳樂：入場後等固定秒數，經副本選單「退出」直接退出任務（拿最低獎勵）。</summary>
internal static unsafe class LeapOfFaithQuickExit
{
    private enum Phase
    {
        Off,
        Waiting,
        Exiting
    }

    private const string MenuAddon = "ContentsFinderMenu";

    /// <summary>Addon#2545＝「確定要退出任務嗎？」。</summary>
    private const uint LeavePromptRow = 2545;

    private const int DumpSlots = 24;

    private const int YesAlreadyPauseMs = 3_000;

    private const long PressDeadlineMs = 2_000;

    private const long MenuRetryMs = 1_000;

    // 按了「退出」之後這麼久都沒有確認框就放棄；關閉中的選單只有幾幀，遠小於這個值。
    private const long MenuSettleMs = 3_000;

    private const long ExitLimitMs = 60_000;

    private static readonly ManualSteerWatch steer = new();

    private static Phase phase;

    /// <summary>上一幀在不在登高跳跳樂裡；null＝這次啟用後還沒看過。</summary>
    private static bool? wasInside;

    private static int delaySeconds;

    /// <summary>倒數起點（角色真的出現之後）；0＝還沒開始算。</summary>
    private static long waitStart;

    private static long phaseSince;

    private static long nextMenuShowAt;

    private static long menuNotReadySince;

    private static nint pressedMenu;

    private static long pressedAt;

    private static int menuShows;

    private static bool confirmSeen;

    /// <summary>自己叫出來的那一扇選單（只做等值比較）；0＝目前沒有。</summary>
    private static nint ownMenu;

    private static bool awaitingOwnMenu;

    // 暫時不能按之後（關了自己的選單），到這一刻前不碰選單也不重叫。
    private static long menuCooldownUntil;

    private static int menuRefusals;

    private static ContentsFinderMenuGate.Verdict lastVerdict;

    private static string? lastRefusal;

    private static nint claimedYesno;

    private static long claimDeadline;

    private static bool textsLoaded;

    private static string? leavePrompt;

    public static bool IsActive => phase != Phase.Off;

    /// <summary>等待或退出中：自動移動＋跳躍停手，免得被當成你自己在動、也不必再跳。</summary>
    public static bool HoldsMovement => phase != Phase.Off;

    /// <summary>給設定面板的一句話。</summary>
    public static string StatusText => phase switch
    {
        Phase.Waiting when waitStart == 0 => "目前：已入場，等角色出現後開始倒數",
        Phase.Waiting => $"目前：{Math.Max(0, delaySeconds - ((Environment.TickCount64 - waitStart) / 1000))} 秒後退出任務",
        Phase.Exiting => "目前：正在退出任務",
        _ => "目前：沒有在倒數"
    };

    /// <summary>台服「登高跳跳樂大挑戰」的 27 個 ContentFinderCondition 只對應這三個區域；物件偵測當備援。</summary>
    private static bool IsInside() =>
        (uint)Svc.ClientState.TerritoryType is 792 or 899 or 1098 || LeapOfFaithDetection.IsActive;

    /// <summary>每幀由模組呼叫。</summary>
    public static void Tick()
    {
        var inside = IsInside();
        var entered = inside && wasInside == false;
        wasInside = inside;
        if (!inside)
        {
            if (phase == Phase.Exiting)
            {
                Svc.Log.Information("[Saucy] 登高跳跳樂提早退出：已離開副本");
            }
            else if (phase == Phase.Waiting)
            {
                Svc.Log.Information("[Saucy] 登高跳跳樂提早退出：倒數中就離開了副本，取消");
            }

            ClearRun();
            return;
        }

        if (entered)
        {
            Arm();
        }

        var now = Environment.TickCount64;
        TickPendingPress(now);
        switch (phase)
        {
            case Phase.Waiting:
                TickWaiting(now);
                break;
            case Phase.Exiting:
                TickExiting(now);
                break;
        }
    }

    /// <summary>取消這一場的提早退出（停止指令、急停用）。回傳原本有沒有在跑。</summary>
    public static bool Cancel(string reason)
    {
        if (phase == Phase.Off)
        {
            return false;
        }

        Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：取消（{reason}）");
        ClearRun();
        return true;
    }

    /// <summary>模組停用時呼叫：連「看過入場沒有」一起忘掉。</summary>
    public static void Reset()
    {
        ClearRun();
        wasInside = null;
    }

    private static void ClearRun()
    {
        phase = Phase.Off;
        waitStart = 0;
        pressedMenu = 0;
        menuNotReadySince = 0;
        nextMenuShowAt = 0;
        menuShows = 0;
        confirmSeen = false;
        ownMenu = 0;
        awaitingOwnMenu = false;
        menuCooldownUntil = 0;
        menuRefusals = 0;
        lastVerdict = ContentsFinderMenuGate.Verdict.Press;
        lastRefusal = null;
        claimedYesno = 0;
        steer.Reset();
    }

    private static void Arm()
    {
        if (!C.GoldSaucerGates.LeapOfFaithQuickExit)
        {
            return;
        }

        if (GateEmergencyHold.IsHeld)
        {
            GateEmergencyHold.NoteSkipped("登高跳跳樂提早退出");
            return;
        }

        ClearRun();
        phase = Phase.Waiting;
        delaySeconds = Math.Clamp(C.GoldSaucerGates.LeapOfFaithQuickExitDelaySeconds, 0, GoldSaucerGateSettings.LeapOfFaithQuickExitMaxDelaySeconds);
        Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：入場，角色出現後 {delaySeconds} 秒退出任務");
        Svc.Chat.Print($"[Saucy] 登高跳跳樂：{delaySeconds} 秒後退出任務（拿最低獎勵）。自己移動或 /saucy stop 可取消。");
    }

    // 只從 TickExiting 呼叫（框架執行緒）：放棄前把自己叫出來的選單關掉。
    private static void Fail(string reason)
    {
        CloseOwnMenu("放棄");
        Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：放棄（{reason}）");
        Svc.Chat.Print($"[Saucy] 登高跳跳樂：沒有自動退出（{reason}），需要的話請自己退出任務。");
        ClearRun();
    }

    private static void TickWaiting(long now)
    {
        if (!C.GoldSaucerGates.LeapOfFaithQuickExit)
        {
            Cancel("選項已關閉");
            return;
        }

        if (GateEmergencyHold.IsHeld)
        {
            Cancel("急停");
            return;
        }

        if (!Player.Available || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        if (waitStart == 0)
        {
            waitStart = now;
            steer.Reset();
        }

        var step = steer.Observe(Player.Position, navigating: false, GoldSaucerNavigator.IsManualMovementRequested(), busy: false, now);
        if (step == ManualSteerWatch.Step.Started)
        {
            Cancel("你自己移動了");
            Svc.Chat.Print("[Saucy] 登高跳跳樂：你自己移動了，這一場不自動退出。");
            return;
        }

        if (now - waitStart < delaySeconds * 1000L)
        {
            return;
        }

        phase = Phase.Exiting;
        phaseSince = now;
        Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：等滿 {delaySeconds} 秒，開始退出（副本選單「退出」→ 確認框；{ExitLimitMs / 1000} 秒內沒離開就放棄）");
    }

    private static void TickExiting(long now)
    {
        if (GateEmergencyHold.IsHeld)
        {
            Cancel("急停");
            return;
        }

        if (now - phaseSince > ExitLimitMs)
        {
            Fail(lastRefusal != null && pressedMenu == 0 && !confirmSeen
                ? $"{ExitLimitMs / 1000} 秒內副本選單一直不能按「退出」：{lastRefusal}"
                : $"{ExitLimitMs / 1000} 秒內沒離開：叫出副本選單 {menuShows} 次、{(pressedMenu != 0 ? "按了" : "沒按到")}「退出」、確認框{(confirmSeen ? "看到了" : "沒看到")}");
            return;
        }

        if (claimedYesno != 0 || confirmSeen || !IsFree())
        {
            return;
        }

        if (TryGetAddonByName<AtkUnitBase>(SelectYesnoHelper.AddonName, out var yesno) && IsAddonReady(yesno))
        {
            return;
        }

        var found = TryGetAddonByName<AtkUnitBase>(MenuAddon, out var menu);
        var ready = found && IsAddonReady(menu);
        if (ready && awaitingOwnMenu)
        {
            ownMenu = (nint)menu;
            awaitingOwnMenu = false;
        }

        if (pressedMenu != 0)
        {
            if (now - pressedAt >= MenuSettleMs)
            {
                Fail($"按了「退出」{MenuSettleMs / 1000} 秒都沒有出現確認框");
            }

            return;
        }

        if (now < menuCooldownUntil)
        {
            return;
        }

        if (!ready)
        {
            // 可見但沒就緒時先讓它自己載完；卡超過 MenuSettleMs 才再叫一次。
            if (found && menu->IsVisible)
            {
                if (menuNotReadySince == 0)
                {
                    menuNotReadySince = now;
                }

                if (now - menuNotReadySince < MenuSettleMs)
                {
                    return;
                }
            }

            menuNotReadySince = 0;
            if (now < nextMenuShowAt)
            {
                return;
            }

            nextMenuShowAt = now + MenuRetryMs;
            var agents = AgentModule.Instance();
            var agent = agents == null ? null : agents->GetAgentByInternalId(AgentId.ContentsFinderMenu);
            if (agent != null)
            {
                menuShows++;
                awaitingOwnMenu = true;
                agent->Show();
            }

            return;
        }

        var verdict = JudgeMenu(menu, out var refusal);
        if (verdict != ContentsFinderMenuGate.Verdict.Press)
        {
            if (!ContentsFinderMenuGate.CanRetry(verdict))
            {
                DumpMenu(menu, $"不按：{refusal}");
                Fail($"副本選單不能按「退出」：{refusal}");
                return;
            }

            // 暫時不能按：關掉自己叫出來的那扇、冷卻後重叫；別人開的不關，冷卻後重讀。總時限在上面。
            menuRefusals++;
            lastRefusal = refusal;
            var own = (nint)menu == ownMenu;
            if (verdict != lastVerdict)
            {
                lastVerdict = verdict;
                DumpMenu(menu, $"暫時不按：{refusal}；{(own ? "關掉選單、" : string.Empty)}{MenuRetryMs / 1000} 秒後再看（第 {menuRefusals} 次）");
            }

            if (own)
            {
                CloseOwnMenu("暫時不能按「退出」");
            }

            menuCooldownUntil = now + MenuRetryMs;
            return;
        }

        if (!AddonPressGuard.TryBeginPress(MenuAddon, menu))
        {
            return;
        }

        // 先記下再按：確認框可能在 Fire 裡同步建立，PreSetup 要認得出是自己按出來的。
        lastRefusal = null;
        pressedMenu = (nint)menu;
        pressedAt = now;
        DumpMenu(menu, $"按「退出」（callback {ContentsFinderMenuGate.LeaveCallbackValue}）");
        Callback.Fire(menu, true, ContentsFinderMenuGate.LeaveCallbackValue);
    }

    // 只關自己叫出來、當幀照名字重找到而且就緒的那一扇；按窗守衛擋住（例如正在關閉）就不送。
    // 剛按了「退出」還在等確認框、或確認框已出現時不碰（那扇選單已經在走退出流程）。
    private static void CloseOwnMenu(string why)
    {
        if (ownMenu == 0 || confirmSeen || claimedYesno != 0 || (pressedMenu != 0 && Environment.TickCount64 - pressedAt < MenuSettleMs))
        {
            return;
        }

        if (!TryGetAddonByName<AtkUnitBase>(MenuAddon, out var menu) || (nint)menu != ownMenu || !IsAddonReady(menu))
        {
            return;
        }

        if (!AddonPressGuard.TryBeginPress(MenuAddon, menu))
        {
            return;
        }

        ownMenu = 0;
        Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：關掉自己叫出來的副本選單（{why}）");
        Callback.Fire(menu, false, ContentsFinderMenuGate.CloseCallbackValue);
    }

    // 同一幀把 AtkValues 抄成值交給純函式；values 為空時不讀任何一格。
    private static ContentsFinderMenuGate.Verdict JudgeMenu(AtkUnitBase* menu, out string? refusal)
    {
        var values = menu->AtkValues;
        if (values == null)
        {
            refusal = "AtkValues 是空的";
            return ContentsFinderMenuGate.Verdict.TooFew;
        }

        int count = menu->AtkValuesCount;
        Span<int> types = stackalloc int[ContentsFinderMenuGate.FingerprintLength];
        var n = Math.Min(count, types.Length);
        for (var i = 0; i < n; i++)
        {
            types[i] = (int)values[i].Type;
        }

        return ContentsFinderMenuGate.Judge(count, types[..n], n > 0 ? values[0].UInt : 0, n > 1 ? values[1].Byte : (byte)0, out refusal);
    }

    // 只在決定的那一刻寫（按：guard 放行之後；不按：緊接著 Fail；暫時不按：理由換了才寫）。
    private static void DumpMenu(AtkUnitBase* menu, string decision)
    {
        var values = menu->AtkValues;
        int count = values == null ? 0 : menu->AtkValuesCount;
        var sb = new StringBuilder($"[Saucy] 登高跳跳樂提早退出：副本選單 count={count} 區域={Svc.ClientState.TerritoryType}");
        for (var i = 0; i < Math.Min(count, DumpSlots); i++)
        {
            var v = values[i];
            sb.Append($" [{i}]{(int)v.Type:X2}:");
            switch ((AtkValueType)((int)v.Type & (int)AtkValueType.TypeMask))
            {
                case AtkValueType.Bool:
                    sb.Append(v.Byte);
                    break;
                case AtkValueType.Int:
                    sb.Append(v.Int);
                    break;
                case AtkValueType.UInt:
                    sb.Append(v.UInt);
                    break;
                case AtkValueType.String or AtkValueType.String8:
                    var text = GetAtkValueTextOrNull(menu, i);
                    sb.Append(text == null ? "?" : $"「{text.ReplaceLineEndings("⏎")}」");
                    break;
                default:
                    sb.Append('-');
                    break;
            }
        }

        sb.Append(" → ").Append(decision);
        Svc.Log.Information(sb.ToString());
    }

    /// <summary>確認框 Setup 之前先認：是自己按出來的退出確認才請 YesAlready 讓開並記下那一扇；認不出就什麼都不做，YesAlready 照常判斷。</summary>
    public static void OnYesnoPreSetup(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonSetupArgs setup)
        {
            return;
        }

        var address = setup.Addon.Address;
        if (address != nint.Zero && address == claimedYesno)
        {
            claimedYesno = 0;
            Svc.Log.Information("[Saucy] 登高跳跳樂提早退出：接手的確認框還沒按就關了（同一位置又開了一扇），不代按那一扇");
        }

        if (phase != Phase.Exiting || pressedMenu == 0)
        {
            return;
        }

        var prompt = SelectYesnoHelper.ReadSetupPrompt(setup);
        var expected = LeavePrompt();
        if (expected == null)
        {
            confirmSeen = true;
            Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：讀不到 Addon#{LeavePromptRow} 的退出確認句，確認框留給你或 YesAlready 按：{prompt}");
            return;
        }

        if (prompt.ReplaceLineEndings(string.Empty) != expected)
        {
            Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：退出中跳出的確認框不是退出任務，不代按：{prompt}");
            return;
        }

        confirmSeen = true;
        if (!YesAlready.TryPauseSelectYesno(YesAlreadyPauseMs))
        {
            Svc.Log.Information("[Saucy] 登高跳跳樂提早退出：無法確認 YesAlready 不會按，確認框留給它或你自己按");
            return;
        }

        claimedYesno = address;
        claimDeadline = Environment.TickCount64 + PressDeadlineMs;
        Svc.Log.Information("[Saucy] 登高跳跳樂提早退出：退出確認框建立前就認出，YesAlready 不會碰這一扇，下一幀起代按「是」");
    }

    private static void TickPendingPress(long now)
    {
        if (claimedYesno == 0)
        {
            return;
        }

        if (now > claimDeadline)
        {
            Svc.Log.Information("[Saucy] 登高跳跳樂提早退出：接手的確認框在時限內已不在或沒準備好（可能已被按掉或關閉），不代按");
            claimedYesno = 0;
            return;
        }

        if (!SelectYesnoHelper.TryGetVisibleAt(claimedYesno, out var yesno))
        {
            return;
        }

        var prompt = SelectYesnoHelper.GetValuePrompt(yesno);
        if (phase != Phase.Exiting || prompt.ReplaceLineEndings(string.Empty) != LeavePrompt())
        {
            Svc.Log.Information($"[Saucy] 登高跳跳樂提早退出：接手的確認框已換成別的句子或已取消退出，不代按：{prompt}");
            claimedYesno = 0;
            return;
        }

        if (SelectYesnoHelper.PressYes(yesno))
        {
            claimedYesno = 0;
            Svc.Log.Information("[Saucy] 登高跳跳樂提早退出：已代按退出確認框「是」");
        }
    }

    private static bool IsFree()
    {
        var c = Svc.Condition;
        return !(c[ConditionFlag.InCombat] || c[ConditionFlag.Casting] || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51] ||
                 c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.OccupiedInCutSceneEvent] ||
                 c[ConditionFlag.Occupied] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]);
    }

    private static string? LeavePrompt()
    {
        LoadTexts();
        return leavePrompt;
    }

    private static void LoadTexts()
    {
        if (textsLoaded)
        {
            return;
        }

        textsLoaded = true;
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        leavePrompt = Normalize(sheet?.GetRowOrDefault(LeavePromptRow)?.Text.ExtractText());
    }

    private static string? Normalize(string? text) =>
        string.IsNullOrEmpty(text) ? null : text.ReplaceLineEndings(string.Empty);
}
