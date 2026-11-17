using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Saucy.AirForce;
using Saucy.Cliffhanger;
using Saucy.Framework;
using Saucy.IPC;
using Saucy.LeapOfFaith;
using Saucy.OtherGames;
using System;
using System.Collections.Generic;
using System.Numerics;
using JumboNumberPlan = Saucy.JumboCactpot.JumboCactpotNumberPlan;
namespace Saucy;

public unsafe partial class PluginUI
{
    /// <summary>力量表難度下拉選單的顯示名稱——三個成員本身是怪物名(泰坦/毛爾波爾/仙人掌怪)，
    /// 沿用本檔既有註解與提示文字裡已經在用的譯名，不是新創。</summary>
    private static readonly Dictionary<global::Saucy.OutOnALimb.LimbDifficulty, string> LimbDifficultyNames = new()
    {
        [global::Saucy.OutOnALimb.LimbDifficulty.Titan] = "Titan".Loc(),
        [global::Saucy.OutOnALimb.LimbDifficulty.Morbol] = "Morbol".Loc(),
        [global::Saucy.OutOnALimb.LimbDifficulty.Cactuar] = "Cactuar".Loc(),
    };

    private static void DrawOutOnALimbPanel()
    {
        DrawPanelHeader("Out on a Limb".Loc(), "Chocobo Square logging machine".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.OutOnALimb);
        if (ImGui.Checkbox("啟用##OutOnALimb", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.OutOnALimb, enabled);
            C.Save();
        }

        ImGui.TextWrapped("啟用後，孤樹無援的機台畫面開著時會自動幫你停力量表、並依每一刀的系統訊息手感" +
                          "（沒手感／接觸到／很接近／正中目標）逐步收斂出最佳砍伐位置後揮斧。" +
                          "除非你另外打開下面的「連續遊玩」並按下開始，" +
                          "模組不會自己去找機台、不會自己互動、也不會自己開始新的一局。");

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("這是伺服器看得見的行為模式：自動出手沒有人類的反應延遲，連續遊玩時尤其明顯。" +
                             "模組只讀取畫面上本來就顯示給你看的資料（機台面板欄位、指針刻度、樹的量表），" +
                             "不讀寫遊戲封包、不修改遊戲記憶體，出手方式就是按下畫面上原本就有的按鈕。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();

            var difficulty = C.OutOnALimb.Difficulty;
            ImGui.SetNextItemWidth(140f);
            if (ImGuiEx.EnumCombo("力量表目標##LimbDifficulty", ref difficulty, names: LimbDifficultyNames))
            {
                C.OutOnALimb.Difficulty = difficulty;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("第一階段力量表要停在哪一格。泰坦最快（獎勵最高）也最難停中，仙人掌怪最慢最好停。\n" +
                                 "三個名字對應的是「當下量到的三段寬度排名」（最窄＝泰坦），" +
                                 "不是寫死的節點編號——伺服器每一局給的區間寬度可以不一樣。");
            }

            var tolerance = C.OutOnALimb.Tolerance;
            ImGui.SetNextItemWidth(140f);
            if (ImGui.SliderInt("容許誤差##LimbTolerance", ref tolerance, 1, 4))
            {
                C.OutOnALimb.Tolerance = Math.Clamp(tolerance, 1, 4);
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                C.Save();
            }

            var requiredFps = RequiredFramerate(C.OutOnALimb.Difficulty, C.OutOnALimb.Tolerance);
            var currentFps = (int)ImGui.GetIO().Framerate;
            SaucyTheme.TextMuted($"參考畫面更新率：{requiredFps} FPS（你目前 {currentFps} FPS）。" +
                                 "現在除了「這一幀夠不夠近」之外，還會判斷「兩幀之間有沒有掃過目標」，" +
                                 "所以更新率不夠時不會像以前那樣整段掃過去都按不到；這個數字只是精準度的參考。");

            var autoPowerMeter = C.OutOnALimb.AutoPowerMeter;
            if (ImGui.Checkbox("自動停力量表##LimbAutoPowerMeter", ref autoPowerMeter))
            {
                C.OutOnALimb.AutoPowerMeter = autoPowerMeter;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("力量表畫面是孤樹無援與礦脈探索共用的，所以只有在附近確實認得出" +
                                 "孤樹無援機台時才會幫你停——認不出來就完全不碰，由你自己停表，砍伐階段照樣會接手。");
            }

            var autoContinue = C.OutOnALimb.AutoContinue;
            if (ImGui.Checkbox("自動接受「挑戰翻倍」##LimbAutoContinue", ref autoContinue))
            {
                C.OutOnALimb.AutoContinue = autoContinue;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("預設關閉：砍完一棵樹後的續戰確認框交給你自己按。" +
                                 "開啟後，只有在剩餘時間足夠時才會接受；讀不到剩餘時間一律按「否」收工。");
            }

            if (autoContinue)
            {
                var stopAt = C.OutOnALimb.StopAtSecondsRemaining;
                ImGui.SetNextItemWidth(140f);
                if (ImGui.DragInt("剩餘秒數低於此值就收工##LimbStopAt", ref stopAt, 0.5f, 0, 60))
                {
                    C.OutOnALimb.StopAtSecondsRemaining = Math.Clamp(stopAt, 0, 60);
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    C.Save();
                }
            }

            DrawOutOnALimbAutoReplay();

            ImGui.Dummy(new(0, 4));
            var logDiag = C.OutOnALimb.LogBoardDiagnostics;
            if (ImGui.Checkbox("把機台面板欄位寫進 log##LimbDiag", ref logDiag))
            {
                C.OutOnALimb.LogBoardDiagnostics = logDiag;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("診斷用：每秒把機台面板的 AtkValue[0..15] 與指針刻度寫一行到 log（Information 等級）。" +
                                 "只有要回報問題時才需要打開。");
            }
        }

        ImGui.Dummy(new(0, 4));
        var limbModule = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.OutOnALimb.OutOnALimbModule>();
        SaucyTheme.TextMuted($"狀態：{(limbModule == null ? "模組未載入" : enabled ? limbModule.LastAction : "未啟用")}");
        if (limbModule != null && enabled)
        {
            SaucyTheme.TextMuted($"解題器：{limbModule.SolverSummary}");

            // ⚠️ 回饋來源的健康狀況放在列上而不是 tooltip：
            // 「解題器沒有資料可學」是使用者必須一眼看到的事，不是起疑才查的事。
            SaucyTheme.TextMuted(limbModule.FeedbackSummary);
        }
    }

    /// <summary>連續遊玩區塊。開關是設定、真正會動作的是「開始」按鈕——
    /// 而且停止鈕永遠畫在列上（不藏在 tooltip 或折疊區裡），跑起來時一眼就找得到。</summary>
    private static void DrawOutOnALimbAutoReplay()
    {
        ImGui.Dummy(new(0, 6));
        ImGui.Separator();
        ImGui.Dummy(new(0, 2));

        var module = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.OutOnALimb.OutOnALimbModule>();
        var running = module?.AutoReplayRunning == true;

        var autoReplay = C.OutOnALimb.AutoReplay;
        if (ImGui.Checkbox("允許連續遊玩##LimbAutoReplay", ref autoReplay))
        {
            C.OutOnALimb.AutoReplay = autoReplay;
            C.Save();
            if (!autoReplay)
            {
                module?.StopAutoReplay("設定已關閉");
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("一局結束後自動再跟機台開下一局，省掉中間的等待。\n" +
                             "🔴 預設關閉，而且光是打開這個開關還不會動作——一定要按下面的「開始連續遊玩」。\n" +
                             "這是設計成短時間、有人在旁邊看著的用法，不是掛機：跑滿設定的局數就會自己停。");
        }

        if (!autoReplay)
        {
            SaucyTheme.TextMuted("連續遊玩關閉中：一局結束後由你自己跟機台開下一局。");
            return;
        }

        var maxGames = C.OutOnALimb.AutoReplayMaxGames;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.SliderInt("最多自動幾局##LimbReplayCap", ref maxGames, 1, 20))
        {
            C.OutOnALimb.AutoReplayMaxGames = Math.Clamp(maxGames, 1, 20);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            C.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("到達這個局數就自己停下來，要再跑得重新按一次「開始連續遊玩」。");
        }

        if (module == null)
        {
            SaucyTheme.TextMuted("模組未載入。");
            return;
        }

        ImGui.Dummy(new(0, 2));
        if (running)
        {
            using (ImRaii.PushColor(ImGuiCol.Button, SaucyTheme.TextErrorColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, SaucyTheme.TextErrorColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, SaucyTheme.TextErrorColor))
            {
                if (ImGui.Button("■ 停止連續遊玩##LimbReplayStop", new(220f, 0f)))
                {
                    module.StopAutoReplay("使用者按下停止");
                }
            }

            ImGui.SameLine();
            SaucyTheme.TextWarning($"進行中：{module.AutoReplayGamesDone}/{Math.Max(1, C.OutOnALimb.AutoReplayMaxGames)} 局");
        }
        else
        {
            if (ImGui.Button("▶ 開始連續遊玩##LimbReplayStart", new(220f, 0f)))
            {
                module.StartAutoReplay();
            }

            ImGui.SameLine();
            SaucyTheme.TextMuted("按下之後才會自己跟機台互動；站到機台旁邊再按。");
        }
    }

    /// <summary>指針掃過目標所需的取樣率——容許誤差越窄、難度越高，指針停留在窗口內的時間越短。
    /// 數值沿用上游 Saucy 的實測表。</summary>
    private static int RequiredFramerate(global::Saucy.OutOnALimb.LimbDifficulty difficulty, int tolerance)
    {
        int[] table = difficulty switch
        {
            global::Saucy.OutOnALimb.LimbDifficulty.Titan => [480, 240, 120, 90, 60],
            global::Saucy.OutOnALimb.LimbDifficulty.Morbol => [240, 120, 90, 60, 30],
            var _ => [120, 90, 60, 30, 15]
        };

        var index = Math.Clamp(tolerance, 0, table.Length - 1);
        return table[index];
    }

    private static void DrawSellCardsPanel()
    {
        DrawPanelHeader("快速賣重複卡", "九宮幻卡交換 MGP 輔助");
        var enabled = C.IsModuleEnabled(ModuleNames.SellDuplicateCards);
        if (ImGui.Checkbox("啟用##SellCards", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.SellDuplicateCards, enabled);
            C.Save();
        }

        ImGui.TextWrapped("啟用後，開啟九宮幻卡的「幻卡交換」視窗時，會另外顯示一個小視窗，把你持有重複、" +
                          "可換 MGP 的卡整理出來（依單張 MGP 價值排序，並標出目前用於牌組的卡）。" +
                          "未啟用時不會顯示任何東西。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var keep = C.SellCardsKeepAtLeast;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("每種卡至少保留##SellCardsKeepAtLeast", ref keep, 0, Configuration.SellCardsMaxKeepAtLeast))
            {
                C.SellCardsKeepAtLeast = Math.Clamp(keep, 0, Configuration.SellCardsMaxKeepAtLeast);
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("保底值：只有持有數超過這個張數的卡才會被列為「可賣」，\n" +
                                 "確保每種卡（含牌組用的那張）至少留下這麼多張。預設 1。");
            }
        }

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("🔴 這個模組只顯示，不會替你賣卡：選取、交換、確認三步全部由你在遊戲原生視窗自己按下。" +
                             "不注入按鈕、不送 callback、不發封包——只讀取交換視窗上本來就顯示給你看的卡片清單。");

        var module = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.SellCards.SellDuplicateCardsModule>();
        SaucyTheme.TextMuted($"狀態：{(module == null ? "模組未載入" : enabled ? module.LastAction : "未啟用")}");
    }

    private static BannerInfo BuildBannerInfo()
    {
        var im = InventoryManager.Instance();
        var mgp = im != null ? im->GetInventoryItemCount(MgpItemId, false, false, false) : 0;

        string status;
        if (TriadRunSession.ModuleEnabled)
        {
            status = "Triple Triad";
        }
        else if (C.IsModuleEnabled(ModuleNames.AnyWayTheWindBlows))
        {
            status = "Wind Blows";
        }
        else if (C.IsModuleEnabled(ModuleNames.AirForceOne))
        {
            status = "Air Force One";
        }
        else if (C.IsModuleEnabled(ModuleNames.LeapOfFaith))
        {
            status = "Leap of Faith";
        }
        else if (C.IsModuleEnabled(ModuleNames.Cliffhanger))
        {
            status = "Cliffhanger";
        }
        else if (C.IsModuleEnabled(ModuleNames.MiniCactpot))
        {
            status = "Mini Cactpot";
        }
        else
        {
            status = "Idle";
        }

        var sessionDelta = C.SessionStats.MGPWon + C.SessionStats.CuffMGP + C.SessionStats.LimbMGP +
                           C.SessionStats.AirForceMGP;

        return new()
        {
            Mgp = mgp, SessionDelta = sessionDelta, ModuleStatus = status
        };
    }
}
