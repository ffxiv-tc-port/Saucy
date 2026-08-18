using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ECommons.LanguageHelpers;
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
        if (ImGui.Checkbox("Enable automation".Loc(), ref enabled))
        {
            if (enabled && !TriadNpcProximity.IsRelevantTriadNpcNearby())
            {
                var npcName = TriadNpcProximity.ResolveTriadNpcForProximityCheck()?.Name;
                DuoLog.Warning(string.IsNullOrEmpty(npcName)
                    ? "No Triple Triad NPC nearby (maybe get closer if in front of one).".Loc()
                    : "No Triple Triad NPC nearby (??). Maybe get closer if you're in front of one.".Loc(npcName));
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
            "Accepts match invites, selects a deck, and plays through rematches. Turn on before or during match prep.".Loc());

        var autoOpen = C.OpenAutomatically;
        if (ImGui.Checkbox("Open window when challenging an NPC".Loc(), ref autoOpen))
        {
            C.OpenAutomatically = autoOpen;
            C.Save();
        }

        var collectionUi = C.CollectionUiEnabled;
        if (ImGui.Checkbox("Gold Saucer card search panels".Loc(), ref collectionUi))
        {
            C.CollectionUiEnabled = collectionUi;
            C.Save();
        }

        ImGuiComponents.HelpMarker(
            ("Shows a searchable card list beside the Gold Saucer card UI, including Edit Deck (TriadBuddy-style [No.1] ordering). " +
            "Also shows NPC search on the main card collection screen.").Loc());

        ImGui.Dummy(new(0, 4));

        SaucyTheme.DrawCard("Deck".Loc(), null, DrawDeckBody);
        SaucyTheme.DrawCard("Run mode".Loc(), null, DrawRunModeBody);
        SaucyTheme.DrawCard("Travel".Loc(), "Map navigation".Loc(), TriadTravelMountUi.Draw);
        SaucyTheme.DrawCard("Notifications".Loc(), null, DrawNotificationsBody);
        SaucyTheme.DrawCard("Dependencies".Loc(), "Optional integrations".Loc(), TriadDependenciesUi.Draw);
    }

    private static void DrawDeckOptimizerSettings()
    {
        using var indent = ImRaii.PushIndent();
        var showOptimizerChatSpam = C.ShowOptimizerChatSpam;
        if (ImGui.Checkbox("Show deck automation chat spam".Loc(), ref showOptimizerChatSpam))
        {
            C.ShowOptimizerChatSpam = showOptimizerChatSpam;
            C.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            ("Shows [Saucy] deck optimizer, deck selection, and profile-write messages in chat. " +
            "Does not hide the game's own lines (e.g. \"in play for the next match\").").Loc());

        DrawDeckOptimizerMaxThreadsSlider();
        DrawDeckOptimizerTimeoutSlider();

        TriadDeckOptimizerStatusUi.DrawInline();
    }

    private static void DrawDeckOptimizerMaxThreadsSlider()
    {
        var threads = Configuration.ClampDeckOptimizerMaxThreads(C.DeckOptimizerMaxThreads);
        var maxCores = Environment.ProcessorCount;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Optimizer threads (0 = all)".Loc(), ref threads, 0, maxCores, threads == 0 ? "All".Loc() : "%d"))
        {
            C.DeckOptimizerMaxThreads = Configuration.ClampDeckOptimizerMaxThreads(threads);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            C.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Uses ?? of ?? logical cores for parallel deck tests.".Loc(SaucyParallelism.DeckOptimizerThreads, maxCores));
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Parallel threads while building an optimized deck (0 = all cores).".Loc() +
            (SaucyParallelism.IsWineHost
                ? "\n\nLinux / Wine (XLCore, Steam Deck): deck builds are capped to half your logical cores no matter what you pick here. Using every core for parallel deck builds can hard-crash the game under Wine.".Loc()
                : ""));
    }

    private static void DrawDeckOptimizerTimeoutSlider()
    {
        var timeout = Math.Clamp(C.DeckOptimizerTimeoutMinutes, 1, 15);
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Optimizer timeout (min)".Loc(), ref timeout, 1, 15, "%d min".Loc()))
        {
            C.DeckOptimizerTimeoutMinutes = Math.Clamp(timeout, 1, 15);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            C.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            ("Cancels a background deck build after this long. If the build is not finished by deck select, Saucy falls back to your best profile deck. " +
            "Map navigation waits until a deck is ready.").Loc());
    }

    private static void DrawDeckBody()
    {
        if (TriadRun.profileGS.GetPlayerDecks()!.Count() == 0)
        {
            ImGui.TextWrapped("Challenge an NPC once to load your profile decks here.".Loc());
            return;
        }

        var useAutoDeck = C.UseSimmedDeck;
        if (ImGui.Checkbox("Auto-pick best deck".Loc(), ref useAutoDeck))
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
            ("Picks a deck at deck select. Default: highest opening win % among your profile decks. " +
             "If none of those decks have 5 cards, Saucy uses the game's Recommended button.").Loc());

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
            if (ImGui.Checkbox("Use cached deck if available".Loc(), ref useCachedDeck))
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
                ("At match prep, loads a matching cached deck into profile slot 5 when one exists for this NPC and rules. " +
                "Auto-pick still sims your profile decks and picks the highest opening win %. Cannot be combined with Build optimized deck.").Loc());

            var alwaysBuild = C.AlwaysBuildOptimizedDeck;
            if (ImGui.Checkbox("Build optimized deck".Loc(), ref alwaysBuild))
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
                "At match prep, builds a deck from your owned cards when no valid cache or existing \"NPC (Saucy)\" profile deck fits this NPC and rules.".Loc() + " " +
                "Rebuilds if you have gained ?? or more new cards since the last build for that NPC.".Loc(TriadOptimizedDeckCacheStore.RebuildAfterNewCardCount) + " " +
                "Saves to profile slot 5 and selects it. Cannot be combined with Use cached deck.".Loc());

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
                ImGui.TextDisabled("Target NPC: ?? (calculating win %…)".Loc(targetedNpc.Name));
            }
            else
            {
                ImGui.TextDisabled("Target NPC: ??".Loc(targetedNpc.Name));
            }

            ImGui.Spacing();
        }

        var selectedDeck = C.SelectedDeckIndex;
        var decks = TriadRun.profileGS.GetPlayerDecks()!;
        var previewName = "(none)".Loc();
        if (selectedDeck == Configuration.GameRecommendedDeckIndex)
        {
            previewName = "Game recommended".Loc();
        }
        else if (selectedDeck >= 0 && selectedDeck < decks.Count() && decks[selectedDeck] != null)
        {
            var rawName = decks[selectedDeck]!.name ?? string.Empty;
            var previewData = targetedNpc != null ? TriadRun.GetDeckPreviewData(targetedNpc, selectedDeck) : null;
            previewName = TriadDeckEvalDisplay.FormatDeckNameWithWinChance(rawName, previewData);
            if (string.IsNullOrEmpty(previewName))
            {
                previewName = "(none)".Loc();
            }
        }

        ImGui.TextUnformatted("Select deck".Loc());
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Game recommended uses FFXIV's built-in deck suggestion for the current match (not Saucy sims).".Loc());
        ImGui.SetNextItemWidth(300f * ImGuiHelpers.GlobalScale);
        using var deckCombo = ImRaii.Combo("##SaucyDeckSelect", previewName);
        if (deckCombo)
        {
            if (ImGui.Selectable("(none)".Loc() + "##ClearDeckSelection", selectedDeck == -1))
            {
                C.SelectedDeckIndex = -1;
                C.Save();
            }

            if (ImGui.Selectable("Game recommended".Loc() + "##GameRecommendedDeck",
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
            "Choose when Saucy stops playing. On plugin load no option is selected and Saucy rematches until automation is disabled.".Loc());
        ImGui.Dummy(new(0, 4));

        if (ImGui.RadioButton("Fixed match count".Loc(), TriadRunSession.PlayXTimes))
        {
            CommitDraftMatchCount();
            TriadRunSession.ApplyRunMode(TriadRunMode.PlayXTimes, matchCount: DraftMatchCount);
        }

        if (TriadRunSession.PlayXTimes)
        {
            using var subIndent = ImRaii.PushIndent();
            ImGui.Text("How many times:".Loc());
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
            ImGui.TextDisabled("Matches left this session: ??".Loc(remaining));
        }

        if (ImGui.RadioButton("Stop after first card drop".Loc(), TriadRunSession.PlayUntilCardDrops))
        {
            TriadRunSession.ApplyRunMode(TriadRunMode.PlayUntilAnyCard);
        }

        if (ImGui.RadioButton("Farm all NPC cards once".Loc(), TriadRunSession.PlayUntilAllCardsDropOnce))
        {
            TriadRunSession.ApplyRunMode(TriadRunMode.PlayUntilAllCards, TriadRunTarget.Resolve());
        }

        if (TriadRunSession.NoRunModeSelected)
        {
            ImGui.TextDisabled("No stop condition — runs until automation is disabled.".Loc());
            ImGui.TextDisabled("Stops rematching while Duty Finder is ready.".Loc());
        }

        if (TriadRunSession.PlayUntilAllCardsDropOnce)
        {
            using var subIndent = ImRaii.PushIndent();

            TriadRunTarget.RefreshFromPrep();
            var runTargetNpc = TriadRunTarget.Resolve();
            var onMatchRegistration = uiReaderPrep.HasMatchRequestUI || TriadUiState.IsMatchRegistrationVisible();

            if (runTargetNpc != null)
            {
                ImGui.TextDisabled("NPC: ??".Loc(TriadNpcDB.Get().FindByID(runTargetNpc.npcId).Name));
                if (onMatchRegistration)
                {
                    ImGui.TextDisabled("(match registration open)".Loc());
                }
            }
            else if (onMatchRegistration)
            {
                ImGui.TextDisabled("NPC: reading match registration…".Loc());
            }
            else
            {
                ImGui.TextDisabled("NPC: open match registration to list missing cards.".Loc());
            }

            var onlyUnobtained = C.OnlyUnobtainedCards;
            if (ImGui.Checkbox("Missing cards only".Loc(), ref onlyUnobtained))
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
                ImGui.Text($"• {cardName} — {entry.Value}/1");
            }

            if (onlyUnobtained && runTargetNpc != null &&
                !TriadCardFarmSession.HasUnobtainedNpcRewards(runTargetNpc))
            {
                SaucyTheme.TextErrorWrapped("You already have every card from this NPC. Uncheck \"Missing cards only\" or choose a different NPC.".Loc());
            }
            else if (onlyUnobtained && TriadCardFarmSession.TempCardsWonList.Count == 0)
            {
                SaucyTheme.TextErrorWrapped("Start a match with an NPC to see which cards are still missing.".Loc());
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
        if (ImGui.Checkbox("Log out when run completes".Loc(), ref logOutAfterRun))
        {
            C.LogOutAfterTriadRun = logOutAfterRun;
            C.Save();
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Logs out of the game when a run finishes: fixed match count reaches zero, card drop mode triggers, or card farm completes.".Loc());

        var playSound = C.PlaySound;
        if (ImGui.Checkbox("Play sound when run completes".Loc(), ref playSound))
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
            ImGui.SetTooltip("Open sound folder — drop MP3s here to add your own.".Loc());
        }
    }

    private static string FormatDeckLabel(int deckId, string deckName, TriadNpc? targetNpc)
    {
        if (string.IsNullOrWhiteSpace(deckName))
        {
            deckName = "Deck ??".Loc(deckId + 1);
        }

        if (targetNpc == null)
        {
            return deckName;
        }

        return TriadDeckEvalDisplay.FormatDeckNameWithWinChance(deckName, TriadRun.GetDeckPreviewData(targetNpc, deckId));
    }
}
