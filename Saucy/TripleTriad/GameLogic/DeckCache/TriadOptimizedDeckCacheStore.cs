using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace Saucy.TripleTriad.GameLogic;

internal static class TriadOptimizedDeckCacheStore
{
    // Old Dalamud has no IPlayerState service (Svc.PlayerState); read the same data directly
    // from the FFXIVClientStructs UIState.PlayerState struct and the local player object instead.
    private static unsafe bool LocalPlayerStateIsLoaded
    {
        get
        {
            var uiState = UIState.Instance();
            return uiState != null && uiState->PlayerState.IsLoaded;
        }
    }

    private static unsafe ulong LocalPlayerContentId
    {
        get
        {
            var uiState = UIState.Instance();
            return uiState != null ? uiState->PlayerState.ContentId : 0;
        }
    }

    private static unsafe string LocalPlayerCharacterName
    {
        get
        {
            var uiState = UIState.Instance();
            return uiState != null ? uiState->PlayerState.CharacterNameString : string.Empty;
        }
    }
    public const int SchemaVersion = 2;
    public const int RebuildAfterNewCardCount = 5;

    private const string CacheFileName = "OptimizedDeckCache.json";
    private const string LegacyCacheFolderName = "OptimizedDeckCache";

    private static readonly object FileLock = new();

    private static ulong activeContentId;
    private static TriadOptimizedDeckCacheFile? activeFile;
    private static bool loadedForCharacter;

    // GetCharacterCacheViews() is called every frame the settings "Cache" tab is drawn; caching
    // the result and only recomputing when the underlying data actually changed avoids rescanning
    // the plugin-configs directory and re-parsing every character's JSON cache file every frame.
    private static IReadOnlyList<TriadOptimizedDeckCacheCharacterView>? cachedCharacterViews;
    private static bool characterViewsDirty = true;

    public static void TickCharacter()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            ResetActive();
            return;
        }

        var contentId = GetLocalContentId();
        if (contentId == 0)
        {
            ResetActive();
            return;
        }

        if (!loadedForCharacter || contentId != activeContentId)
        {
            LoadForCharacter(contentId);
        }
    }

    public static bool TryGetEntry(string sessionKey, out TriadOptimizedDeckCacheEntry? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(sessionKey))
        {
            return false;
        }

        EnsureLoaded();
        return activeFile != null &&
               activeFile.Entries.TryGetValue(sessionKey, out entry);
    }

    public static bool TryGetRegionalMods(int npcId, out List<TriadGameModifier> regionMods)
    {
        regionMods = [];
        if (npcId < 0)
        {
            return false;
        }

        EnsureLoaded();
        if (activeFile?.RegionalRuleSignaturesByNpcId == null ||
            !activeFile.RegionalRuleSignaturesByNpcId.TryGetValue(npcId, out var signatures) ||
            signatures is not { Length: > 0 })
        {
            return false;
        }

        regionMods = TriadOptimizerSessionKey.RegionModsFromSignatures(signatures);
        return regionMods.Count > 0;
    }

    public static void UpsertRegionalMods(int npcId, IReadOnlyList<TriadGameModifier> regionMods)
    {
        if (npcId < 0)
        {
            return;
        }

        EnsureLoaded();
        activeFile ??= new();
        activeFile.Version = SchemaVersion;
        activeFile.RegionalRuleSignaturesByNpcId ??= new();

        if (regionMods == null || regionMods.Count == 0)
        {
            if (activeFile.RegionalRuleSignaturesByNpcId.Remove(npcId))
            {
                SaveActive();
            }

            return;
        }

        var signatures = TriadOptimizerSessionKey.GetModSignatures(regionMods);
        if (signatures.Length == 0)
        {
            return;
        }

        if (activeFile.RegionalRuleSignaturesByNpcId.TryGetValue(npcId, out var existing) &&
            existing.SequenceEqual(signatures, StringComparer.Ordinal))
        {
            return;
        }

        activeFile.RegionalRuleSignaturesByNpcId[npcId] = signatures;
        SaveActive();
    }

    public static bool HasAnyEntryForNpc(int npcId)
    {
        if (npcId < 0)
        {
            return false;
        }

        EnsureLoaded();
        if (activeFile == null)
        {
            return false;
        }

        foreach (var entry in activeFile.Entries.Values)
        {
            if (entry.NpcId == npcId)
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetOwnedSnapshotForNpc(int npcId, string sessionKey, out int[] ownedAtBuild)
    {
        ownedAtBuild = [];
        EnsureLoaded();
        if (activeFile == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(sessionKey) &&
            activeFile.Entries.TryGetValue(sessionKey, out var sessionEntry) &&
            HasOwnedSnapshot(sessionEntry))
        {
            ownedAtBuild = sessionEntry.OwnedCardIdsAtBuild;
            return true;
        }

        TriadOptimizedDeckCacheEntry? latest = null;
        foreach (var entry in activeFile.Entries.Values)
        {
            if (entry.NpcId != npcId || !HasOwnedSnapshot(entry))
            {
                continue;
            }

            if (latest == null || entry.BuiltUtcTicks > latest.BuiltUtcTicks)
            {
                latest = entry;
            }
        }

        if (latest == null)
        {
            return false;
        }

        ownedAtBuild = latest.OwnedCardIdsAtBuild;
        return true;
    }

    private static bool HasOwnedSnapshot(TriadOptimizedDeckCacheEntry entry) =>
        entry?.OwnedCardIdsAtBuild is { Length: > 0 };

    public static IReadOnlyList<TriadOptimizedDeckCacheCharacterView> GetCharacterCacheViews()
    {
        lock (FileLock)
        {
            EnsureLoaded();

            if (!characterViewsDirty && cachedCharacterViews != null)
            {
                return cachedCharacterViews;
            }

            var currentContentId = GetLocalContentId();
            var views = new List<TriadOptimizedDeckCacheCharacterView>();
            var configsRoot = GetPluginConfigsRoot();

            if (Directory.Exists(configsRoot))
            {
                foreach (var charDir in Directory.EnumerateDirectories(configsRoot, "CHAR_*"))
                {
                    var folderName = Path.GetFileName(charDir);
                    if (!TryParseContentIdFromFolder(folderName, out var contentId))
                    {
                        continue;
                    }

                    var cachePath = Path.Combine(charDir, Svc.PluginInterface.InternalName, CacheFileName);
                    if (!TryLoadCacheFile(cachePath, out var file))
                    {
                        continue;
                    }

                    var cacheFile = contentId == currentContentId && activeFile != null && loadedForCharacter
                        ? activeFile
                        : file;
                    if (cacheFile == null)
                    {
                        continue;
                    }

                    views.Add(BuildCharacterView(contentId, cacheFile, contentId == currentContentId));
                }
            }

            if (currentContentId != 0 &&
                loadedForCharacter &&
                activeFile != null &&
                views.All(v => v.ContentId != currentContentId))
            {
                views.Add(BuildCharacterView(currentContentId, activeFile, true));
            }

            cachedCharacterViews =
            [
                .. views
                    .OrderByDescending(v => v.IsCurrentCharacter)
                    .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            ];
            characterViewsDirty = false;
            return cachedCharacterViews;
        }
    }

    /// <summary>
    /// Forces the next call to <see cref="GetCharacterCacheViews"/> to rescan disk instead of
    /// returning the cached list. Called automatically whenever cache data actually changes;
    /// exposed publicly so the settings UI can offer a manual refresh too.
    /// </summary>
    public static void InvalidateCharacterCacheViews()
    {
        lock (FileLock)
        {
            characterViewsDirty = true;
        }
    }

    public static void Upsert(TriadOptimizedDeckCacheEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.SessionKey))
        {
            return;
        }

        EnsureLoaded();
        activeFile ??= new();
        activeFile.Version = SchemaVersion;
        PruneOtherEntriesForNpc(entry.NpcId, entry.SessionKey);
        activeFile.Entries[entry.SessionKey] = entry;
        SaveActive();
    }

    private static void PruneOtherEntriesForNpc(int npcId, string keepSessionKey)
    {
        if (activeFile == null || activeFile.Entries.Count == 0)
        {
            return;
        }

        var staleKeys = activeFile.Entries
            .Where(kvp => kvp.Value.NpcId == npcId &&
                          !string.Equals(kvp.Key, keepSessionKey, StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            activeFile.Entries.Remove(key);
        }
    }

    public static bool TryUpdateEstWinChance(string sessionKey, float estWinChance)
    {
        if (string.IsNullOrEmpty(sessionKey) || estWinChance <= 0f)
        {
            return false;
        }

        EnsureLoaded();
        if (activeFile == null || !activeFile.Entries.TryGetValue(sessionKey, out var entry))
        {
            return false;
        }

        entry.EstWinChance = estWinChance;
        SaveActive();
        return true;
    }

    public static void Remove(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
        {
            return;
        }

        EnsureLoaded();
        if (activeFile?.Entries.Remove(sessionKey) == true)
        {
            SaveActive();
        }
    }

    public static void RemoveAllForNpc(int npcId)
    {
        EnsureLoaded();
        if (activeFile == null || activeFile.Entries.Count == 0)
        {
            return;
        }

        var staleKeys = activeFile.Entries
            .Where(kvp => kvp.Value.NpcId == npcId)
            .Select(kvp => kvp.Key)
            .ToList();

        if (staleKeys.Count == 0)
        {
            return;
        }

        foreach (var key in staleKeys)
        {
            activeFile.Entries.Remove(key);
        }

        SaveActive();
    }

    public static void ClearActiveCharacter()
    {
        lock (FileLock)
        {
            if (activeContentId == 0)
            {
                return;
            }

            activeFile = new();
            loadedForCharacter = true;
            characterViewsDirty = true;

            try
            {
                var path = GetCachePath(activeContentId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[Saucy] Failed to delete optimized deck cache file.");
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            return;
        }

        var contentId = GetLocalContentId();
        if (contentId == 0)
        {
            return;
        }

        if (!loadedForCharacter || contentId != activeContentId)
        {
            LoadForCharacter(contentId);
        }
    }

    private static ulong GetLocalContentId()
    {
        if (!Svc.ClientState.IsLoggedIn || !LocalPlayerStateIsLoaded)
        {
            return 0;
        }

        return LocalPlayerContentId;
    }

    private static void LoadForCharacter(ulong contentId)
    {
        lock (FileLock)
        {
            activeContentId = contentId;
            loadedForCharacter = true;
        }

        // TickCharacter() calls this from the framework thread once per login/character switch.
        // The rest of this class assumes single-threaded (framework-thread-only) access to
        // activeFile and isn't otherwise lock-protected, so do the file I/O + JSON parsing on a
        // background thread but publish the result back on the framework thread instead of
        // touching activeFile directly here.
        Task.Run(() =>
        {
            var path = GetCachePath(contentId);
            if (!File.Exists(path))
            {
                TryMigrateLegacyCache(contentId, path);
            }

            TriadOptimizedDeckCacheFile loaded;
            if (!File.Exists(path))
            {
                loaded = new();
                loaded.RegionalRuleSignaturesByNpcId = new();
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(path);
                    loaded = JsonConvert.DeserializeObject<TriadOptimizedDeckCacheFile>(json) ??
                                 new TriadOptimizedDeckCacheFile();
                    if (loaded.Version != SchemaVersion)
                    {
                        if (loaded.Version == 1)
                        {
                            loaded.Version = SchemaVersion;
                            loaded.RegionalRuleSignaturesByNpcId ??= new();
                        }
                        else
                        {
                            loaded = new();
                        }
                    }

                    loaded.RegionalRuleSignaturesByNpcId ??= new();
                }
                catch (Exception ex)
                {
                    Svc.Log.Warning(ex, "[Saucy] Failed to load optimized deck cache; starting empty.");
                    loaded = new();
                }
            }

            Svc.Framework.RunOnFrameworkThread(() =>
            {
                lock (FileLock)
                {
                    if (activeContentId != contentId)
                        return; // character changed again before this finished; drop stale result

                    activeFile = loaded;
                    characterViewsDirty = true;
                    ImportLegacyBuildTimestampsLocked();
                }
            });
        });
    }

    private static void ImportLegacyBuildTimestampsLocked()
    {
        if (activeFile == null || C.TriadOptimizedDeckBuiltUtcTicksByNpcId.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var entry in activeFile.Entries.Values)
        {
            if (entry.BuiltUtcTicks > 0)
            {
                continue;
            }

            if (!C.TriadOptimizedDeckBuiltUtcTicksByNpcId.TryGetValue(entry.NpcId, out var ticks))
            {
                continue;
            }

            entry.BuiltUtcTicks = ticks;
            changed = true;
        }

        if (changed)
        {
            SaveActive();
        }
    }

    private static void SaveActive()
    {
        if (!loadedForCharacter || activeFile == null)
        {
            return;
        }

        lock (FileLock)
        {
            try
            {
                StampCharacterMetadata(activeFile);
                var path = GetCachePath(activeContentId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonConvert.SerializeObject(activeFile, Formatting.Indented);
                File.WriteAllText(path, json);
                characterViewsDirty = true;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[Saucy] Failed to save optimized deck cache.");
            }
        }
    }

    private static void ResetActive()
    {
        loadedForCharacter = false;
        activeFile = null;
        activeContentId = 0;
        characterViewsDirty = true;
    }

    private static string GetCachePath(ulong contentId)
    {
        var charDir = GetCharacterConfigDirectory(contentId);
        return Path.Combine(charDir, CacheFileName);
    }

    private static string GetPluginConfigsRoot()
    {
        var pluginConfigDir = Svc.PluginInterface.GetPluginConfigDirectory();
        return Directory.GetParent(pluginConfigDir)?.FullName ?? pluginConfigDir;
    }

    private static string GetCharacterConfigDirectory(ulong contentId) =>
        Path.Combine(GetPluginConfigsRoot(), $"CHAR_{contentId}", Svc.PluginInterface.InternalName);

    private static bool TryParseContentIdFromFolder(string folderName, out ulong contentId)
    {
        contentId = 0;
        const string prefix = "CHAR_";
        if (!folderName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return ulong.TryParse(folderName[prefix.Length..], out contentId);
    }

    private static bool TryLoadCacheFile(string path, out TriadOptimizedDeckCacheFile? file)
    {
        file = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            file = JsonConvert.DeserializeObject<TriadOptimizedDeckCacheFile>(json);
            if (file == null || file.Version is not (1 or 2))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[Saucy] Failed to read optimized deck cache at {Path}.", path);
            return false;
        }
    }

    private static TriadOptimizedDeckCacheCharacterView BuildCharacterView(
        ulong contentId,
        TriadOptimizedDeckCacheFile file,
        bool isCurrentCharacter)
        => new()
        {
            ContentId = contentId,
            DisplayName = ResolveCharacterDisplayName(contentId, file, isCurrentCharacter),
            IsCurrentCharacter = isCurrentCharacter,
            Entries =
            [
                .. file.Entries.Values
                    .OrderByDescending(e => e.BuiltUtcTicks)
                    .ThenBy(e => e.NpcName, StringComparer.OrdinalIgnoreCase)
            ]
        };

    private static void StampCharacterMetadata(TriadOptimizedDeckCacheFile file)
    {
        if (!LocalPlayerStateIsLoaded || activeContentId == 0 || LocalPlayerContentId != activeContentId)
        {
            return;
        }

        file.ContentId = activeContentId;
        file.CharacterName = LocalPlayerCharacterName;
        file.HomeWorldRowId = Svc.ClientState.LocalPlayer?.HomeWorld.RowId ?? 0;
    }

    private static string ResolveCharacterDisplayName(
        ulong contentId,
        TriadOptimizedDeckCacheFile file,
        bool isCurrentCharacter)
    {
        if (!string.IsNullOrWhiteSpace(file.CharacterName))
        {
            var worldName = ResolveWorldName(file.HomeWorldRowId);
            return string.IsNullOrEmpty(worldName)
                ? file.CharacterName
                : $"{file.CharacterName} @ {worldName}";
        }

        if (isCurrentCharacter && LocalPlayerStateIsLoaded && LocalPlayerContentId == contentId)
        {
            var worldName = Svc.ClientState.LocalPlayer?.HomeWorld.ValueNullable?.Name.ToString();
            return string.IsNullOrEmpty(worldName)
                ? LocalPlayerCharacterName
                : $"{LocalPlayerCharacterName} @ {worldName}";
        }

        return $"Character {contentId}";
    }

    private static string ResolveWorldName(uint homeWorldRowId)
    {
        if (homeWorldRowId == 0)
        {
            return string.Empty;
        }

        var world = Svc.Data.GetExcelSheet<World>()?.GetRow(homeWorldRowId);
        return world?.Name.ToString() ?? string.Empty;
    }

    private static void TryMigrateLegacyCache(ulong contentId, string newPath)
    {
        var pluginDir = Svc.PluginInterface.GetPluginConfigDirectory();
        var legacyPath = Path.Combine(pluginDir, LegacyCacheFolderName, $"{contentId}.json");
        if (!File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Move(legacyPath, newPath);
            Svc.Log.Info($"[Saucy] Migrated optimized deck cache to CHAR_{contentId} layout.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[Saucy] Failed to migrate legacy optimized deck cache.");
        }
    }
}
