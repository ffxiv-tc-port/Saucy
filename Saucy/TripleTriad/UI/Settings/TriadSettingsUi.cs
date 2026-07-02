using ImGuiNET;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
namespace Saucy.TripleTriad;

internal static class TriadSettingsUi
{
    private static int DraftMatchCount = 1;

    public static void Draw()
    {
        DraftMatchCount = Math.Max(1, C.TriadMatchCount);
        var runTargetNpc = TriadRunTarget.Resolve();

        var enabled = TriadRunSession.ModuleEnabled;
        if (ImGui.Checkbox("啟用自動化", ref enabled))
        {
            if (enabled && !TriadNpcProximity.IsRelevantTriadNpcNearby())
            {
                var npcName = TriadNpcProximity.ResolveTriadNpcForProximityCheck()?.Name;
                DuoLog.Warning(string.IsNullOrEmpty(npcName)
                    ? "附近沒有九宮牌 NPC（如果就站在對方面前，試著再靠近一點）。"
                    : $"附近沒有九宮牌 NPC（{npcName}）。如果就站在對方面前，試著再靠近一點。");
            }
            else
            {
                TriadRunSession.ModuleEnabled = enabled;
                if (enabled)
                {
                    CommitDraftMatchCount();
                    TriadRunSession.BeginAutomationSession();
                    TriadCardFarmSession.SyncDisplay(runTargetNpc);
                    TriadAutomator.RunModule();
                }
                else
                {
                    TriadCardFarmSession.DeactivateSession(clearProgress: true);
                }
            }
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "接受對戰邀請、選擇卡組並自動進行連戰。請在準備對戰前或對戰準備期間開啟。");

        var autoOpen = C.OpenAutomatically;
        if (ImGui.Checkbox("挑戰 NPC 時自動開啟視窗", ref autoOpen))
        {
            C.OpenAutomatically = autoOpen;
            C.Save();
        }

        var collectionUi = C.CollectionUiEnabled;
        if (ImGui.Checkbox("金碟遊樂園卡片搜尋面板", ref collectionUi))
        {
            C.CollectionUiEnabled = collectionUi;
            C.Save();
        }

        ImGuiComponents.HelpMarker(
            "在金碟遊樂園卡片介面旁顯示可搜尋的卡片清單，包含編輯卡組（TriadBuddy 風格的 [No.1] 排序）。" +
            "同時會在卡片收藏主畫面顯示 NPC 搜尋功能。");

        ImGui.Dummy(new(0, 4));

        SaucyTheme.DrawCard("卡組", null, DrawDeckBody);
        SaucyTheme.DrawCard("執行模式", null, DrawRunModeBody);
        SaucyTheme.DrawCard("移動", "地圖導航", TriadTravelMountUi.Draw);
        SaucyTheme.DrawCard("通知", null, DrawNotificationsBody);
        SaucyTheme.DrawCard("相依項目", "選用整合功能", TriadDependenciesUi.Draw);
    }

    private static void DrawDeckOptimizerSettings()
    {
        using var indent = ImRaii.PushIndent();
        var showOptimizerChatSpam = C.ShowOptimizerChatSpam;
        if (ImGui.Checkbox("顯示卡組自動化聊天訊息", ref showOptimizerChatSpam))
        {
            C.ShowOptimizerChatSpam = showOptimizerChatSpam;
            C.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "在聊天欄顯示 [Saucy] 卡組最佳化、卡組選擇及設定檔寫入訊息。" +
            "不會隱藏遊戲本身的訊息（例如「將於下一場對戰中使用」）。");

        DrawDeckOptimizerMaxThreadsSlider();
        DrawDeckOptimizerTimeoutSlider();

        TriadDeckOptimizerStatusUi.DrawInline();
    }

    private static void DrawDeckOptimizerMaxThreadsSlider()
    {
        var threads = Configuration.ClampDeckOptimizerMaxThreads(C.DeckOptimizerMaxThreads);
        var maxCores = Environment.ProcessorCount;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("最佳化執行緒數（0 = 全部）", ref threads, 0, maxCores, threads == 0 ? "全部" : "%d"))
        {
            C.DeckOptimizerMaxThreads = Configuration.ClampDeckOptimizerMaxThreads(threads);
            C.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"平行卡組測試將使用 {maxCores} 個邏輯核心中的 {SaucyParallelism.DeckOptimizerThreads} 個。");
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "建構最佳化卡組時使用的平行執行緒數（0 = 全部核心）。" +
            (SaucyParallelism.IsWineHost
                ? "\n\nLinux / Wine（XLCore、Steam Deck）：無論此處如何設定，卡組建構都會限制為邏輯核心數的一半。在 Wine 下使用全部核心進行平行卡組建構可能導致遊戲直接崩潰。"
                : ""));
    }

    private static void DrawDeckOptimizerTimeoutSlider()
    {
        var timeout = Math.Clamp(C.DeckOptimizerTimeoutMinutes, 1, 15);
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("最佳化逾時時間（分鐘）", ref timeout, 1, 15, "%d 分鐘"))
        {
            C.DeckOptimizerTimeoutMinutes = Math.Clamp(timeout, 1, 15);
            C.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "超過此時間後將取消背景卡組建構。若在選擇卡組時仍未完成建構，Saucy 會改用你最佳的設定檔卡組。" +
            "地圖導航會等待卡組準備完成後才會開始。");
    }

    private static void DrawDeckBody()
    {
        if (TriadRun.profileGS.GetPlayerDecks()!.Count() == 0)
        {
            ImGui.TextWrapped("先挑戰一次 NPC 以載入此處的設定檔卡組。");
            return;
        }

        var useAutoDeck = C.UseSimmedDeck;
        if (ImGui.Checkbox("自動選擇最佳卡組", ref useAutoDeck))
        {
            C.UseSimmedDeck = useAutoDeck;
            C.Save();
            if (!useAutoDeck)
            {
                TriadRun.ResetDeckOptimizerState();
            }
        }

        var targetedNpc = TriadTargetNpc.FromWorldTarget();
        var autoPickNpc = targetedNpc ?? TriadRun.preGameNpc;
        if (C.UseSimmedDeck && autoPickNpc != null)
        {
            var autoPickSummary = TriadRun.GetAutoPickDeckSummary(autoPickNpc);
            if (!string.IsNullOrEmpty(autoPickSummary))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(autoPickSummary);
            }
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "在選擇卡組時自動挑選卡組。預設：選擇你設定檔卡組中開局勝率最高的一副。");

        if (C.UseSimmedDeck)
        {
            using var indent = ImRaii.PushIndent();

            if (autoPickNpc != null)
            {
                TriadRun.RefreshPrepRulesFromLive();
                TriadRun.EnsurePreviewEvalForNpc(autoPickNpc);
                if (TriadRun.ShouldBuildOptimizedDeck())
                {
                    TriadRun.EnsureOptimizedDeckPreviewEval(autoPickNpc);
                }
            }

            if (C.AlwaysBuildOptimizedDeck && C.UseCachedOptimizedDeckIfAvailable)
            {
                C.UseCachedOptimizedDeckIfAvailable = false;
                C.Save();
            }

            var useCachedDeck = C.UseCachedOptimizedDeckIfAvailable;
            if (ImGui.Checkbox("若有快取卡組則使用快取卡組", ref useCachedDeck))
            {
                if (useCachedDeck)
                {
                    C.UseCachedOptimizedDeckIfAvailable = true;
                    C.AlwaysBuildOptimizedDeck = false;
                    if (!TriadCardFarmSession.IsModeActive())
                    {
                        TriadRun.CancelDeckOptimizerJob(userCancelled: true);
                    }
                }
                else
                {
                    C.UseCachedOptimizedDeckIfAvailable = false;
                    TriadRun.ResetDeckOptimizerState();
                }

                C.Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker(
                "在準備對戰時，若此 NPC 與規則存在符合的快取卡組，會將其載入設定檔卡組槽 5。" +
                "自動選擇仍會模擬你的設定檔卡組並挑選開局勝率最高者。無法與「建構最佳化卡組」同時使用。");

            var alwaysBuild = C.AlwaysBuildOptimizedDeck;
            if (ImGui.Checkbox("建構最佳化卡組", ref alwaysBuild))
            {
                if (alwaysBuild)
                {
                    C.AlwaysBuildOptimizedDeck = true;
                    C.UseCachedOptimizedDeckIfAvailable = false;
                    TriadRun.ResetDeckOptimizerState();
                }
                else
                {
                    C.AlwaysBuildOptimizedDeck = false;
                    if (!TriadCardFarmSession.IsModeActive())
                    {
                        TriadRun.CancelDeckOptimizerJob(userCancelled: true);
                    }
                }

                C.Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker(
                "在準備對戰時，若沒有符合此 NPC 與規則的有效快取或現有「NPC (Saucy)」設定檔卡組，會使用你擁有的卡片建構卡組。" +
                $"若自上次為該 NPC 建構卡組以來已獲得 {TriadOptimizedDeckCacheStore.RebuildAfterNewCardCount} 張以上新卡，則會重新建構。" +
                "會儲存至設定檔卡組槽 5 並自動選擇。無法與「使用快取卡組」同時使用。");

            if (C.AlwaysBuildOptimizedDeck)
            {
                DrawDeckOptimizerSettings();
            }

            return;
        }

        if (targetedNpc != null)
        {
            TriadRun.RefreshPrepRulesFromLive();
            TriadRun.EnsurePreviewEvalForNpc(targetedNpc);

            if (TriadRun.IsPreviewEvalPendingForNpc(targetedNpc))
            {
                ImGui.TextDisabled($"目標 NPC：{targetedNpc.Name}（計算勝率中…）");
            }
            else
            {
                ImGui.TextDisabled($"目標 NPC：{targetedNpc.Name}");
            }

            ImGui.Spacing();
        }

        var selectedDeck = C.SelectedDeckIndex;
        var decks = TriadRun.profileGS.GetPlayerDecks()!;
        var previewName = "（無）";
        if (selectedDeck == Configuration.GameRecommendedDeckIndex)
        {
            previewName = "遊戲推薦";
        }
        else if (selectedDeck >= 0 && selectedDeck < decks.Count() && decks[selectedDeck] != null)
        {
            var rawName = decks[selectedDeck]!.name ?? string.Empty;
            var previewData = targetedNpc != null ? TriadRun.GetDeckPreviewData(targetedNpc, selectedDeck) : null;
            previewName = TriadDeckEvalDisplay.FormatDeckNameWithWinChance(rawName, previewData);
            if (string.IsNullOrEmpty(previewName))
            {
                previewName = "（無）";
            }
        }

        ImGui.TextUnformatted("選擇卡組");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "遊戲推薦使用 FFXIV 內建的卡組建議進行本場對戰（並非 Saucy 的模擬結果）。");
        ImGui.SetNextItemWidth(300f * ImGuiHelpers.GlobalScale);
        using var deckCombo = ImRaii.Combo("##SaucyDeckSelect", previewName);
        if (deckCombo)
        {
            if (ImGui.Selectable("（無）##ClearDeckSelection", selectedDeck == -1))
            {
                C.SelectedDeckIndex = -1;
                C.Save();
            }

            if (ImGui.Selectable("遊戲推薦##GameRecommendedDeck",
                selectedDeck == Configuration.GameRecommendedDeckIndex))
            {
                C.SelectedDeckIndex = Configuration.GameRecommendedDeckIndex;
                C.Save();
            }

            foreach (var deck in decks)
            {
                if (deck is null)
                {
                    continue;
                }

                if (ImGui.Selectable(FormatDeckLabel(deck.id, deck.name, targetedNpc), deck.id == selectedDeck))
                {
                    C.SelectedDeckIndex = deck.id;
                    C.Save();
                }
            }
        }
    }

    private static void DrawRunModeBody()
    {
        ImGui.TextWrapped(
            "選擇 Saucy 何時停止對戰。插件載入時預設未選擇任何選項，Saucy 會持續連戰直到自動化被停用。");
        ImGui.Dummy(new(0, 4));

        if (ImGui.RadioButton("固定對戰場數", TriadRunSession.PlayXTimes))
        {
            CommitDraftMatchCount();
            TriadRunSession.ApplyRunMode(TriadRunMode.PlayXTimes, matchCount: DraftMatchCount);
        }

        if (TriadRunSession.PlayXTimes)
        {
            using var subIndent = ImRaii.PushIndent();
            ImGui.Text("次數：");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(56f * ImGuiHelpers.GlobalScale);
            var count = Math.Max(1, C.TriadMatchCount);
            if (ImGui.InputInt("###TriadMatchCount", ref count) ||
                ImGui.IsItemDeactivatedAfterEdit())
            {
                ApplyMatchCount(count);
            }

            DraftMatchCount = Math.Max(1, count);

            var remaining = TriadRunSession.ModuleEnabled
                ? Math.Max(0, TriadRunSession.NumberOfTimes)
                : Math.Max(1, C.TriadMatchCount);
            ImGui.TextDisabled($"本場次剩餘對戰數：{remaining}");
        }

        if (ImGui.RadioButton("首次掉落卡片後停止", TriadRunSession.PlayUntilCardDrops))
        {
            TriadRunSession.ApplyRunMode(TriadRunMode.PlayUntilAnyCard);
        }

        if (ImGui.RadioButton("刷取此 NPC 所有卡片各一張", TriadRunSession.PlayUntilAllCardsDropOnce))
        {
            TriadRunSession.ApplyRunMode(TriadRunMode.PlayUntilAllCards, TriadRunTarget.Resolve());
        }

        if (TriadRunSession.NoRunModeSelected)
        {
            ImGui.TextDisabled("未設定停止條件 — 將持續執行直到自動化被停用。");
            ImGui.TextDisabled("在多人任務搜尋器準備完成時會停止連戰。");
        }

        if (TriadRunSession.PlayUntilAllCardsDropOnce)
        {
            using var subIndent = ImRaii.PushIndent();

            TriadRunTarget.RefreshFromPrep();
            var runTargetNpc = TriadRunTarget.Resolve();
            var onMatchRegistration = uiReaderPrep.HasMatchRequestUI || TriadUiState.IsMatchRegistrationVisible();

            if (runTargetNpc != null)
            {
                ImGui.TextDisabled($"NPC：{TriadNpcDB.Get().FindByID(runTargetNpc.npcId).Name}");
                if (onMatchRegistration)
                {
                    ImGui.TextDisabled("（對戰登記視窗已開啟）");
                }
            }
            else if (onMatchRegistration)
            {
                ImGui.TextDisabled("NPC：讀取對戰登記中…");
            }
            else
            {
                ImGui.TextDisabled("NPC：請開啟對戰登記視窗以列出尚缺的卡片。");
            }

            var onlyUnobtained = C.OnlyUnobtainedCards;
            if (ImGui.Checkbox("僅顯示尚缺卡片", ref onlyUnobtained))
            {
                C.OnlyUnobtainedCards = onlyUnobtained;
                C.Save();
                if (runTargetNpc != null)
                {
                    TriadCardFarmSession.StartTargets(runTargetNpc);
                }
            }

            if (runTargetNpc != null)
            {
                TriadCardFarmSession.SyncDisplay(runTargetNpc);
            }

            foreach (var entry in TriadCardFarmSession.TempCardsWonList)
            {
                var cardInfo = GameCardDB.Get().FindById((int)entry.Key);
                var cardName = cardInfo != null
                    ? TriadCardDB.Get().FindById(cardInfo.CardId)?.Name ?? $"Card #{entry.Key}"
                    : $"Card #{entry.Key}";
                ImGui.Text($"\u2022 {cardName} \u2014 {entry.Value}/1");
            }

            if (onlyUnobtained && runTargetNpc != null &&
                !TriadCardFarmSession.HasUnobtainedNpcRewards(runTargetNpc))
            {
                SaucyTheme.TextErrorWrapped("\u4f60\u5df2\u7d93\u64c1\u6709\u9019\u4f4d NPC \u7684\u6240\u6709\u5361\u7247\u3002\u8acb\u53d6\u6d88\u52fe\u9078\u300c\u50c5\u986f\u793a\u5c1a\u7f3a\u5361\u7247\u300d\u6216\u9078\u64c7\u5176\u4ed6 NPC\u3002");
            }
            else if (onlyUnobtained && TriadCardFarmSession.TempCardsWonList.Count == 0)
            {
                SaucyTheme.TextErrorWrapped("\u8207 NPC \u958b\u59cb\u4e00\u5834\u5c0d\u6230\u4ee5\u67e5\u770b\u5c1a\u7f3a\u7684\u5361\u7247\u3002");
            }
        }
    }

    private static void CommitDraftMatchCount()
    {
        if (!TriadRunSession.PlayXTimes)
        {
            return;
        }

        ApplyMatchCount(DraftMatchCount);
    }

    private static void ApplyMatchCount(int count) => TriadRunSession.SyncPlayXTimesSession(Math.Max(1, count), true);

    private static void DrawNotificationsBody()
    {
        var logOutAfterRun = C.LogOutAfterTriadRun;
        if (ImGui.Checkbox("執行完成後登出遊戲", ref logOutAfterRun))
        {
            C.LogOutAfterTriadRun = logOutAfterRun;
            C.Save();
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "當執行完成時登出遊戲：固定場數歸零、卡片掉落模式觸發，或卡片刷取完成。");

        var playSound = C.PlaySound;
        if (ImGui.Checkbox("執行完成後播放音效", ref playSound))
        {
            C.PlaySound = playSound;
            C.Save();
        }

        if (playSound)
        {
            using var _ = ImRaii.PushIndent();
            DrawSoundPicker();
        }
    }

    private static void DrawSoundPicker()
    {
        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
        using var soundCombo = ImRaii.Combo("###SelectSound", C.SelectedSound);
        if (soundCombo)
        {
            var path = Path.Combine(Svc.PluginInterface.AssemblyLocation.Directory!.FullName, "Sounds");
            Directory.CreateDirectory(path);
            foreach (var file in new DirectoryInfo(path).GetFiles())
            {
                var name = Path.GetFileNameWithoutExtension(file.FullName);
                if (ImGui.Selectable(name, C.SelectedSound == name))
                {
                    C.SelectedSound = name;
                    C.Save();
                }
            }
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.FolderOpen))
        {
            Process.Start("explorer.exe", Path.Combine(Svc.PluginInterface.AssemblyLocation.Directory!.FullName, "Sounds"));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("開啟音效資料夾 — 將 MP3 檔案放入此處即可新增自訂音效。");
        }
    }

    private static string FormatDeckLabel(int deckId, string deckName, TriadNpc? targetNpc)
    {
        if (string.IsNullOrWhiteSpace(deckName))
        {
            deckName = $"卡組 {deckId + 1}";
        }

        if (targetNpc == null)
        {
            return deckName;
        }

        return TriadDeckEvalDisplay.FormatDeckNameWithWinChance(deckName, TriadRun.GetDeckPreviewData(targetNpc, deckId));
    }
}
