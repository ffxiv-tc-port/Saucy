namespace Saucy.Framework;

/// <summary>Saucy 要自己用 vnavmesh 開走前，要不要先請 Lifestream 整條停下。</summary>
/// <remarks>本檔契約：純邏輯、不碰 Dalamud；只在 framework 執行緒用。
/// Lifestream 的跟隨路徑一看到 vnavmesh 在動就整條自行中止並在聊天印紅字，所以由要開走的一方先請它停；
/// 不改成等它跑完：它的流程可能比 Saucy 的時段還長，而且那段時間 Saucy 的閘門就被別人的旗標卡住。
/// Saucy 自己送的那一趟不在這裡停（由發起的流程收）。<see cref="RetryMs"/> 內最多送一次；送了對方沒停，呼叫端也照舊開走。</remarks>
internal sealed class LifestreamForeignAbort
{
    public const long RetryMs = 2_000;

    private bool sentBefore;
    private long lastSentAtMs;

    /// <summary><paramref name="busy"/>＝Lifestream 現在忙；<paramref name="ownTripActive"/>＝那一趟是 Saucy 自己送的。回 true＝現在送中止（並記下時間）。</summary>
    public bool ShouldAbort(bool busy, bool ownTripActive, long nowMs)
    {
        if (!busy || ownTripActive)
        {
            return false;
        }

        if (sentBefore && nowMs - lastSentAtMs < RetryMs)
        {
            return false;
        }

        sentBefore = true;
        lastSentAtMs = nowMs;
        return true;
    }
}
