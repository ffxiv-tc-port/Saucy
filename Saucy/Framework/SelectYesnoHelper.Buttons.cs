using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static ECommons.GenericHelpers;
using AddonSetupArgs = Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonSetupArgs;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Saucy.Framework;

public static unsafe partial class SelectYesnoHelper
{
    private static bool TryGetVisibleButtonByNodeId(AddonSelectYesno* yesno, uint nodeId, out AtkComponentButton* button)
    {
        button = null;
        if (yesno == null)
        {
            return false;
        }

        button = yesno->AtkUnitBase.GetComponentButtonById(nodeId);
        if (button == null || button->AtkResNode == null)
        {
            return false;
        }

        return button->AtkResNode->IsVisible();
    }

    /// <summary>把「是／否」真的按下去——本外掛所有模組按確認框的<b>總出口</b>（<see cref="PressYes"/>／<see cref="PressNo"/> 全部走這裡）。</summary>
    /// <remarks>
    /// 🔴🔴 <b>送出之前一定要過 <see cref="AddonPressGuard.TryBeginPress"/>。</b>確認框被按下之後有「正在關閉中」的幾幀，這段期間 <see cref="TryGetVisible"/> 仍拿得到實例、<c>IsAddonReady</c> 三關也全過（<b>所以那兩道判斷不是防護</b>），此時再送一次callback 或再模擬一次點擊就是原生 AccessViolation，而 AVE 在 .NET Core 是 corrupted-state exception，遊戲當場關閉。
    /// 閘門下沉在這裡而不是各呼叫端，是因為本外掛按確認框的路徑全部匯流到這一支，漏掉任何一個都等於沒防護。下面五條後援路徑（結構欄位按鈕、節點 id 按鈕、<c>AddonMaster</c>、<c>Callback.Fire</c>、<c>FireCallbackInt</c>）也一併罩住。
    /// 📌 被擋下時回 <see langword="false"/>，與「按不到任何按鈕」同一個語意（這一幀沒做成、下一幀再試）；所有呼叫端本來就是每幀重試的。
    /// </remarks>
    private static bool PressCallback(AddonSelectYesno* yesno, int callbackId, Action<AddonMaster.SelectYesno> fallback)
    {
        if (yesno == null)
        {
            return false;
        }

        // 🔴 內文讀到 U+FFFD＝這扇窗的記憶體正在變動（崩潰前實測看到的徵兆），該幀不碰。
        // 放在登記守衛之前：登記了卻不按會白白封鎖到逃生口。內文是 SeString 只取文字 payload
        // 的結果，正常文字（含顏色／圖示 payload）不會出現替代字元。
        if (AddonPressGuard.LooksCorrupted(GetPromptText(yesno)))
        {
            if (EzThrottler.Throttle("SelectYesnoHelper-Corrupted", 1000))
            {
                Svc.Log.Information(
                    $"[SelectYesno] 確認框（實例 0x{(nint)yesno:X}）內文讀到 U+FFFD，記憶體正在變動，這一幀不按。");
            }

            return false;
        }

        if (!AddonPressGuard.TryBeginPress(AddonName, &yesno->AtkUnitBase))
        {
            return false;
        }

        var wantsYes = callbackId == 0;
        if (wantsYes && TryResolveYesButton(yesno, out var yesButton) &&
            TryClickStructButton(yesno, yesButton, forceEnable: true))
        {
            return true;
        }

        if (TryResolveYesNoButtons(yesno, out yesButton, out var noButton))
        {
            var targetButton = wantsYes ? yesButton : noButton;
            if (targetButton != null &&
                TryClickStructButton(yesno, targetButton, forceEnable: wantsYes))
            {
                return true;
            }
        }

        try
        {
            fallback(new(yesno));
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] AddonMaster fallback failed for callback {callbackId}");
        }

        if (wantsYes)
        {
            foreach (var nodeId in YesButtonNodeCandidates)
            {
                if (TryGetVisibleButtonByNodeId(yesno, nodeId, out var button) &&
                    TryClickButton(yesno, button, forceEnable: true))
                {
                    return true;
                }
            }
        }

        try
        {
            Callback.Fire(&yesno->AtkUnitBase, true, callbackId);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] Callback.Fire({callbackId}) failed");
        }

        try
        {
            yesno->FireCallbackInt(callbackId);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] FireCallbackInt({callbackId}) failed");
        }

        return false;
    }

    private static bool TryResolveYesButton(AddonSelectYesno* yesno, out AtkComponentButton* yesButton)
    {
        yesButton = null;
        if (yesno == null)
        {
            return false;
        }

        if (HasVisibleStructButton(yesno->YesButton, out yesButton))
        {
            return true;
        }

        foreach (var nodeId in YesButtonNodeCandidates)
        {
            if (nodeId == YesButtonNodeId && IsComponentNodeVisible(yesno, TicketPurchaseNoButtonNodeId))
            {
                continue;
            }

            if (TryGetVisibleButtonByNodeId(yesno, nodeId, out yesButton))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveYesNoButtons(
        AddonSelectYesno* yesno,
        out AtkComponentButton* yesButton,
        out AtkComponentButton* noButton)
    {
        yesButton = null;
        noButton = null;
        if (yesno == null)
        {
            return false;
        }

        if (HasVisibleStructButton(yesno->YesButton, out yesButton) &&
            HasVisibleStructButton(yesno->NoButton, out noButton))
        {
            return true;
        }

        if (TryGetVisibleButtonByNodeId(yesno, TicketPurchaseYesButtonNodeId, out yesButton) &&
            IsComponentNodeVisible(yesno, TicketPurchaseNoButtonNodeId))
        {
            TryGetVisibleButtonByNodeId(yesno, TicketPurchaseNoButtonNodeId, out noButton);
            return true;
        }

        foreach (var (yesNodeId, noNodeId) in YesNoButtonNodeLayouts)
        {
            if (yesNodeId == YesButtonNodeId &&
                noNodeId == NoButtonNodeId &&
                IsComponentNodeVisible(yesno, TicketPurchaseNoButtonNodeId))
            {
                continue;
            }

            if (!TryGetVisibleButtonByNodeId(yesno, yesNodeId, out yesButton))
            {
                continue;
            }

            if (noNodeId == TicketPurchaseNoButtonNodeId)
            {
                if (IsComponentNodeVisible(yesno, noNodeId))
                {
                    TryGetVisibleButtonByNodeId(yesno, noNodeId, out noButton);
                    return true;
                }

                continue;
            }

            if (TryGetVisibleButtonByNodeId(yesno, noNodeId, out noButton))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsComponentNodeVisible(AddonSelectYesno* yesno, uint nodeId)
    {
        var componentNode = yesno->AtkUnitBase.GetComponentNodeById(nodeId);
        if (componentNode == null)
        {
            return false;
        }

        return ((AtkResNode*)componentNode)->IsVisible();
    }

    private static bool HasVisibleStructButton(AtkComponentButton* button, out AtkComponentButton* resolved)
    {
        resolved = button;
        if (button == null || button->AtkResNode == null)
        {
            resolved = null;
            return false;
        }

        return button->AtkResNode->IsVisible();
    }

    private static bool TryClickStructButton(AddonSelectYesno* yesno, AtkComponentButton* button, bool forceEnable = false) =>
        HasVisibleStructButton(button, out var resolved) &&
        TryClickButton(yesno, resolved, forceEnable);

    private static bool TryClickButton(AddonSelectYesno* yesno, AtkComponentButton* button, bool forceEnable = false)
    {
        if (button == null)
        {
            return false;
        }

        if (forceEnable && !AddonButton.IsEnabledSafe(button))
        {
            TryForceEnableButton(button);
        }

        return AddonButton.TryClick(&yesno->AtkUnitBase, button, requireEnabled: !forceEnable);
    }

    private static void TryForceEnableButton(AtkComponentButton* button)
    {
        // 🔴 OwnerNode 為 null 時下面的解參考會丟 AccessViolationException，
        //    而 AVE 是 corrupted-state exception，底下的 try/catch 攔不到 —— 必須在這裡先擋。
        if (button == null || button->AtkComponentBase.OwnerNode == null)
        {
            return;
        }

        try
        {
            var flagsPtr = (ushort*)&button->AtkComponentBase.OwnerNode->AtkResNode.NodeFlags;
            *flagsPtr ^= 1 << 5;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[SelectYesno] Force-enable button failed");
        }
    }
}
