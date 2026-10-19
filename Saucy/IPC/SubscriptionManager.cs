using Dalamud.Plugin;
using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
namespace Saucy.IPC;

internal static class SubscriptionManager
{
    private static IpcEntry[]? _ipcEntries;
    private static readonly Dictionary<string, EzIPCDisposalToken[]> InitializedIpcs = [];
    private static int _subscribeTick;

    // 已安裝清單只在 Dalamud 通知插件載入／卸載／更新後重抓（每次抓都要鎖＋複製＋逐一包裝）；
    // IsLoaded 仍每次即時讀，所以「卸載中」照舊立刻算沒載入。通知可能來自 Dalamud 的載入執行緒，那裡只設旗標。
    private static IExposedPlugin[] _installedSnapshot = [];
    private static int _installedStale = 1;
    private static bool _watchingPluginList;

    internal static void Prepare()
    {
        if (_ipcEntries != null)
        {
            return;
        }

        Svc.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        _watchingPluginList = true;
        Interlocked.Exchange(ref _installedStale, 1);

        _ipcEntries =
        [
            .. Assembly.GetExecutingAssembly()
                .GetTypes()
                .Select(type => (Type: type, Attr: type.GetCustomAttribute<IPCAttribute>()))
                .Where(entry => entry.Attr != null)
                .Select(entry => new IpcEntry(entry.Type, entry.Attr!.Name, entry.Attr.IpcPrefix ?? entry.Attr.Name))
        ];
    }

    internal static bool IsInitialized(string plugin) =>
        InitializedIpcs.ContainsKey(plugin) && IsLoaded(plugin);

    internal static bool IsLoaded(string pluginName)
    {
        if (!_watchingPluginList)
        {
            return IsLoadedUncached(pluginName);
        }

        if (Interlocked.Exchange(ref _installedStale, 0) != 0)
        {
            try
            {
                Volatile.Write(ref _installedSnapshot, [.. Svc.PluginInterface.InstalledPlugins]);
            }
            catch
            {
                Interlocked.Exchange(ref _installedStale, 1);
                throw;
            }
        }

        foreach (var plugin in Volatile.Read(ref _installedSnapshot))
        {
            if (plugin.InternalName == pluginName && plugin.IsLoaded)
            {
                return true;
            }
        }

        return false;
    }

    // 另開一支：lambda 捕獲參數時閉包在方法入口就配置，放在 IsLoaded 裡會讓快取路徑也付這筆。
    private static bool IsLoadedUncached(string pluginName) =>
        Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == pluginName && x.IsLoaded);

    private static void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) =>
        Interlocked.Exchange(ref _installedStale, 1);

    internal static void Subscribe()
    {
        try
        {
            Prepare();
            var entries = _ipcEntries!;
            var allInitialized = InitializedIpcs.Count == entries.Length;
            _subscribeTick++;

            if (allInitialized)
            {
                if (_subscribeTick % 120 != 0)
                {
                    return;
                }
            }
            else if (_subscribeTick % 10 != 0)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (!IsInitialized(entry.PluginName))
                {
                    if (!IsLoaded(entry.PluginName))
                    {
                        continue;
                    }

                    InitializedIpcs[entry.PluginName] = EzIPC.Init(entry.Type, entry.IpcPrefix);
                }
                else if (!IsLoaded(entry.PluginName))
                {
                    foreach (var token in InitializedIpcs[entry.PluginName])
                    {
                        token.Dispose();
                    }

                    InitializedIpcs.Remove(entry.PluginName);
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Could not subscribe to IPCs");
        }
    }

    internal static void DisposeAll()
    {
        if (_watchingPluginList)
        {
            Svc.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
            _watchingPluginList = false;
            Volatile.Write(ref _installedSnapshot, []);
        }

        foreach (var tokens in InitializedIpcs.Values)
        {
            foreach (var token in tokens)
            {
                token.Dispose();
            }
        }

        InitializedIpcs.Clear();
        QuestionableTriad.ClearUnsupportedCache();
    }
    private sealed record IpcEntry(Type Type, string PluginName, string IpcPrefix);
}
