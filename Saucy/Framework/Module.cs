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
    /// 🔴 等級刻意維持 <c>Warning</c>:原本就是 Warning,降級只會弱化訊號。
    /// 🔴 也刻意<b>不</b>改 ECommons —— 全艦隊二十幾個消費端共用那份。
    /// ⚠️ <c>remainingTimeMS</c> 是 <c>ref</c>:改它等於偷偷延長逾時,這裡只讀不寫。
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
    // 同一插件的 Framework.Update 處理器共用 Dalamud 的一個 try/catch：任一模組擲例外，
    // 之後訂閱的處理器（含 RunBot 與急停的 ProcessPending）那一幀全被跳過。所以每個模組各自包一層。
    private IFramework.OnUpdateDelegate? updateBody;
    private IFramework.OnUpdateDelegate? guardedUpdate;
    private readonly UpdateFailureDedup updateFailures = new();

    /// <summary>訂閱每幀更新（包一層 try）。重複呼叫只會留一份訂閱。只在 framework 執行緒呼叫。</summary>
    protected void SubscribeUpdate(IFramework.OnUpdateDelegate body)
    {
        guardedUpdate ??= GuardedUpdate;
        updateBody = body;
        Svc.Framework.Update -= guardedUpdate;
        Svc.Framework.Update += guardedUpdate;
    }

    protected void UnsubscribeUpdate()
    {
        if (guardedUpdate != null)
        {
            Svc.Framework.Update -= guardedUpdate;
        }

        updateBody = null;
    }

    private void GuardedUpdate(IFramework framework)
    {
        var body = updateBody;
        if (body == null)
        {
            return;
        }

        try
        {
            body(framework);
        }
        catch (Exception ex)
        {
            if (updateFailures.ShouldReport(ex))
            {
                Svc.Log.Error(ex, $"[{InternalName}] 每幀更新擲例外（同一種錯誤只記這一次；本模組下一幀照常重試，其他模組與急停不受影響）");
            }
        }
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
