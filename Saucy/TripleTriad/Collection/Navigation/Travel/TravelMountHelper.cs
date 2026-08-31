using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
namespace Saucy.TripleTriad;

/// <summary>
/// <see cref="TravelMountHelper.TryMount"/> 的結果。
/// 刻意不用 bool:「騎不上去」和「還在騎上去的路上」對呼叫端是完全不同的兩件事,
/// 用 bool 表示會逼得其中一種要說謊(舊版就是把「騎不上去」回報成 true,呼叫端因此
/// 以為已經在坐騎上,把 fly=true 交給 vnavmesh,角色站著不動而且沒有任何訊息)。
/// </summary>
internal enum MountAttemptResult
{
    /// <summary>還在嘗試(節流等待、施法中、動作鎖中……)。呼叫端應該什麼都別做,下一幀再問。</summary>
    InProgress = 0,

    /// <summary>已經在坐騎上。前置條件確實達成了。</summary>
    Mounted,

    /// <summary>當下騎不上去(本區禁止騎乘,或坐騎技能不可用)。呼叫端應降級成走路,不要再等。</summary>
    Unavailable,
}

internal static unsafe class TravelMountHelper
{
    private const uint GeneralActionMountRoulette = 9;

    /// <summary>
    /// <c>GetActionStatus</c> 對「剛切換區域/剛落地/剛結束戰鬥」這類短暫狀態也會回非 0。
    /// 一觀察到就宣告 Unavailable 會讓整段路程都用走的,所以先給一段連續觀察的寬限期。
    /// </summary>
    private static readonly TimeSpan MountUnavailableGrace = TimeSpan.FromSeconds(3);

    private static DateTime _mountUnavailableSinceUtc = DateTime.MinValue;

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
        // 刻意不在這裡代替使用者上坐騎——要上坐騎的路徑另有 TryResolveMountForNav/TryMount。
        // 那條路徑現在會誠實回報 Unavailable(不再謊稱成功),但它回報之後呼叫端就照樣往下走了,
        // 所以這層「真的在坐騎上嗎」的檢查仍然是最後一道防線。
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

    /// <summary>
    /// 嘗試把玩家弄上坐騎。<see cref="MountAttemptResult.Mounted"/> 才等價於「前置條件已達成」;
    /// 騎不上去一律回 <see cref="MountAttemptResult.Unavailable"/> 交給呼叫端降級走路,絕不謊稱成功。
    /// </summary>
    public static MountAttemptResult TryMount()
    {
        if (!CanMountInCurrentTerritory())
        {
            // 這個區域本來就不能騎坐騎,走路是唯一選項,是正常狀況所以不出診斷訊息。
            ClearMountUnavailableTracking();
            return MountAttemptResult.Unavailable;
        }

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            ClearMountUnavailableTracking();
            return MountAttemptResult.Mounted;
        }

        if (Svc.Condition[ConditionFlag.MountOrOrnamentTransition] || Svc.Condition[ConditionFlag.Casting])
        {
            EzThrottler.Throttle("SaucyTravelMountWait", 2000, true);
        }

        if (Svc.Condition[ConditionFlag.Jumping])
        {
            return MountAttemptResult.InProgress;
        }

        if (!EzThrottler.Check("SaucyTravelMountWait"))
        {
            return MountAttemptResult.InProgress;
        }

        var mountId = C.TriadCollection.TravelMountId;
        var actionManager = ActionManager.Instance();
        if (mountId == 0 || !IsMountUnlocked(mountId))
        {
            if (actionManager->GetActionStatus(ActionType.GeneralAction, GeneralActionMountRoulette) != 0)
            {
                return TrackMountUnavailable("坐騎輪盤現在無法使用");
            }

            ClearMountUnavailableTracking();
            if (Player.IsAnimationLocked || !EzThrottler.Throttle("SaucyTravelMount"))
            {
                return MountAttemptResult.InProgress;
            }

            actionManager->UseAction(ActionType.GeneralAction, GeneralActionMountRoulette);
            return MountAttemptResult.InProgress;
        }

        if (actionManager->GetActionStatus(ActionType.Mount, mountId) != 0)
        {
            return TrackMountUnavailable("設定的旅行坐騎現在無法使用");
        }

        ClearMountUnavailableTracking();
        if (Player.IsAnimationLocked || !EzThrottler.Throttle("SaucyTravelMount"))
        {
            return MountAttemptResult.InProgress;
        }

        actionManager->UseAction(ActionType.Mount, mountId);
        return MountAttemptResult.InProgress;
    }

    private static MountAttemptResult TrackMountUnavailable(string reason)
    {
        var now = DateTime.UtcNow;
        if (_mountUnavailableSinceUtc == DateTime.MinValue)
        {
            _mountUnavailableSinceUtc = now;
        }

        if (now - _mountUnavailableSinceUtc < MountUnavailableGrace)
        {
            return MountAttemptResult.InProgress;
        }

        // 使用者跑 LogLevel 2,所以診斷寫 Information;節流是為了不要每幀洗版。
        if (EzThrottler.Throttle("SaucyTravelMountUnavailableNotice", 10000))
        {
            Svc.Log.Information(
                $"[Saucy] {reason},這次導航改用地面路徑(不會自動替你上坐騎)。" +
                "常見原因:戰鬥中、正在施法、當前區域或狀態暫時禁止騎乘。");
        }

        return MountAttemptResult.Unavailable;
    }

    private static void ClearMountUnavailableTracking() => _mountUnavailableSinceUtc = DateTime.MinValue;

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
