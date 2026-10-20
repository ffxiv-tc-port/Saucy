using System;
using System.Numerics;

namespace Saucy.Framework.GoldSaucer;

/// <summary>
/// 導航走路途中「使用者自己接手」的判斷：按了移動鍵、vnavmesh 停著角色卻還在動（滑鼠按住前進、vnavmesh 讓位給輸入），
/// 或正在詠唱／跟 NPC 對話（再送路徑會打斷他）。接手就讓位、不重送；角色靜止且沒在忙滿 <see cref="ResumeQuietMs"/> 才回報一次「接續」；
/// 一次讓位滿 <see cref="MaxYieldMs"/> 還沒接續就回報「放棄」。
/// 純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。速度＝水平位移除以約 <see cref="WindowMs"/> 的時間窗，歷史不夠長是 -1（不知道）。
/// </summary>
internal sealed class ManualSteerWatch
{
    public enum Step
    {
        None,
        Started,
        Resume,
        GaveUp,

        /// <summary>導航剛停下，時間窗裡還是導航的速度：這一幀先不要送路徑，也還不算讓位。</summary>
        Settle
    }

    public const long SampleIntervalMs = 100;

    public const long WindowMs = 300;

    /// <summary>算「在動」的水平速度（米／秒）：走路模式約 2.4、跑步約 6 都要算；門檻與 BOCCHI 趕路讓位相同。</summary>
    public const float MoveSpeedMin = 1.5f;

    /// <summary>算「靜止」的水平速度上限（米／秒）。</summary>
    public const float StillSpeedMax = 1f;

    /// <summary>超過這個速度當成傳送或瞬移，不是移動：清掉歷史。</summary>
    public const float TeleportSpeed = 50f;

    /// <summary>停手這麼久才接續：方向鍵點放、滑鼠放開重按之間的空檔都比這短，使用者還在操作時不會被搶回方向。</summary>
    public const long ResumeQuietMs = 2000;

    /// <summary>一次讓位最長這麼久：使用者自己走了半分鐘還沒停下來，就當成他改主意了，不再接續。</summary>
    public const long MaxYieldMs = 30_000;

    /// <summary>導航停下後這麼久內，速度還混著導航自己的移動，不拿來判斷是不是使用者在動。</summary>
    public const long SettleMs = WindowMs + SampleIntervalMs;

    private const int Capacity = 8;

    private readonly long[] times = new long[Capacity];
    private readonly float[] xs = new float[Capacity];
    private readonly float[] zs = new float[Capacity];
    private int count;
    private int head;
    private long nextSampleAt;
    private float speed = -1f;
    private long yieldStartedAt;
    private long stillSince;
    private long lastNavigatingAt;

    public bool Yielding => yieldStartedAt != 0;

    public void Reset()
    {
        count = 0;
        head = 0;
        nextSampleAt = 0;
        speed = -1f;
        yieldStartedAt = 0;
        stillSince = 0;
        lastNavigatingAt = 0;
    }

    /// <param name="navigating">vnavmesh 在走或還在算路：那時角色在動是導航造成的，不算手動。</param>
    /// <param name="manualKey">這一幀按著移動鍵。</param>
    /// <param name="busy">詠唱中或跟 NPC 對話中。</param>
    /// <param name="now">單調遞增的毫秒時間。</param>
    public Step Observe(Vector3 player, bool navigating, bool manualKey, bool busy, long now)
    {
        Sample(player, navigating, now);
        if (!Yielding)
        {
            if (!manualKey && !busy)
            {
                if (navigating)
                {
                    return Step.None;
                }

                if (lastNavigatingAt != 0 && now - lastNavigatingAt < SettleMs)
                {
                    return Step.Settle;
                }

                if (speed < MoveSpeedMin)
                {
                    return Step.None;
                }
            }

            yieldStartedAt = now;
            stillSince = 0;
            return Step.Started;
        }

        // 讓位中別的東西又在送路徑，也不算停手。
        var quiet = !manualKey && !busy && !navigating && speed >= 0 && speed < StillSpeedMax;
        stillSince = !quiet ? 0 : stillSince == 0 ? now : stillSince;
        if (stillSince != 0 && now - stillSince >= ResumeQuietMs)
        {
            yieldStartedAt = 0;
            stillSince = 0;
            return Step.Resume;
        }

        if (now - yieldStartedAt >= MaxYieldMs)
        {
            yieldStartedAt = 0;
            stillSince = 0;
            return Step.GaveUp;
        }

        return Step.None;
    }

    /// <summary>只記位置、不判斷，並結束進行中的讓位：呼叫端這一幀不需要讓位判斷（已經到了、被別的狀態擋住），
    /// 但下一次 <see cref="Observe"/> 要有速度可用。</summary>
    public void Track(Vector3 player, bool navigating, long now)
    {
        Sample(player, navigating, now);
        yieldStartedAt = 0;
        stillSince = 0;
    }

    private void Sample(Vector3 player, bool navigating, long now)
    {
        if (now >= nextSampleAt)
        {
            nextSampleAt = now + SampleIntervalMs;
            speed = Push(player, now);
        }

        if (navigating)
        {
            lastNavigatingAt = now;
        }
    }

    // 記下這一筆，回傳到時間窗以前最近那一筆的水平速度；歷史不夠長回 -1。瞬移時清掉歷史、回 -1。
    private float Push(Vector3 player, long now)
    {
        var result = -1f;
        for (var i = 0; i < count; i++)
        {
            var index = (head - 1 - i + Capacity) % Capacity;
            if (now - times[index] < WindowMs)
            {
                continue;
            }

            var seconds = (now - times[index]) / 1000f;
            var dx = player.X - xs[index];
            var dz = player.Z - zs[index];
            result = MathF.Sqrt((dx * dx) + (dz * dz)) / seconds;
            break;
        }

        if (result > TeleportSpeed)
        {
            count = 0;
            result = -1f;
        }

        times[head] = now;
        xs[head] = player.X;
        zs[head] = player.Z;
        head = (head + 1) % Capacity;
        count = Math.Min(count + 1, Capacity);
        return result;
    }
}
