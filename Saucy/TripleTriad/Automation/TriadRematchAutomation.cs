using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using System;
namespace Saucy.TripleTriad;

internal static unsafe class TriadRematchAutomation
{
    private const int ResultOutcomeFallbackFrames = 120;
    private const int RematchRetryCooldownFrames = 15;

    /// <summary>結果窗的 addon 名稱。也是它在 <see cref="AddonPressGuard"/> 裡的鍵（單答終結窗：
    /// 關閉 <c>FireCallbackInt(1)</c>／<c>Close(true)</c>／再戰 <c>FireCallbackInt(0)</c> 全併同一鍵）。</summary>
    private const string ResultAddonName = "TripleTriadResult";

    private static int framesSinceRematchAttempt;

    /// <summary>關窗級聯走到第幾招（0 = callback(1)、1 = Close(true)），以及那是對哪一個實例。</summary>
    private static int dismissChainStage;

    private static nint dismissChainAddress;
    private static bool sessionEndDismissRequested;
    private static int framesSinceSessionEndDismiss;
    private static nint lastRecordedResultAddonPtr;
    private static int framesWaitingForResultOutcome;

    public static bool RematchPending { get; private set; }

    public static bool PendingRegistrationDismiss { get; private set; }

    public static void ClearPendingRegistrationDismiss() => PendingRegistrationDismiss = false;

    public static void CancelSessionEndDismissRequest() => sessionEndDismissRequested = false;

    public static void ResetSessionFlags()
    {
        RematchPending = false;
        sessionEndDismissRequested = false;
        PendingRegistrationDismiss = false;
        framesSinceSessionEndDismiss = 0;
        framesSinceRematchAttempt = 0;
        framesWaitingForResultOutcome = 0;
        lastRecordedResultAddonPtr = nint.Zero;
    }

    public static void RequestRematch()
    {
        RematchPending = true;
        sessionEndDismissRequested = false;
    }

    public static void ClearRematchPending()
    {
        RematchPending = false;
        framesSinceRematchAttempt = 0;
    }

    public static void RequestSessionEndDismiss()
    {
        ClearRematchPending();
        sessionEndDismissRequested = true;
        framesSinceSessionEndDismiss = 0;
        PendingRegistrationDismiss = true;
    }

    public static void ResetResultMatchRecording() => lastRecordedResultAddonPtr = nint.Zero;

    internal static bool IsResultMatchRecorded(nint resultAddonPtr) =>
        resultAddonPtr != nint.Zero && resultAddonPtr == lastRecordedResultAddonPtr;

    internal static bool IsResultReady(AtkUnitBase* addon) => addon != null && addon->IsReady;

    public static void RecordMatchResultIfNeeded(nint resultAddonPtr = default, bool requireActionButtons = false)
    {
        if (!TriadRunSession.ModuleEnabled)
        {
            return;
        }

        if (resultAddonPtr == nint.Zero)
        {
            if (!TriadLocalClientStructs.TryGetResult(out var liveResult))
            {
                return;
            }

            resultAddonPtr = (nint)liveResult;
        }

        if (TriadCardFarmSession.IsModeActive() && TriadCardFarmSession.IsDropVerificationPending())
        {
            return;
        }

        if (resultAddonPtr == lastRecordedResultAddonPtr)
        {
            return;
        }

        var resultAddon = (AtkUnitBase*)resultAddonPtr;
        if (!resultAddon->IsVisible)
        {
            return;
        }

        if (requireActionButtons && !IsResultReady(resultAddon))
        {
            return;
        }

        lastRecordedResultAddonPtr = resultAddonPtr;
        TriadCardFarmSession.EnsureArmed();

        if (TriadCardFarmSession.IsModeActive())
        {
            sessionEndDismissRequested = false;
            if (!TriadCardFarmSession.IsComplete())
            {
                RequestRematch();
            }
            else
            {
                TriadCardFarmSession.DeactivateSession();
                RequestSessionEndDismiss();
                Svc.Framework.Run(TryDismissResultIfSessionEnded);
            }

            return;
        }

        if (TriadRunSession.PlayUntilCardDrops && TriadRunSession.PlayUntilAnyCardDropped)
        {
            TriadCardFarmSession.DeactivateSession();
            RequestSessionEndDismiss();
            Svc.Framework.Run(TryDismissResultIfSessionEnded);
            return;
        }

        if (TriadRunSession.PlayXTimes && !TriadRunSession.PlayUntilAllCardsDropOnce && !TriadRunSession.PlayUntilCardDrops)
        {
            TriadRunSession.MatchesCompletedThisSession++;
            if (TriadRunSession.NumberOfTimes > 0)
            {
                TriadRunSession.NumberOfTimes--;
            }
        }

        if (TriadRunSession.ShouldContinue())
        {
            RequestRematch();
        }
        else
        {
            RequestSessionEndDismiss();
            Svc.Framework.Run(TryDismissResultIfSessionEnded);
        }
    }

    public static void TryDismissResultIfSessionEnded()
    {
        if (TriadRunSession.ShouldContinue() || !TriadUiState.IsResultVisible())
        {
            return;
        }

        if (!TriadLocalClientStructs.TryGetResult(out var resultAddon, false))
        {
            return;
        }

        TryDismissTriadResult(&resultAddon->AtkUnitBase);
    }

    public static bool Tick()
    {
        if (!TriadUiState.IsResultVisible())
        {
            ResetResultMatchRecording();
            framesWaitingForResultOutcome = 0;
            sessionEndDismissRequested = false;
            return false;
        }

        if (!TriadLocalClientStructs.TryGetResult(out var resultAddon, false))
        {
            return false;
        }

        var addon = &resultAddon->AtkUnitBase;

        TriadCardFarmSession.EnsureArmed();

        if (IsResultMatchRecorded((nint)addon))
        {
            framesWaitingForResultOutcome = 0;
        }
        else if (++framesWaitingForResultOutcome >= ResultOutcomeFallbackFrames)
        {
            uiReaderMatchResults.ForceNotifyFromFallback((nint)addon);
            framesWaitingForResultOutcome = 0;
        }

        if (TriadCardFarmSession.IsModeActive() && TriadCardFarmSession.HasPendingDrops())
        {
            if (TriadCardFarmSession.IsDropVerificationPending())
            {
                return true;
            }

            sessionEndDismissRequested = false;
            if (!RematchPending && IsResultMatchRecorded((nint)addon))
            {
                RequestRematch();
            }
        }

        // Wait for the results reader to publish stats (MGP/card rewards populate a
        // moment after the addon reports ready) before recording and moving on;
        // the reader's own frame fallback bounds how long this can stall.
        if (TriadRunSession.ModuleEnabled &&
            !IsResultMatchRecorded((nint)addon) &&
            !uiReaderMatchResults.HasPendingNotify &&
            IsResultReady(addon))
        {
            RecordMatchResultIfNeeded((nint)addon, true);
        }

        if (sessionEndDismissRequested)
        {
            if (TriadCardFarmSession.IsModeActive() && TriadCardFarmSession.HasPendingDrops())
            {
                if (TriadCardFarmSession.IsDropVerificationPending())
                {
                    return true;
                }

                sessionEndDismissRequested = false;
                if (!RematchPending)
                {
                    RequestRematch();
                }
            }
            else if (framesSinceSessionEndDismiss <= 0)
            {
                if (TryDismissTriadResult(addon))
                {
                    sessionEndDismissRequested = false;
                    return true;
                }

                framesSinceSessionEndDismiss = RematchRetryCooldownFrames;
            }
            else
            {
                framesSinceSessionEndDismiss--;
            }

            return true;
        }

        if (!TriadRunSession.ModuleEnabled || !RematchPending)
        {
            return false;
        }

        if (TriadCardFarmSession.IsDropVerificationPending())
        {
            return true;
        }

        if (framesSinceRematchAttempt > 0)
        {
            framesSinceRematchAttempt--;
            return true;
        }

        try
        {
            if (!IsResultReady(addon))
            {
                return true;
            }

            if (DidEnterRematchFlow())
            {
                ClearRematchPending();
                return true;
            }

            TryFireResultRematch(addon);

            if (DidEnterRematchFlow())
            {
                ClearRematchPending();
            }
            else if (!addon->IsVisible && TriadCardFarmSession.IsModeActive() && TriadCardFarmSession.HasPendingDrops())
            {
                RequestRematch();
            }

            framesSinceRematchAttempt = RematchRetryCooldownFrames;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[TriadRematchAutomation] Tick failed");
        }

        return true;
    }

    private static bool TryDismissTriadResult(AtkUnitBase* addon)
    {
        // 🔴🔴 原本是同一次呼叫內「callback(1) → 看 IsVisible 沒落 → Close(true)」的級聯，但關閉中的那幾幀
        // IsVisible 仍為 true，第二下就是打在正在關的窗上（攔不到的存取違規）；而且 Svc.Framework.Run 排的
        // 那一次與同 tick 的 Tick 會各按一次同位址。改成：一個守衛窗口只送一招；守衛走逃生口放行
        // （＝窗 90 幀都沒收掉，上一招真的沒生效）時才輪到下一招。兩招的順序與內容都沒變。
        if (!AddonPressGuard.TryBeginPress(
                ResultAddonName, addon, AddonPressGuard.WholeWindowKey, AddonPressGuard.ReleaseEscapeFrames,
                out var viaEscape))
        {
            return !addon->IsVisible;
        }

        var address = (nint)addon;
        dismissChainStage = viaEscape && dismissChainAddress == address ? dismissChainStage + 1 : 0;
        dismissChainAddress = address;

        if (dismissChainStage % 2 == 0)
        {
            try
            {
                addon->FireCallbackInt(1);
                addon->Update(0);
            }
            catch (Exception ex)
            {
                Svc.Log.Verbose(ex, "[TriadRematchAutomation] Result FireCallbackInt(1) failed");
            }
        }
        else
        {
            try
            {
                addon->Close(true);
                addon->Update(0);
            }
            catch (Exception ex)
            {
                Svc.Log.Verbose(ex, "[TriadRematchAutomation] Result Close(true) failed");
            }
        }

        return !addon->IsVisible;
    }

    private static bool TryFireResultRematch(AtkUnitBase* addon)
    {
        // 再戰 callback 按下結果窗就關：與關窗級聯共用同一個以位址為鍵的守衛。
        if (!AddonPressGuard.TryBeginPress(ResultAddonName, addon))
        {
            return false;
        }

        try
        {
            addon->FireCallbackInt(0);
            addon->Update(0);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[TriadRematchAutomation] Result FireCallbackInt(0) failed");
            return false;
        }
    }

    private static bool DidEnterRematchFlow() =>
        TriadUiState.IsPrepDeckSelectVisible() || TriadUiState.IsMatchRegistrationVisible();
}
