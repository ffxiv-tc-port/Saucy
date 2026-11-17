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
    private static void DrawMiniCactpotPanel()
    {
        DrawPanelHeader("Mini Cactpot".Loc(), "Daily scratch lottery".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.MiniCactpot);
        if (ImGui.Checkbox("啟用##MiniCactpot", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.MiniCactpot, enabled);
            C.Save();
        }

        ImGui.TextWrapped("啟用後，開啟仙人微彩（每日刮刮樂）面板時會自動依期望值翻格、選線並領獎。" +
                          "未啟用時不註冊任何監聽，手動遊玩不會被搶操作。");

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var playAgain = C.MiniCactpotAutoPlayAgain;
            if (ImGui.Checkbox("自動購買下一張（一次完成當日全部彩券）##MiniCactpotPlayAgain", ref playAgain))
            {
                C.MiniCactpotAutoPlayAgain = playAgain;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("一張完成關窗後，自動在「要購買下一張彩票嗎」按確認。" +
                                 "只會按仙人微彩自己的購票確認框（長按式按鈕版面），其他對話框一律不碰。");
            }

            var autoTalk = C.MiniCactpotAutoTalkOnArrival;
            if (ImGui.Checkbox("When you stand next to the broker (however you got there), talk to them and play today's tickets".Loc() +
                               "##MiniCactpotAutoTalk", ref autoTalk))
            {
                C.MiniCactpotAutoTalkOnArrival = autoTalk;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(("Talks to the broker once you are standing still next to them in the Gold Saucer, however you got there: the Navigation panel, DailyDuty, Lifestream, or on foot.\n" +
                                  "It starts after you have stood still within talking range for about 1 second, once per approach: you must have been more than 10 yalms away (or teleported in) first. Logging in, or turning this on while already standing there, does not start it.\n" +
                                  "It does not start while you are talking, in an event, a menu or combat, or if today's tickets are already known to be used up. If you started talking yourself, it leaves that visit alone.\n" +
                                  "It talks to the broker and picks the join option, nothing more. The broker's dialog and the purchase confirmation (10 MGP a ticket) must be handled by YesAlready or by you; Saucy never presses them.\n" +
                                  "While picking the join option it asks YesAlready to pause its list selection for a few seconds, so the two never pick at the same time.\n" +
                                  "If you move before the broker answers, it stops; only a Navigation trip walks closer on its own. If no ticket opens within 30 seconds, it stops and says why. Stop it any time with Stop on the Navigation panel.").Loc());
            }

            var alliedDailies = C.MiniCactpotStartQuestionableAlliedDailies;
            if (ImGui.Checkbox("When today's tickets are done, start Questionable's allied society dailies".Loc() +
                               "##MiniCactpotQuestionableAlliedDailies", ref alliedDailies))
            {
                C.MiniCactpotStartQuestionableAlliedDailies = alliedDailies;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(("Requires Questionable, with the allied societies you want ticked in Questionable's own settings first; Saucy only asks it to start.\n" +
                                  "Only after a finished ticket's panel closes and today's remaining count is known to be 0 (from the broker's event). If the count is unknown it does not ask.\n" +
                                  "At most once per character per game day (resets at the daily reset). It waits for the broker's closing dialog to end, up to 10 seconds.\n" +
                                  "Questionable decides the rest: if it is already running, or there is nothing to do today, it does not start and says why in its own log.\n" +
                                  "One line in chat either way. Logging in, zoning or loading never starts it.").Loc());
            }

            ImGui.Dummy(new(0, 4));

            var clickInterval = C.MiniCactpotClickIntervalMs;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("點擊間隔（毫秒）##MiniCactpotClickInterval", ref clickInterval,
                    Configuration.MiniCactpotMinClickIntervalMs, Configuration.MiniCactpotMaxClickIntervalMs))
            {
                C.MiniCactpotClickIntervalMs = clickInterval;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("每次翻格／選線之間至少間隔這麼久。\n" +
                                 "下限刻意設在 400 毫秒：金碟遊樂園的自動操作是伺服器看得見的行為，" +
                                 "「看起來像人在操作」本身就有價值，不建議為了快而壓到極限。");
            }

            var closeDelay = C.MiniCactpotCloseDelayMs;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("開獎後關窗延遲（毫秒）##MiniCactpotCloseDelay", ref closeDelay,
                    0, Configuration.MiniCactpotMaxCloseDelayMs))
            {
                C.MiniCactpotCloseDelayMs = closeDelay;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("全部翻開後等這麼久再領獎關窗，讓開獎動畫與派彩數字跑完——" +
                                 "設成 0 就會立刻關掉，你會看不到中了多少。");
            }

            ImGui.Dummy(new(0, 4));

            var praise = C.MiniCactpotJackpotTataruPraise;
            if (ImGui.Checkbox("中獎時請塔塔露提醒（需安裝 TataruPraise）##MiniCactpotTataruPraise", ref praise))
            {
                C.MiniCactpotJackpotTataruPraise = praise;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("開獎後派彩達到門檻時，請「塔塔露誇獎」念一句。\n" +
                                 "沒安裝那個插件就什麼都不會發生；不論有沒有念，都不影響本模組的自動流程。\n" +
                                 "派彩是照選中那條線的線和查派彩表算出來的，不讀面板文字。");
            }

            if (praise)
            {
                using var praiseIndent = ImRaii.PushIndent();
                var threshold = C.MiniCactpotJackpotThresholdMgp;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderInt("提醒門檻（金碟幣）##MiniCactpotTataruThreshold", ref threshold,
                        Configuration.MiniCactpotJackpotMinThresholdMgp,
                        Configuration.MiniCactpotJackpotMaxThresholdMgp))
                {
                    C.MiniCactpotJackpotThresholdMgp = threshold;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    C.Save();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("派彩達到這個數字才提醒。\n" +
                                     "仙人微彩的派彩表最低 36、最高 10000 金碟幣，" +
                                     "所以拉到最左邊等於「中任何獎都提醒」。");
                }
            }
        }

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("第一張彩券的購買確認（花費 10 MGP）永遠由你自己按下——模組只接手面板開啟後的流程。");

        var module = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.MiniCactpot.MiniCactpotModule>();
        SaucyTheme.TextMuted($"狀態：{(module == null ? "模組未載入" : enabled ? module.LastAction : "未啟用")}");
        if (enabled && global::Saucy.MiniCactpot.MiniCactpotAutoPlay.IsActive)
        {
            SaucyTheme.TextMuted(global::Saucy.MiniCactpot.MiniCactpotAutoPlay.StatusText);
        }

        if (enabled)
        {
            SaucyTheme.TextMuted(global::Saucy.Framework.GoldSaucer.GoldSaucerEventTracker.DescribeRemaining());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("這個數字來自跟仙人微彩發放員對話時伺服器送的那筆事件；之後每開一張彩券就從它扣一（一筆事件只扣一次）。\n" +
                                 "還沒跟他說過話、換了角色、或已經跨過每日重置都會顯示「未知」。\n" +
                                 "除了顯示，只有「從導航抵達後自動對話」會看它：確定今天剩 0 張就不對話，未知一律照常對話。");
            }
        }
    }

    private static void DrawJumboCactpotPanel()
    {
        DrawPanelHeader("Jumbo Cactpot".Loc(), "Weekly lottery".Loc());
        var enabled = C.IsModuleEnabled(ModuleNames.JumboCactpot);
        if (ImGui.Checkbox("Enable".Loc() + "##JumboCactpot", ref enabled))
        {
            C.SetModuleEnabled(ModuleNames.JumboCactpot, enabled);
            C.Save();
        }

        ImGui.TextWrapped("When enabled, opening the Jumbo Cactpot ticket purchase panel automatically fills in the numbers and brings up the purchase confirmation. When disabled, no listener is registered and manual purchases are not intercepted.".Loc());

        if (enabled)
        {
            using var indent = ImRaii.PushIndent();
            var useFixed = C.JumboCactpotUseFixedNumber;
            if (ImGui.Checkbox("Use fixed number".Loc() + "##JumboCactpotUseFixed", ref useFixed))
            {
                C.JumboCactpotUseFixedNumber = useFixed;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(("Unchecked (default) = every ticket rerolls randomly (0000-9999, inclusive).\n" +
                                 "Checked = use a number you pick; to use a different number for each of the three tickets, check this then see the option below.").Loc());
            }

            if (useFixed)
            {
                using var fixedIndent = ImRaii.PushIndent();
                var perTicket = C.JumboCactpotPerTicketNumbers;
                if (ImGui.Checkbox("Use a different number per ticket".Loc() + "##JumboCactpotPerTicket", ref perTicket))
                {
                    C.JumboCactpotPerTicketNumbers = perTicket;
                    C.Save();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(("Unchecked (default) = all three tickets use the number set below, same as before.\n" +
                                     "Checked = each of the week's three tickets uses its own number; a ticket without \"use a specific number\" checked rerolls randomly.\n" +
                                     "\"Which ticket\" is how many you have bought this week: the count the server reports when you talk to the broker, plus each purchase seen after that. If that count is unknown, it is the tickets bought since you entered the Gold Saucer this visit.").Loc());
                }

                if (perTicket)
                {
                    DrawJumboPerTicketNumbers();
                }
                else
                {
                    var fixedNumber = C.JumboCactpotFixedNumber;
                    ImGui.SetNextItemWidth(220);
                    if (ImGui.InputInt("Fixed number".Loc() + "##JumboCactpotFixedNumber", ref fixedNumber))
                    {
                        C.JumboCactpotFixedNumber = Math.Clamp(fixedNumber, 0, Configuration.JumboCactpotMaxNumber);
                        C.Save();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("0000 to 9999; out-of-range values are automatically clamped.".Loc());
                    }

                    SaucyTheme.TextMuted("Current number: ??".Loc(Math.Clamp(C.JumboCactpotFixedNumber, 0, Configuration.JumboCactpotMaxNumber).ToString("D4")));
                }
            }

            ImGui.Dummy(new(0, 4));
            var autoTalk = C.JumboCactpotAutoTalkOnArrival;
            if (ImGui.Checkbox("After arriving from Navigation, talk to the broker and open the ticket panel".Loc() +
                               "##JumboCactpotAutoTalk", ref autoTalk))
            {
                C.JumboCactpotAutoTalkOnArrival = autoTalk;
                C.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(("Only runs when you press Go on the Jumbo Cactpot row of the Navigation panel and the walk reaches the broker; logging in, zoning or loading never starts it.\n" +
                                  "It talks to the broker and picks \"join\" in the broker's menu, once per trip, and stops as soon as the ticket panel opens. This module then fills in the number.\n" +
                                  "The broker's dialog, every purchase confirmation and \"buy another?\" are handled by YesAlready or by you; Saucy never presses them.\n" +
                                  "While waiting for the menu it asks YesAlready to pause its SelectString option picking for a few seconds at a time, so the two never pick at the same time; if YesAlready cannot be asked, it leaves the menu to you.\n" +
                                  "If you move on your own while it is still walking up to the broker, it pauses and walks on once you stop for 2 seconds. Stop it any time with Stop on the Navigation panel.").Loc());
            }
        }

        ImGui.Dummy(new(0, 4));
        SaucyTheme.TextMuted("Every ticket's purchase confirmation (which spends MGP) is always confirmed by you — the module only fills in the number and brings up the confirmation. For the week's three tickets, keep choosing \"Buy Ticket\" from the NPC menu as usual; the module fills in a number each time you open the panel. It never looks for the NPC itself, never clicks the dialogue menu itself, and never opens the next one after you buy one.".Loc());
        if (enabled && C.JumboCactpotAutoTalkOnArrival)
        {
            SaucyTheme.TextMuted("Exception you turned on: a Navigation trip to the broker talks to them and picks \"join\" once.".Loc());
        }

        var module = global::Saucy.Saucy.ModuleManager.GetModule<global::Saucy.JumboCactpot.JumboCactpotModule>();
        SaucyTheme.TextMuted("Status: ??".Loc(module == null ? "Module not loaded".Loc() : enabled ? module.LastAction : "Disabled".Loc()));
        if (enabled && global::Saucy.JumboCactpot.JumboCactpotAutoTalk.IsActive)
        {
            SaucyTheme.TextMuted(global::Saucy.JumboCactpot.JumboCactpotAutoTalk.StatusText);
        }

        if (enabled)
        {
            SaucyTheme.TextMuted(global::Saucy.Framework.GoldSaucer.GoldSaucerEventTracker.DescribeWeeklyTickets());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(("These three numbers come from the event the server sends when talking to the Jumbo Cactpot broker — the module does not remember them on its own.\n" +
                                 "If you have not talked to them yet, switched characters, or this week's drawing has already passed, it will show \"unknown\".\n" +
                                 "The module uses how many are already bought to decide which of your three numbers comes next.").Loc());
            }

            SaucyTheme.TextMuted("YesAlready also has a Jumbo Cactpot auto-fill; while you talk to the broker, Saucy asks it to pause so the two never fill the same panel.".Loc());
        }

        // 「下一張會用哪個號碼」放在列上而不是 tooltip：這是使用者按下確認前唯一能核對的地方，
        // 而且模組算的張數與他心裡想的可能不一樣，看不見就等於猜。
        if (enabled && module != null && C.JumboCactpotUseFixedNumber && C.JumboCactpotPerTicketNumbers)
        {
            var next = module.NextTicketIndex;
            SaucyTheme.TextMuted(next < Configuration.JumboCactpotTicketsPerWeek
                ? "Next ticket is #??, will use: ??".Loc((next + 1).ToString(), JumboNumberPlan.Describe(C, next))
                : "This week's three tickets are already bought; opening the panel again will use random numbers.".Loc());
        }
    }

    /// <summary>三張彩券各自的號碼設定。
    /// 🔴 索引一律走 JumboCactpotNumberPlan.Normalize——設定檔是使用者改得到的 JSON，
    /// 陣列長度與元素是不是 null 都不能假設。</summary>
    private static void DrawJumboPerTicketNumbers()
    {
        var slots = JumboNumberPlan.Normalize(C);
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            var useSpecific = slot.UseSpecificNumber;
            if (ImGui.Checkbox("Ticket ?? uses a specific number".Loc((i + 1).ToString()) + $"##JumboCactpotTicketUse{i}", ref useSpecific))
            {
                slot.UseSpecificNumber = useSpecific;
                C.Save();
            }

            ImGui.SameLine();
            if (!useSpecific)
            {
                SaucyTheme.TextMuted("Random".Loc());
                continue;
            }

            ImGui.SetNextItemWidth(160);
            var number = slot.Number;
            if (ImGui.InputInt($"##JumboCactpotTicketNumber{i}", ref number))
            {
                slot.Number = Math.Clamp(number, 0, Configuration.JumboCactpotMaxNumber);
                C.Save();
            }

            ImGui.SameLine();
            SaucyTheme.TextMuted($"{Math.Clamp(slot.Number, 0, Configuration.JumboCactpotMaxNumber):D4}");
        }
    }
}
