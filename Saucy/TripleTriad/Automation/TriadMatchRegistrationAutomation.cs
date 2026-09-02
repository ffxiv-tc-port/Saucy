using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using Saucy.Framework;
using System;
using static ECommons.GenericHelpers;

namespace Saucy.TripleTriad;

internal static unsafe class TriadMatchRegistrationAutomation
{
    private const int MatchAcceptRetryCooldownFrames = 15;
    private const string DismissThrottleKey = "TriadRequestQuit";

    /// <summary>報名窗的 addon 名稱。也是它在 <see cref="AddonPressGuard"/> 裡的鍵（單答終結窗：
    /// 挑戰鈕／<c>FireCallbackInt(1)</c>／<c>Quit()</c>／<c>Close(true)</c> 全併同一鍵）。</summary>
    private const string RequestAddonName = "TripleTriadRequest";

    private static int framesSinceMatchAcceptAttempt;

    /// <summary>關窗級聯走到第幾招（0 = callback(1)、1 = Quit 鈕、2 = Close(true)），以及那是對哪一個實例。</summary>
    private static int dismissChainStage;

    private static nint dismissChainAddress;

    public static void ResetSession() => framesSinceMatchAcceptAttempt = 0;

    public static void Tick()
    {
        TriadRewardDropTracker.ResetSnapshot();
        TryAccept();
    }

    public static bool TryDismiss()
    {
        if (!TriadLocalClientStructs.TryGetRequest(out var request))
        {
            return true;
        }

        var addon = &request->AtkUnitBase;

        if (!EzThrottler.Throttle(DismissThrottleKey))
        {
            return false;
        }

        // 🔴🔴 原本是同一次呼叫內「callback(1) → 看 IsVisible 沒落 → Quit 鈕 → 沒落 → Close(true)」的級聯，
        // 但關閉中的那幾幀 IsVisible 仍為 true，第二、三下就是打在正在關的窗上（攔不到的存取違規）。
        // 改成：一個守衛窗口只送一招；守衛走逃生口放行（＝窗 90 幀都沒收掉，上一招真的沒生效）
        // 時才輪到下一招。三招的順序與內容都沒變，只是不再擠在同一次呼叫裡。
        // 守衛以報名窗位址為鍵、與 TryAccept 的挑戰鈕共用（單答終結窗併 key）。
        if (!AddonPressGuard.TryBeginPress(
                RequestAddonName, addon, AddonPressGuard.WholeWindowKey, AddonPressGuard.ReleaseEscapeFrames,
                out var viaEscape))
        {
            return !addon->IsVisible;
        }

        var address = (nint)addon;
        dismissChainStage = viaEscape && dismissChainAddress == address ? dismissChainStage + 1 : 0;
        dismissChainAddress = address;

        switch (dismissChainStage % 3)
        {
            case 0:
                try
                {
                    addon->FireCallbackInt(1);
                    addon->Update(0);
                }
                catch (Exception ex)
                {
                    Svc.Log.Verbose(ex, "[TriadMatchRegistrationAutomation] Request FireCallbackInt(1) failed");
                }

                break;
            case 1:
                try
                {
                    new AddonMaster.TripleTriadRequest(addon).Quit();
                    addon->Update(0);
                }
                catch (Exception ex)
                {
                    Svc.Log.Verbose(ex, "[TriadMatchRegistrationAutomation] AddonMaster Quit failed");
                }

                break;
            default:
                try
                {
                    addon->Close(true);
                    addon->Update(0);
                }
                catch (Exception ex)
                {
                    Svc.Log.Verbose(ex, "[TriadMatchRegistrationAutomation] Registration Close(true) failed");
                }

                break;
        }

        return !addon->IsVisible;
    }

    private static void TryAccept()
    {
        try
        {
            if (!TriadLocalClientStructs.TryGetRequest(out var request))
            {
                framesSinceMatchAcceptAttempt = 0;
                return;
            }

            var addon = &request->AtkUnitBase;

            if (framesSinceMatchAcceptAttempt > 0)
            {
                framesSinceMatchAcceptAttempt--;
                return;
            }

            if (!IsAddonReady(addon))
            {
                return;
            }

            var challengeButton = addon->GetComponentButtonById(41);
            if (challengeButton != null && challengeButton->AtkResNode != null &&
                challengeButton->AtkResNode->IsVisible() &&
                // 挑戰鈕按下報名窗就關：與 TryDismiss 共用同一個以位址為鍵的守衛，
                // ResetSession 把幀數計數器歸零、或另一條路徑先按過，都不會再打第二下。
                AddonPressGuard.TryBeginPress(RequestAddonName, addon))
            {
                try
                {
                    challengeButton->ClickAddonButton(addon);
                    addon->Update(0);
                }
                catch (Exception clickEx)
                {
                    Svc.Log.Verbose(clickEx, "[TriadMatchRegistrationAutomation] Challenge button click failed");
                }
            }

            framesSinceMatchAcceptAttempt = MatchAcceptRetryCooldownFrames;

            if (TriadRunSession.PlayUntilAllCardsDropOnce)
            {
                TriadCardFarmSession.EnsureArmed();
                TriadCardFarmSession.SyncDisplay(TriadRunTarget.Resolve());
            }

            TriadRewardDropTracker.SnapshotAtMatchStart();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[TriadMatchRegistrationAutomation] TryAccept failed");
        }
    }
}
