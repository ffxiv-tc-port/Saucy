using Saucy.IPC;
using System;
namespace Saucy.Framework;

/// <summary>替你翻 NPC 對話（Talk）前先請 YesAlready 暫停它的 Talk 功能。</summary>
/// <remarks>本檔契約：只在 framework 執行緒呼叫。
/// YesAlready 的 Talk 在 PostUpdate 每幀判斷，兩邊同幀按同一扇＝攔不到的存取違規。
/// 暫停要先連續生效 <see cref="SettleMs"/> 才按：暫停之前它按下的那一頁有時間關完。問不到（IPC 失敗、版本不明）就不按。
/// 暫停很短且只在要翻的期間續期，停手後它自己到期，不需要另外恢復。</remarks>
internal sealed class YesAlreadyTalkClaim(string cannotClaimLog)
{
    public const int ClaimMs = 1_500;

    public const long RenewMs = 500;

    public const long SettleMs = 500;

    /// <summary>這一段連續暫停的起點與到期（<see cref="Environment.TickCount64"/>）；到期或問不到之後再請到就是新的一段。</summary>
    private long claimFrom;

    private long claimUntil;

    private long lastAttempt;

    private bool failLogged;

    /// <summary>每幀可呼叫：YesAlready 沒載入就照舊翻；載入了就先確認它的 Talk 已讓開夠久才翻，問不到就不翻。按前由 <see cref="TalkHelper"/> 照名字重找視窗。</summary>
    /// <returns>這一次真的翻了一頁。</returns>
    public bool TryAdvance(string throttleKey)
    {
        if (!YesAlready.IsLoaded)
        {
            claimUntil = 0;
            return TalkHelper.TryAdvance(throttleKey);
        }

        var now = Environment.TickCount64;
        if (!Tick(now, force: false) || now - claimFrom < SettleMs)
        {
            return false;
        }

        return TalkHelper.TryAdvance(throttleKey);
    }

    /// <summary>Talk 的 PreSetup 呼叫：視窗還沒開始被 YesAlready 判斷前就先請它暫停（不受續期間隔限制）。</summary>
    public void ClaimAtSetup()
    {
        if (YesAlready.IsLoaded)
        {
            Tick(Environment.TickCount64, force: true);
        }
    }

    /// <summary>true＝YesAlready 的 Talk 現在確定暫停著（或它本來就沒開）。</summary>
    private bool Tick(long now, bool force)
    {
        if (!force && now - lastAttempt < RenewMs)
        {
            return claimUntil > now;
        }

        lastAttempt = now;
        var startingNewClaim = claimUntil <= now;
        if (!YesAlready.TryPauseTalk(ClaimMs, logOutcome: startingNewClaim && !failLogged))
        {
            // 問不到就當暫停已斷（它可能剛重新載入、舊的暫停已丟掉）：之後請到要重新等滿 SettleMs。
            claimUntil = 0;
            if (!failLogged)
            {
                failLogged = true;
                Svc.Log.Information(cannotClaimLog);
            }

            return false;
        }

        failLogged = false;
        if (startingNewClaim)
        {
            claimFrom = now;
        }

        claimUntil = now + ClaimMs;
        return true;
    }
}
