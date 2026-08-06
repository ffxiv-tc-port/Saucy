using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
namespace Saucy.Framework;

public static unsafe class AddonButton
{
    /// <summary>
    /// 安全地讀取按鈕的啟用狀態。
    /// <para>
    /// 🔴 <c>AtkComponentButton.IsEnabled</c> 的實作是
    /// <c>AtkComponentBase.OwnerNode-&gt;AtkResNode.NodeFlags.HasFlag(...)</c>，
    /// 對 <c>OwnerNode</c> 沒有任何 null 檢查 —— <c>OwnerNode</c> 為 null 時會丟出
    /// AccessViolationException，而 AVE 在 .NET Core 是 corrupted-state exception，
    /// <c>try/catch</c> 攔不到。所有讀取 <c>IsEnabled</c> 的地方都必須改走這裡。
    /// </para>
    /// <para>
    /// ⚠️ <c>AtkComponentBase</c> 有兩個指標欄位：<c>AtkResNode</c>(0xA0) 與 <c>OwnerNode</c>(0xA8)。
    /// 檢查 <c>AtkResNode</c> 擋不到 <c>IsEnabled</c> 的解參考 —— 那是不同的欄位。
    /// </para>
    /// </summary>
    /// <returns>按鈕存在、OwnerNode 有效、且處於啟用狀態時回 true；任何一層取不到都回 false。</returns>
    public static bool IsEnabledSafe(AtkComponentButton* button)
    {
        return button != null
            && button->AtkComponentBase.OwnerNode != null
            && button->IsEnabled;
    }

    public static bool TryClick(AtkUnitBase* addon, uint nodeId)
    {
        if (addon == null)
        {
            return false;
        }

        return TryClick(addon, addon->GetComponentButtonById(nodeId));
    }

    public static bool TryClick(AtkUnitBase* addon, AtkComponentButton* button, bool requireEnabled = true)
    {
        if (addon == null || button == null || button->AtkResNode == null || !button->AtkResNode->IsVisible())
        {
            return false;
        }

        if (requireEnabled && !IsEnabledSafe(button))
        {
            return false;
        }

        try
        {
            button->ClickAddonButton(addon);
            addon->Update(0);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[AddonButton] click failed");
            return false;
        }
    }
}
