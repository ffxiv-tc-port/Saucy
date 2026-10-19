using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;

namespace Saucy.Framework;

/// <summary>「同一扇視窗的同一個按法按過就不要再按，直到它真的收掉」的共用閘門。</summary>
/// <remarks>
/// 🔴🔴 <b>存在的唯一理由是「按下之後那幾幀又被按第二次」會讓遊戲當場關閉</b>：AVE 在 .NET Core 是 corrupted-state exception，各呼叫端那幾層 <c>try</c>/<c>catch</c> <b>攔不到</b>，<b>唯一的防護是「不要送第二次」</b>。
/// 📌 <b>粒度＝（窗，位址，按法）</b>，同名多扇並存時各自記帳。🔴 全程只做<b>位址等值比較，永遠不解參</b>。<b>例外是「終結動作」</b>（<see cref="WholeWindowKey"/>）：還熱著（未過它自己的逃生口）時，同一位址<b>任何</b>按法都不准。
/// 解除點：位址從同名清單消失（每幀輪詢）、同位址 <see cref="AddonEvent.PostSetup"/>（這一幀才登記的不清）、常駐窗連續隱藏 N 幀、逃生口。<see cref="AddonEvent.PreFinalize"/> 是<b>上鎖點</b>：鎖到位址消失或同位址重建。
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發。
/// 🔴 <b>逃生口是刻意的</b>：沒有它呼叫端會<b>永遠</b>按不下去。⚠️ 只在主執行緒使用（與呼叫端的 <c>EzThrottler</c> 同一個前提）。任何不確定一律回 <see langword="false"/>。
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

    /// <summary>給「按一次翻一頁、窗不會因為被按而消失」的多次互動窗用的短逃生口（15 幀）。</summary>
    /// <remarks>
    /// Talk 是代表；機台的揮擊鈕、幻卡棋盤出牌、仙人微彩翻格也是這個形狀。走這個逃生口是常態，放行 log 寫 Debug。
    /// </remarks>
    public const int RoutineRePressEscapeFrames = 15;

    /// <summary>終結動作登記之後，同一位址其他按法被擋住的「熱窗」長度＝終結動作自己的逃生口。</summary>
    /// <remarks>
    /// 不會把後援按法永久餓死：熱窗與逃生口在同一幀到期，同一幀裡<b>先送後援、後送終結</b>的呼叫端（選牌組流程就是）
    /// 在終結動作重新登記之前就已經送出。代價是熱窗內的後援一律被擋（只會慢，不會崩）。
    /// </remarks>
    public const int TerminalHotFrames = ReleaseEscapeFrames;

    /// <summary>選牌組視窗的短熱窗（刻意例外）：它的後援每 30 幀推進一步，熱窗比這長就等於沒試。</summary>
    public const int SelDeckTerminalHotFrames = 15;

    private const string SelDeckAddonName = "TripleTriadSelDeck";

    /// <summary>
    /// 「終結動作」的按法鍵：按了這扇窗就會走（確認鈕、<c>close:true</c> callback、<c>Close(true)</c>）。
    /// 登記之後，同一位址<b>任何</b>按法在它走完生命週期（或逃生口）之前都不准。
    /// </summary>
    public const string WholeWindowKey = "";

    /// <summary>輪詢時最多掃到第幾個同名實例（遊戲同名實例上限）；掃到第一個空的就提早停。</summary>
    private const int MaxAddonIndex = 256;

    /// <summary>常駐窗按下後，要連續觀察到「還在清單裡、但已隱藏」這麼多幀才解除；看到可見就歸零重數。</summary>
    private const int HiddenReleaseFrames = 20;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗：這些名字底下的按法一律併成 <see cref="WholeWindowKey"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 這一組是<b>必要的</b>：同一扇窗在本外掛裡會被<b>好幾種機制</b>按到，不併 key 就會出現「兩條路徑接力按同一扇關閉中的窗」。
    /// 📌 <c>SelectString</c>／<c>SelectIconString</c> 選了就關：同一實例被重用成下一層選單時，下一層要等逃生口（只會慢，不會崩）。
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "SelectString",
        "SelectIconString",
        "TripleTriadRequest",
        "TripleTriadResult",
    };

    /// <summary>實機證實的常駐窗：關閉只是隱藏，位址整場留在清單裡，生命週期事件不再來。</summary>
    /// <remarks>這份名單同時是「送出前必須可見」的名單（與「連續隱藏才解除」互為反面）。</remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextIconMenu",
        "ContextMenu",
        "Talk",
    };

    /// <summary>一次按下的紀錄。<see cref="Frame"/> 是<b>守衛幀號</b>（<see cref="frameCount"/>），不是繪製幀。</summary>
    private sealed class PressRecord(long frame, int escapeFrames)
    {
        public long Frame { get; } = frame;

        /// <summary>登記當時的逃生口；終結動作的熱窗也用它（多次互動窗的終結鍵因此只熱 15 幀）。</summary>
        public int EscapeFrames { get; } = escapeFrames;

        /// <summary>常駐窗自這次按下起連續隱藏的幀數。</summary>
        public int HiddenFrames { get; set; }
    }

    /// <summary>一個按法底下「已經按過的位址 → 按下紀錄」。同名窗可能同時開好幾扇，所以是集合不是單一格。</summary>
    private sealed class AddressSlot
    {
        public readonly Dictionary<nint, PressRecord> Pressed = [];
    }

    /// <summary>addon 名稱 → 按法 → 位址集合。</summary>
    private static readonly Dictionary<string, Dictionary<string, AddressSlot>> PressedByAddon =
        new(StringComparer.Ordinal);

    /// <summary>addon 名稱 → 看過 PreFinalize、還沒消失也還沒在同位址重建的實例位址。</summary>
    private static readonly Dictionary<string, HashSet<nint>> FinalizedByAddon = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers =
        new(StringComparer.Ordinal);

    // 可重用緩衝：只在框架執行緒上用，沒有紀錄時輪詢不配置任何東西。
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly HashSet<nint> HiddenBuf = [];
    private static readonly List<nint> AddrBuf = [];
    private static readonly List<string> KeyBuf = [];
    private static readonly List<string> NameBuf = [];

    /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information。</summary>
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    /// <summary>守衛自己的幀計數器：<c>Svc.Framework.Update</c> 每個遊戲 tick 加一。</summary>
    /// <remarks>
    /// 🔴🔴 <b>刻意<u>不</u>用 <c>Svc.PluginInterface.UiBuilder.FrameCount</c></b>：過場或隱藏 UI 期間它完全不前進，
    /// 逃生口與熱窗永不到期。<c>Svc.Framework.Update</c> 掛在遊戲自己的 update 迴圈上，與畫不畫 ImGui 無關。
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

    /// <summary>登記「即將對這扇視窗送出這一種按法」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b></summary>
    /// <param name="addonName">視窗名稱（解除封鎖的監聽器與輪詢都以它為準）。空的一律不放行。</param>
    /// <param name="addon">目標實例。<b>只當作識別用的位址，本方法不解參。</b></param>
    /// <param name="pressKey">這一次的「按法」（參數組）。傳 <see cref="WholeWindowKey"/> 代表終結動作：還熱著時同一位址的任何按法都不准。</param>
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

        ReleaseVanished();
        EnsureWatching(addonName);

        var address = (nint)addon;
        var frame = frameCount;
        var routine = escapeFrames <= RoutineRePressEscapeFrames;
        var label = string.IsNullOrEmpty(pressKey) ? "終結動作" : $"按法「{pressKey}」";

        // 常駐窗的解除條件是「連續隱藏」，這裡是它的反面：隱藏中（或這一幀查不到）的常駐窗一律不送。
        if (PersistentAddons.Contains(addonName) && !IsListedAndVisible(addonName, address))
        {
            return false;
        }

        if (IsRetiringInstance(addonName, address))
        {
            return false;
        }

        PressedByAddon.TryGetValue(addonName, out var presses);
        if (presses != null)
        {
            // 終結動作按過而且還熱著：同一扇窗的其他按法一律不准——那之後它就是在關閉中。
            if (pressKey != WholeWindowKey && IsTerminalHot(addonName, presses, address, frame))
            {
                if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                {
                    Svc.Log.Information(
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）終結動作按過之後還沒觀察到它收掉，" +
                        $"這一幀不再送{label} —— 對關閉中的視窗送輸入是攔不到的存取違規。");
                }

                return false;
            }

            if (presses.TryGetValue(pressKey, out var byAddress) && byAddress.Pressed.TryGetValue(address, out var pressed))
            {
                var waited = frame - pressed.Frame;
                if (waited < escapeFrames)
                {
                    // 🔴 這就是會崩潰的那一幀。單答窗寫 Information（要使用者回報的診斷）；Talk 類寫 Debug 免得洗版。
                    if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                    {
                        LogAt(routine,
                            $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，{label}）按過之後還沒觀察到它收掉，" +
                            "這一幀不再送 —— 對關閉中的視窗送輸入是攔不到的存取違規。");
                    }

                    return false;
                }

                viaEscape = true;
                if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
                {
                    LogAt(routine,
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，{label}）按下後 {waited} 幀" +
                        "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
                }
            }
        }

        if (presses == null)
        {
            presses = new Dictionary<string, AddressSlot>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }

        if (!presses.TryGetValue(pressKey, out var slot))
        {
            slot = new AddressSlot();
            presses[pressKey] = slot;
        }

        slot.Pressed[address] = new PressRecord(frame, escapeFrames);
        // 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
        Svc.Log.Verbose($"[按窗診斷] plugin=Saucy addon={addonName} addr=0x{address:X} key={pressKey}");
        return true;
    }

    /// <summary>
    /// 給「按了窗<b>不會</b>關」的按法用的輕量閘門（機台鈕、幻卡棋盤出牌、仙人微彩翻格／選線、卡片清單點格／切頁、選牌組窗的最後手段隱藏）：<b>不登記按下紀錄</b>、不改任何重試節奏，只擋兩種「這扇實例正在走」的狀態——①已經看過它 PreFinalize（消失或重建之前）、②同一位址的終結動作還熱著。<b>回 <see langword="false"/> ＝這一幀絕對不能碰。</b>
    /// </summary>
    /// <remarks>
    /// 🔑 這種窗不能套「同窗只按一次」：一局要揮很多刀、一張彩券要翻三格，而且它們不會因為被按而消失。能加的只有「已經在銷毀／已經在關的實例不要再碰」，這正是本方法。
    /// </remarks>
    public static bool TryTouch(string addonName, AtkUnitBase* addon)
    {
        if (addon == null || string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        EnsureWatching(addonName);

        var address = (nint)addon;
        var frame = frameCount;

        if (IsRetiringInstance(addonName, address))
        {
            return false;
        }

        if (PressedByAddon.TryGetValue(addonName, out var presses) && IsTerminalHot(addonName, presses, address, frame))
        {
            if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）終結動作還在熱窗內，" +
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

    private static void LogAt(bool routine, string message)
    {
        if (routine)
        {
            Svc.Log.Debug(message);
        }
        else
        {
            Svc.Log.Information(message);
        }
    }

    /// <summary>同一位址的終結動作是否還熱著：一般是它自己的逃生口，選牌組視窗另取 <see cref="SelDeckTerminalHotFrames"/>。</summary>
    private static bool IsTerminalHot(string addonName, Dictionary<string, AddressSlot> presses, nint address, long frame)
    {
        if (!presses.TryGetValue(WholeWindowKey, out var whole) || !whole.Pressed.TryGetValue(address, out var rec))
        {
            return false;
        }

        var hot = addonName == SelDeckAddonName ? Math.Min(rec.EscapeFrames, SelDeckTerminalHotFrames) : rec.EscapeFrames;
        return frame - rec.Frame < hot;
    }

    /// <summary>「已經看過這個實例 PreFinalize、它還沒消失、同位址也還沒重建」＝正在銷毀，不碰。</summary>
    /// <remarks>鎖沒有逃生口：解除只靠「從同名清單消失」與「同位址 PostSetup」，兩者都不會讓鎖永遠卡住。</remarks>
    private static bool IsRetiringInstance(string addonName, nint address)
    {
        if (!FinalizedByAddon.TryGetValue(addonName, out var finalized) || !finalized.Contains(address))
        {
            return false;
        }

        if (EzThrottler.Throttle($"AddonPressGuard-Finalized-{addonName}", 1000))
        {
            Svc.Log.Information(
                $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）已經看到它 PreFinalize，" +
                "還沒看到它消失或重建，這一幀不碰它。");
        }

        return true;
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
    /// 清掉「被記下的那個實例已經不在同名 addon 清單裡」的紀錄與 PreFinalize 鎖；常駐窗另數連續隱藏幀。
    /// </summary>
    /// <remarks>
    /// 🔴 只做位址等值比較，永遠不解參（可見性讀的是 <c>GetAddonByName</c> 這一幀交回來的指標）。
    /// ⚠️ 判準刻意<b>不</b>用「視窗看起來還 ready 嗎」：關閉中的那幾幀三關全過，拿那個當「窗不見了」會在最危險的那幾幀把封鎖解除掉。
    /// 查詢不成立（同名實例超過掃描上限）時不解除。
    /// </remarks>
    private static void ReleaseVanished(bool countHidden = false)
    {
        if (PressedByAddon.Count == 0 && FinalizedByAddon.Count == 0)
        {
            return;
        }

        NameBuf.Clear();
        foreach (var name in PressedByAddon.Keys)
        {
            NameBuf.Add(name);
        }

        foreach (var name in FinalizedByAddon.Keys)
        {
            if (!PressedByAddon.ContainsKey(name))
            {
                NameBuf.Add(name);
            }
        }

        foreach (var name in NameBuf)
        {
            var persistent = PersistentAddons.Contains(name);
            if (!ScanInstances(name, persistent))
            {
                continue;
            }

            if (FinalizedByAddon.TryGetValue(name, out var finalized))
            {
                finalized.RemoveWhere(a => !PresentBuf.Contains(a));
                if (finalized.Count == 0)
                {
                    FinalizedByAddon.Remove(name);
                }
            }

            if (PressedByAddon.TryGetValue(name, out var presses))
            {
                ReleaseInPresses(name, presses, persistent && countHidden);
            }
        }
    }

    private static void ReleaseInPresses(string name, Dictionary<string, AddressSlot> presses, bool countHidden)
    {
        KeyBuf.Clear();
        foreach (var (pressKey, slot) in presses)
        {
            AddrBuf.Clear();
            foreach (var (address, rec) in slot.Pressed)
            {
                if (!PresentBuf.Contains(address))
                {
                    AddrBuf.Add(address);
                    continue;
                }

                if (!countHidden)
                {
                    continue;
                }

                // 只有「在清單裡而且隱藏」才累加，看得見就歸零：關閉中那幾幀窗還看得見，永遠累不滿。
                rec.HiddenFrames = HiddenBuf.Contains(address) ? rec.HiddenFrames + 1 : 0;
                if (rec.HiddenFrames >= HiddenReleaseFrames)
                {
                    AddrBuf.Add(address);
                    if (HiddenReleaseReported.Add(name))
                    {
                        Svc.Log.Information(
                            $"[AddonPressGuard] 「{name}」是常駐視窗（隱藏而不銷毀），按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。" +
                            "這一行每個視窗名每次遊戲只寫一次。");
                    }
                }
            }

            foreach (var address in AddrBuf)
            {
                slot.Pressed.Remove(address);
            }

            if (slot.Pressed.Count == 0)
            {
                KeyBuf.Add(pressKey);
            }
        }

        foreach (var pressKey in KeyBuf)
        {
            presses.Remove(pressKey);
        }

        if (presses.Count == 0)
        {
            PressedByAddon.Remove(name);
        }
    }

    /// <summary>把這個名字底下這一幀的實例位址收進 <see cref="PresentBuf"/>（常駐窗另收隱藏中的進 <see cref="HiddenBuf"/>）。</summary>
    /// <returns><see langword="false"/> ＝掃到上限還沒掃完，查詢不成立，這一輪不解除。</returns>
    private static bool ScanInstances(string addonName, bool readVisibility)
    {
        PresentBuf.Clear();
        HiddenBuf.Clear();
        for (var i = 1; i <= MaxAddonIndex; i++)
        {
            var unit = Svc.GameGui.GetAddonByName(addonName, i);
            if (unit.IsNull)
            {
                return true;
            }

            PresentBuf.Add(unit.Address);
            if (readVisibility && !unit.IsVisible)
            {
                HiddenBuf.Add(unit.Address);
            }
        }

        return false;
    }

    /// <summary>在同名清單裡找到 <paramref name="address"/>，而且它這一幀看得見；查不到一律回 <see langword="false"/>。</summary>
    private static bool IsListedAndVisible(string addonName, nint address)
    {
        for (var i = 1; i <= MaxAddonIndex; i++)
        {
            var unit = Svc.GameGui.GetAddonByName(addonName, i);
            if (unit.IsNull)
            {
                return false;
            }

            if (unit.Address == address)
            {
                return unit.IsVisible;
            }
        }

        return false;
    }

    /// <summary>
    /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）：動態掛／拆比較容易留下懸空的監聽器。
    /// </remarks>
    private static void EnsureWatching(string addonName)
    {
        // 🔴 時鐘要在下面那個 early return 之前掛上：第二個以後的名字進來時才不會漏掛。
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
    /// 📌 <b>外掛啟動時就先呼叫一次</b>（<c>Saucy</c> 建構子裡、其他 <c>Framework.Update</c> 訂閱之前）：
    /// 同一外掛的處理常式共用一個 try/catch，排在前面的擲例外時，後面的那一個 tick 全部不會被呼叫。
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

    /// <summary>守衛的時鐘：每個遊戲 tick 加一，之後輪詢解除。</summary>
    /// <remarks>🔴 遞增必須是第一行、前面<b>不可以</b>有任何條件：計數器一停，逃生口的幀差就失真。</remarks>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        frameCount++;
        ReleaseVanished(countHidden: true);
    }

    /// <summary>
    /// 生命週期監聽器。<b>🔴 一切都按「這一個實例位址」處理，不按名稱整包清。</b>
    /// </summary>
    /// <remarks>
    /// 📌 <c>PreFinalize</c> <b>刻意不清</b>按下紀錄、只上鎖：那扇窗正在被銷毀，這時候「忘記它被按過」沒有任何好處。
    /// 📌 <c>PostSetup</c>＝這個位址上是全新的一扇窗：舊紀錄與鎖一起過期，但<b>這一幀才登記的不清</b>
    /// （監聽器之間的呼叫順序不保證，在 PostSetup 處理常式裡按下的不能被守衛當場清掉）。
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
            LockFinalizing(addonName, address);
            return;
        }

        ForgetPresses(addonName, address);

        if (FinalizedByAddon.TryGetValue(addonName, out var finalized) && finalized.Remove(address) && finalized.Count == 0)
        {
            FinalizedByAddon.Remove(addonName);
        }
    }

    /// <summary>這扇窗進入銷毀流程：鎖到它從同名清單消失或同位址重建。</summary>
    private static void LockFinalizing(string addonName, nint address)
    {
        if (!FinalizedByAddon.TryGetValue(addonName, out var set))
        {
            set = [];
            FinalizedByAddon[addonName] = set;
        }

        set.Add(address);
    }

    /// <summary>只清掉<b>這一個實例位址</b>、而且不是這一幀才登記的按下紀錄，同名的其他實例不受影響。</summary>
    private static void ForgetPresses(string addonName, nint address)
    {
        if (!PressedByAddon.TryGetValue(addonName, out var presses))
        {
            return;
        }

        KeyBuf.Clear();
        foreach (var (pressKey, slot) in presses)
        {
            if (slot.Pressed.TryGetValue(address, out var rec) && rec.Frame != frameCount)
            {
                slot.Pressed.Remove(address);
            }

            if (slot.Pressed.Count == 0)
            {
                KeyBuf.Add(pressKey);
            }
        }

        foreach (var pressKey in KeyBuf)
        {
            presses.Remove(pressKey);
        }

        if (presses.Count == 0)
        {
            PressedByAddon.Remove(addonName);
        }
    }
}
