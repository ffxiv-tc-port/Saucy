using ECommons.EzIpcManager;
using System;
namespace Saucy.IPC;

// BMR 的 InternalName 是 BossModReborn，IPC 端點仍沿用舊前綴 "BossMod."。
[IPC(IPCNames.BossMod, IpcPrefix = "BossMod")]
internal static class BossMod
{
    public const string GateAiPresetName = "VBM AI";

    [EzIPC("Presets.GetActive")]
    private static Func<string?> PresetsGetActiveRpc = null!;

    [EzIPC("Presets.SetActive")]
    private static Func<string, bool> PresetsSetActiveRpc = null!;

    [EzIPC("Presets.ClearActive")]
    private static Func<bool> PresetsClearActiveRpc = null!;

    private static string? _savedActivePreset;
    private static bool _gateAiEngaged;

    public static bool IsInstalled => SubscriptionManager.IsInitialized(IPCNames.BossMod);

    public static bool TryEnableGateAi()
    {
        if (!IsInstalled)
        {
            return false;
        }

        if (_gateAiEngaged)
        {
            return true;
        }

        _savedActivePreset = PresetsGetActiveRpc.TryInvoke(out var active) ? active : null;
        if (!PresetsSetActiveRpc.TryInvoke(GateAiPresetName, out var started) || !started)
        {
            return false;
        }

        _gateAiEngaged = true;
        return true;
    }

    public static void TryDisableGateAi()
    {
        if (!_gateAiEngaged || !IsInstalled)
        {
            _gateAiEngaged = false;
            return;
        }

        _gateAiEngaged = false;

        if (!string.IsNullOrEmpty(_savedActivePreset))
        {
            PresetsSetActiveRpc.TryInvoke(_savedActivePreset, out var _);
        }
        else
        {
            PresetsClearActiveRpc.TryInvoke(out var _);
        }

        _savedActivePreset = null;
    }
}
