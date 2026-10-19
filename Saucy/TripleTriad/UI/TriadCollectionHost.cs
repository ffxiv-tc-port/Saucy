using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
namespace Saucy.TripleTriad;

internal sealed class TriadCollectionHost : IDisposable

{
    private readonly TriadCardInfoWindow _cardInfoWindow;

    private readonly TriadCardSearchWindow _cardSearchWindow;

    private readonly TriadNpcStatsWindow _npcStatsWindow;

    private readonly StatTracker _statTracker = new();

    private readonly UIReaderTriadCardList _uiReaderCardList = new();

    private readonly WindowSystem _windowSystem = new("SaucyTriadCollection");

    private bool _isDrawing;

    private UIStateTriadResults? _lastRecordedResult;

    private bool _sawGameDataReady;

    public TriadCollectionHost(IDalamudPluginInterface pluginInterface)

    {
        _npcStatsWindow = new(_statTracker);

        _cardSearchWindow = new(_uiReaderCardList, _npcStatsWindow);

        _cardInfoWindow = new(_uiReaderCardList, _cardSearchWindow);

        _windowSystem.AddWindow(_cardSearchWindow);

        _windowSystem.AddWindow(_cardInfoWindow);

        _windowSystem.AddWindow(_npcStatsWindow);

        uiReaderScheduler.AddObservedAddon(new GatedCardListReader(_uiReaderCardList));

        pluginInterface.UiBuilder.Draw += OnDraw;
    }

    /// <summary>框架執行緒呼叫。每場對戰結果記一次 NPC 統計；勝負都沒讀到的結果不記。</summary>
    /// <remarks>結果讀取器在同一扇結果視窗可能逾時補發，補發的是同一個結果物件，以參考相等去重。</remarks>
    public void RecordMatchResult(UIStateTriadResults result)
    {
        if (!result.isWin && !result.isDraw && !result.isLose)
        {
            return;
        }

        if (ReferenceEquals(result, _lastRecordedResult))
        {
            return;
        }

        _lastRecordedResult = result;
        if (TriadRun.matchNpc is not { } npc)
        {
            Svc.Log.Information("[NPC 統計] 這場沒在棋盤上辨識到對手 NPC（PvP 或名字對不上），不記錄");
            return;
        }

        if (!_statTracker.OnMatchFinished(npc.Id, result))
        {
            Svc.Log.Information($"[NPC 統計] {npc.Name}（id {npc.Id}）不在 NPC 資料表裡，不記錄");
            return;
        }

        Svc.Log.Debug($"[NPC 統計] 已記錄 {npc.Name}：勝={result.isWin} 和={result.isDraw} 負={result.isLose} MGP={result.numMGP} 卡={result.cardItemId}");
    }

    public void Dispose()

    {
        Svc.PluginInterface.UiBuilder.Draw -= OnDraw;

        _windowSystem.RemoveAllWindows();

        _cardSearchWindow.Dispose();

        _cardInfoWindow.Dispose();

        _npcStatsWindow.Dispose();
    }

    private void OnDraw()

    {
        if (_isDrawing || !C.CollectionUiEnabled || !Svc.ClientState.IsLoggedIn || !_uiReaderCardList.IsVisible)

        {
            return;
        }

        if (!dataLoader.IsDataReady)

        {
            return;
        }

        _isDrawing = true;

        try

        {
            if (!_sawGameDataReady)

            {
                _sawGameDataReady = true;

                _cardSearchWindow.OnGameDataReady();
            }

            _cardSearchWindow.SyncVisibility();

            _cardInfoWindow.SyncVisibility();

            using var theme = SaucyTheme.PushScope();
            _windowSystem.Draw();
        }

        finally

        {
            _isDrawing = false;
        }
    }

    private sealed class GatedCardListReader(UIReaderTriadCardList inner) : IUIReader

    {
        public string GetAddonName() => inner.GetAddonName();

        public void OnAddonLost()

        {
            if (C.CollectionUiEnabled)

            {
                inner.OnAddonLost();
            }
        }

        public void OnAddonShown(nint addonPtr)

        {
            if (C.CollectionUiEnabled)

            {
                inner.OnAddonShown(addonPtr);
            }
        }

        public void OnAddonUpdate(nint addonPtr)

        {
            if (C.CollectionUiEnabled)

            {
                inner.OnAddonUpdate(addonPtr);
            }
        }
    }
}
