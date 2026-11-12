namespace Saucy.Framework;

/// <summary>Lifestream 上正在跑的那一趟是不是 Saucy 自己送的、是哪一條流程送的。</summary>
/// <remarks>本檔契約：純邏輯、不碰 Dalamud；只在 framework 執行緒用。
/// Lifestream 沒有「這一趟是誰的」可問，所以用發起旗標：送出前它閒著、送出成功才記；
/// 看到它忙過又閒下來＝這一趟結束；送出後 <see cref="StartGraceMs"/> 內都沒忙過（例如直接傳送不經它的任務鏈）＝沒有東西可收，也放掉。
/// 放掉之後 Lifestream 上的任何流程都當成別人的，不中止。</remarks>
internal sealed class LifestreamOwnership
{
    public const long StartGraceMs = 5_000;

    private string? owner;
    private long requestedAtMs;
    private bool seenBusy;

    public string? Owner => owner;

    /// <summary>剛送出請求、而且送出成功時呼叫。<paramref name="busyBefore"/>＝送出前 Lifestream 已經在忙（別人的），這時不記。</summary>
    public void NoteRequested(string requester, bool busyBefore, long nowMs)
    {
        if (busyBefore)
        {
            return;
        }

        owner = requester;
        requestedAtMs = nowMs;
        seenBusy = false;
    }

    /// <summary>每幀呼叫；沒有記錄時什麼都不做。</summary>
    public void Observe(bool busy, long nowMs)
    {
        if (owner == null)
        {
            return;
        }

        if (busy)
        {
            seenBusy = true;
            return;
        }

        if (seenBusy || nowMs - requestedAtMs > StartGraceMs)
        {
            owner = null;
        }
    }

    /// <summary>要中止時呼叫：這一趟是 <paramref name="requester"/> 送的才回 true（並結案）；不是就什麼都不改、回 false。</summary>
    public bool TryTakeForAbort(string requester)
    {
        if (owner != requester)
        {
            return false;
        }

        owner = null;
        return true;
    }
}
