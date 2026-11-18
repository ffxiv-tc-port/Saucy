using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace Saucy.Framework;

/// <summary>每個 :00／:20／:40 GATE 時段在 log 寫一行 Information 帳（類型、去過區域、參加與否與原因、金碟幣增減），空軍裝甲駕駛員副本另記進出各一行。</summary>
/// <remarks>
/// 本檔契約：只在 framework 執行緒呼叫；只讀，不改任何 GATE 行為。導演、背包都當幀重取、判空，不跨幀保存原生指標。
/// 寫帳時刻：時段開始滿 6 分鐘後，且已離開場上／GATE 副本／區域切換滿 10 秒（讓獎勵入帳）；到時段結束還沒寫就在下一時段開始時補寫。
/// 整個時段都沒到過金碟遊樂場或 GATE 副本的不寫。原因欄是推斷：記錄到的事件優先，否則依類型與設定推。
/// </remarks>
internal static unsafe class GateSlotLedger
{
    private const string Prefix = "[Saucy] [GATE帳] ";

    private const uint MgpItemId = 29;

    private const uint SaucerTerritory = 144;

    private const uint AirForceTerritory = 832;

    private static readonly TimeSpan WriteAfter = TimeSpan.FromMinutes(6);

    private const long SettleMs = 10_000;

    private const long AirForceEndWaitMs = 30_000;

    private static DateTime slotStart;

    private static DateTime observedFrom;

    private static bool slotOpen;

    private static bool written;

    private static bool relevant;

    private static int startMgp = -1;

    private static ulong startCid;

    private static bool characterChanged;

    private static readonly List<uint> territories = [];

    private static readonly List<Module.GateType> gateTypes = [];

    private static bool sawOnStage;

    private static bool sawGateDuty;

    private static bool joinInteracted;

    private static long lastBusyAt;

    /// <summary>報名那一分鐘（:00／:20／:40）有登入觀察到；沒有的話報名時段的狀態不拿來推原因。</summary>
    private static bool sawJoinMinute;

    private static bool heldDuringJoin;

    private static bool inSaucerDuringJoin;

    private static bool vnavMissingDuringJoin;

    private static string coordinatorState = "未知";

    private static readonly List<string> notes = [];

    private static bool airForceActive;

    private static long airForceEnteredAt;

    private static int airForceStartMgp = -1;

    private static long airForceLeftAt;

    private static bool tickFaulted;

    /// <summary>每幀由插件主迴圈呼叫；自己吞例外（只記一次），不讓同一個 Update 裡後面的處理被跳過。</summary>
    public static void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            if (!tickFaulted)
            {
                tickFaulted = true;
                Svc.Log.Warning(ex, Prefix + "記帳失敗，之後同樣的錯誤不再記");
            }
        }
    }

    /// <summary>自動化在這個時段遇到、會影響參加的事（同一句同一時段只記一次）。</summary>
    public static void Note(string what)
    {
        if (slotOpen && !notes.Contains(what))
        {
            notes.Add(what);
        }
    }

    /// <summary>這個時段跟 GATE 報名 NPC 的互動送出了。</summary>
    public static void NoteJoinInteract()
    {
        if (slotOpen)
        {
            joinInteracted = true;
        }
    }

    private static void TickCore()
    {
        var now = DateTime.Now;
        var currentSlot = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 20 * 20, 0, now.Kind);
        if (!slotOpen || currentSlot != slotStart)
        {
            if (slotOpen && !written && relevant)
            {
                Write(forced: true);
            }

            BeginSlot(currentSlot, now);
        }

        var nowMs = Environment.TickCount64;
        var available = Player.Available;
        var territory = available ? (uint)Svc.ClientState.TerritoryType : 0;
        TickAirForce(territory, available, nowMs);
        if (!available)
        {
            return;
        }

        if (startCid == 0)
        {
            observedFrom = now;
            startCid = Player.CID;
        }
        else if (Player.CID != startCid)
        {
            characterChanged = true;
        }

        if (startMgp < 0)
        {
            startMgp = ReadMgp();
        }

        if (territory != 0 && !territories.Contains(territory))
        {
            territories.Add(territory);
        }

        var inGateDuty = GateDirector.InGateDuty;
        if (territory == SaucerTerritory || inGateDuty)
        {
            relevant = true;
        }

        var gate = GateDirector.GetCurrentGate();
        if (gate != Module.GateType.None && !gateTypes.Contains(gate))
        {
            gateTypes.Add(gate);
        }

        var onStage = GateDirector.InSaucer && GateDirector.IsPlayerOnStage();
        sawOnStage |= onStage;
        sawGateDuty |= inGateDuty;
        if (onStage || inGateDuty || Svc.Condition[ConditionFlag.BetweenAreas])
        {
            lastBusyAt = nowMs;
        }

        if (now - slotStart < TimeSpan.FromMinutes(1))
        {
            sawJoinMinute = true;
            heldDuringJoin |= GateEmergencyHold.IsHeld;
            inSaucerDuringJoin |= GateDirector.InSaucer;
            vnavMissingDuringJoin |= !Vnavmesh.IsInstalled;
        }

        if (!written && relevant && now - slotStart >= WriteAfter && (lastBusyAt == 0 || nowMs - lastBusyAt >= SettleMs))
        {
            Write(forced: false);
        }
    }

    private static void BeginSlot(DateTime start, DateTime now)
    {
        slotStart = start;
        observedFrom = now;
        slotOpen = true;
        written = false;
        relevant = false;
        startMgp = -1;
        startCid = 0;
        characterChanged = false;
        territories.Clear();
        gateTypes.Clear();
        sawOnStage = false;
        sawGateDuty = false;
        joinInteracted = false;
        sawJoinMinute = false;
        lastBusyAt = 0;
        heldDuringJoin = false;
        inSaucerDuringJoin = false;
        vnavMissingDuringJoin = false;
        notes.Clear();
        coordinatorState = DescribeCoordinator(start, now);
    }

    // 活動解說員時段是時段開始前那一分鐘（:55／:15／:35）；過了下一個解說員時段才開始觀察就分不清是哪一次的紀錄。
    private static string DescribeCoordinator(DateTime start, DateTime now)
    {
        if (now - start >= TimeSpan.FromMinutes(15))
        {
            return "未知";
        }

        var startUtc = start.ToUniversalTime().Ticks;
        var fromUtc = startUtc - TimeSpan.FromMinutes(5).Ticks;
        var gates = C.GoldSaucerGates;
        bool InRange(long ticks) => ticks >= fromUtc && ticks < startUtc;
        if (InRange(gates.LastCoordinatorTeleportUtcTicks))
        {
            return "已傳送";
        }

        if (InRange(gates.LastCoordinatorHandledUtcTicks))
        {
            return "有處理、沒看到傳送";
        }

        return gates.EventCoordinatorAutoNavigate ? "沒去" : "沒開自動走去";
    }

    private static void Write(bool forced)
    {
        written = true;
        var endMgp = ReadMgp();
        var mgp = characterChanged ? "未知（換過角色）"
            : startMgp < 0 || endMgp < 0 ? "未知"
            : (endMgp - startMgp).ToString("+#,0;-#,0;0");

        var joined = sawOnStage || sawGateDuty || joinInteracted;
        var joinDetail = sawGateDuty ? "是（進了 GATE 副本）"
            : sawOnStage ? "是（在場上）"
            : joined ? "是（跟報名 NPC 互動過，沒看到上場）"
            : "否";

        var sb = new StringBuilder(Prefix);
        sb.Append(slotStart.ToString("HH:mm")).Append(" 時段：類型=")
          .Append(gateTypes.Count == 0 ? "未知" : string.Join("→", gateTypes.Select(GateName)))
          .Append("、去過區域=").Append(territories.Count == 0 ? "無" : string.Join(",", territories))
          .Append("、參加=").Append(joinDetail)
          .Append("、原因=").Append(sawOnStage || sawGateDuty ? "—" : Reason())
          .Append("、MGP ").Append(mgp)
          .Append("、解說員=").Append(coordinatorState);

        if (observedFrom - slotStart >= TimeSpan.FromSeconds(30))
        {
            sb.Append("（從 ").Append(observedFrom.ToString("HH:mm:ss")).Append(" 起才開始觀察）");
        }

        if (forced && lastBusyAt != 0 && Environment.TickCount64 - lastBusyAt < SettleMs)
        {
            sb.Append("（到時段結束仍在 GATE 中，金碟幣可能還沒入帳）");
        }

        Svc.Log.Information(sb.ToString());
    }

    // 先列實際記錄到的事，再依報名時段的狀態與類型推斷；推不出來就寫「不明」。
    private static string Reason()
    {
        var reasons = new List<string>(notes);
        if (joinInteracted)
        {
            reasons.Add("報名互動送出後沒看到上場");
            return string.Join("／", reasons.Distinct());
        }

        if (heldDuringJoin)
        {
            reasons.Add("急停壓住這個時段");
        }

        if (!sawJoinMinute)
        {
            reasons.Add("報名那一分鐘沒有觀察到（未登入或插件還沒載入）");
        }
        else if (!inSaucerDuringJoin)
        {
            reasons.Add("報名那一分鐘不在金碟遊樂場");
        }
        else if (vnavMissingDuringJoin)
        {
            reasons.Add("vnavmesh 沒載入");
        }

        foreach (var gate in gateTypes)
        {
            var why = WhyNotAutoJoined(gate);
            if (why != null)
            {
                reasons.Add($"{GateName(gate)}：{why}");
            }
        }

        if (gateTypes.Count == 0)
        {
            reasons.Add("讀不到 GATE 類型");
        }

        return reasons.Count == 0 ? "不明" : string.Join("／", reasons.Distinct());
    }

    private static string? WhyNotAutoJoined(Module.GateType gate)
    {
        var gates = C.GoldSaucerGates;
        return gate switch
        {
            Module.GateType.AirForceOne or Module.GateType.LeapOfFaith =>
                !gates.AirForceAutoJoin ? "沒開定時自動報名" : SpotMissing(gates.AirForceNpcSpot),
            Module.GateType.AnyWayTheWindBlows =>
                !gates.WindBlowsAutoJoin ? "沒開定時自動報名" : SpotMissing(gates.WindBlowsNpcSpot),
            Module.GateType.SliceIsRight =>
                !gates.SliceIsRightAutoJoin ? "沒開定時自動報名" : SpotMissing(gates.SliceIsRightNpcSpot),
            Module.GateType.Cliffhanger =>
                !gates.CliffhangerAutoJoin ? "沒開定時自動報名"
                : gates.CliffhangerNpcSpots.Any(s => s.Recorded && s.DataId != 0) ? null : "沒有錄到報名 NPC",
            _ => "Saucy 不支援這種 GATE",
        };
    }

    private static string? SpotMissing(GateNpcSpot spot) => spot.Recorded && spot.DataId != 0 ? null : "沒有錄到報名 NPC";

    // 2／3／4 沿用列舉名：Saucy 沒有這三種的既有中文名，不自己翻。
    private static string GateName(Module.GateType gate) => gate switch
    {
        Module.GateType.Cliffhanger => "搶救小雛鳥大作戰",
        Module.GateType.AnyWayTheWindBlows => "暴風倖存者",
        Module.GateType.LeapOfFaith => "登高跳跳樂",
        Module.GateType.AirForceOne => "空軍裝甲駕駛員",
        Module.GateType.SliceIsRight => "必中一閃快刀斬魔",
        Module.GateType.VaseOff or Module.GateType.SkinchangeWeCanBelieveIn or Module.GateType.TheTimeOfMyLife => gate.ToString(),
        _ => $"未知({(byte)gate})",
    };

    private static void TickAirForce(uint territory, bool available, long nowMs)
    {
        if (territory == AirForceTerritory)
        {
            if (!airForceActive)
            {
                airForceActive = true;
                airForceEnteredAt = nowMs;
                airForceStartMgp = -1;
                airForceLeftAt = 0;
                Svc.Log.Information(Prefix + $"空軍裝甲駕駛員開始（區域 {AirForceTerritory}）");
            }

            if (airForceStartMgp < 0)
            {
                airForceStartMgp = ReadMgp();
            }

            airForceLeftAt = 0;
            return;
        }

        if (!airForceActive)
        {
            return;
        }

        // 離開後等到能讀背包（載入完）再結帳；登出或等太久就寫「未知」。
        if (airForceLeftAt == 0)
        {
            airForceLeftAt = nowMs;
        }

        var endMgp = available && !Svc.Condition[ConditionFlag.BetweenAreas] ? ReadMgp() : -1;
        if (endMgp < 0 && Svc.ClientState.IsLoggedIn && nowMs - airForceLeftAt < AirForceEndWaitMs)
        {
            return;
        }

        airForceActive = false;
        var mgp = airForceStartMgp < 0 || endMgp < 0 ? "未知" : (endMgp - airForceStartMgp).ToString("+#,0;-#,0;0");
        Svc.Log.Information(Prefix + $"空軍裝甲駕駛員結束：在副本裡 {(airForceLeftAt - airForceEnteredAt) / 1000} 秒、MGP {mgp}");
    }

    /// <summary>金碟幣持有數；沒登入、背包還沒載入回 -1（未載入時遊戲會回 0，不能當真）。</summary>
    private static int ReadMgp()
    {
        if (!Player.Available)
        {
            return -1;
        }

        var im = InventoryManager.Instance();
        if (im is null)
        {
            return -1;
        }

        var currency = im->GetInventoryContainer(InventoryType.Currency);
        if (currency is null || !currency->IsLoaded)
        {
            return -1;
        }

        return im->GetInventoryItemCount(MgpItemId, false, false, false);
    }
}
