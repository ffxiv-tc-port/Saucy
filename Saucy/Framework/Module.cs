using ECommons.Automation.NeoTaskManager;
using System;
namespace Saucy.Framework;

public abstract partial class Module : IModule
{
    public enum GatePositionType : byte
    {
        WonderSquareEast = 1,
        EventSquare = 2,
        RoundSquare = 3,
        TheCactpotBoard = 4
    }

    public enum GateType : byte
    {
        None = 0,
        Cliffhanger = 1,
        VaseOff = 2,
        SkinchangeWeCanBelieveIn = 3,
        TheTimeOfMyLife = 4,
        AnyWayTheWindBlows = 5,
        LeapOfFaith = 6,
        AirForceOne = 7,
        SliceIsRight = 8
    }

    protected TaskManager TaskManager;
    protected TaskManagerConfiguration TaskManagerConfiguration;

    public Module()
    {
        InternalName = GetType().Name;
        TaskManagerConfiguration = CreateTaskManagerConfiguration();
        TaskManager = new(TaskManagerConfiguration);
    }
    public bool InSaucer => GateDirector.InSaucer;

    public bool PlayerOnStage => GateDirector.IsPlayerOnStage();

    public GateType CurrentGate => GateDirector.GetCurrentGate();

    public string InternalName { get; init; }
    public abstract string Name { get; }
    public virtual bool IsEnabled { get; protected set; }
    public virtual void Enable() { }
    public virtual void Disable() { }

    protected bool IsInGate(GateType gate) => GateDirector.IsInGate(gate);

    protected virtual TaskManagerConfiguration CreateTaskManagerConfiguration() => new()
    {
        ShowDebug = false, TimeLimitMS = 5000, AbortOnTimeout = true,
        // 見 OnTaskTimedOut:ECommons 擲的 TaskTimeoutException **不帶任務名**,
        // 所以改由我方在 OnTaskTimeout 事件裡印出帶名字的那一行,
        // 並把 ECommons 自己那行沒有名字的 Warning 關掉,避免同一件事印兩行。
        TimeoutSilently = true, OnTaskTimeout = OnTaskTimedOut
    };

    /// <summary>
    /// 任務逾時時先把「是哪一步」寫進 log,再讓 ECommons 照原本的流程中止佇列。
    ///
    /// <para>
    /// 由來:實機 2026-09-01~09-03 共 4 次 <c>TaskTimeoutException</c> 的 Warning,
    /// 內容只有例外類別名 —— ECommons 是 <c>throw new TaskTimeoutException()</c> 之後
    /// 直接 <c>e.LogWarning()</c>,任務名只在 <c>ShowDebug</c> 那條分支裡出現。
    /// 於是「小仙人微彩跑完之後 7 秒有一行 WRN」完全查不出是哪一個任務。
    /// </para>
    ///
    /// <para>
    /// 🔴 等級刻意維持 <c>Warning</c>:原本就是 Warning,降級只會弱化訊號。
    /// 🔴 也刻意<b>不</b>改 ECommons —— 全艦隊二十幾個消費端共用那份。
    /// ⚠️ <c>remainingTimeMS</c> 是 <c>ref</c>:改它等於偷偷延長逾時,這裡只讀不寫。
    /// </para>
    /// </summary>
    private void OnTaskTimedOut(TaskManagerTask task, ref long remainingTimeMS)
    {
        var limit = task.Configuration?.TimeLimitMS ?? TaskManagerConfiguration?.TimeLimitMS;
        LogWarning($"任務逾時:[{task.Name}@{task.Location}] 上限 {(limit?.ToString() ?? "?")} ms,佇列中止");
    }
}

public abstract partial class Module
{
    internal virtual void EnableInternal()
    {
        try
        {
            Log($"Enabling module {InternalName}");
            IsEnabled = true;
            Enable();
        }
        catch (Exception ex)
        {
            LogError($"Failed to enable module: {ex}");
            IsEnabled = false;
        }
    }

    internal virtual void DisableInternal()
    {
        try
        {
            Log($"Disabling module {InternalName}");
            Disable();
        }
        catch (Exception ex)
        {
            LogError($"Failed to disable module: {ex}");
            return;
        }

        IsEnabled = false;
    }
}

public abstract partial class Module
{
    public void Log(string message) => PluginLog.Information($"[{InternalName}] {message}");
    public void LogDebug(string message) => PluginLog.Debug($"[{InternalName}] {message}");
    public void LogVerbose(string message) => PluginLog.Verbose($"[{InternalName}] {message}");
    public void LogWarning(string message) => PluginLog.Warning($"[{InternalName}] {message}");
    public void LogError(string message) => PluginLog.Error($"[{InternalName}] {message}");
}
