using System;
using System.Collections.Generic;
namespace Saucy.TripleTriad;

internal static class TriadDeckLog
{
    public static void Print(string message, bool force = false)
    {
        if (!force && !C.ShowOptimizerChatSpam)
        {
            return;
        }

        if (TriadChatDeferral.TryDefer(message))
        {
            return;
        }

        Svc.Chat.Print(message);
    }
}

/// <summary>
///     持鎖時不要直接呼叫 <c>Svc.Chat</c>：繪製端與背景工作共用同一把鎖，
///     鎖內送聊天等於讓每個等鎖的人一起排隊。
///     用 <see cref="Begin" /> 開一個延後範圍，範圍內要印的訊息先收下來，
///     範圍結束（已經出鎖）之後再依原順序送出。
///     判斷「印不印」的閘門仍然留在原本的位置與時機，只有送出這一步被移到鎖外。
/// </summary>
internal static class TriadChatDeferral
{
    // 緩衝區跟著「持鎖的那一條執行緒」走，所以是 ThreadStatic：
    // _preGameLock 由 framework 執行緒、繪製端與預覽模擬的 Task 共同競用，
    // 共用一份緩衝區會把別條執行緒的訊息混進來。
    [ThreadStatic] private static int deferralDepth;
    [ThreadStatic] private static List<string>? pendingMessages;

    public static Scope Begin()
    {
        deferralDepth++;
        return default;
    }

    /// <summary>在延後範圍內就收下訊息並回報 true；不在範圍內回 false，由呼叫端自己送出。</summary>
    public static bool TryDefer(string message)
    {
        if (deferralDepth <= 0)
        {
            return false;
        }

        pendingMessages ??= new List<string>();
        pendingMessages.Add(message);
        return true;
    }

    internal readonly struct Scope : IDisposable
    {
        public void Dispose()
        {
            if (--deferralDepth > 0)
            {
                return;
            }

            deferralDepth = 0;

            var pending = pendingMessages;
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            // 先拍快照再清空：送出期間如果又有新訊息進來，不會跟這一輪混在一起。
            var messages = pending.ToArray();
            pending.Clear();
            foreach (var message in messages)
            {
                Svc.Chat.Print(message);
            }
        }
    }
}
