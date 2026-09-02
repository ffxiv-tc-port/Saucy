using FFXIVClientStructs.FFXIV.Client.UI;
using Saucy.Framework;
namespace Saucy.TripleTriad.UI;

internal static unsafe class GoldSaucerCardListUi
{
    /// <summary>卡片清單的 addon 名稱。也是它在 <see cref="AddonPressGuard"/> 裡的鍵。</summary>
    /// <remarks>
    /// 點格／切頁都不關窗，「同窗只按一次」不適用（導航會逐幀重按同一格直到選中）；
    /// 只接 <see cref="AddonPressGuard.TryTouch"/>，擋「玩家在導航進行中關掉清單」那幾幀。
    /// </remarks>
    internal const string AddonName = "GSInfoCardList";

    internal static bool TryClickGridButton(nint addonPtr, int pageIndex, int cellIndex)
    {
        if (addonPtr == nint.Zero || cellIndex < 0 || cellIndex >= 30)
        {
            return false;
        }

        var addon = (AddonGSInfoCardList*)addonPtr;
        var atkUnit = &addon->AtkUnitBase;

        if (!AddonPressGuard.TryTouch(AddonName, atkUnit))
        {
            return false;
        }

        if (pageIndex >= 0 && pageIndex != addon->SelectedPage)
        {
            // Old FFXIVClientStructs has no separate RequestedPage hint field; the tab
            // controller call below is what actually drives the page change.
            addon->TabController.SetTabIndexAndCallBack(pageIndex);
            atkUnit->Update(0);
        }

        return TryClickCell(addonPtr, cellIndex);
    }

    internal static bool TryClickCell(nint addonPtr, int cellIndex)
    {
        if (addonPtr == nint.Zero || cellIndex < 0 || cellIndex >= 30)
        {
            return false;
        }

        var addon = (AddonGSInfoCardList*)addonPtr;
        if (!AddonPressGuard.TryTouch(AddonName, &addon->AtkUnitBase))
        {
            return false;
        }

        var cardButton = addon->CardButtons[cellIndex];
        return AddonButton.TryClick(&addon->AtkUnitBase, cardButton, false);
    }
}
