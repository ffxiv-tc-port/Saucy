using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using static ECommons.GenericHelpers;

namespace Saucy.Framework;

public static unsafe class TalkHelper
{
    /// <summary>對話框的 addon 名稱。也是它在 <see cref="AddonPressGuard"/> 裡的鍵。</summary>
    public const string AddonName = "Talk";

    public static bool IsVisible()
    {
        if (!TryGetAddonByName<AtkUnitBase>("Talk", out var talk))
        {
            return false;
        }

        return talk->IsVisible && IsAddonReady(talk);
    }

    public static bool TryAdvance(string throttleKey = "Saucy.Talk.Advance", int throttleMs = 400)
    {
        if (!TryGetAddonByName<AtkUnitBase>("Talk", out var talk) || !IsAddonReady(talk) || !talk->IsVisible)
        {
            return false;
        }

        if (!EzThrottler.Throttle(throttleKey, throttleMs))
        {
            return false;
        }

        // 🔴🔴 節流 key 是每個呼叫端自訂的（本外掛有六個），不是每扇窗：
        // 兩個 key 在同一幀各自首次放行，就是對同一扇 Talk 連按兩下——最後一頁時第二下打在
        // 正在關的窗上＝攔不到的存取違規。守衛以 Talk 的位址為鍵，六個 key 全部經過同一道。
        // Talk 按一次翻一頁、窗不消失，逃生口用 15 幀（艦隊政策），走逃生口是常態寫 Debug。
        if (!AddonPressGuard.TryBeginPress(
                AddonName, talk, AddonPressGuard.WholeWindowKey, AddonPressGuard.RoutineRePressEscapeFrames))
        {
            return false;
        }

        try
        {
            new AddonMaster.Talk(talk).Click();
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TalkHelper] Talk click failed; trying callback");
        }

        try
        {
            talk->FireCallbackInt(0);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TalkHelper] Talk callback failed");
            return false;
        }
    }
}
