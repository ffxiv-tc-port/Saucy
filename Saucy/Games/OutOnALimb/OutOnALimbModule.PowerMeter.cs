using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using static ECommons.GenericHelpers;
namespace Saucy.OutOnALimb;

public unsafe partial class OutOnALimbModule
{
    /// <summary>第一階段：力量表。指針掃進所選難度的那一格時按下停止鈕，一輪只按一次。
    /// 🔴 力量表 addon 與礦脈探索共用，所以動它之前要先確認面前那台真的是孤樹無援；認不出來就完全不碰（玩家自己停表，砍伐階段照樣會接手）。
    /// 出手條件是連言：**當幀的指針必須真的落在目標格裡**（用遊戲自己的<c>pos &gt; low &amp;&amp; pos &lt;= high</c> 判定），再加上一個會隨時間放寬的精準度要求。**不需要、也不應該加「提前量」**。
    /// </summary>
    private void RunPowerMeterPhase()
    {
        var addon = LimbBoard.TryGetAddon(LimbBoard.AimgAddon);
        if (addon == null)
        {
            powerMeterClicked = false;
            lastAimgCursor = null;
            ResetAimgAttempt();
            return;
        }

        if (powerMeterClicked || !Cfg.AutoPowerMeter)
        {
            return;
        }

        if (LimbBoard.IsMinerOpen || !LimbMachine.IsNearLimbMachine())
        {
            if (EzThrottler.Throttle(MachineDiagThrottleKey, MachineDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] power meter left alone: no Out on a Limb machine identified nearby " +
                                    "(the power meter screen is shared with the mining minigame)");
                LastAction = "力量表由你自己停（附近沒認出孤樹無援機台）";
            }

            return;
        }

        var cursor = LimbBoard.ReadCursor();
        if (cursor == null)
        {
            if (EzThrottler.Throttle(CursorDiagThrottleKey, CursorDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] cursor position unavailable " +
                                    "(GoldSaucerArcadeMachine number array not readable); leaving the power meter alone");
                LastAction = "力量表由你自己停（讀不到指針位置）";
            }

            return;
        }

        if (!TryGetZonesBySize(addon, out var zones, out var source))
        {
            if (EzThrottler.Throttle(ZoneDiagThrottleKey, ZoneDiagThrottleMs))
            {
                Svc.Log.Information("[OutOnALimb] power meter zones unreadable " +
                                    "(neither AtkValue[4]/[5] nor the block nodes gave a usable layout); " +
                                    "leaving the power meter alone");
                LastAction = "力量表由你自己停（讀不到區塊配置）";
            }

            lastAimgCursor = cursor;
            return;
        }

        var wanted = Math.Clamp((int)Cfg.Difficulty, 0, zones.Length - 1);
        var zone = zones[wanted];

        // 追蹤指針速度。⚠️ 折返／重設造成的大跳躍不算位移（沿用 HasCrossed 的同一個門檻）。
        if (lastAimgCursor != null)
        {
            var delta = Math.Abs(cursor.Value - lastAimgCursor.Value);
            if (delta > 0 && delta <= LimbBoard.CursorScale / 4)
            {
                aimgMaxStep = Math.Max(aimgMaxStep, delta);
                aimgLiveSinceMs ??= Environment.TickCount64;
            }
        }

        lastAimgCursor = cursor.Value;

        // 🔴 指針還停在待命位置就完全不動作。待命值 0 落在最窄那一格裡面，
        // 少了這一條，「等久了就放寬」會在遊戲根本還沒開始的時候按下去。
        if (aimgLiveSinceMs == null)
        {
            return;
        }

        aimgLiveFrames++;
        var waited = Environment.TickCount64 - aimgLiveSinceMs.Value;

        // 放寬階梯。⚠️ 每前進一階都寫一行 Information，使用者才看得出「它在等，不是壞了」。
        var stage = waited >= WidenZoneMs ? 3 : waited >= AnyInZoneMs ? 2 : waited >= PreciseWindowMs ? 1 : 0;
        if (stage > aimgRelaxStage)
        {
            aimgRelaxStage = stage;
            Svc.Log.Information($"[OutOnALimb] power meter still hunting {Cfg.Difficulty} after {waited}ms " +
                                $"({aimgLiveFrames} frames, biggest one-frame step {aimgMaxStep} vs zone width " +
                                $"{zone.Width}); relaxing to stage {stage} " +
                                $"({DescribeRelaxStage(stage)})");
            LastAction = $"力量表：等不到準點，已放寬到第 {stage} 階（{DescribeRelaxStage(stage)}）";
        }

        // 🔴 出手條件的第一條：
        //    **當幀的指針必須真的落在可接受的格子裡**，用的就是遊戲自己的判定式。
        //    只看「跨過中心」而沒有這層限制的話，會在指針已經衝過頭的那一幀按下去。
        // 先看目標格，再往寬的方向放。⚠️ 順序有意義：命中目標格永遠優先。
        var acceptTo = stage >= 3 ? zones.Length - 1 : stage >= 2 ? Math.Min(wanted + 1, zones.Length - 1) : wanted;
        var landed = -1;
        for (var i = wanted; i <= acceptTo && landed < 0; i++)
        {
            if (zones[i].Contains(cursor.Value))
            {
                landed = i;
            }
        }

        // 最後一階是「落在哪一格都收」，所以也要收**比目標更窄**的那些格
        // （想要魔界花卻掃到泰坦格時就是這一段在收）。少了它，最後一階名不副實。
        if (landed < 0 && stage >= 3)
        {
            for (var i = 0; i < zones.Length && landed < 0; i++)
            {
                if (zones[i].Contains(cursor.Value))
                {
                    landed = i;
                }
            }
        }

        if (landed < 0)
        {
            return;
        }

        var hit = zones[landed];

        // 邊界留一點餘裕，免得剛好卡在兩格交界被判到隔壁那格。
        // 📌 這只是**偏好**：第 0 階要求它，之後就放掉——寧可停得靠邊，也不要一直不停表。
        if (stage == 0)
        {
            var margin = Math.Clamp(hit.Width / 6, 1, 400);
            if (cursor.Value <= hit.LowExclusive + margin || cursor.Value > hit.HighInclusive - margin)
            {
                return;
            }
        }

        if (TryClickButton(LimbBoard.AimgAddon, addon, LimbBoard.AimgStopButtonId(addon), AimgThrottleKey, AimgThrottleMs))
        {
            powerMeterClicked = true;
            var landedName = (LimbDifficulty)Math.Clamp(landed, 0, 2);
            var onTarget = landed == wanted;
            LastAction = onTarget
                ? $"力量表已停在 {Cfg.Difficulty}（刻度 {cursor.Value}）"
                : $"力量表停在 {landedName}（想要 {Cfg.Difficulty}，刻度 {cursor.Value}）";

            // 📌 這一行是使用者判斷力量表停得對不對的唯一依據，所以它必須說出
            //    **實際落在哪一格**，而不是只印我們想瞄哪一格。
            Svc.Log.Information($"[OutOnALimb] power meter stopped at {cursor.Value} -> landed in {landedName} " +
                                $"(zone slot {hit.Slot} = ({hit.LowExclusive}, {hit.HighInclusive}], " +
                                $"width {hit.Width}); wanted {Cfg.Difficulty}; " +
                                $"{(onTarget ? "ON TARGET" : "MISSED, took a wider zone")}; " +
                                $"source {source}, waited {waited}ms over {aimgLiveFrames} frames, " +
                                $"biggest one-frame step {aimgMaxStep}, relax stage {stage}");
        }
    }

    private static string DescribeRelaxStage(int stage) => stage switch
    {
        0 => "只收離邊界有餘裕的",
        1 => "落在目標格裡就收",
        2 => "目標格或次寬的那一格",
        var _ => "落在哪一格都收"
    };

    private void ResetAimgAttempt()
    {
        aimgLiveSinceMs = null;
        aimgMaxStep = 0;
        aimgLiveFrames = 0;
        aimgRelaxStage = 0;
    }

    /// <summary>取得力量表的三格，**依寬度由小到大**排序。
    /// 所以不管伺服器把區間切成什麼樣子、也不管畫面上的區塊順序怎麼洗牌，索引 0（泰坦）永遠指最窄（最難停中）的那一格、索引 2（仙人掌怪）永遠指最寬的。</summary>
    private static bool TryGetZonesBySize(AtkUnitBase* addon, out LimbZone[] zones, out string source)
    {
        source = "none";
        if (LimbBoard.TryGetPowerZones(addon, out zones))
        {
            source = "AtkValue[4]/[5]";
        }
        else if (LimbBoard.TryGetPowerZonesFromNodes(addon, out zones))
        {
            source = "block nodes";
        }
        else
        {
            return false;
        }

        return zones.Length > 0 && zones[0].Width > 0;
    }

    /// <summary>兩幀之間指針有沒有跨過目標。低更新率下指針一幀可以跳很遠，
    /// 只比「這一幀夠不夠近」會整段掃過去都按不到。</summary>
    private static bool HasCrossed(int? previous, int current, int target)
    {
        if (previous == null)
        {
            return false;
        }

        var low = Math.Min(previous.Value, current);
        var high = Math.Max(previous.Value, current);

        // 指針折返或重設時的跳躍不算「掃過」，否則會在錯的位置出手。
        if (high - low > LimbBoard.CursorScale / 4)
        {
            return false;
        }

        return target >= low && target <= high;
    }
}
