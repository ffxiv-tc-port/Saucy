using Dalamud.Configuration;
using ECommons.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
namespace Saucy;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int ConfigVersionBackgroundCpuCores = 1;

    public const int GameRecommendedDeckIndex = -2;
    public ObservableCollection<string> EnabledModules =
    [
        "AnyWayTheWindBlows", "LeapOfFaith", "AirForceOne", "SliceIsRight", "Cliffhanger"
    ];

    public bool UseSimmedDeck { get; set; } = true;

    public bool AlwaysBuildOptimizedDeck { get; set; } = true;

    public bool SkipOptimizedDeckForBeatenOrCompletedNpcs { get; set; } = false;

    public bool PauseOptimizedDeckBuildWhileQuestionable { get; set; } = false;

    public bool UseCachedOptimizedDeckIfAvailable { get; set; } = false;
    public bool ShowOptimizerChatSpam { get; set; } = true;

    public Dictionary<int, long> TriadOptimizedDeckBuiltUtcTicksByNpcId { get; set; } = [];

    public int SelectedDeckIndex { get; set; } = -1;

    [JsonIgnore]
    public TriadRunMode TriadRunMode { get; set; } = TriadRunMode.None;

    public int TriadMatchCount { get; set; } = 1;

    public bool LogOutAfterTriadRun { get; set; }

    public Stats Stats { get; set; } = new();

    [JsonIgnore]
    public Stats SessionStats { get; set; } = new();

    [JsonIgnore]
    public DateTime SessionStartTime { get; set; } = DateTime.UtcNow;

    public bool PlaySound { get; set; } = false;

    /// <summary>幻卡對戰獲勝時，透過 IPC 請 TataruPraise 念一句「中獎」（每贏一場叫一次）。
    /// 對方沒安裝就什麼都不會發生，也不影響任何自動化流程。</summary>
    public bool TriadWinTataruPraise { get; set; } = true;

    public string SelectedSound { get; set; } = "Moogle";
    public bool OnlyUnobtainedCards { get; set; } = true;
    public bool OpenAutomatically { get; set; } = true;

    public bool SaucyThemeEnabled { get; set; } = false;

    public bool CollectionUiEnabled { get; set; } = true;

    [JsonProperty("BackgroundWorkCpuCores")]
    public int DeckOptimizerMaxThreads { get; set; }

    public int DeckOptimizerTimeoutMinutes { get; set; } = 5;

    [JsonProperty("CpuUsagePercent")]
    private int LegacyCpuUsagePercent { get; set; } = 100;

    public TriadCollectionSettings TriadCollection { get; set; } = new();

    public GoldSaucerGateSettings GoldSaucerGates { get; set; } = new();

    public bool PauseForAutoRetainer { get; set; }

    /// <summary>仙人微彩：一張完成關窗後自動確認「購買下一張」，把當日彩券一次完成。
    /// 只在 MiniCactpot 模組啟用時生效。</summary>
    public bool MiniCactpotAutoPlayAgain { get; set; } = true;

    /// <summary>仙人微彩：兩次點擊之間的最短間隔（毫秒）。
    /// 🔴 金蝶遊樂園的自動化是伺服器看得見的行為，「看起來像人在操作」本身就有價值。
    /// 下限刻意留在 <see cref="MiniCactpotMinClickIntervalMs"/>，明顯慢於同類外掛的 100 ms —— 不要為了快把節奏壓到極限。</summary>
    public int MiniCactpotClickIntervalMs { get; set; } = 800;

    /// <summary>仙人微彩：全部翻開後，等開獎動畫與派彩數字跑完再關窗的時間（毫秒）。</summary>
    public int MiniCactpotCloseDelayMs { get; set; } = 1600;

    public const int MiniCactpotMinClickIntervalMs = 400;
    public const int MiniCactpotMaxClickIntervalMs = 5000;
    public const int MiniCactpotMaxCloseDelayMs = 10000;

    /// <summary>仙人微彩：這一張的派彩達到 <see cref="MiniCactpotJackpotThresholdMgp"/> 時，
    /// 透過 IPC 請 TataruPraise 念一句「中獎」。對方沒安裝就什麼都不會發生。</summary>
    public bool MiniCactpotJackpotTataruPraise { get; set; } = true;

    /// <summary>仙人微彩：要請塔塔露提醒的派彩門檻（金碟幣）。
    /// 📌 派彩表最小的非零值是 36、最大是 10000，所以滑桿就夾在這兩個真實值之間——
    /// 設成 <see cref="MiniCactpotJackpotMinThresholdMgp"/> 等於「中任何獎都念」。</summary>
    public int MiniCactpotJackpotThresholdMgp { get; set; } = 1000;

    public const int MiniCactpotJackpotMinThresholdMgp = 36;
    public const int MiniCactpotJackpotMaxThresholdMgp = 10000;

    /// <summary>仙人仙彩：號碼來源。false（預設）＝每次隨機、true＝固定使用
    /// <see cref="JumboCactpotFixedNumber"/>。只在 JumboCactpot 模組啟用時生效。</summary>
    public bool JumboCactpotUseFixedNumber { get; set; } = false;

    /// <summary>仙人仙彩：固定號碼（0000-9999）。</summary>
    public int JumboCactpotFixedNumber { get; set; } = 0;

    public const int JumboCactpotMaxNumber = 9999;

    /// <summary>重複幻卡交換：安全線。只有持有數超過這個值的卡才會被列為「可賣」，
    /// 確保每種卡（含牌組用的那張）至少留這麼多張。預設 1。只在 SellDuplicateCards 模組啟用時生效。</summary>
    public int SellCardsKeepAtLeast { get; set; } = 1;

    public const int SellCardsMaxKeepAtLeast = 10;

    /// <summary>孤樹無援（陸行鳥廣場伐木機台）自動遊玩設定。只在 OutOnALimb 模組啟用時生效。</summary>
    public OutOnALimb.LimbSettings OutOnALimb { get; set; } = new();

    public int Version { get; set; }

    public void MigrateToBackgroundCpuCores()
    {
        if (Version < ConfigVersionBackgroundCpuCores)
        {
            if (DeckOptimizerMaxThreads <= 0)
            {
                var pct = Math.Clamp(LegacyCpuUsagePercent, 10, 100);
                DeckOptimizerMaxThreads = pct >= 100
                    ? 0
                    : Math.Max(1, Environment.ProcessorCount * pct / 100);
            }
            else
            {
                DeckOptimizerMaxThreads = ClampDeckOptimizerMaxThreads(DeckOptimizerMaxThreads);
            }

            Version = ConfigVersionBackgroundCpuCores;
        }
        else
        {
            DeckOptimizerMaxThreads = ClampDeckOptimizerMaxThreads(DeckOptimizerMaxThreads);
        }
    }

    public static int ClampDeckOptimizerMaxThreads(int threads) =>
        Math.Clamp(threads, 0, Environment.ProcessorCount);

    public bool IsModuleEnabled(string moduleName) => EnabledModules.Contains(moduleName);

    public void SetModuleEnabled(string moduleName, bool enabled)
    {
        if (enabled)
        {
            if (!EnabledModules.Contains(moduleName))
            {
                EnabledModules.Add(moduleName);
            }
        }
        else
        {
            EnabledModules.Remove(moduleName);
        }
    }

    public void UpdateStats(Action<Stats> updateAction)
    {
        updateAction(Stats);
        updateAction(SessionStats);
    }

    public void Save() => EzConfig.Save();
}

[Serializable]
public class GoldSaucerGateSettings
{
    public bool WindBlowsAutoMovement { get; set; } = true;
    public bool LeapOfFaithAutoMovement { get; set; }
    public float LeapOfFaithJumpIntervalSeconds { get; set; } = 1.2f;
    public bool CliffhangerAutoMovement { get; set; }
    public float AirForceBombAvoidRadius { get; set; } = 134f;
    public float CliffhangerBombBlastRadiusGuess { get; set; } = 2.6f;
    public float CliffhangerBombDisplaySeconds { get; set; } = 2.3f;

    // Each overlay draws its own full-screen ImGui window every frame — drawing all of them at
    // once (esp. the platform markers/planes, which can be hundreds of points) measurably drops
    // FPS. Split into independent toggles so each can be turned off without losing the others.
    public bool LeapOfFaithShowPlatformMarkers { get; set; } = true;
    public bool LeapOfFaithShowOwnTrail { get; set; }
    public bool LeapOfFaithShowOtherPlayerTrails { get; set; }
    public bool LeapOfFaithShowTargetPointer { get; set; } = true;
    public bool CliffhangerShowOwnTrail { get; set; }
    public bool CliffhangerShowBombBlastCircles { get; set; } = true;
    public bool AirForceShowPredictionCircles { get; set; } = true;

    // Registration NPC positions are never guessed/hardcoded (see the repeated DataId
    // misidentification lessons) — the user targets the real NPC in-game once and hits a "record"
    // button, which stores whatever they had targeted. These defaults are the user's own
    // recorded spots, promoted to defaults on request ("讀取目前設定 並設為預設值").
    public GateNpcSpot AirForceNpcSpot { get; set; } = new()
    {
        Recorded = true, X = -57.8622f, Y = 3.29f, Z = -65.3993f, DataId = 1016306, NpcName = "仙人掌怪導覽員"
    };
    public bool AirForceNpcAutoNavigate { get; set; } = true;
    public GateNpcSpot WindBlowsNpcSpot { get; set; } = new()
    {
        Recorded = true, X = 77.59336f, Y = -5.0000005f, Z = -69.821f, DataId = 1010476, NpcName = "傲慢的咒術士"
    };
    public bool WindBlowsNpcAutoNavigate { get; set; } = true;
    public GateNpcSpot SliceIsRightNpcSpot { get; set; } = new()
    {
        Recorded = true, X = 77.89336f, Y = -5.000001f, Z = -69.821f, DataId = 1031796, NpcName = "保鑣的小弟"
    };
    public bool SliceIsRightNpcAutoNavigate { get; set; } = true;

    // Once actually inside Slice is Right, the fight itself is handled by BossModReborn — Saucy
    // only needs to walk the player to the field boundary/starting spot first, then hand off. A
    // separate spot from SliceIsRightNpcSpot above (that one's the pre-GATE registration NPC,
    // outside the instance; this one is a position INSIDE the GATE itself).
    public GateNpcSpot SliceIsRightStartSpot { get; set; } = new()
    {
        Recorded = true, X = 70.34072f, Y = -4.4730473f, Z = -50.693813f, DataId = 0, NpcName = "場地邊界"
    };
    public bool SliceIsRightStartAutoNavigate { get; set; } = true;

    // Cliffhanger's registration NPC actually appears at two different spots (confirmed by user),
    // so unlike the single-spot GateNpcSpot fields above this needs a user-managed list — same
    // add/delete pattern as EventCoordinatorSpots, never guessed.
    public List<GateNpcSpot> CliffhangerNpcSpots { get; set; } =
    [
        new() { Recorded = true, X = -17.27307f, Y = 3.2837293f, Z = -83.23351f, DataId = 1010473, NpcName = "束手無策的女性" },
        new() { Recorded = true, X = 49.60059f, Y = 3.9997206f, Z = 45.0887f, DataId = 1010447, NpcName = "束手無策的少女" }
    ];
    public bool CliffhangerNpcAutoNavigate { get; set; } = true;

    // Sparse, user-marked route: start, each jump takeoff point (with its own recorded facing
    // direction, captured separately from the position), and the end — walked in order, letting
    // vnavmesh handle the actual walking between marked points (real navmesh coverage confirmed
    // for this GATE) and only taking manual key control at a jump point using the recorded
    // direction. Much more precise than deriving jump timing/direction from a dense auto-recording.
    public List<CliffhangerRouteWaypoint> CliffhangerRoute { get; set; } = [];
    public bool CliffhangerRouteAutoNavigate { get; set; } = true;

    // Scoped-down 3-point unit test for jump mechanics, developed directly in the Debug tab rather
    // than the full ordered route list — per request ("換到debug頁面開發移動和跳躍...只要能記錄三
    // 個點就好 跳躍起點 跳躍點 跳躍完後的下一個跳躍起點"): the pre-jump approach start, the actual
    // jump takeoff point, and where the NEXT segment should start after landing — enough to isolate
    // and tune one jump segment at a time without touching the real route.
    public CliffhangerJumpTestSpot CliffhangerJumpTestStart { get; set; } = new();
    public CliffhangerJumpTestSpot CliffhangerJumpTestJump { get; set; } = new();
    public CliffhangerJumpTestSpot CliffhangerJumpTestNextStart { get; set; } = new();

    public bool AutoOpenUiOnGateJoin { get; set; } = true;

    // Event Coordinator NPCs ("活動解說員") teleport the player to whichever area has the next
    // GATE — there are several of them scattered around the Gold Saucer, so unlike the single
    // per-GATE registration spots above, this is a user-managed list (add/delete freely), never
    // guessed. Defaults promoted from the user's own recorded list.
    public List<GateNpcSpot> EventCoordinatorSpots { get; set; } =
    [
        new() { Recorded = true, X = 44.174805f, Y = -5.000001f, Z = -16.678162f, DataId = 1011093, NpcName = "活動解說員" },
        new() { Recorded = true, X = 21.530457f, Y = 3.9997296f, Z = 39.902344f, DataId = 1011080, NpcName = "活動解說員" },
        new() { Recorded = true, X = -12.527649f, Y = 3.2546434f, Z = -73.16705f, DataId = 1011084, NpcName = "活動解說員" }
    ];
    public bool EventCoordinatorAutoNavigate { get; set; } = true;

    // "為每個GATE單獨加上自動報名開關" — replaces the old single AutoJoinNearSupportedNpc toggle
    // (which applied to every supported GATE at once) with an independent switch per GATE, so e.g.
    // Cliffhanger auto-register can stay on while WindBlows is turned off.
    public bool AirForceAutoJoin { get; set; } = true;
    public bool WindBlowsAutoJoin { get; set; } = true;
    public bool SliceIsRightAutoJoin { get; set; } = true;
    public bool CliffhangerAutoJoin { get; set; }

    // Persisted (not just an in-memory static flag) so a plugin reload mid-window doesn't forget
    // "already handled this window" and immediately search for/walk to the same NPC again
    // ("我已參加過 重載後記錄消失 又回去找NPC").
    public long LastCoordinatorHandledUtcTicks { get; set; }
    public long LastJoinHandledUtcTicks { get; set; }
}

[Serializable]
public class GateNpcSpot
{
    public bool Recorded { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public uint DataId { get; set; }
    public string NpcName { get; set; } = "";
}

[Serializable]
public class CliffhangerJumpTestSpot
{
    public bool Recorded { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

[Serializable]
public class CliffhangerRouteWaypoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool IsJumpPoint { get; set; }
    public string Label { get; set; } = "";
}
