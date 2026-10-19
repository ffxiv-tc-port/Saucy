using Saucy.AirForce;
using Saucy.OtherGames;
using System;
using System.Collections.Generic;
using System.Linq;
namespace Saucy.Framework;

/// <summary>急停後「只在執行期」壓住目前（或即將開始）的那一個 GATE 時段：定時走去活動解說員／GATE 報名、依設定啟用的 GATE 模組都不自動重新開始。</summary>
/// <remarks>
/// 本檔契約：只在 framework 執行緒呼叫；不改、不存設定，插件重新載入就清掉。
/// 時段＝以 GATE 開始時刻 T（:00／:20／:40）為中心的 [T−10 分, T+10 分)，邊界在 :10／:30／:50（本機時間，與 GATE 排程同一個時鐘）；
/// 涵蓋 T−5 的活動解說員、T 的報名與 GATE 本身。時段結束時人還在 GATE 場上就壓到離場為止。
/// 手動開始（GATE 的手動移動／報名／互動按鈕、啟用 GATE 模組）＝解除。
/// </remarks>
internal static class GateEmergencyHold
{
    private static DateTime? holdUntil;

    private static bool skipLogged;

    // 被暫停的 GATE 模組：只呼叫 Disable()／Enable()，IsEnabled 與設定都不動。
    private static readonly List<Module> paused = [];

    public static bool IsHeld => holdUntil != null;

    /// <summary>開始（或延長）壓住 <paramref name="now"/> 所在的時段；回傳時段結束時刻。</summary>
    public static DateTime Begin(DateTime now)
    {
        var end = SlotEnd(now);
        if (holdUntil is not { } current || end > current)
        {
            holdUntil = end;
            skipLogged = false;
        }

        if (GateDirector.InSaucer)
        {
            PauseGateModules();
        }

        return holdUntil.Value;
    }

    /// <summary><paramref name="now"/> 所在時段的結束時刻（下一個 :10／:30／:50）。</summary>
    internal static DateTime SlotEnd(DateTime now)
    {
        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
        var n = (int)Math.Floor(((now - hourStart).TotalMinutes - 10) / 20) + 1;
        return hourStart.AddMinutes(10 + (20 * n));
    }

    /// <summary>每幀呼叫：到期（且不在 GATE 場上）就解除；在金碟遊樂場裡就暫停已啟用的 GATE 模組。</summary>
    public static void Tick()
    {
        if (holdUntil is not { } until)
        {
            return;
        }

        if (DateTime.Now >= until && !GateDirector.IsPlayerOnStage())
        {
            Release("時段結束");
            return;
        }

        if (GateDirector.InSaucer)
        {
            var names = PauseGateModules();
            if (names.Count > 0)
            {
                NoteSkipped("依設定啟用的 GATE 模組（" + string.Join("、", names) + "）");
            }
        }
    }

    /// <summary>壓住期間略過了某個定時流程：同一個時段只寫一行。</summary>
    public static void NoteSkipped(string what)
    {
        if (skipLogged || holdUntil is not { } until)
        {
            return;
        }

        skipLogged = true;
        Svc.Log.Information($"[Saucy] 因急停壓住本時段（到 {until:HH:mm}）：略過{what}。手動按開始即可解除。");
    }

    /// <summary>解除並恢復被暫停的模組；沒有壓住時什麼都不做。</summary>
    public static void Release(string reason)
    {
        if (holdUntil == null)
        {
            return;
        }

        holdUntil = null;
        var resumed = new List<string>();
        foreach (var m in paused)
        {
            // 壓住期間使用者自己關掉的模組不恢復。
            if (!m.IsEnabled)
            {
                continue;
            }

            try
            {
                m.Enable();
                resumed.Add(m.Name);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[Saucy] 恢復 GATE 模組 {m.InternalName} 失敗");
            }
        }

        paused.Clear();
        var tail = resumed.Count > 0 ? "，恢復 " + string.Join("、", resumed) : "";
        Svc.Log.Information($"[Saucy] 解除急停對 GATE 時段的壓制（{reason}）{tail}。");
    }

    /// <summary>設定裡有模組要從關變開：視為手動開始，先解除（要在 EnableInternal 之前，避免同一模組訂閱兩次）。</summary>
    public static void OnModulesChanging()
    {
        if (holdUntil != null && global::Saucy.Saucy.ModuleManager.Modules.Any(m => C.EnabledModules.Contains(m.InternalName) && !m.IsEnabled))
        {
            Release("手動啟用模組");
        }
    }

    private static List<string> PauseGateModules()
    {
        var names = new List<string>();
        foreach (var m in global::Saucy.Saucy.ModuleManager.Modules)
        {
            if (!IsGateModule(m) || !m.IsEnabled || paused.Contains(m))
            {
                continue;
            }

            try
            {
                m.Disable();
                paused.Add(m);
                names.Add(m.Name);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[Saucy] 暫停 GATE 模組 {m.InternalName} 失敗");
            }
        }

        return names;
    }

    private static bool IsGateModule(Module m) =>
        m is global::Saucy.Cliffhanger.Cliffhanger or AnyWayTheWindBlows or SliceIsRight or global::Saucy.LeapOfFaith.LeapOfFaith or AirForceOne;
}
