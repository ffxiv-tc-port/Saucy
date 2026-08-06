using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;
using Saucy.Framework;
using System;
using System.Collections.Generic;
namespace Saucy.OutOnALimb;

/// <summary>
/// 判斷玩家面前那台採集機台是不是「孤樹無援」。
///
/// 為什麼需要：力量表畫面 <c>MiniGameAimg</c> 是孤樹無援與礦脈探索**共用**的，
/// 只憑它開著就自動停表，會連玩家在玩礦脈探索時也一起接手——那是玩家沒有啟用的東西。
/// 砍伐階段（<c>MiniGameBotanist</c>）本身就只屬於孤樹無援，不需要這層判斷。
///
/// 判斷方式刻意用**名稱**而不是 DataId：台服的 NPC／物件 DataId 與國際服不一定相同，
/// 名稱則是直接從當前用戶端的資料表讀出來的，一定跟畫面上顯示的一致。
/// 涵蓋兩種機台：金碟遊樂園的公共機台（EObjName#2005423）與房屋家具版（Item#30425）。
/// </summary>
internal static class LimbMachine
{
    private const uint ArcadeEObjNameRowId = 2005423;
    private const uint HousingItemRowId = 30425;

    /// <summary>機台判定的水平距離上限（公尺）。互動距離本來就很近，放寬只會讓隔壁機台被誤認。</summary>
    private const float MaxDistance = 5f;

    private static HashSet<string>? machineNames;

    /// <summary>附近是否有一台孤樹無援機台。取不到名稱資料時回 false（寧可不動作）。</summary>
    internal static bool IsNearLimbMachine() => TryFindNearbyMachine(out _);

    /// <summary>找出附近那台孤樹無援機台。
    ///
    /// 🔴 回傳的 <see cref="IGameObject"/> **只在當幀有效**：它的 <c>Address</c> 是建構當下凍結的，
    /// 呼叫端一律當幀用完就丟，絕不可以存起來跨幀再用。</summary>
    internal static bool TryFindNearbyMachine(out IGameObject? machine)
    {
        machine = null;
        var names = machineNames ??= BuildMachineNames();
        if (names.Count == 0)
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (!IsCandidateKind(obj))
            {
                continue;
            }

            var name = obj.Name.TextValue;
            if (string.IsNullOrEmpty(name) || !names.Contains(name))
            {
                continue;
            }

            var distance = ObjectHelper.GetHorizontalEdgeDistance(obj);
            if (distance <= MaxDistance && distance < bestDistance)
            {
                bestDistance = distance;
                machine = obj;
            }
        }

        return machine != null;
    }

    /// <summary>
    /// 這段確認框文字裡有沒有出現孤樹無援的機台名稱。
    ///
    /// 台服的街機遊玩確認框是 Addon 9321 的模板，第一行就是**粗體的機台名**
    /// （使用者回報的截圖第一行正是「孤樹無援」）。名稱一律從當前用戶端的資料表讀
    /// （<see cref="BuildMachineNames"/>：EObjName#2005423／Item#30425），
    /// **不寫死任何語言的字串**，所以換語言、改譯名都不會靜默失效。
    ///
    /// 讀不到名稱時回 false —— 那個方向是「不動作」，不是「亂按」。
    /// </summary>
    internal static bool PromptMentionsMachine(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var names = machineNames ??= BuildMachineNames();
        foreach (var name in names)
        {
            if (prompt.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCandidateKind(IGameObject obj) =>
        obj.ObjectKind is ObjectKind.EventObj or ObjectKind.Housing;

    private static HashSet<string> BuildMachineNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ⚠️ 台服有「列存在但欄位是空字串＝該內容未開放」的情形，所以判定要看內容而不是列是否存在。
        var eobjName = Svc.Data.GetExcelSheet<EObjName>()?.GetRowOrDefault(ArcadeEObjNameRowId)?.Singular.ExtractText();
        if (!string.IsNullOrWhiteSpace(eobjName))
        {
            names.Add(eobjName);
        }

        var itemName = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(HousingItemRowId)?.Singular.ExtractText();
        if (!string.IsNullOrWhiteSpace(itemName))
        {
            names.Add(itemName);
        }

        if (names.Count == 0)
        {
            Svc.Log.Warning("[OutOnALimb] no Out on a Limb machine name found in game data; " +
                            "power meter automation will stay off");
        }
        else
        {
            Svc.Log.Information($"[OutOnALimb] machine names: {string.Join(" / ", names)}");
        }

        return names;
    }
}
