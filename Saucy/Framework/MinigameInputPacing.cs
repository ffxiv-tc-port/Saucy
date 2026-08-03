using System;
namespace Saucy.Framework;

internal static class MinigameInputPacing
{
    public const int ClickIntervalMs = 1000;

    /// <summary>面板第一次開啟時的暖機：版面還在初始化，太早送輸入會被吃掉。</summary>
    public const int BoardWarmupMs = 1400;

    /// <summary>同一次進場、面板重開時的暖機。版面已經建過一次了，只需要留一小段緩衝——
    /// 對「連續玩好幾張彩券」這種流程，完整暖機是純粹的等待浪費。</summary>
    public const int RepeatBoardWarmupMs = 400;

    public static bool TryMarkWarmup(ref DateTime? readyUtc) =>
        TryMarkWarmup(ref readyUtc, BoardWarmupMs);

    public static bool TryMarkWarmup(ref DateTime? readyUtc, int warmupMs)
    {
        readyUtc ??= DateTime.UtcNow;
        return (DateTime.UtcNow - readyUtc.Value).TotalMilliseconds >= warmupMs;
    }

    public static void Reset(ref DateTime? readyUtc) => readyUtc = null;
}
