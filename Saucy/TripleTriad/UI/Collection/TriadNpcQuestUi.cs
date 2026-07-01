using ImGuiNET;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Saucy.IPC;
namespace Saucy.TripleTriad;

internal static class TriadNpcQuestUi
{
    private static uint _cachedQuestId;
    private static QuestSnapshot? _snapshot;

    public static void InvalidateCache()
    {
        _cachedQuestId = 0;
        _snapshot = null;
    }

    public static void DrawUnlockQuestIconRow(GameNpcInfo? npcInfo)
    {
        if (npcInfo == null || npcInfo.UnlockQuestId == 0)
        {
            return;
        }

        if (TriadMemoryReads.IsNpcUnlockedByProgress(npcInfo) ||
            TriadNpcUnlockHelper.IsUnlockRequirementSatisfied(npcInfo))
        {
            return;
        }

        var snapshot = GetSnapshot(npcInfo);
        if (snapshot.IsComplete)
        {
            return;
        }

        var questName = npcInfo.UnlockQuestName;
        if (string.IsNullOrEmpty(questName))
        {
            questName = $"任務 #{npcInfo.UnlockQuestId}";
        }

        var tooltip = BuildTooltip(snapshot, questName);

        using var questDisabled = ImRaii.Disabled(!snapshot.HasAutomationPath);
        ImGuiLayout.DrawIconTextRow(
            FontAwesomeIcon.BookOpen,
            tooltip,
            () => HandleUnlockQuestClick(npcInfo, questName, snapshot),
            () => ImGui.Text(questName));
    }

    private static void HandleUnlockQuestClick(GameNpcInfo npcInfo, string questName, QuestSnapshot snapshot)
    {
        if (!Questionable.IsInstalled)
        {
            Svc.Chat.Print("[Saucy] 請安裝 Questionable (/qst) 以便從 Saucy 開始任務。");
            return;
        }

        if (!snapshot.HasAutomationPath)
        {
            return;
        }

        InvalidateCache();
        snapshot = GetSnapshot(npcInfo);

        if (QuestionableTriad.TryStartSingleQuest(npcInfo.UnlockQuestId))
        {
            Svc.Chat.Print($"[Saucy] 已將「{questName}」傳送至 Questionable。");
            InvalidateCache();
            return;
        }

        if (!string.IsNullOrEmpty(snapshot.StatusMessage))
        {
            Svc.Chat.PrintError($"[Saucy] {snapshot.StatusMessage}");
        }
        else
        {
            Svc.Chat.PrintError($"[Saucy] Questionable 無法開始「{questName}」。");
        }
    }

    private static string? BuildTooltip(QuestSnapshot snapshot, string questName)
    {
        if (!Questionable.IsInstalled)
        {
            return "請安裝 Questionable (/qst) 以開始此任務。";
        }

        if (!snapshot.HasAutomationPath)
        {
            return "Questionable 尚未支援此任務。";
        }

        if (snapshot.CanStart)
        {
            return $"使用 Questionable 開始「{questName}」";
        }

        return snapshot.StatusMessage;
    }

    private static QuestSnapshot GetSnapshot(GameNpcInfo npcInfo)
    {
        var questId = npcInfo.UnlockQuestId;
        if (_snapshot != null && _cachedQuestId == questId)
        {
            return _snapshot;
        }

        _cachedQuestId = questId;
        _snapshot = BuildSnapshot(npcInfo);
        return _snapshot;
    }

    private static QuestSnapshot BuildSnapshot(GameNpcInfo npcInfo)
    {
        var questId = npcInfo.UnlockQuestId;
        if (TriadNpcUnlockHelper.IsUnlockRequirementSatisfied(npcInfo))
        {
            return CompleteSnapshot(QuestionableTriad.HasAutomationPath(questId));
        }

        if (!Questionable.IsInstalled)
        {
            return new()
            {
                IsComplete = false, HasAutomationPath = true, CanStart = false, StatusMessage = null
            };
        }

        var hasAutomationPath = QuestionableTriad.HasAutomationPath(questId);

        if (TriadMemoryReads.IsQuestCompleteOrUnneeded(questId) ||
            QuestionableTriad.IsQuestComplete(questId))
        {
            return CompleteSnapshot(hasAutomationPath);
        }

        if (!hasAutomationPath)
        {
            return new()
            {
                IsComplete = false, HasAutomationPath = false, CanStart = false, StatusMessage = null
            };
        }

        if (QuestionableTriad.IsQuestAccepted(questId))
        {
            return new()
            {
                IsComplete = false, HasAutomationPath = true, CanStart = false, StatusMessage = "任務已接取。"
            };
        }

        if (QuestionableTriad.IsQuestUnobtainable(questId))
        {
            return new()
            {
                IsComplete = false, HasAutomationPath = true, CanStart = false, StatusMessage = "Questionable 中無法使用此任務。"
            };
        }

        if (!QuestionableTriad.IsReadyToAccept(questId))
        {
            // Finished quests are also not "ready to accept" in Questionable — don't blame prerequisites for that.
            if (TriadMemoryReads.IsQuestCompleteOrUnneeded(questId))
            {
                return CompleteSnapshot(hasAutomationPath);
            }

            return new()
            {
                IsComplete = false, HasAutomationPath = true, CanStart = false, StatusMessage = "尚未達成前置條件（請確認 Questionable /qst）。"
            };
        }

        return new()
        {
            IsComplete = false, HasAutomationPath = true, CanStart = true, StatusMessage = null
        };
    }

    private static QuestSnapshot CompleteSnapshot(bool hasAutomationPath) =>
        new()
        {
            IsComplete = true, HasAutomationPath = hasAutomationPath, CanStart = false, StatusMessage = null
        };

    private sealed class QuestSnapshot
    {
        public required bool IsComplete { get; init; }
        public required bool HasAutomationPath { get; init; }
        public required bool CanStart { get; init; }
        public required string? StatusMessage { get; init; }
    }
}
