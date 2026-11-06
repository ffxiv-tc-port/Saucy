using System.Numerics;
namespace Saucy.AirForce;

/// <summary>同一個畫面點（半徑內）兩次開槍的最短間隔。純邏輯、只在框架執行緒用；時間由呼叫端給（毫秒）。</summary>
internal sealed class SamePointShotLimiter
{
    // 全域每 60 ms 最多一發，64 格涵蓋約 3.8 秒，大於設定上限 2 秒，擋得住的那幾發不會先被覆蓋。
    private const int Capacity = 64;

    private readonly Vector2[] points = new Vector2[Capacity];

    private readonly long[] times = new long[Capacity];

    private int next;

    public bool IsTooSoon(Vector2 point, long now, long minIntervalMs, float radius)
    {
        var radiusSquared = radius * radius;
        for (var i = 0; i < Capacity; i++)
        {
            if (times[i] != 0 && now - times[i] < minIntervalMs && Vector2.DistanceSquared(points[i], point) < radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    public void Record(Vector2 point, long now)
    {
        points[next] = point;
        times[next] = now;
        next = (next + 1) % Capacity;
    }

    public void Reset()
    {
        System.Array.Clear(times);
        next = 0;
    }
}
