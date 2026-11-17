using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.Text;
namespace Saucy.LeapOfFaith;

// 本檔契約：純診斷，只讀不按、不改任何流程；只在 framework 執行緒跑。
// 只在提早退出代按「是」之後啟動，每一場最多寫固定幾行（快照 3 行、條件變化與系統訊息各有上限）。
// 視窗清單當幀從 AllLoadedUnitsList 走完、只抄名字，不留任何原生指標。

/// <summary>登高跳跳樂提早退出：按下退出確認「是」之後還留在副本裡的那段時間，記下畫面上的視窗、條件旗標與系統訊息。</summary>
internal static unsafe class LeapOfFaithLeaveDiag
{
    private const string Prefix = "[Saucy] 登高跳跳樂提早退出診斷：";

    private static readonly long[] InsideSnapshotMs = [5_000, 15_000];

    private const long AfterLeaveSnapshotMs = 3_000;

    // 超過就收手：退出流程本身的時限是按下後 120 秒，這裡多留一些看最後到底有沒有離開。
    private const long InsideGiveUpMs = 300_000;

    private const long AfterLeaveGiveUpMs = 30_000;

    private const int MaxConditionLines = 30;

    private const int MaxChatLines = 12;

    private const int MaxChatChars = 160;

    private const int MaxUnits = 256;

    private static readonly string[] RewardCandidates = ["GoldSaucerReward", "MountainClimbingResult"];

    /// <summary>代按「是」的時刻；0＝沒在記。</summary>
    private static long yesAt;

    private static int insideSnapshotsDone;

    private static long betweenAreasAt;

    private static long leftAt;

    private static uint leftTerritory;

    private static int conditionLines;

    private static int chatLines;

    public static bool IsActive => yesAt != 0;

    /// <summary>代按「是」成功的那一幀呼叫。</summary>
    public static void Begin(long now)
    {
        yesAt = now;
        insideSnapshotsDone = 0;
        betweenAreasAt = 0;
        leftAt = 0;
        leftTerritory = 0;
        conditionLines = 0;
        chatLines = 0;
    }

    public static void Stop() => yesAt = 0;

    /// <summary>每幀由提早退出呼叫（在不在副本裡都要呼叫）；沒在記時只比一個欄位就回。</summary>
    public static void Tick(bool inside)
    {
        if (yesAt == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        var since = now - yesAt;
        if (betweenAreasAt == 0 && (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]))
        {
            betweenAreasAt = now;
        }

        if (inside && leftAt == 0)
        {
            if (insideSnapshotsDone < InsideSnapshotMs.Length && since >= InsideSnapshotMs[insideSnapshotsDone])
            {
                Snapshot($"按「是」後 {since / 1000.0:0.0} 秒還在副本裡");
                while (insideSnapshotsDone < InsideSnapshotMs.Length && since >= InsideSnapshotMs[insideSnapshotsDone])
                {
                    insideSnapshotsDone++;
                }
            }

            if (since > InsideGiveUpMs)
            {
                Svc.Log.Information($"{Prefix}按「是」後 {InsideGiveUpMs / 1000} 秒還在副本裡，不再記錄");
                Stop();
            }

            return;
        }

        if (leftAt == 0)
        {
            leftAt = now;
            leftTerritory = (uint)Svc.ClientState.TerritoryType;
            var loading = betweenAreasAt != 0 ? $"{(betweenAreasAt - yesAt) / 1000.0:0.0} 秒開始切換區域（BetweenAreas）、" : "沒看到切換區域旗標（BetweenAreas）、";
            Svc.Log.Information($"{Prefix}離開副本：按「是」後 {loading}{since / 1000.0:0.0} 秒區域變成 {leftTerritory}");
            return;
        }

        if (now - leftAt > AfterLeaveGiveUpMs)
        {
            Svc.Log.Information($"{Prefix}離開副本 {AfterLeaveGiveUpMs / 1000} 秒都還在讀取或角色不在，不記離場後的視窗");
            Stop();
            return;
        }

        if (now - leftAt >= AfterLeaveSnapshotMs && Player.Available && !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            Snapshot($"離開副本後 {(now - leftAt) / 1000.0:0.0} 秒（按「是」後 {since / 1000.0:0.0} 秒）");
            Stop();
        }
    }

    public static void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (yesAt == 0 || conditionLines > MaxConditionLines)
        {
            return;
        }

        try
        {
            var since = (Environment.TickCount64 - yesAt) / 1000.0;
            conditionLines++;
            Svc.Log.Information(conditionLines > MaxConditionLines
                ? $"{Prefix}條件變化超過 {MaxConditionLines} 次，這一場之後不再記"
                : $"{Prefix}條件變化（按「是」後 {since:0.0} 秒）：{flag}={value}");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"{Prefix}記錄條件變化失敗：{ex.Message}");
        }
    }

    // 只記遊戲自己的系統類訊息（低 7 位元＝頻道），不記玩家聊天。
    public static void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (yesAt == 0 || chatLines >= MaxChatLines)
        {
            return;
        }

        var channel = (int)type & 0x7F;
        if (channel is not (57 or 58 or 59 or 60 or 61 or 68))
        {
            return;
        }

        try
        {
            chatLines++;
            var text = message.TextValue.ReplaceLineEndings("⏎");
            if (text.Length > MaxChatChars)
            {
                text = text[..MaxChatChars] + "…";
            }

            Svc.Log.Information($"{Prefix}系統訊息（按「是」後 {(Environment.TickCount64 - yesAt) / 1000.0:0.0} 秒，類型 {(int)type}）：{text}");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"{Prefix}記錄系統訊息失敗：{ex.Message}");
        }
    }

    private static void Snapshot(string when)
    {
        var sb = new StringBuilder(Prefix).Append(when).Append("：區域 ").Append((uint)Svc.ClientState.TerritoryType);
        sb.Append("、條件 [").Append(string.Join(",", Svc.Condition.AsReadOnlySet())).Append(']');

        var visible = new List<string>();
        var states = new string[RewardCandidates.Length];
        Array.Fill(states, "未載入");
        var loaded = 0;
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            sb.Append("、讀不到視窗清單");
        }
        else
        {
            var units = &manager->AtkUnitManager.AllLoadedUnitsList;
            var count = Math.Min((int)units->Count, MaxUnits);
            for (var i = 0; i < count; i++)
            {
                var addon = units->Entries[i].Value;
                if (addon == null)
                {
                    continue;
                }

                loaded++;
                var name = addon->NameString;
                var shown = addon->IsVisible;
                if (shown)
                {
                    visible.Add(name);
                }

                var idx = Array.IndexOf(RewardCandidates, name);
                if (idx >= 0)
                {
                    states[idx] = shown ? (addon->IsReady ? "顯示中" : "顯示中（未就緒）") : "已載入未顯示";
                }
            }

            visible.Sort(StringComparer.Ordinal);
            sb.Append("、已載入視窗 ").Append(loaded).Append(" 個，畫面上 ").Append(visible.Count).Append(" 個：").Append(string.Join(",", visible));
        }

        sb.Append("；結算視窗候選");
        for (var i = 0; i < RewardCandidates.Length; i++)
        {
            sb.Append(' ').Append(RewardCandidates[i]).Append('=').Append(states[i]);
        }

        Svc.Log.Information(sb.ToString());
    }
}
