using ECommons.GameHelpers;
using Saucy.Framework.GoldSaucer;
using Saucy.IPC;
using System;

namespace Saucy.Framework;

/// <summary>
/// 自動移動途中讓位給使用者的共用接線（判斷本身在 <see cref="ManualSteerWatch"/>）：使用者接手就停掉自己送的路徑、之後不再送；
/// 停手滿 <see cref="ManualSteerWatch.ResumeQuietMs"/> 回報「接續」一次；一次讓位滿 <see cref="ManualSteerWatch.MaxYieldMs"/> 回報「放棄」。
/// 只在 framework 執行緒用；不持有任何原生指標，每次呼叫當場讀玩家位置。
/// </summary>
internal sealed class ManualSteerYield(string label)
{
    public enum Result
    {
        /// <summary>照常：可以送路徑。</summary>
        Proceed,

        /// <summary>讓位中：這一幀不要送路徑，也不要算逾時。</summary>
        Hold,

        /// <summary>使用者停手了：從目前位置接續一次（呼叫端照常送路徑，逾時時鐘往後推 <c>pausedFor</c>）。</summary>
        Resumed,

        /// <summary>讓位太久：呼叫端結束這一趟，不要停使用者自己的移動。</summary>
        GaveUp
    }

    private readonly ManualSteerWatch watch = new();
    private DateTime yieldStartedUtc;

    public bool Yielding => watch.Yielding;

    public void Reset() => watch.Reset();

    /// <param name="ownsPath">vnavmesh 現在走的路徑是不是自己送的（送路徑成功才算、vnavmesh 停了就不算）；是才在使用者接手時停掉它。</param>
    /// <param name="busy">呼叫端認定「使用者在做別的事、送路徑會打斷他」的狀態（例如詠唱）；自己流程造成的狀態不要算進來。</param>
    public Result Tick(bool ownsPath, bool busy, out TimeSpan pausedFor)
    {
        pausedFor = TimeSpan.Zero;
        var navigating = Vnavmesh.IsMoving();
        var step = watch.Observe(
            Player.Position, navigating, GoldSaucerNavigator.IsManualMovementRequested(), busy, Environment.TickCount64);
        switch (step)
        {
            case ManualSteerWatch.Step.Started:
                if (navigating && ownsPath)
                {
                    Vnavmesh.StopOwnPath();
                }

                yieldStartedUtc = DateTime.UtcNow;
                Svc.Log.Information($"[Saucy] {label}：你自己接手移動，讓位、不重送路徑");
                return Result.Hold;

            case ManualSteerWatch.Step.Resume:
                pausedFor = DateTime.UtcNow - yieldStartedUtc;
                Svc.Log.Information($"[Saucy] {label}：停手滿 {ManualSteerWatch.ResumeQuietMs / 1000} 秒，從目前位置接續（讓位 {pausedFor.TotalSeconds:0.0} 秒）");
                return Result.Resumed;

            case ManualSteerWatch.Step.GaveUp:
                Svc.Log.Information($"[Saucy] {label}：你自己移動超過 {ManualSteerWatch.MaxYieldMs / 1000} 秒，不再自動接續");
                return Result.GaveUp;

            case ManualSteerWatch.Step.Settle:
                return Result.Hold;

            default:
                return watch.Yielding ? Result.Hold : Result.Proceed;
        }
    }
}
