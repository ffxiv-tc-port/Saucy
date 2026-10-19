using ECommons.Automation;
using Saucy.Framework;
using System;
namespace Saucy.TripleTriad;

internal static unsafe class TriadBoardAutomation
{
    /// <summary>棋盤的 addon 名稱（<see cref="TriadLocalClientStructs.TryGetBoard"/> 用的同一個）。</summary>
    private const string BoardAddonName = "TripleTriad";

    private static bool boardActiveForSnapshot;

    public static bool Tick()
    {
        if (!TriadUiState.IsBoardVisible())
        {
            boardActiveForSnapshot = false;
            return false;
        }

        if (!TriadLocalClientStructs.TryGetBoard(out var triadAddon, false))
        {
            return false;
        }

        if (!boardActiveForSnapshot)
        {
            boardActiveForSnapshot = true;
            TriadRun.ResetForNewMatch();
            TriadCardFarmSession.ResetDropVerification();
            TriadRewardDropTracker.SnapshotAtMatchStart();
        }

        uiReaderGame.SyncCurrentFromAddon((nint)triadAddon);

        var state = uiReaderGame.currentState;
        var turnState = (byte)triadAddon->TurnState;
        TriadRun.UpdateGame(state);

        var canPlace = state != null &&
                       TriadTurnState.CanBlueAct(turnState, state.isPlayerTurn) &&
                       !(TriadTurnState.IsBoardPickPhase(turnState) && state.turnBannerVisible);

        if (canPlace &&
            TriadRun.hasMove &&
            TriadRun.IsMoveReadyForPlacement() &&
            TriadRun.moveCardIdx >= 0 &&
            TriadRun.moveBoardIdx >= 0)
        {
            PlaceCard(TriadRun.moveCardIdx, TriadRun.moveBoardIdx);
            return true;
        }

        return false;
    }

    private static bool PlaceCard(int which, int slot)
    {
        try
        {
            if (!TriadLocalClientStructs.TryGetBoard(out var addon, false))
            {
                return false;
            }

            // 出牌不關窗（棋盤要到局末才由遊戲收），「同窗只按一次」不適用；單次性靠下面寫回 TurnState。
            // 能加的只有「已經看過 PreFinalize 的實例不要再碰」——局末收板那幾幀就是它要擋的。
            if (!AddonPressGuard.TryTouch(BoardAddonName, &addon->AtkUnitBase))
            {
                return false;
            }

            Callback.Fire(&addon->AtkUnitBase, true, 14, (uint)slot + ((uint)which << 16));
            addon->AtkUnitBase.Update(0);
            addon->TurnState = 0;
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[TriadBoardAutomation] PlaceCard failed");
            return false;
        }
    }
}
