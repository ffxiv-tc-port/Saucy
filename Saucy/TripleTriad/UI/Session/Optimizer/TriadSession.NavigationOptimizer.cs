#nullable disable
using ECommons.LanguageHelpers;
using Saucy.IPC;
using System;
namespace Saucy.TripleTriad.UI;

public partial class TriadSession
{
    public bool IsNavigationBlockedWaitingForOptimizer(TriadNpc npc)
    {
        if (!ShouldBuildOptimizedDeck() || npc == null)
        {
            return false;
        }

        EnsureNavigationDeckOptimizerStarted(npc);

        // 鎖內不做聊天／log／存設定／寫牌組快取檔：這段的呼叫鏈經過 ResolveRegionModsForNpc
        // 的 prep 同步子樹會走到那幾種副作用，先收起來，出鎖之後才做。
        using var deferScope = TriadDeferredSideEffects.Begin();
        lock (_preGameLock)
        {
            var sessionKey = BuildOptimizerSessionKey(npc, ResolveRegionModsForNpc(npc));
            if (HasOptimizedDeckApplied && _optimizerSessionKey == sessionKey)
            {
                return false;
            }
        }

        return OptimizerInProgress;
    }

    public void EnsureNavigationDeckOptimizerStarted(TriadNpc npc)
    {
        if (TriadNpcUnlockHelper.TryReject(npc, out var _))
        {
            TriadMapNavigation.CancelActiveNavigation();
            return;
        }

        if (!ShouldBuildOptimizedDeck())
        {
            return;
        }

        if (TriadMapNavigation.IsExecutingMultiAreaRoute ||
            (TriadMapNavigation.IsNavigationActive &&
             !TriadMapNavigation.IsInNavigationTargetTerritory()) ||
            Vnavmesh.ShouldDeferDeckOptimizerWork())
        {
            return;
        }

        if (preGameNpc?.Id != npc.Id)
        {
            OnNpcSelected(npc, [], true, true);
            return;
        }

        // 鎖內不做聊天／log／存設定：這段的呼叫鏈會走到那三種副作用，先收起來出鎖再做。
        using var chatScope = TriadDeferredSideEffects.Begin();
        lock (_preGameLock)
        {
            if (OptimizerInProgress)
            {
                return;
            }
            var sessionKey = BuildOptimizerSessionKey(npc, ResolveRegionModsForNpc(npc));
            if (HasOptimizedDeckApplied && _optimizerSessionKey == sessionKey)
            {
                return;
            }

            // 出鎖後才做:StartDeckOptimizer 的前導會打別的外掛的 IPC。
            var startMods = ResolveRegionModsForNpc(npc);
            TriadDeferredSideEffects.RunAfterLock(
                () => StartDeckOptimizer(npc, startMods, navigationRequest: true));
        }
    }

    private bool TryRestartNavigationDeckOptimizer(TriadDeckOptimizerResult result)
    {
        if (!result.NavigationRequest ||
            !TriadMapNavigation.IsNavigationActive ||
            result.Npc == null ||
            !ShouldBuildOptimizedDeck())
        {
            return false;
        }

        var sessionKey = BuildOptimizerSessionKey(result.Npc, ResolveRegionModsForNpc(result.Npc));
        if (!string.Equals(_navigationOptimizerRetrySessionKey, sessionKey, StringComparison.Ordinal))
        {
            _navigationOptimizerRetrySessionKey = sessionKey;
            _navigationOptimizerRetryCount = 0;
        }

        if (_navigationOptimizerRetryCount >= MaxNavigationOptimizerRetries)
        {
            return false;
        }

        _navigationOptimizerRetryCount++;
        PrintOptimizerChat(
            "[Saucy] " + "Deck optimization interrupted for ??; retry ??/??…".Loc(result.Npc.Name, _navigationOptimizerRetryCount, MaxNavigationOptimizerRetries));
        _optimizerTimedOut = false;
        // 出鎖後才做:同上。
        var restartNpc = result.Npc;
        var restartMods = ResolveRegionModsForNpc(restartNpc);
        TriadDeferredSideEffects.RunAfterLock(
            () => StartDeckOptimizer(restartNpc, restartMods, navigationRequest: true));
        return true;
    }
}
