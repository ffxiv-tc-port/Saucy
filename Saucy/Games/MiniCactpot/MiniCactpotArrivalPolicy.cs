using System;
using System.Numerics;

namespace Saucy.MiniCactpot;

/// <summary>
/// 「不論怎麼走到仙人微彩發放員旁，站定就自動對話」的決策。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// 一次靠近最多觸發一次：要先看到「離開過」（在 <see cref="ArmDistance"/> 外、或瞬移／城內傳送）才上膛，觸發、
/// 已知今天打完、或你自己先開始對話都會退膛。所以登入、重載插件、打開設定時人已經站在發放員旁都不會觸發。
/// </summary>
internal sealed class MiniCactpotArrivalPolicy
{
    /// <summary>離發放員（水平）比這遠才算離開過，下一次靠近才會再觸發。</summary>
    public const float ArmDistance = 10f;

    /// <summary>比這近才看狀態與互動距離。</summary>
    public const float NearDistance = 8f;

    /// <summary>兩次取樣之間水平位移超過這個值＝瞬移或城內傳送，也算離開過。</summary>
    public const float JumpDistance = 20f;

    /// <summary>站定多久才觸發。</summary>
    public const long StillMs = 1000;

    /// <summary>離站定起點的水平位移小於這個值算沒動。</summary>
    public const float StillTolerance = 0.3f;

    public enum Decision : byte
    {
        Idle,
        Wait,
        Fire
    }

    public enum Note : byte
    {
        None,

        /// <summary>站定了但不在互動距離內（一次靠近只回報一次）。</summary>
        OutOfInteractRange,

        /// <summary>已知今天打完，退膛。</summary>
        KnownZero,

        /// <summary>在發放員旁時在對話、事件或選單中（多半是你自己先開始了），退膛。</summary>
        Busy
    }

    private bool armed;
    private bool hasLast;
    private Vector2 last;
    private Vector2 stillAnchor;
    private long stillSince;
    private bool outOfRangeNoted;

    public bool Armed => armed;

    public void Reset()
    {
        armed = false;
        hasLast = false;
        stillSince = 0;
        outOfRangeNoted = false;
    }

    /// <summary>別的流程（導航抵達、已在跑的接手）已經處理這次靠近。</summary>
    public void Disarm() => armed = false;

    /// <param name="brokerDistance">到發放員的水平距離；物件表裡找不到給 NaN（剛登入、物件還沒載入時也是找不到，不算離開過）。</param>
    /// <param name="inInteractRange">遊戲判定的互動距離；只在 <see cref="NearDistance"/> 內有意義。</param>
    /// <param name="busy">對話、事件、選單、彩券面板或戰鬥中。</param>
    /// <param name="moving">vnavmesh／Lifestream／Saucy 自己的導航在動、動作鎖定中，或按著移動鍵。</param>
    /// <param name="knownZero">今天的仙人微彩確定剩 0 張。</param>
    public Decision Decide(long now, Vector3 player, float brokerDistance, bool inInteractRange, bool busy, bool moving,
                           bool knownZero, out Note note)
    {
        note = Note.None;
        var here = new Vector2(player.X, player.Z);
        var jumped = hasLast && Vector2.Distance(here, last) > JumpDistance;
        last = here;
        hasLast = true;

        if (moving || stillSince == 0 || jumped || Vector2.Distance(here, stillAnchor) > StillTolerance)
        {
            stillAnchor = here;
            stillSince = now;
        }

        if (jumped || brokerDistance > ArmDistance)
        {
            armed = true;
            outOfRangeNoted = false;
        }

        if (!armed || float.IsNaN(brokerDistance) || brokerDistance > NearDistance)
        {
            return Decision.Idle;
        }

        if (busy)
        {
            armed = false;
            note = Note.Busy;
            return Decision.Idle;
        }

        if (moving || now - stillSince < StillMs)
        {
            return Decision.Wait;
        }

        if (!inInteractRange)
        {
            if (!outOfRangeNoted)
            {
                outOfRangeNoted = true;
                note = Note.OutOfInteractRange;
            }

            return Decision.Wait;
        }

        armed = false;
        if (knownZero)
        {
            note = Note.KnownZero;
            return Decision.Idle;
        }

        return Decision.Fire;
    }
}
