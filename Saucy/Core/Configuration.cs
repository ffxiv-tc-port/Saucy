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
    public ObservableCollection<string> EnabledModules = [];

    public bool UseSimmedDeck { get; set; } = false;

    public bool AlwaysBuildOptimizedDeck { get; set; } = false;

    public bool UseCachedOptimizedDeckIfAvailable { get; set; } = false;
    public bool ShowOptimizerChatSpam { get; set; } = false;

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
    public string SelectedSound { get; set; } = "Moogle";
    public bool OnlyUnobtainedCards { get; set; } = false;
    public bool OpenAutomatically { get; set; } = false;

    public bool SaucyThemeEnabled { get; set; } = true;

    public bool CollectionUiEnabled { get; set; } = true;

    [JsonProperty("BackgroundWorkCpuCores")]
    public int DeckOptimizerMaxThreads { get; set; }

    public int DeckOptimizerTimeoutMinutes { get; set; } = 2;

    [JsonProperty("CpuUsagePercent")]
    private int LegacyCpuUsagePercent { get; set; } = 100;

    public TriadCollectionSettings TriadCollection { get; set; } = new();

    public GoldSaucerGateSettings GoldSaucerGates { get; set; } = new();

    public bool PauseForAutoRetainer { get; set; }

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
    public bool WindBlowsAutoMovement { get; set; }
    public bool LeapOfFaithAutoMovement { get; set; }
    public bool LeapOfFaithInvertTurn { get; set; }
    public float LeapOfFaithJumpIntervalSeconds { get; set; } = 1.3f;
    public bool CliffhangerAutoMovement { get; set; }
    public bool CliffhangerInvertTurn { get; set; }
    public float AirForceBombAvoidRadius { get; set; } = 220f;
    public float CliffhangerBombBlastRadiusGuess { get; set; } = 2f;
    public float CliffhangerBombDisplaySeconds { get; set; } = 3f;

    // Each overlay draws its own full-screen ImGui window every frame — drawing all of them at
    // once (esp. the platform markers/planes, which can be hundreds of points) measurably drops
    // FPS. Split into independent toggles so each can be turned off without losing the others.
    public bool LeapOfFaithShowPlatformMarkers { get; set; } = true;
    public bool LeapOfFaithShowOwnTrail { get; set; } = true;
    public bool LeapOfFaithShowOtherPlayerTrails { get; set; } = true;
    public bool LeapOfFaithShowTargetPointer { get; set; } = true;
    public bool CliffhangerShowOwnTrail { get; set; } = true;
    public bool CliffhangerShowBombBlastCircles { get; set; } = true;
    public bool AirForceShowPredictionCircles { get; set; } = true;

    // Registration NPC positions are never guessed/hardcoded (see the repeated DataId
    // misidentification lessons) — the user targets the real NPC in-game once and hits a "record"
    // button, which stores whatever they had targeted. Auto-navigation only ever walks toward a
    // point the user personally confirmed, and stops short of it (interact/registration itself
    // stays manual, per "3 不用做" / "1. NPC 可手動登記").
    public GateNpcSpot AirForceNpcSpot { get; set; } = new();
    public bool AirForceNpcAutoNavigate { get; set; }
    public GateNpcSpot WindBlowsNpcSpot { get; set; } = new();
    public bool WindBlowsNpcAutoNavigate { get; set; }
    public GateNpcSpot SliceIsRightNpcSpot { get; set; } = new();
    public bool SliceIsRightNpcAutoNavigate { get; set; }

    public bool AutoOpenUiOnGateJoin { get; set; } = true;

    // Event Coordinator NPCs ("活動解說員") teleport the player to whichever area has the next
    // GATE — there are several of them scattered around the Gold Saucer, so unlike the single
    // per-GATE registration spots above, this is a user-managed list (add/delete freely), never
    // guessed. Both automations default OFF — the user turns them on explicitly once they've
    // recorded at least one spot.
    public List<GateNpcSpot> EventCoordinatorSpots { get; set; } = [];
    public bool EventCoordinatorAutoNavigate { get; set; }
    public bool AutoJoinNearSupportedNpc { get; set; }
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
