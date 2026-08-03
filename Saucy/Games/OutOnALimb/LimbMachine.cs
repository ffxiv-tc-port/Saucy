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
    internal static bool IsNearLimbMachine()
    {
        var names = machineNames ??= BuildMachineNames();
        if (names.Count == 0)
        {
            return false;
        }

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

            if (ObjectHelper.GetHorizontalEdgeDistance(obj) <= MaxDistance)
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
