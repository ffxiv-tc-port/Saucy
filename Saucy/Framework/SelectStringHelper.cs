using ECommons.Automation;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using static ECommons.GenericHelpers;

namespace Saucy.Framework;

public static unsafe class SelectStringHelper
{
    /// <summary>兩種選單的 addon 名稱。也是它們在 <see cref="AddonPressGuard"/> 裡的鍵。</summary>
    /// <remarks>
    /// 選項按下即關窗，但刻意<b>不</b>併成整扇窗一鍵（巢狀選單會重用同一個實例只換內容），
    /// 改用「選項索引」當按法鍵——本外掛對同一扇選單永遠算出同一個索引，同幀雙按照樣擋得住。
    /// </remarks>
    public const string SelectStringAddonName = "SelectString";

    public const string SelectIconStringAddonName = "SelectIconString";

    public const uint ListNodeId = 3;

    public const int YesnoMenuEntryCount = 2;

    public const int ArcadeStartMenuMaxEntryCount = 3;

    public const int YesEntryIndex = 0;

    public const int NoEntryIndex = 1;

    private static readonly uint[] TriadListEntryIconIds = [60091u, 61721u, 61723u];

    public static bool IsNpcListMenuVisible() =>
        TryGetVisibleSelectString(out var _) || TryGetVisibleSelectIconString(out var _);

    public static bool TryGetArcadeMenu(out AddonSelectString* menu) =>
        TryGetAgentMenu(out menu, SelectYesnoHelper.IsArcadeAddon);

    public static bool TryGetTriadMenu(out AddonSelectString* menu) =>
        TryGetAgentMenu(out menu, SelectYesnoHelper.IsTriadAddon);

    public static bool IsArcadeYesnoMenu(AddonSelectString* menu) =>
        IsAgentArcadeStartMenu(menu, SelectYesnoHelper.IsArcadeAddon);

    public static bool IsTriadYesnoMenu(AddonSelectString* menu) =>
        IsAgentYesnoMenu(menu, SelectYesnoHelper.IsTriadAddon);

    public static bool TryGetLotteryDailyMenu(out AddonSelectString* menu) =>
        TryGetAgentMenu(out menu, SelectYesnoHelper.IsLotteryDailyAddon);

    public static bool TryGetLotteryWeeklyMenu(out AddonSelectString* menu) =>
        TryGetAgentMenu(out menu, SelectYesnoHelper.IsLotteryWeeklyAddon);

    public static bool IsLotteryDailyYesnoMenu(AddonSelectString* menu) =>
        IsAgentYesnoMenu(menu, SelectYesnoHelper.IsLotteryDailyAddon);

    public static bool IsLotteryWeeklyYesnoMenu(AddonSelectString* menu) =>
        IsAgentYesnoMenu(menu, SelectYesnoHelper.IsLotteryWeeklyAddon);

    public static bool TrySelectEntryContaining(AddonSelectString* menu, string textFragment)
    {
        if (menu == null || !IsAddonReady(&menu->AtkUnitBase) || !menu->AtkUnitBase.IsVisible)
        {
            return false;
        }

        try
        {
            var select = new AddonMaster.SelectString(menu);
            for (var i = 0; i < select.Entries.Length; i++)
            {
                var text = select.Entries[i].Text;
                if (AddonPressGuard.LooksCorrupted(text))
                {
                    // 選項文字讀到 U+FFFD＝窗記憶體正在變動，該幀不碰。
                    return false;
                }

                if (text.Contains(textFragment, StringComparison.OrdinalIgnoreCase))
                {
                    if (!AddonPressGuard.TryBeginPress(SelectStringAddonName, &menu->AtkUnitBase, PressKey(i)))
                    {
                        return false;
                    }

                    select.Entries[i].Select();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] Entry containing \"{textFragment}\" select failed");
        }

        return false;
    }

    public static bool TrySelectYesEntry(AddonSelectString* menu) => TrySelectEntry(menu, YesEntryIndex);

    public static bool TrySelectNoEntry(AddonSelectString* menu) => TrySelectEntry(menu, NoEntryIndex);

    public static bool TrySelectEntry(AddonSelectString* menu, int index)
    {
        if (menu == null || !IsAddonReady(&menu->AtkUnitBase) || !menu->AtkUnitBase.IsVisible)
        {
            return false;
        }

        // 🔴 選項按下即關窗；幻卡代理的兩項式選單在同一幀會被三四條路徑各按一次（第一次生效後
        // 仍 IsVisible + ready），第二下就打在正在關的窗上。守衛以位址＋索引為鍵，下面兩條後援
        // 是「送出成功即停」的鏈，登記一次就夠。
        if (!AddonPressGuard.TryBeginPress(SelectStringAddonName, &menu->AtkUnitBase, PressKey(index)))
        {
            return false;
        }

        try
        {
            Callback.Fire(&menu->AtkUnitBase, true, index);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] Callback.Fire({index}) failed");
        }

        try
        {
            var select = new AddonMaster.SelectString(menu);
            var entryIndex = 0;
            foreach (var entry in select.Entries)
            {
                if (entryIndex == index)
                {
                    entry.Select();
                    return true;
                }

                entryIndex++;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] Entry {index} select failed");
        }

        return false;
    }

    public static bool TrySelectTriadEntry(string throttleKey = "SaucyTriadSelectMenu")
    {
        if (TryGetTriadMenu(out var triadMenu) && IsTriadYesnoMenu(triadMenu))
        {
            return TrySelectYesEntry(triadMenu);
        }

        if (TryGetVisibleSelectIconString(out var iconMenu) &&
            TrySelectTriadIconStringEntry(iconMenu, throttleKey))
        {
            return true;
        }

        if (TryGetVisibleSelectString(out var menu) &&
            TrySelectTriadListEntry(&menu->AtkUnitBase, throttleKey))
        {
            return true;
        }

        return false;
    }

    public static void CollectTriadMenuDebugLines(List<string> lines)
    {
        lines.Add($"npc menu visible: {IsNpcListMenuVisible()}");

        if (TryGetTriadMenu(out var triadMenu))
        {
            lines.Add($"triad-agent SelectString: yesnoMenu={IsTriadYesnoMenu(triadMenu)}");
        }

        if (TryGetVisibleSelectIconString(out var iconMenu))
        {
            AppendMenuListDebug(&iconMenu->AtkUnitBase, "SelectIconString", lines);
            try
            {
                var popupCount = new AddonMaster.SelectIconString(iconMenu).EntryCount;
                var fallbackIndex0 = TryFindTriadIconStringEntryIndex(iconMenu, out var resolvedIndex);
                lines.Add($"SelectIconString popup entries={popupCount}, resolvedIndex={resolvedIndex}, fallbackIndex0={fallbackIndex0}");
            }
            catch (Exception ex)
            {
                lines.Add($"SelectIconString popup entries: read failed ({ex.Message})");
            }
        }

        if (TryGetVisibleSelectString(out var menu))
        {
            AppendMenuListDebug(&menu->AtkUnitBase, "SelectString", lines);
        }
    }

    private static bool TryGetAgentMenu(out AddonSelectString* menu, AgentAddonPredicate isAgentAddon)
    {
        menu = null;
        for (var i = 1; i < 100; i++)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectString", i).Address;
            if (addon == null)
            {
                break;
            }

            if (!addon->IsVisible || !IsAddonReady(addon) || !isAgentAddon(addon))
            {
                continue;
            }

            menu = (AddonSelectString*)addon;
            return true;
        }

        return false;
    }

    private static bool IsAgentYesnoMenu(AddonSelectString* menu, AgentAddonPredicate isAgentAddon)
    {
        if (menu == null || !isAgentAddon(&menu->AtkUnitBase))
        {
            return false;
        }

        var listNode = menu->AtkUnitBase.GetNodeById(ListNodeId);
        if (listNode == null || !listNode->IsVisible())
        {
            return false;
        }

        return TryGetEntryCount(menu, out var entryCount) && entryCount == YesnoMenuEntryCount;
    }

    private static bool IsAgentArcadeStartMenu(AddonSelectString* menu, AgentAddonPredicate isAgentAddon)
    {
        if (menu == null || !isAgentAddon(&menu->AtkUnitBase))
        {
            return false;
        }

        var listNode = menu->AtkUnitBase.GetNodeById(ListNodeId);
        if (listNode == null || !listNode->IsVisible())
        {
            return false;
        }

        if (!TryGetEntryCount(menu, out var entryCount))
        {
            return false;
        }

        return entryCount is >= YesnoMenuEntryCount and <= ArcadeStartMenuMaxEntryCount;
    }

    public static bool TryGetArcadeMenuEntryCount(AddonSelectString* menu, out int entryCount) =>
        TryGetEntryCount(menu, out entryCount);

    private static bool TrySelectTriadIconStringEntry(
        AddonSelectIconString* menu,
        string throttleKey,
        int throttleMs = 400)
    {
        if (menu == null || !IsAddonReady(&menu->AtkUnitBase) || !menu->AtkUnitBase.IsVisible)
        {
            return false;
        }

        if (!EzThrottler.Throttle(throttleKey, throttleMs))
        {
            return false;
        }

        if (!TryFindTriadIconStringEntryIndex(menu, out var index))
        {
            return false;
        }

        return TryFireSelectIconStringEntry(menu, index);
    }

    private static bool TryFireSelectIconStringEntry(AddonSelectIconString* menu, int index)
    {
        if (menu == null || index < 0)
        {
            return false;
        }

        // 同 TrySelectEntry：位址＋索引為鍵，三條後援登記一次。
        if (!AddonPressGuard.TryBeginPress(SelectIconStringAddonName, &menu->AtkUnitBase, PressKey(index)))
        {
            return false;
        }

        try
        {
            new AddonMaster.SelectIconString(menu).Entries[index].Select();
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] SelectIconString AddonMaster entry {index} failed");
        }

        try
        {
            Callback.Fire(&menu->AtkUnitBase, true, index);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] SelectIconString Callback.Fire({index}) failed");
        }

        var list = menu->AtkUnitBase.GetComponentListById(ListNodeId);
        if (list != null)
        {
            try
            {
                list->SelectItem(index, true);
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Verbose(ex, $"[SelectString] SelectIconString SelectItem({index}) failed");
            }
        }

        return false;
    }

    private static bool TrySelectTriadListEntry(
        AtkUnitBase* menu,
        string throttleKey,
        int throttleMs = 400,
        bool skipThrottle = false)
    {
        if (menu == null || !IsAddonReady(menu) || !menu->IsVisible)
        {
            return false;
        }

        if (!skipThrottle && !EzThrottler.Throttle(throttleKey, throttleMs))
        {
            return false;
        }

        if (!TryFindTriadEntryIndex(menu, out var index))
        {
            return false;
        }

        // 同 TrySelectEntry：位址＋索引為鍵，兩條後援登記一次。
        if (!AddonPressGuard.TryBeginPress(SelectStringAddonName, menu, PressKey(index)))
        {
            return false;
        }

        try
        {
            Callback.Fire(menu, true, index);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] Triad Callback.Fire({index}) failed");
        }

        try
        {
            new AddonMaster.SelectString((AddonSelectString*)menu).Entries[index].Select();
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] SelectString entry {index} failed");
        }

        return false;
    }

    private static bool TryFindTriadEntryIndex(AtkUnitBase* menu, out int index)
    {
        index = -1;
        var listNode = menu->GetNodeById(ListNodeId);
        if (listNode == null || !listNode->IsVisible())
        {
            return false;
        }

        var list = menu->GetComponentListById(ListNodeId);
        if (list == null)
        {
            return false;
        }

        var count = list->GetItemCount();
        if (count <= 0)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (TryGetListEntryIconId(list, i, out var iconId) && IsTriadListEntryIcon(iconId))
            {
                index = i;
                return true;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var text = TryGetListEntryText(menu, i);
            if (AddonPressGuard.LooksCorrupted(text))
            {
                // 🔴 選項文字讀到 U+FFFD＝窗記憶體正在變動（多半是關閉中），該幀不做任何判定。
                return false;
            }

            if (IsTriadListEntryText(text))
            {
                index = i;
                return true;
            }
        }

        if (count == 1)
        {
            index = 0;
            return true;
        }

        return false;
    }

    private static bool TryFindTriadIconStringEntryIndex(AddonSelectIconString* menu, out int index)
    {
        if (TryFindTriadEntryIndex(&menu->AtkUnitBase, out index))
        {
            return true;
        }

        try
        {
            var entryCount = new AddonMaster.SelectIconString(menu).EntryCount;
            if (entryCount is >= 2 and <= 4)
            {
                index = 0;
                return true;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[SelectString] Failed to read SelectIconString entry count");
        }

        index = -1;
        return false;
    }

    private static bool IsTriadListEntryText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("Triple Triad", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Triple-Triad", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("triad", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("triade", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("triplo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("トリプル", StringComparison.OrdinalIgnoreCase) ||
               // 台服：選單項目是「幻卡挑戰」（Addon 9160/9173/9179/9184/9224），
               // 對局室相關則是「九宮幻卡」（Addon 9529/9991/10800）。
               // 少了這一條，台服用戶端在 NPC 選單裡永遠找不到幻卡項目（靜默失效）。
               text.Contains("幻卡", StringComparison.Ordinal);
    }

    private static bool IsTriadListEntryIcon(uint iconId) =>
        Array.IndexOf(TriadListEntryIconIds, iconId) >= 0;

    /// <summary>選單的按法鍵＝選項索引（不變文化，免得別的地區設定把同一個索引格式成兩種字串）。</summary>
    private static string PressKey(int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryGetListEntryIconId(AtkComponentList* list, int entryIndex, out uint iconId)
    {
        // Old FFXIVClientStructs' AtkComponentListItemRenderer has no IconId field to read here;
        // callers already fall back to text-based matching (and a single-entry heuristic), so
        // simply report "no icon available" instead of guessing an unverifiable memory offset.
        iconId = 0;
        return false;
    }

    private static void AppendMenuListDebug(AtkUnitBase* menu, string addonName, List<string> lines)
    {
        var list = menu->GetComponentListById(ListNodeId);
        var count = list == null ? 0 : list->GetItemCount();
        var listNode = menu->GetNodeById(ListNodeId);
        var foundTriad = TryFindTriadEntryIndex(menu, out var triadIndex);
        lines.Add(
            $"{addonName}: listReady={list != null}, listNodeVisible={listNode != null && listNode->IsVisible()}, entries={count}, triadIndex={triadIndex}, matched={foundTriad}");

        if (list == null)
        {
            lines.Add($"{addonName}: list node {ListNodeId} missing");
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var icon = TryGetListEntryIconId(list, i, out var iconId) ? iconId.ToString() : "?";
            var text = TryGetListEntryText(menu, i) ?? "";
            lines.Add($"  [{i}] icon={icon} text=\"{text}\"");
        }
    }

    private static string? TryGetListEntryText(AtkUnitBase* menu, int index)
    {
        try
        {
            if (menu->NameString.Contains("SelectIconString", StringComparison.Ordinal))
            {
                return new AddonMaster.SelectIconString((AddonSelectIconString*)menu).Entries[index].Text;
            }

            return new AddonMaster.SelectString((AddonSelectString*)menu).Entries[index].Text;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectString] Failed to read entry {index} text");
            return null;
        }
    }

    private static bool TryGetEntryCount(AddonSelectString* menu, out int entryCount)
    {
        entryCount = 0;
        try
        {
            foreach (var _ in new AddonMaster.SelectString(menu).Entries)
            {
                entryCount++;
            }

            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[SelectString] Failed to read entry count");
            return false;
        }
    }

    public static bool TryGetVisibleSelectString(out AddonSelectString* menu) =>
        TryGetVisibleAddon("SelectString", out menu);

    public static bool TryGetVisibleSelectIconString(out AddonSelectIconString* menu) =>
        TryGetVisibleAddon("SelectIconString", out menu);

    private static bool TryGetVisibleAddon<T>(string addonName, out T* menu) where T : unmanaged
    {
        menu = null;
        for (var i = 1; i < 100; i++)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(addonName, i).Address;
            if (addon == null)
            {
                break;
            }

            if (!addon->IsVisible || !IsAddonReady(addon))
            {
                continue;
            }

            menu = (T*)addon;
            return true;
        }

        return false;
    }
    private delegate bool AgentAddonPredicate(AtkUnitBase* addon);
}
