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
/// 🔴🔴 <b>存在的唯一理由是「按下之後那幾幀又被按第二次」會讓遊戲當場關閉</b>：
/// <c>SelectYesno</c> 這類確認框被按下之後有<b>「正在關閉中」的幾幀</b>，這段期間
/// <c>GetAddonByName</c> 仍然回得到實例、<c>IsVisible</c> 與
/// <c>UldManager.LoadedState == Loaded</c> 也<b>三關全過</b>——也就是說
/// <see cref="SelectYesnoHelper.TryGetVisible"/> 用的 <c>IsAddonReady</c>
/// <b>擋不住這個窗口</b>。此時再對它送 callback／模擬點擊／<c>ReceiveEvent</c> 就是原生 AccessViolation
/// （<c>C0000005</c>）。AVE 在 .NET Core 是 corrupted-state exception，
/// 各呼叫端那幾層 <c>try</c>/<c>catch</c> <b>攔不到</b>，
/// 遊戲當場關閉 ——<b>唯一的防護是「不要送第二次」，不是「送了再接住」</b>。
/// <para>
/// ⚠️ 呼叫端原有的 <c>EzThrottler</c> <b>不是</b>防護：它記的是「上一次動作在哪個時刻」
/// 而不是「這扇窗已經按過」，而且<b>首次一律放行</b>、key 是全域持久的、每個呼叫端各用各的 key
/// ——兩個呼叫端接力按同一扇窗時兩邊的節流都會放行（幻卡 Talk 六個 key、SelectString 兩個 key
/// 就是這個形狀）。幻卡那條每幀鏈（<c>Saucy.Tick</c> → <see cref="TriadDialogueSkip"/>）
/// 連節流都沒有，按下之後下一幀就會再按一次。
/// </para>
/// <para>
/// 🔑 <b>做法</b>：按下之前先登記「這個名字底下的哪一個實例位址、被送過哪一種按法」，
/// 在觀察到那扇窗真的走完生命週期之前不准再送同一種。
/// 🔴 全程只做<b>位址等值比較，永遠不解參</b>——被記下的那個位址隨時可能已經失效。
/// </para>
/// <para>
/// 📌 <b>粒度＝（窗，位址，按法）</b>而不是「一扇窗只按一次」：幻卡選牌組那扇窗
/// （<c>TripleTriadSelDeck</c>）刻意在同一幀先點列、再送 deck callback、最後才按確認鈕，
/// 只看位址會把這條正常流程整個擋掉。所以按法各自成鍵，互不干擾。
/// <b>例外是「終結動作」</b>（<see cref="WholeWindowKey"/>）：確認鈕、<c>close:true</c> 的 callback、
/// <c>Close(true)</c> 這類「按了窗就會走」的動作登記之後，同一位址<b>任何</b>按法在
/// <see cref="TerminalHotFrames"/> 幀內都不准，因為那幾幀這扇窗可能正在關閉。
/// 🔴 熱窗<b>只有那麼短是刻意的</b>：拿逃生口長度（90 幀）當熱窗會把呼叫端的後援階梯永久餓死，
/// 理由見 <see cref="TerminalHotFrames"/>。
/// 而「一扇窗一生只回答一次」的窗（<see cref="SingleAnswerAddons"/>）
/// 不管走哪一條路徑、送什麼參數，一律併成終結動作。
/// </para>
/// <para>
/// <b>解除封鎖有兩條互補的觀察點</b>（兩條都只會讓封鎖<b>提早</b>解除，不會延後）：
/// <list type="number">
/// <item>
/// <b>輪詢</b>：被記下的位址已經不在該名稱的 addon 清單裡 ⇒ 那扇窗真的收乾淨了。
/// 這條在本外掛可行，是因為按視窗的呼叫端幾乎全部掛在 <c>Svc.Framework.Update</c>
/// 底下、<b>每個 tick 都會再進來一次</b>。
/// </item>
/// <item>
/// <b><see cref="IAddonLifecycle"/> 事件</b>：<see cref="AddonEvent.PreFinalize"/>（這一扇正在被銷毀）
/// 與 <see cref="AddonEvent.PostSetup"/>（有新的一扇被建立起來）。
/// 🔴 這條是<b>必要的</b>而不是錦上添花：同名 addon 關掉再開常常會<b>重用同一塊記憶體位址</b>，
/// 只靠第 1 條的話，重開的那扇會被誤認成「按過的那扇還沒收掉」而白白被擋到逃生口
/// ——<b>幻卡連續對局正是這個形狀</b>（每一局都要重開一次確認框）。
/// 這條也是<b>唯一</b>能罩住 AddonLifecycle 事件驅動呼叫端（<see cref="TriadDialogueSkip"/>
/// 的 Talk <c>PostUpdate</c> 監聽器）的解除點：那種監聽器在 addon 不存在的幀根本不會被叫到。
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，
/// 那會把封鎖提早解除，正好把這道防線變成沒有。
/// </item>
/// </list>
/// 另外，<b>看過 <see cref="AddonEvent.PreFinalize"/> 的那個位址</b>在下一次 PostSetup 之前一律不碰
/// （有幀數上限 <see cref="FinalizedGraceFrames"/>，防「PostSetup 沒來」變成永久鎖）：
/// 這是給「按了窗也不會關」的機台鈕／棋盤／翻格用的形狀——它們不能用「同窗只按一次」，
/// 唯一能加的就是「已經在銷毀的實例不要再碰」。
/// </para>
/// <para>
/// 🔴 <b>逃生口是刻意的</b>（<see cref="ReleaseEscapeFrames"/>）：萬一某扇窗既不 finalize
/// 也不重新 setup（例如上一次的 callback 根本沒生效、視窗就是還開著），
/// 沒有它的話呼叫端會<b>永遠</b>按不下去，等於把崩潰換成靜默失效。
/// 用<b>幀數</b>而不是毫秒：危險窗口的長度本來就是以幀計的，遊戲卡頓時兩者一起拉長。
/// 走逃生口時 <c>viaEscape</c> 會回 <see langword="true"/>——有「按了看沒關就換下一招」
/// 級聯的呼叫端（幻卡報名／結果／選牌組）靠它<b>換到下一個後援</b>，而不是在同一次呼叫裡
/// 對正在關的窗連按（那正是會崩的那一下）。
/// </para>
/// <para>
/// 🔴 <b>Talk 類（按一次翻一頁、窗不會因為被按而消失）用 <see cref="RoutineRePressEscapeFrames"/>（15 幀）</b>：
/// 那種窗整段都不關也不重建，兩條解除點都不會觸發，走逃生口是<b>常態</b>而不是異常——
/// 所以放行 log 寫 Debug 不洗版。關閉中的危險窗口 &lt; 10 幀，15 幀不落在裡面。
/// ⚠️ 刻意<b>不</b>用「文字變了」當翻頁證據：關閉中的窗文字會讀壞（U+FFFD）。
/// </para>
/// <para>
/// 📌 <b>正常路徑行為零變化</b>：第一次看到某扇窗的某個按法一律當場按下去，
/// 被擋下時回 <see langword="false"/>，與「視窗還沒出現」同一個語意
/// （這一幀沒做成、下一幀再試），而所有呼叫端本來就是每幀重試的。
/// </para>
/// <para>⚠️ 只在主執行緒使用（與呼叫端的 <c>EzThrottler</c> 同一個前提）。</para>
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
    /// 🔴🔴 <b>熱窗與逃生口（<see cref="ReleaseEscapeFrames"/>）是兩件不同的事，絕對不能共用同一個數字。</b>
    /// 熱窗要涵蓋的是「這扇窗正在關閉」的那幾幀（實測 &lt; 10 幀）；逃生口要涵蓋的是
    /// 「上一次按下根本沒生效」的判定門檻（90 幀）。
    /// <para>
    /// 🔴 <b>把熱窗也設成 90 會讓後援按法永久餓死</b>（2026-09-02 實際踩過的形狀）：
    /// 終結動作每次走逃生口放行都會<b>重新登記</b>、時間戳歸零，於是熱窗永遠接得上下一個熱窗，
    /// 同一扇窗的其他按法一次都送不出去 —— 呼叫端的多段後援階梯全部靜默空轉，
    /// 每個候選都被標成「試過」卻一下都沒真的按到。要維持的不變式是
    /// <b>本常數必須遠小於「兩次終結動作之間的最小間隔」</b>（後者由 <see cref="ReleaseEscapeFrames"/>
    /// 保證），15 對 90 有六倍餘裕。
    /// </para>
    /// 熱窗過了之後那扇窗若還在，代表它既沒 <c>PreFinalize</c> 也沒從 addon 清單消失
    /// （<see cref="ReleaseVanished"/> 每次登記前都會先掃一遍），也就是「上一次按下沒讓它關」——
    /// 那就不是關閉中，其他按法可以送。
    /// <para>
    /// 數值與 <see cref="RoutineRePressEscapeFrames"/> 相同純屬巧合（同一份「危險窗口 &lt; 10 幀」的判準），
    /// <b>兩者語意不同，不要合併</b>。
    /// </para>
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
    /// 🔴 這一組是<b>必要的</b>，不是保守起見：同一扇窗在本外掛裡會被<b>好幾種機制</b>按到 ——
    /// 確認框走結構按鈕／節點按鈕／<c>AddonMaster</c>／<c>Callback.Fire</c>／<c>FireCallbackInt</c> 五條後援，
    /// 幻卡報名窗（<c>TripleTriadRequest</c>）走 <c>FireCallbackInt(1)</c>／<c>Quit()</c>／<c>Close(true)</c>
    /// 級聯外加另一條路徑的挑戰鈕，幻卡結果窗（<c>TripleTriadResult</c>）走 <c>FireCallbackInt(1)</c>／
    /// <c>Close(true)</c>／再戰 <c>FireCallbackInt(0)</c>。這些按法的參數本來各不相同，
    /// 不併 key 就會出現「兩條路徑接力按同一扇關閉中的窗」。
    /// <para>
    /// 📌 <c>SelectString</c>／<c>SelectIconString</c> 刻意<b>不</b>在此（與 AutoDuty 同一個判斷）：
    /// 巢狀選單常常<b>重用同一個實例</b>只換內容（不觸發 PostSetup），併 key 會讓下一層的選擇被擋到逃生口。
    /// 那兩個改用「選項索引」當按法鍵，本外掛對同一扇選單永遠算出同一個索引，同幀雙按照樣擋得住。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "TripleTriadRequest",
        "TripleTriadResult",
    };

    /// <param name="Address">被按的那個實例的位址，<b>只做等值比較</b>。</param>
    /// <param name="Frame">按下時的繪製幀號。</param>
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
    /// 這一次的「按法」（參數組）。同一扇窗上不同的按法互不干擾；要擋的是<b>同一個按法重複送</b>。
    /// 傳 <see cref="WholeWindowKey"/> 代表終結動作：登記後同一位址的任何按法都不准。
    /// </param>
    /// <param name="escapeFrames">逃生口幀數：單答終結窗用 <see cref="ReleaseEscapeFrames"/>，
    /// Talk 類多次互動窗用 <see cref="RoutineRePressEscapeFrames"/>。</param>
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
        var frame = (long)Svc.PluginInterface.UiBuilder.FrameCount;
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
                    // 🔴 這就是會崩潰的那一幀。單答窗寫 Information（使用者跑 LogLevel 2）；
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
        return true;
    }

    /// <summary>
    /// 給「按了窗<b>不會</b>關」的按法用的輕量閘門（機台鈕、幻卡棋盤出牌、仙人微彩翻格／選線、
    /// 卡片清單點格／切頁、選牌組窗的最後手段隱藏）：<b>不登記按下紀錄</b>、不改任何重試節奏，
    /// 只擋兩種「這扇實例正在走」的狀態——①已經看過它 PreFinalize（下一次 PostSetup 之前）、
    /// ②同一位址在 <paramref name="terminalHotFrames"/> 幀內送過終結動作（確認鈕／<c>close:true</c>
    /// callback／<c>Close(true)</c>）。<b>回 <see langword="false"/> ＝這一幀絕對不能碰。</b>
    /// </summary>
    /// <remarks>
    /// 🔑 這種窗不能套「同窗只按一次」：一局要揮很多刀、一張彩券要翻三格，而且它們不會因為被按而消失，
    /// 「窗走完生命週期」在遊戲收掉之前永遠不會發生。能加的只有「已經在銷毀／已經在關的實例不要再碰」，
    /// 這正是本方法。<paramref name="terminalHotFrames"/> 預設 <see cref="TerminalHotFrames"/>：
    /// 關閉中的危險窗口 &lt; 10 幀，15 幀不落在裡面；而終結動作之後窗若仍在，就不是在關閉中。
    /// </remarks>
    public static bool TryTouch(string addonName, AtkUnitBase* addon, int terminalHotFrames = TerminalHotFrames)
    {
        if (addon == null || string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        EnsureWatching(addonName);

        var address = (nint)addon;
        var frame = (long)Svc.PluginInterface.UiBuilder.FrameCount;

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
    /// 「已經看過這個實例 PreFinalize、還沒看到新的 PostSetup」＝正在銷毀，不碰。
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
        if (Watchers.ContainsKey(addonName))
        {
            return;
        }

        IAddonLifecycle.AddonEventDelegate handler = (type, args) => OnLifecycle(addonName, type, args);

        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }

    private static void OnLifecycle(string addonName, AddonEvent type, AddonArgs args)
    {
        // 銷毀中或剛建好：那扇窗的按下紀錄都不算數了。
        PressedByAddon.Remove(addonName);

        if (type == AddonEvent.PreFinalize)
        {
            // 只記位址，不解參（args.Addon 只是拿來取位址）。
            FinalizedByAddon[addonName] = new FinalizeRecord(
                args.Addon.Address,
                (long)Svc.PluginInterface.UiBuilder.FrameCount);
        }
        else
        {
            FinalizedByAddon.Remove(addonName);
        }
    }
}
