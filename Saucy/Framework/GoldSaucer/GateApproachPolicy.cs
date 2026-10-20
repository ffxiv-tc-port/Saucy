using System;
using System.Numerics;

namespace Saucy.Framework.GoldSaucer;

/// <summary>
/// GATE 報名 NPC 自動靠近的每幀決策，一個 NPC 一份。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// 呼叫端負責：判斷這個 GATE 現在能不能跑、量距離與各種狀態，再依回傳值送路徑／停自己的路徑／互動。
/// 自己從不把角色帶離近距範圍，所以「上一次判斷在近距、這一次在外面」一律當成使用者接手。
/// </summary>
internal sealed class GateApproachPolicy
{
    public const float CloseRange = 3f;

    /// <summary>讓位中離目的地比這段讓位裡最近時再遠這麼多（米，水平）就當成使用者在走開。</summary>
    public const float WalkAwayMargin = 2f;

    public enum Decision : byte
    {
        /// <summary>不在觸發範圍：停掉自己的路徑；放棄與讓位狀態已清掉。</summary>
        OutOfRange,

        /// <summary>被擋（原因見 <see cref="Block"/>）：停掉自己的路徑、不互動。</summary>
        Blocked,

        /// <summary>已在近距：停掉自己的路徑，可以互動。</summary>
        Close,

        /// <summary>這一幀不要送路徑。</summary>
        Hold,

        /// <summary>可以送路徑。</summary>
        Proceed,

        /// <summary>這一趟放棄：清掉擁有權，不停使用者自己的移動。</summary>
        GaveUp
    }

    public enum BlockReason : byte
    {
        None,
        CoordinatorRun,
        Occupied,
        NearCoordinator
    }

    public enum Note : byte
    {
        None,

        /// <summary>剛被擋住（換了原因也算）；已放棄、或在近距的對話（報名本身）不回報。</summary>
        Blocked,
        YieldStarted,
        Resumed,
        GaveUpTimeout,
        WalkingAway
    }

    private readonly ManualSteerWatch watch = new();
    private bool gaveUp;
    private bool wasClose;
    private float yieldMinDistance = float.NaN;
    private long yieldStartedAt;

    public BlockReason Block { get; private set; }

    /// <summary>這段讓位裡離目的地最近的距離；NaN＝沒在讓位。</summary>
    public float YieldMinDistance => yieldMinDistance;

    /// <summary>最近一次結束的讓位持續多久（毫秒）。</summary>
    public long LastYieldMs { get; private set; }

    public bool HasGivenUp => gaveUp;

    public void Forget()
    {
        watch.Reset();
        ClearTrip();
    }

    private void ClearTrip()
    {
        gaveUp = false;
        wasClose = false;
        yieldMinDistance = float.NaN;
        Block = BlockReason.None;
    }

    /// <param name="distance">到目的地的水平距離。</param>
    /// <param name="ownsPath">這一趟是自己發起、還沒結束的（只影響「在解說員旁」：已經在路上就不中途停下）。</param>
    /// <param name="coordinatorRun">活動解說員排程正在管移動。</param>
    /// <param name="occupied">在對話、事件、過場或換區中。</param>
    /// <param name="nearCoordinator">站在已記錄的活動解說員旁。</param>
    public Decision Decide(long now, Vector3 player, float distance, bool inRange, bool ownsPath, bool navigating, bool manualKey,
                           bool coordinatorRun, bool occupied, bool nearCoordinator, out Note note)
    {
        note = Note.None;
        if (!inRange)
        {
            // 範圍外也記位置：自己走進範圍的那一幀就知道在動，不會先送一次路徑。
            ClearTrip();
            watch.Track(player, navigating, now);
            return Decision.OutOfRange;
        }

        var close = distance <= CloseRange;
        var block = coordinatorRun ? BlockReason.CoordinatorRun
            : occupied ? BlockReason.Occupied
            : nearCoordinator && !ownsPath ? BlockReason.NearCoordinator
            : BlockReason.None;
        if (block != BlockReason.None)
        {
            watch.Track(player, navigating, now);
            yieldMinDistance = float.NaN;
            if (block != Block && !gaveUp && !(block == BlockReason.Occupied && close))
            {
                note = Note.Blocked;
            }

            Block = block;
            return Decision.Blocked;
        }

        Block = BlockReason.None;
        var leftClose = wasClose && !close;
        wasClose = close;
        if (close)
        {
            watch.Track(player, navigating, now);
            yieldMinDistance = float.NaN;
            return Decision.Close;
        }

        if (gaveUp)
        {
            watch.Track(player, navigating, now);
            return Decision.Hold;
        }

        var step = watch.Observe(player, navigating, manualKey || leftClose, busy: false, now);
        if (step == ManualSteerWatch.Step.Started)
        {
            yieldMinDistance = distance;
            yieldStartedAt = now;
            note = Note.YieldStarted;
            return Decision.Hold;
        }

        if (!float.IsNaN(yieldMinDistance))
        {
            yieldMinDistance = MathF.Min(yieldMinDistance, distance);
            if (distance - yieldMinDistance > WalkAwayMargin)
            {
                gaveUp = true;
                LastYieldMs = now - yieldStartedAt;
                watch.Track(player, navigating, now);
                note = Note.WalkingAway;
                return Decision.GaveUp;
            }

            if (!watch.Yielding)
            {
                LastYieldMs = now - yieldStartedAt;
                yieldMinDistance = float.NaN;
            }
        }

        switch (step)
        {
            case ManualSteerWatch.Step.Resume:
                note = Note.Resumed;
                return Decision.Proceed;
            case ManualSteerWatch.Step.GaveUp:
                gaveUp = true;
                note = Note.GaveUpTimeout;
                return Decision.GaveUp;
            case ManualSteerWatch.Step.Settle:
                return Decision.Hold;
            default:
                return watch.Yielding ? Decision.Hold : Decision.Proceed;
        }
    }
}
