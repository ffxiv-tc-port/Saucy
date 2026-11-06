using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.AirForce;
using System;
using static ECommons.GenericHelpers;
namespace Saucy.Framework.GoldSaucer;

// 本檔契約：只在 framework 執行緒跑。視窗位址只做等值比較、永不跨幀解參，要碰視窗都當幀以名稱重取。
// 只在 GATE 副本（登高跳跳樂三區、Air Force One）裡、對應模組啟用時動作；金碟遊樂園本區的結算視窗不碰。
// 關窗走視窗自己 X 鈕的同一個 callback（-1、close），守衛走逃生口（窗 90 幀都沒收）才改用 Close(true)。

/// <summary>GATE 副本結束後的金碟幣結算視窗：出現一段時間後替你關掉，並記下從出現到離開副本花了多久。</summary>
internal static unsafe class GoldSaucerRewardDismiss
{
    public const string AddonName = "GoldSaucerReward";

    /// <summary>出現後先等這麼久才關：讓 Saucy 讀完 Air Force One 的金碟幣，也讓「一出現就關」的其他插件先動手。</summary>
    private const long CloseDelayMs = 2_000;

    /// <summary>Air Force One 的金碟幣最多等這麼久；讀不到也照關，統計只是少記一場。</summary>
    private const long StatsWaitLimitMs = 10_000;

    private const ushort AirForceTerritory = 832;

    private static nint shownAddon;

    private static long shownAt;

    private static uint shownTerritory;

    private static int closeStage;

    private static long closedAt;

    /// <summary>這一場在副本裡看過結算視窗的時刻；0＝沒看過。離開副本時拿來算等了多久。</summary>
    private static long firstShownAt;

    /// <summary>結算視窗現在開著（GATE 副本裡）。</summary>
    public static bool IsShown => shownAddon != 0;

    /// <summary>這次結算視窗開了幾毫秒；沒開回 0。</summary>
    public static long ShownForMs => shownAddon != 0 ? Environment.TickCount64 - shownAt : 0;

    private static bool IsGateDuty(uint territory, out bool airForce)
    {
        airForce = territory == AirForceTerritory;
        return airForce
            ? C.IsModuleEnabled(ModuleNames.AirForceOne)
            : territory is 792 or 899 or 1098 && C.IsModuleEnabled(ModuleNames.LeapOfFaith);
    }

    /// <summary>每幀由插件主迴圈呼叫。</summary>
    public static void Tick()
    {
        var now = Environment.TickCount64;
        var territory = (uint)Svc.ClientState.TerritoryType;
        if (!IsGateDuty(territory, out var airForce))
        {
            if (firstShownAt != 0)
            {
                var closedNote = closedAt != 0 ? $"，代按關閉後 {(now - closedAt) / 1000.0:0.0} 秒" : "，沒有代按關閉";
                Svc.Log.Information($"[Saucy] 金碟幣結算視窗：已離開副本（區域 {shownTerritory}），結算視窗出現後 {(now - firstShownAt) / 1000.0:0.0} 秒{closedNote}");
            }

            Forget();
            return;
        }

        if (!TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) || !IsAddonReady(addon))
        {
            if (shownAddon != 0)
            {
                Svc.Log.Information($"[Saucy] 金碟幣結算視窗：已關閉（開了 {(now - shownAt) / 1000.0:0.0} 秒），還在副本裡等離開");
                shownAddon = 0;
            }

            return;
        }

        if ((nint)addon != shownAddon)
        {
            shownAddon = (nint)addon;
            shownAt = now;
            shownTerritory = territory;
            closeStage = 0;
            if (firstShownAt == 0)
            {
                firstShownAt = now;
            }

            Svc.Log.Information($"[Saucy] 金碟幣結算視窗：出現（區域 {territory}）；" +
                                (C.GoldSaucerGates.GateRewardWindowAutoClose ? $"{CloseDelayMs / 1000} 秒後替你關閉" : "自動關閉沒開，等遊戲自己結束"));
        }

        if (!C.GoldSaucerGates.GateRewardWindowAutoClose || GateEmergencyHold.IsHeld)
        {
            return;
        }

        var shownFor = now - shownAt;
        if (shownFor < CloseDelayMs || (airForce && AirForceAutomation.ShouldTrackReward && shownFor < StatsWaitLimitMs))
        {
            return;
        }

        if (!AddonPressGuard.TryBeginPress(AddonName, addon, AddonPressGuard.WholeWindowKey, AddonPressGuard.ReleaseEscapeFrames, out var viaEscape))
        {
            return;
        }

        closeStage = viaEscape ? closeStage + 1 : 0;
        closedAt = now;
        Svc.Log.Information($"[Saucy] 金碟幣結算視窗：出現 {shownFor / 1000.0:0.0} 秒後代按關閉（{(closeStage % 2 == 0 ? "視窗自己的關閉 callback" : "Close(true)")}）");
        if (closeStage % 2 == 0)
        {
            Callback.Fire(addon, true, -1);
        }
        else
        {
            addon->Close(true);
        }
    }

    private static void Forget()
    {
        shownAddon = 0;
        shownAt = 0;
        closeStage = 0;
        closedAt = 0;
        firstShownAt = 0;
    }
}
