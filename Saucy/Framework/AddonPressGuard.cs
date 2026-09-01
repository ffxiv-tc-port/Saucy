using Dalamud.Game.Addon.Lifecycle;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Saucy.Framework;

/// <summary>
/// 「同一扇視窗按過就不要再按，直到它真的收掉」的共用閘門。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>存在的唯一理由是「按下之後那幾幀又被按第二次」會讓遊戲當場關閉</b>：
/// <c>SelectYesno</c> 這類確認框被按下之後有<b>「正在關閉中」的幾幀</b>，這段期間
/// <c>GetAddonByName</c> 仍然回得到實例、<c>IsVisible</c> 與
/// <c>UldManager.LoadedState == Loaded</c> 也<b>三關全過</b>——也就是說
/// <see cref="SelectYesnoHelper.TryGetVisible"/> 用的 <c>IsAddonReady</c>
/// <b>擋不住這個窗口</b>。此時再對它送 callback／模擬點擊就是原生 AccessViolation
/// （<c>C0000005</c>）。AVE 在 .NET Core 是 corrupted-state exception，
/// <see cref="SelectYesnoHelper"/> 裡那幾層 <c>try</c>/<c>catch</c> <b>攔不到</b>，
/// 遊戲當場關閉 ——<b>唯一的防護是「不要送第二次」，不是「送了再接住」</b>。
/// <para>
/// ⚠️ 呼叫端原有的 <c>EzThrottler</c> <b>不是</b>防護：它記的是「上一次動作在哪個時刻」
/// 而不是「這扇窗已經按過」，而且<b>首次一律放行</b>、key 是全域持久的。
/// 幻卡那條每幀鏈（<c>Saucy.Tick</c> → <see cref="TriadDialogueSkip"/>）
/// 連節流都沒有，按下之後下一幀就會再按一次。
/// </para>
/// <para>
/// 🔑 <b>做法</b>：按下之前先登記「這個名字底下的哪一個實例位址被按過」，
/// 在觀察到那扇窗真的走完生命週期之前不准再按同一個位址。
/// 🔴 全程只做<b>位址等值比較，永遠不解參</b>——被記下的那個位址隨時可能已經失效。
/// </para>
/// <para>
/// <b>解除封鎖有兩條互補的觀察點</b>（兩條都只會讓封鎖<b>提早</b>解除，不會延後）：
/// <list type="number">
/// <item>
/// <b>輪詢</b>：被記下的位址已經不在該名稱的 addon 清單裡 ⇒ 那扇窗真的收乾淨了。
/// 這條在本外掛可行，是因為按確認框的呼叫端全部掛在 <c>Svc.Framework.Update</c>
/// （<c>Saucy.RunBot</c>）底下、<b>每個 tick 都會再進來一次</b>。
/// </item>
/// <item>
/// <b><see cref="IAddonLifecycle"/> 事件</b>：<see cref="AddonEvent.PreFinalize"/>（這一扇正在被銷毀）
/// 與 <see cref="AddonEvent.PostSetup"/>（有新的一扇被建立起來）。
/// 🔴 這條是<b>必要的</b>而不是錦上添花：同名 addon 關掉再開常常會<b>重用同一塊記憶體位址</b>，
/// 只靠第 1 條的話，重開的那扇會被誤認成「按過的那扇還沒收掉」而白白被擋到逃生口
/// ——<b>幻卡連續對局正是這個形狀</b>（每一局都要重開一次確認框）。
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，
/// 那會把封鎖提早解除，正好把這道防線變成沒有。
/// </item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>逃生口是刻意的</b>（<see cref="ReleaseEscapeFrames"/>）：萬一某扇窗既不 finalize
/// 也不重新 setup（例如上一次的 callback 根本沒生效、視窗就是還開著），
/// 沒有它的話呼叫端會<b>永遠</b>按不下去，等於把崩潰換成靜默失效。
/// 用<b>幀數</b>而不是毫秒：危險窗口的長度本來就是以幀計的，遊戲卡頓時兩者一起拉長。
/// </para>
/// <para>
/// 📌 <b>正常路徑行為零變化</b>：第一次看到某扇窗一律當場按下去，
/// <see cref="SelectYesnoHelper.PressYes"/>／<see cref="SelectYesnoHelper.PressNo"/>
/// 的回傳值也與改動前逐一相同；被擋下時回 <see langword="false"/>，
/// 與「確認框還沒出現」同一個語意（這一幀沒做成、下一幀再試），
/// 而所有呼叫端本來就是每幀重試的。
/// </para>
/// <para>
/// ⚠️ <b>已知範圍限制</b>：每個 addon 名稱只記<b>一筆</b>按下紀錄（與 TCToolbox／ChilledLeves
/// 兩份已出貨的同名元件一致）。同一個名字底下<b>同時</b>有兩扇窗、而且呼叫端在兩扇之間來回按時，
/// 後按的那筆會蓋掉前一筆。實務上碰不到：<see cref="SelectYesnoHelper.TryGetVisible"/>
/// 是照索引由小到大取<b>第一扇可見的</b>，而索引順序即建立順序 ——
/// 關閉中的那扇（本元件要防的就是它）索引一定比新開的那扇小，所以會被優先取到、被擋住，
/// 紀錄不會被蓋掉。
/// </para>
/// <para>⚠️ 只在主執行緒使用（與呼叫端的 <c>EzThrottler</c> 同一個前提）。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 已經按過、那扇窗卻既沒消失也沒重建時，最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗只按一次」，這個值只是防死鎖的逃生口。
    /// 90 幀（60fps 下約 1.5 秒）遠遠大於「關閉中的那幾幀」，補按永遠不會落在危險窗口內。
    /// </remarks>
    private const int ReleaseEscapeFrames = 90;

    /// <summary>輪詢解除時最多掃到第幾個同名實例。</summary>
    /// <remarks>同名視窗同時開著超過這個數量在實務上不存在；掃到第一個空的就提早停。</remarks>
    private const int MaxAddonIndex = 32;

    private readonly record struct PressRecord(nint Address, long Frame);

    private static readonly Dictionary<string, PressRecord> PressedByAddon = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 登記「即將對這扇視窗送出 callback」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b>——這支一回 <see langword="true"/> 就已經把
    /// 「按過了」記下去，登記完卻不按的話會白白封鎖到逃生口為止。
    /// </remarks>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon)
    {
        if (addon == null || string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        // 先把「那扇窗已經從 addon 清單消失」的紀錄清掉（含其他名字的），
        // 下一扇同名窗才會被當成全新的窗處理。
        ReleaseVanished();
        EnsureWatching(addonName);

        var address = (nint)addon;
        var frame = (long)Svc.PluginInterface.UiBuilder.FrameCount;

        if (PressedByAddon.TryGetValue(addonName, out var pressed) && pressed.Address == address)
        {
            var waited = frame - pressed.Frame;
            if (waited < ReleaseEscapeFrames)
            {
                // 🔴 這就是會崩潰的那一幀。診斷寫 Information（使用者跑 LogLevel 2），並節流免得洗版。
                if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                {
                    Svc.Log.Information(
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按過之後還沒觀察到它收掉，" +
                        "這一幀不再送 callback —— 對關閉中的視窗送 callback 是攔不到的存取違規。");
                }

                return false;
            }

            if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按下後 {waited} 幀" +
                    "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
            }
        }

        PressedByAddon[addonName] = new PressRecord(address, frame);
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

        Watchers.Clear();
        PressedByAddon.Clear();
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

        // 先抄一份鍵：字典在迭代途中不能移除。同時存在的紀錄實務上是 0~2 個，這份複製可忽略，
        // 而且只有在真的有按下紀錄時才會走到這裡。
        foreach (var addonName in PressedByAddon.Keys.ToArray())
        {
            if (PressedByAddon.TryGetValue(addonName, out var pressed) &&
                !IsStillPresent(addonName, pressed.Address))
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

        IAddonLifecycle.AddonEventDelegate handler = (_, _) => PressedByAddon.Remove(addonName);

        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }
}
