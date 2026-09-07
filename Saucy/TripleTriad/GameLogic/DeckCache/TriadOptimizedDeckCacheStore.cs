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

    // 只包住真正碰磁碟的那幾行，跟 FileLock 分開。有它才保住「同一個檔不會被兩條執行緒同時寫」
    // ——那件事原本是 FileLock 順手扛著的，把 I/O 搬出 FileLock 之後就沒人管了。
    // 取鎖順序永遠是「先放掉 FileLock 再拿 IoLock」，反過來會形成環。
    private static readonly object IoLock = new();

    private static ulong activeContentId;
    private static TriadOptimizedDeckCacheFile? activeFile;
    private static bool loadedForCharacter;

    // GetCharacterCacheViews() is called every frame the settings "Cache" tab is drawn; caching
    // the result and only recomputing when the underlying data actually changed avoids rescanning
    // the plugin-configs directory and re-parsing every character's JSON cache file every frame.
    private static IReadOnlyList<TriadOptimizedDeckCacheCharacterView>? cachedCharacterViews;
    private static bool characterViewsDirty = true;

    // 每拍一份存檔快照就 +1（在 FileLock 內）；lastWrittenSequence 是實際落地的最後一號
    // （在 IoLock 內）。舊快照輪到寫入時若已經有更新的落地了就整份跳過，不把新的蓋回舊的。
    private static long saveSequence;
    private static long lastWrittenSequence;

    // 掃磁碟已經搬到 FileLock 外面，所以要有辦法知道「掃的期間資料有沒有又被改過」。
    private static long viewsScanEpoch;

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
        EnsureLoaded();

        long scanEpoch;
        lock (FileLock)
        {
            if (!characterViewsDirty && cachedCharacterViews != null)
            {
                return cachedCharacterViews;
            }

            scanEpoch = viewsScanEpoch;
        }

        // 掃目錄與逐檔讀 JSON 都搬到 FileLock 外面，繪製端不會在持著那把鎖的時候等磁碟。
        // 這一段改用 IoLock，只擋「同一批檔案同時被寫」，不與繪製端的狀態共用一把鎖。
        var currentContentId = GetLocalContentId();
        var scanned = new List<(ulong ContentId, TriadOptimizedDeckCacheFile File)>();
        lock (IoLock)
        {
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
                    if (!TryLoadCacheFile(cachePath, out var file) || file == null)
                    {
                        continue;
                    }

                    scanned.Add((contentId, file));
                }
            }
        }

        lock (FileLock)
        {
            var views = new List<TriadOptimizedDeckCacheCharacterView>();
            foreach (var (contentId, file) in scanned)
            {
                var cacheFile = contentId == currentContentId && activeFile != null && loadedForCharacter
                    ? activeFile
                    : file;
                views.Add(BuildCharacterView(contentId, cacheFile, contentId == currentContentId));
            }

            if (currentContentId != 0 &&
                loadedForCharacter &&
                activeFile != null &&
                views.All(v => v.ContentId != currentContentId))
            {
                views.Add(BuildCharacterView(currentContentId, activeFile, true));
            }

            IReadOnlyList<TriadOptimizedDeckCacheCharacterView> ordered =
            [
                .. views
                    .OrderByDescending(v => v.IsCurrentCharacter)
                    .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            ];

            // 掃描期間資料若又被改過（存檔、換角色、手動重新整理），這批結果只回傳、不落快取，
            // 讓下一幀重掃；不要拿舊掃描的結果去蓋掉新的狀態。
            if (scanEpoch == viewsScanEpoch)
            {
                cachedCharacterViews = ordered;
                characterViewsDirty = false;
            }

            return ordered;
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
            MarkCharacterViewsDirtyLocked();
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
        ulong contentId;
        long sequence;

        lock (FileLock)
        {
            if (activeContentId == 0)
            {
                return;
            }

            activeFile = new();
            loadedForCharacter = true;
            MarkCharacterViewsDirtyLocked();
            contentId = activeContentId;
            sequence = ++saveSequence;
        }

        // 出鎖之後才碰磁碟：在 _preGameLock 的延後範圍內時排到出鎖後才刪，
        // 不在範圍內時當場刪，與原本逐字相同。
        if (TriadDeferredSideEffects.TryDeferFileWrite(() => DeleteCacheFile(contentId, sequence)))
        {
            return;
        }

        DeleteCacheFile(contentId, sequence);
    }

    private static void DeleteCacheFile(ulong contentId, long sequence)
    {
        // 刪檔與寫檔共用同一組號碼與同一把 IoLock，所以「清空」不會被一份更早拍的快照蓋回來。
        lock (IoLock)
        {
            if (sequence <= lastWrittenSequence)
            {
                return;
            }

            try
            {
                var path = GetCachePath(contentId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                lastWrittenSequence = sequence;
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
                bool needsSave;
                lock (FileLock)
                {
                    if (activeContentId != contentId)
                        return; // character changed again before this finished; drop stale result

                    activeFile = loaded;
                    MarkCharacterViewsDirtyLocked();
                    needsSave = ImportLegacyBuildTimestampsLocked();
                }

                // 存檔一定要在 FileLock 外面呼叫：SaveActive() 之後會去拿 IoLock 碰磁碟，
                // 在 FileLock 內呼叫等於把磁碟等待又搬回這把鎖裡。
                if (needsSave)
                {
                    SaveActive();
                }
            });
        });
    }

    /// <summary>回報有沒有改到資料；實際存檔由呼叫端在放掉 FileLock 之後自己做。</summary>
    private static bool ImportLegacyBuildTimestampsLocked()
    {
        if (activeFile == null || C.TriadOptimizedDeckBuiltUtcTicksByNpcId.Count == 0)
        {
            return false;
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

        return changed;
    }

    // 只在持有 FileLock 時呼叫。
    private static void MarkCharacterViewsDirtyLocked()
    {
        characterViewsDirty = true;
        viewsScanEpoch++;
    }

    private static void SaveActive()
    {
        string json;
        ulong contentId;
        long sequence;

        lock (FileLock)
        {
            if (!loadedForCharacter || activeFile == null)
            {
                return;
            }

            StampCharacterMetadata(activeFile);

            // 鎖內只做「拍快照」：序列化成字串之後就沒有任何可變狀態逸出鎖外，
            // 組路徑（會建目錄）與寫檔都留到出鎖之後。
            json = JsonConvert.SerializeObject(activeFile, Formatting.Indented);
            contentId = activeContentId;
            sequence = ++saveSequence;
            MarkCharacterViewsDirtyLocked();
        }

        // 出鎖之後才碰磁碟：在 _preGameLock 的延後範圍內時排到出鎖後才寫，
        // 不在範圍內時當場寫，與原本逐字相同。序號閘門保證被延後的舊快照
        // 不會蓋掉已經落地的新快照。
        if (TriadDeferredSideEffects.TryDeferFileWrite(() => WriteCacheFile(contentId, json, sequence)))
        {
            return;
        }

        WriteCacheFile(contentId, json, sequence);
    }

    private static void WriteCacheFile(ulong contentId, string json, long sequence)
    {
        lock (IoLock)
        {
            if (sequence <= lastWrittenSequence)
            {
                // 已經有更新的快照落地了，這一份是舊的，寫下去等於回退。
                return;
            }

            try
            {
                var path = GetCachePath(contentId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                lastWrittenSequence = sequence;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[Saucy] Failed to save optimized deck cache.");
            }
        }
    }

    private static void ResetActive()
    {
        lock (FileLock)
        {
            loadedForCharacter = false;
            activeFile = null;
            activeContentId = 0;
            MarkCharacterViewsDirtyLocked();
        }
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
        file.HomeWorldRowId = Svc.Objects.LocalPlayer?.HomeWorld.RowId ?? 0;
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
            var worldName = Svc.Objects.LocalPlayer?.HomeWorld.ValueNullable?.Name.ToString();
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

        var world = Svc.Data.GetExcelSheet<World>()?.GetRowOrDefault(homeWorldRowId);
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
