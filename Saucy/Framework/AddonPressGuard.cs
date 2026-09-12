using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Saucy.Framework;

/// <summary>
/// 「同一扇視窗的同一個按法按過就不要再按，直到它真的收掉」的共用閘門。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>存在的唯一理由是「按下之後那幾幀又被按第二次」會讓遊戲當場關閉</b>：AVE 在 .NET Core 是 corrupted-state exception，各呼叫端那幾層 <c>try</c>/<c>catch</c> <b>攔不到</b>，<b>唯一的防護是「不要送第二次」，不是「送了再接住」</b>。
/// 📌 <b>粒度＝（窗，位址，按法）</b>而不是「一扇窗只按一次」。🔴 全程只做<b>位址等值比較，永遠不解參</b>——被記下的那個位址隨時可能已經失效。<b>例外是「終結動作」</b>（<see cref="WholeWindowKey"/>）：同一位址<b>任何</b>按法在<see cref="TerminalHotFrames"/> 幀內都不准，因為那幾幀這扇窗可能正在關閉。
/// <b>輪詢</b>：被記下的位址已經不在該名稱的 addon 清單裡 ⇒ 那扇窗真的收乾淨了。<b><see cref="IAddonLifecycle"/> 的 <see cref="AddonEvent.PostSetup"/></b>（<b>這個位址上</b>有全新的一扇被建立起來 ⇒ 那個位址的舊紀錄過期）。
/// 🔴🔴 <b>解除一律以「事件講的那一個位址」為準，不是「這個名字底下全部」</b>。而 <see cref="AddonEvent.PreFinalize"/> 不是解除點<b>而是加鎖點</b>（記進 <see cref="FinalizedByAddon"/>，見 <see cref="IsRetiringInstance"/>）。⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，那會把封鎖提早解除，正好把這道防線變成沒有。
/// 🔴 <b>逃生口是刻意的</b>：沒有它的話呼叫端會<b>永遠</b>按不下去，等於把崩潰換成靜默失效。⚠️ 只在主執行緒使用（與呼叫端的 <c>EzThrottler</c> 同一個前提）。
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 已經按過、那扇窗卻既沒消失也沒重建時，最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗的同一個按法只按一次」，這個值只是防死鎖的逃生口。
    /// 90 幀（60fps 下約 1.5 秒）遠遠大於「關閉中的那幾幀」，補按永遠不會落在危險窗口內。
    /// </remarks>
    public const int ReleaseEscapeFrames = 90;

    /// <summary>
    /// 給「按一次翻一頁、窗不會因為被按而消失」的多次互動窗用的短逃生口（15 幀）。
    /// </summary>
    /// <remarks>
    /// Talk 是代表；機台的揮擊鈕、幻卡棋盤出牌、仙人微彩翻格也是這個形狀。
    /// 走這個逃生口是常態，放行 log 寫 Debug。（2026-09-02 艦隊政策：Talk 類一律 15 幀。）
    /// </remarks>
    public const int RoutineRePressEscapeFrames = 15;

    /// <summary>
    /// 終結動作登記之後，<b>同一位址的其他按法</b>被擋住的「熱窗」長度（幀）。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>熱窗與逃生口（<see cref="ReleaseEscapeFrames"/>）是兩件不同的事，絕對不能共用同一個數字。</b>熱窗要涵蓋的是「這扇窗正在關閉」的那幾幀（實測 &lt; 10 幀）；逃生口要涵蓋的是「上一次按下根本沒生效」的判定門檻（90 幀）。
    /// 🔴 <b>把熱窗也設成 90 會讓後援按法永久餓死</b>：終結動作每次走逃生口放行都會<b>重新登記</b>、時間戳歸零，於是熱窗永遠接得上下一個熱窗，同一扇窗的其他按法一次都送不出去。
    /// 要維持的不變式是<b>本常數必須遠小於「兩次終結動作之間的最小間隔」</b>（後者由 <see cref="ReleaseEscapeFrames"/>保證），15 對 90 有六倍餘裕。數值與 <see cref="RoutineRePressEscapeFrames"/> 相同純屬巧合（同一份「危險窗口 &lt; 10 幀」的判準），<b>兩者語意不同，不要合併</b>。
    /// </remarks>
    public const int TerminalHotFrames = 15;

    /// <summary>
    /// 「終結動作」的按法鍵：按了這扇窗就會走（確認鈕、<c>close:true</c> callback、<c>Close(true)</c>）。
    /// 登記之後，同一位址<b>任何</b>按法在它走完生命週期（或逃生口）之前都不准。
    /// </summary>
    public const string WholeWindowKey = "";

    /// <summary>看過 PreFinalize 的位址最多封鎖這麼多幀（防 PostSetup 沒來變成永久鎖）。</summary>
    /// <remarks>
    /// 真正要擋的是「同一幀／同一次呼叫裡對剛被 finalize 的實例再按」——那之後輪詢呼叫端從
    /// <c>GetAddonByName</c> 就拿不到它了。30 幀只是保險，超過就當「這個位址已經是別的東西」。
    /// </remarks>
    private const int FinalizedGraceFrames = 30;

    /// <summary>輪詢解除時最多掃到第幾個同名實例。</summary>
    /// <remarks>同名視窗同時開著超過這個數量在實務上不存在；掃到第一個空的就提早停。</remarks>
    private const int MaxAddonIndex = 32;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗：這些名字底下的按法一律併成 <see cref="WholeWindowKey"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 這一組是<b>必要的</b>，不是保守起見：同一扇窗在本外掛裡會被<b>好幾種機制</b>按到，不併 key 就會出現「兩條路徑接力按同一扇關閉中的窗」。
    /// 📌 <c>SelectString</c>／<c>SelectIconString</c> 刻意<b>不</b>在此（與 AutoDuty 同一個判斷）：巢狀選單常常<b>重用同一個實例</b>只換內容（不觸發 PostSetup），併 key 會讓下一層的選擇被擋到逃生口。那兩個改用「選項索引」當按法鍵，本外掛對同一扇選單永遠算出同一個索引，同幀雙按照樣擋得住。
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "TripleTriadRequest",
        "TripleTriadResult",
    };

    /// <param name="Address">被按的那個實例的位址，<b>只做等值比較</b>。</param>
    /// <param name="Frame">按下時的<b>守衛幀號</b>（<see cref="frameCount"/>，遊戲 tick；<b>不是</b>繪製幀）。</param>
    /// <remarks>
    /// 🔴 刻意<b>不</b>把「登記當時的逃生口幀數」記進來：其他按法判「終結動作還熱著」一律用
    /// <see cref="TerminalHotFrames"/>。拿逃生口長度當熱窗會把後援按法餓死（見該常數說明），
    /// 把它存進紀錄裡只是讓那個錯誤更容易被寫回來。
    /// </remarks>
    private readonly record struct PressRecord(nint Address, long Frame);

    private readonly record struct FinalizeRecord(nint Address, long Frame);

    /// <summary>addon 名稱 → （按法 → 上一次按的是哪個實例、在第幾幀）。</summary>
    private static readonly Dictionary<string, Dictionary<string, PressRecord>> PressedByAddon =
        new(StringComparer.Ordinal);

    /// <summary>addon 名稱 → 最近一次看到 PreFinalize 的實例位址。</summary>
    private static readonly Dictionary<string, FinalizeRecord> FinalizedByAddon = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers =
        new(StringComparer.Ordinal);

    /// <summary>守衛自己的幀計數器：<c>Svc.Framework.Update</c> 每個遊戲 tick 加一。</summary>
    /// <remarks>
    /// 🔴🔴 <b>刻意<u>不</u>用 <c>Svc.PluginInterface.UiBuilder.FrameCount</c>——那個計數器會停住。</b>
    /// 本 pin 的 <c>UiBuilder.OnDraw()</c> 在三種情況成立時<b>直接 <c>return</c></b>：①使用者隱藏 UI ＋ <c>ToggleUiHide</c>　②<b>過場動畫</b> ＋ <c>ToggleUiHideDuringCutscenes</c>（<b>預設開</b>）　③GPose ＋ <c>ToggleUiHideDuringGpose</c>；也就是說<b>過場或隱藏 UI 期間 <c>UiBuilder.FrameCount</c> 完全不前進</b>。
    /// 用那個時鐘等於<b>過場中所有逃生口永不到期</b>：幻卡對局途中就有過場，於是「後援按法被熱窗餓死」會以「熱窗永遠不過期」的形式原封不動回來。
    /// <c>Svc.Framework.Update</c> 掛在遊戲自己的 update 迴圈上，與畫不畫 ImGui 無關。（做法沿用 TCToolbox 的同名守衛。）
    /// </remarks>
    private static long frameCount;

    /// <summary>幀計數器的訂閱旗標（只掛一次，<see cref="ForceTeardown"/> 拆）。</summary>
    private static bool clockRunning;

    /// <summary>
    /// 登記「即將對這扇視窗送出終結動作」（整扇窗只有一種按法、或按了窗就會走）。
    /// <b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b>——這支一回 <see langword="true"/> 就已經把
    /// 「按過了」記下去，登記完卻不按的話會白白封鎖到逃生口為止。
    /// </remarks>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon) =>
        TryBeginPress(addonName, addon, WholeWindowKey, ReleaseEscapeFrames, out _);

    /// <summary>
    /// 登記「即將對這扇視窗送出這一種按法」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <param name="addonName">視窗名稱（解除封鎖的監聽器與輪詢都以它為準）。</param>
    /// <param name="addon">目標實例。<b>只當作識別用的位址，本方法不解參。</b></param>
    /// <param name="pressKey">
    /// 這一次的「按法」（參數組）。同一扇窗上不同的按法互不干擾；要擋的是<b>同一個按法重複送</b>。傳 <see cref="WholeWindowKey"/> 代表終結動作：登記後同一位址的任何按法都不准。
    /// </param>
    /// <param name="escapeFrames">逃生口幀數：單答終結窗用 <see cref="ReleaseEscapeFrames"/>，Talk 類多次互動窗用 <see cref="RoutineRePressEscapeFrames"/>。</param>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey,
                                     int escapeFrames = ReleaseEscapeFrames) =>
        TryBeginPress(addonName, addon, pressKey, escapeFrames, out _);

    /// <inheritdoc cref="TryBeginPress(string, AtkUnitBase*, string, int)"/>
    /// <param name="viaEscape">
    /// 回 <see langword="true"/> ＝這次放行是<b>走逃生口</b>（同位址同按法按過、窗卻既沒銷毀也沒重建），
    /// 也就是「上一次按下沒生效」。有多個後援按法的呼叫端要靠它換到下一招，
    /// 而不是在同一次呼叫裡連按。
    /// </param>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey, int escapeFrames,
                                     out bool viaEscape)
    {
        viaEscape = false;
        if (addon == null || string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        // 回答一次就結束的窗：不管是哪一條路徑、送的是什麼參數，一律算同一次終結動作。
        if (SingleAnswerAddons.Contains(addonName))
        {
            pressKey = WholeWindowKey;
        }

        pressKey ??= WholeWindowKey;

        // 先把「那扇窗已經從 addon 清單消失」的紀錄清掉（含其他名字的），
        // 下一扇同名窗才會被當成全新的窗處理。
        ReleaseVanished();
        EnsureWatching(addonName);

        var address = (nint)addon;
        var frame = frameCount;
        var routine = escapeFrames <= RoutineRePressEscapeFrames;
        var label = string.IsNullOrEmpty(pressKey) ? "終結動作" : $"按法「{pressKey}」";

        // 看過 PreFinalize 的實例：PostSetup 之前不碰（有幀數上限，見 FinalizedGraceFrames）。
        if (IsRetiringInstance(addonName, address, frame))
        {
            return false;
        }

        PressedByAddon.TryGetValue(addonName, out var presses);
        if (presses != null)
        {
            // 終結動作按過而且還熱著：同一扇窗的其他按法一律不准——那之後它就是在關閉中。
            if (pressKey != WholeWindowKey &&
                presses.TryGetValue(WholeWindowKey, out var whole) &&
                whole.Address == address &&
                frame - whole.Frame < TerminalHotFrames)
            {
                if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                {
                    Svc.Log.Information(
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）終結動作按過之後還沒觀察到它收掉，" +
                        $"這一幀不再送{label} —— 對關閉中的視窗送輸入是攔不到的存取違規。");
                }

                return false;
            }

            if (presses.TryGetValue(pressKey, out var pressed) && pressed.Address == address)
            {
                var waited = frame - pressed.Frame;
                if (waited < escapeFrames)
                {
                    // 🔴 這就是會崩潰的那一幀。單答窗寫 Information（使用者跑 LogLevel 1）；
                    // Talk 類每頁都會走到這裡一次，寫 Debug 免得洗版。
                    if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                    {
                        var message =
                            $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，{label}）按過之後還沒觀察到它收掉，" +
                            "這一幀不再送 —— 對關閉中的視窗送輸入是攔不到的存取違規。";
                        if (routine)
                        {
                            Svc.Log.Debug(message);
                        }
                        else
                        {
                            Svc.Log.Information(message);
                        }
                    }

                    return false;
                }

                viaEscape = true;
                if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
                {
                    var message =
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，{label}）按下後 {waited} 幀" +
                        "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。";
                    if (routine)
                    {
                        Svc.Log.Debug(message);
                    }
                    else
                    {
                        Svc.Log.Information(message);
                    }
                }
            }
        }

        if (presses == null)
        {
            presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }

        presses[pressKey] = new PressRecord(address, frame);
        // 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
        Svc.Log.Debug($"[按窗診斷] plugin=Saucy addon={addonName} addr=0x{address:X} key={pressKey}");
        return true;
    }

    /// <summary>
    /// 給「按了窗<b>不會</b>關」的按法用的輕量閘門（機台鈕、幻卡棋盤出牌、仙人微彩翻格／選線、卡片清單點格／切頁、選牌組窗的最後手段隱藏）：<b>不登記按下紀錄</b>、不改任何重試節奏，只擋兩種「這扇實例正在走」的狀態——①已經看過它 PreFinalize（下一次 PostSetup 之前）、②同一位址在 <paramref name="terminalHotFrames"/> 幀內送過終結動作（確認鈕／<c>close:true</c>callback／<c>Close(true)</c>）。<b>回 <see langword="false"/> ＝這一幀絕對不能碰。</b>
    /// </summary>
    /// <remarks>
    /// 🔑 這種窗不能套「同窗只按一次」：一局要揮很多刀、一張彩券要翻三格，而且它們不會因為被按而消失，「窗走完生命週期」在遊戲收掉之前永遠不會發生。能加的只有「已經在銷毀／已經在關的實例不要再碰」，這正是本方法。
    /// <paramref name="terminalHotFrames"/> 預設 <see cref="TerminalHotFrames"/>：關閉中的危險窗口 &lt; 10 幀，15 幀不落在裡面；而終結動作之後窗若仍在，就不是在關閉中。
    /// </remarks>
    public static bool TryTouch(string addonName, AtkUnitBase* addon, int terminalHotFrames = TerminalHotFrames)
    {
        if (addon == null || string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        EnsureWatching(addonName);

        var address = (nint)addon;
        var frame = frameCount;

        if (IsRetiringInstance(addonName, address, frame))
        {
            return false;
        }

        if (PressedByAddon.TryGetValue(addonName, out var presses) &&
            presses.TryGetValue(WholeWindowKey, out var whole) &&
            whole.Address == address &&
            frame - whole.Frame < terminalHotFrames)
        {
            if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）{frame - whole.Frame} 幀前才送過終結動作，" +
                    "這一幀不再碰它 —— 對關閉中的視窗送輸入是攔不到的存取違規。");
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 視窗文字裡有 U+FFFD（解碼失敗的替代字元）＝那扇窗的記憶體正在變動（多半是關閉中），
    /// <b>該幀不碰</b>。凡是讀窗文字做判定的站，按之前都要先過這一關。
    /// </summary>
    public static bool LooksCorrupted(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains('�');

    /// <summary>
    /// 「已經看過這個實例 PreFinalize、還沒看到<b>同一位址</b>的 PostSetup」＝正在銷毀，不碰。
    /// 超過 <see cref="FinalizedGraceFrames"/> 就當這個位址已經是別的東西，把紀錄清掉。
    /// </summary>
    private static bool IsRetiringInstance(string addonName, nint address, long frame)
    {
        if (!FinalizedByAddon.TryGetValue(addonName, out var finalized) || finalized.Address != address)
        {
            return false;
        }

        if (frame - finalized.Frame <= FinalizedGraceFrames)
        {
            if (EzThrottler.Throttle($"AddonPressGuard-Finalized-{addonName}", 1000))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）已經看到它 PreFinalize，" +
                    "還沒看到新的 PostSetup，這一幀不碰它。");
            }

            return true;
        }

        FinalizedByAddon.Remove(addonName);
        return false;
    }

    /// <summary>外掛卸載時硬拆所有監聽器（不留指向本組件的委派）。</summary>
    public static void ForceTeardown()
    {
        foreach (var (addonName, handler) in Watchers)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
        }

        if (clockRunning)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            clockRunning = false;
        }

        Watchers.Clear();
        PressedByAddon.Clear();
        FinalizedByAddon.Clear();
    }

    /// <summary>
    /// 清掉「被記下的那個實例已經不在同名 addon 清單裡」的紀錄。
    /// </summary>
    /// <remarks>
    /// 🔴 只做位址等值比較，永遠不解參。
    /// ⚠️ 判準刻意<b>不</b>用「視窗看起來還 ready 嗎」：關閉中的那幾幀三關全過，
    /// 拿那個當「窗不見了」會在最危險的那幾幀把封鎖解除掉，等於沒有這道防線。
    /// </remarks>
    private static void ReleaseVanished()
    {
        if (PressedByAddon.Count == 0)
        {
            return;
        }

        // 先抄一份鍵：字典在迭代途中不能移除。同時存在的紀錄實務上是 0~3 個，這份複製可忽略，
        // 而且只有在真的有按下紀錄時才會走到這裡。
        foreach (var addonName in PressedByAddon.Keys.ToArray())
        {
            if (!PressedByAddon.TryGetValue(addonName, out var presses))
            {
                continue;
            }

            foreach (var pressKey in presses.Keys.ToArray())
            {
                if (!IsStillPresent(addonName, presses[pressKey].Address))
                {
                    presses.Remove(pressKey);
                }
            }

            if (presses.Count == 0)
            {
                PressedByAddon.Remove(addonName);
            }
        }
    }

    private static bool IsStillPresent(string addonName, nint address)
    {
        for (var i = 1; i <= MaxAddonIndex; i++)
        {
            var live = (nint)Svc.GameGui.GetAddonByName(addonName, i).Address;
            if (live == 0)
            {
                return false;
            }

            if (live == address)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）：這兩條監聽器只做
    /// 一次字典移除，成本可忽略，而動態掛／拆比較容易留下懸空的監聽器。
    /// </remarks>
    private static void EnsureWatching(string addonName)
    {
        // 🔴 時鐘要在下面那個 early return 之前掛上：這支對「已經在監聽的名字」會直接返回，
        // 把 EnsureClock 放在後面的話，第二個以後的名字進來時計數器根本沒被掛起來。
        EnsureClock();

        if (Watchers.ContainsKey(addonName))
        {
            return;
        }

        IAddonLifecycle.AddonEventDelegate handler = (type, args) => OnLifecycle(addonName, type, args);

        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }

    /// <summary>掛上守衛自己的幀計數器（只掛一次；<see cref="ForceTeardown"/> 拆）。</summary>
    /// <remarks>
    /// 見 <see cref="frameCount"/>：為什麼不能用 <c>UiBuilder.FrameCount</c>。
    /// 📌 <b>外掛啟動時就先呼叫一次</b>（<c>Saucy</c> 建構子裡、<c>Svc.Framework.Update += RunBot</c> 之前），好讓計數器排在本外掛所有 <c>Framework.Update</c> 處理常式的<b>最前面</b>：排在前面的處理常式一擲例外，後面的<b>這一個 tick 全部不會被呼叫</b>。
    /// 排最前面就不會有「別人壞掉順便把守衛的時鐘停住」。（它不會被取消訂閱，所以只是漏數幾個 tick，方向仍是 fail-closed：擋住不按。）
    /// </remarks>
    public static void EnsureClock()
    {
        if (clockRunning)
        {
            return;
        }

        clockRunning = true;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>守衛的時鐘：每個遊戲 tick 加一。</summary>
    /// <remarks>
    /// 🔴 這裡<b>不可以</b>加任何 early return（例如「沒有按下紀錄就不數」）：
    /// 那會讓計數器在沒有紀錄的期間停住，等於把剛修掉的「時鐘會凍結」換個地方再犯一次。
    /// </remarks>
    private static void OnFrameworkUpdate(IFramework framework) => frameCount++;

    /// <summary>
    /// 解除封鎖用的生命週期監聽器。<b>🔴 一切都按「這一個實例位址」處理，不按名稱整包清。</b>
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>曾經是 <c>PressedByAddon.Remove(addonName)</c>（把整個名字底下的紀錄一次清掉），那是會崩的：</b>只清「事件講的那一個位址」就沒有這條路徑。
    /// 📌 <c>PreFinalize</c> <b>刻意不清</b>按下紀錄（只登記 <see cref="FinalizedByAddon"/>）：那扇窗正在被銷毀，這時候「忘記它被按過」沒有任何好處。
    /// 📌 <see cref="FinalizedByAddon"/> 的清除同樣改成按位址：同名的<b>另一扇</b>被建立起來不代表正在銷毀的那一扇已經安全（它還在關閉中，一碰就是存取違規）；只有「這個位址被新的一扇重用」才代表舊的銷毀紀錄過期。🔴 全程只取位址、不解參（<c>args.Addon</c> 是純指標包裝，<c>.Address</c> 不碰記憶體）。
    /// </remarks>
    private static void OnLifecycle(string addonName, AddonEvent type, AddonArgs args)
    {
        var address = args.Addon.Address;
        if (address == 0)
        {
            return;
        }

        if (type == AddonEvent.PreFinalize)
        {
            FinalizedByAddon[addonName] = new FinalizeRecord(address, frameCount);
            return;
        }

        // PostSetup：這個位址上是全新的一扇窗，舊紀錄一律過期。
        ForgetPresses(addonName, address);

        if (FinalizedByAddon.TryGetValue(addonName, out var finalized) && finalized.Address == address)
        {
            FinalizedByAddon.Remove(addonName);
        }
    }

    /// <summary>只清掉<b>這一個實例位址</b>的按下紀錄，同名的其他實例不受影響。</summary>
    /// <remarks>🔴 只做位址等值比較，永遠不解參。</remarks>
    private static void ForgetPresses(string addonName, nint address)
    {
        if (!PressedByAddon.TryGetValue(addonName, out var presses))
        {
            return;
        }

        foreach (var pressKey in presses.Keys.ToArray())
        {
            if (presses[pressKey].Address == address)
            {
                presses.Remove(pressKey);
            }
        }

        if (presses.Count == 0)
        {
            PressedByAddon.Remove(addonName);
        }
    }
}
