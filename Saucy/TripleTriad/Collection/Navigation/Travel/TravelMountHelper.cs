using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
namespace Saucy.TripleTriad;

internal static unsafe class TravelMountHelper
{
    private const uint GeneralActionMountRoulette = 9;

    public static bool CanMountInCurrentTerritory() =>
        CanMountInTerritory(Svc.ClientState.TerritoryType);

    public static bool CanMountInTerritory(uint territoryId)
    {
        var row = Svc.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        return row != null && row.Value.Mount;
    }

    public static bool IsFlyingUnlocked()
    {
        var uiState = UIState.Instance();
        // Old FFXIVClientStructs has no PlayerState.CanFly convenience property; flying in a
        // zone is unlocked once its aether current zone (and thus zone-wide flight) is complete.
        return uiState != null && uiState->PlayerState.IsAetherCurrentZoneComplete(Svc.ClientState.TerritoryType);
    }

    public static bool IsMountUnlocked(uint mountId) =>
        mountId != 0 && UIState.Instance()->PlayerState.IsMountUnlocked(mountId);

    public static bool ResolveUseFlying(bool requestedFly, uint? territoryId = null)
    {
        if (!requestedFly)
        {
            return false;
        }

        var resolvedTerritoryId = territoryId ?? Svc.ClientState.TerritoryType;
        if (!CanMountInTerritory(resolvedTerritoryId))
        {
            return false;
        }

        if (!IsFlyingUnlocked())
        {
            return false;
        }

        // vnavmesh 的 PathfindAndMoveTo/MoveTo 收到 fly=true 但玩家沒騎坐騎時,會直接把移動
        // 停用並返回:角色站著不動、沒有任何訊息,而且 vnavmesh 不會替呼叫端上坐騎。
        // 這裡在把「要飛」交給 vnavmesh 之前先確認真的在坐騎上,否則降級成地面路徑。
        // 刻意不在這裡代替使用者上坐騎——要上坐騎的路徑另有 TryEnsureMountedForNav/TryMountUp,
        // 而且那兩者在「坐騎技能當下不可用」時會回 true 卻仍然是下坐騎狀態,所以這層檢查是必要的。
        // 只在解析「目前所在區域」時才做這個降級;帶 territoryId 預先規劃別的區域時不適用。
        if (territoryId.HasValue && territoryId.Value != Svc.ClientState.TerritoryType)
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.Mounted] || Svc.Condition[ConditionFlag.InFlight])
        {
            return true;
        }

        if (EzThrottler.Throttle("SaucyFlyDowngradeNotMounted", 5000))
        {
            Svc.Log.Information(
                "[Saucy] 目前沒有騎乘坐騎,這次導航改用地面路徑。" +
                "(vnavmesh 收到飛行指令但未騎乘時會直接停住不動,且不會自動上坐騎;Saucy 也不會代替你上坐騎)");
        }

        return false;
    }

    public static bool TryMountUp()
    {
        if (!CanMountInCurrentTerritory())
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.MountOrOrnamentTransition] || Svc.Condition[ConditionFlag.Casting])
        {
            EzThrottler.Throttle("SaucyTravelMountWait", 2000, true);
        }

        if (Svc.Condition[ConditionFlag.Jumping])
        {
            return false;
        }

        if (!EzThrottler.Check("SaucyTravelMountWait"))
        {
            return false;
        }

        var mountId = C.TriadCollection.TravelMountId;
        var actionManager = ActionManager.Instance();
        if (mountId == 0 || !IsMountUnlocked(mountId))
        {
            if (actionManager->GetActionStatus(ActionType.GeneralAction, GeneralActionMountRoulette) != 0)
            {
                return true;
            }

            if (Player.IsAnimationLocked || !EzThrottler.Throttle("SaucyTravelMount"))
            {
                return false;
            }

            actionManager->UseAction(ActionType.GeneralAction, GeneralActionMountRoulette);
            return false;
        }

        if (actionManager->GetActionStatus(ActionType.Mount, mountId) != 0)
        {
            return true;
        }

        if (Player.IsAnimationLocked || !EzThrottler.Throttle("SaucyTravelMount"))
        {
            return false;
        }

        actionManager->UseAction(ActionType.Mount, mountId);
        return false;
    }

    public static bool TryDismount()
    {
        if (!Svc.Condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.Jumping] ||
            Svc.Condition[ConditionFlag.MountOrOrnamentTransition] ||
            Player.IsAnimationLocked ||
            !EzThrottler.Throttle("SaucyTravelDismount"))
        {
            return false;
        }

        Chat.ExecuteGeneralAction(23);
        return false;
    }
}
