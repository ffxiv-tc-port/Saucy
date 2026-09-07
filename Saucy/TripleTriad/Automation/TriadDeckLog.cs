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

        if (TriadDeferredSideEffects.TryDeferChat(message))
        {
            return;
        }

        Svc.Chat.Print(message);
    }
}

/// <summary>
///     持鎖時不要送聊天、不要寫 log、不要存設定：_preGameLock 由繪製端、framework 執行緒
///     與預覽模擬的背景工作共用，這三件事各自會去搶別的元件的鎖或等磁碟
///     （Serilog sink 自己有鎖也可能寫檔、EzConfig 存檔是整份序列化＋寫入），
///     在鎖內做等於讓每個等鎖的人一起排隊。
///     用 <see cref="Begin" /> 開一個延後範圍，範圍內要做的這些事先收下來，
///     範圍結束（已經出鎖）之後再做完 —— 仍然在原本那個方法回傳之前同步完成，
///     不是延後一幀，也不換執行緒。
///     判斷「做不做」的條件全部留在原本的位置與時機，只有「做」這一步被移到鎖外。
///     🔑 不在延後範圍內時每個入口都當場自己做（TryDefer 回 false）——
///     所以漏包某一條持鎖呼叫鏈的後果是「維持原本的行為」，不會變成靜默不做。
/// </summary>
internal static class TriadDeferredSideEffects
{
    // 三個佇列刻意分開、彼此不排序：聊天進遊戲聊天視窗、log 進 Serilog、存檔寫設定檔，
    // 三個接收端之間本來就沒有可觀察的先後關係，混成一個佇列反而是憑空造出順序。
    // 每一個佇列「內部」的先後則逐字保留。
    // 緩衝區跟著「持鎖的那一條執行緒」走，所以是 ThreadStatic。
    [ThreadStatic] private static int deferralDepth;
    [ThreadStatic] private static List<string>? pendingChat;
    [ThreadStatic] private static List<(DeferredLogLevel Level, string Message)>? pendingLog;
    [ThreadStatic] private static int pendingConfigSaves;

    // 第四個佇列是「碰磁碟的動作」：牌組快取的寫檔與刪檔。
    // 它一定排在 pendingActions 之前：出鎖後才跑的動作自己還會再改快取並寫檔，
    // 寫檔排在動作後面的話，先拍的那一份會被序號閘門判成舊的而整份跳過。
    [ThreadStatic] private static List<Action>? pendingFileWrites;
    [ThreadStatic] private static List<Action>? pendingActions;

    private enum DeferredLogLevel
    {
        Info,
        Warning,
        Error
    }

    public static Scope Begin()
    {
        deferralDepth++;
        return default;
    }

    /// <summary>在延後範圍內就收下聊天訊息並回報 true；不在範圍內回 false，由呼叫端自己送出。</summary>
    public static bool TryDeferChat(string message)
    {
        if (deferralDepth <= 0)
        {
            return false;
        }

        pendingChat ??= new List<string>();
        pendingChat.Add(message);
        return true;
    }

    public static void Info(string message)
    {
        if (!TryDeferLog(DeferredLogLevel.Info, message))
        {
            Svc.Log.Info(message);
        }
    }

    public static void Warning(string message)
    {
        if (!TryDeferLog(DeferredLogLevel.Warning, message))
        {
            Svc.Log.Warning(message);
        }
    }

    public static void Error(string message)
    {
        if (!TryDeferLog(DeferredLogLevel.Error, message))
        {
            Svc.Log.Error(message);
        }
    }

    /// <summary>
    ///     出鎖之後才做的動作。用在「整段都不該在持鎖時跑」的工作 —— 目前是
    ///     StartDeckOptimizer、EnsurePreviewEvalForNpc 與 OnPrepRulesUpdated，它們的呼叫鏈會打 vnavmesh／
    ///     Lifestream／Questionable 的 IPC，而 IPC 是在呼叫端的執行緒上執行對方的程式碼，
    ///     在鎖內打等於把自己的鎖交給別的外掛持有。
    ///     🔑 不在延後範圍內時當場執行，與其他入口一樣：漏包的後果是維持原本的行為。
    /// </summary>
    public static void RunAfterLock(Action action)
    {
        if (deferralDepth > 0)
        {
            pendingActions ??= new List<Action>();
            pendingActions.Add(action);
            return;
        }

        action();
    }

    /// <summary>
    ///     存設定。刻意用「計數」而不是旗標：原本的碼在同一次呼叫裡最多會存兩次
    ///     （PruneLegacyOptimizedDeckBuildTimestamps 真的刪到東西時一次、外層再一次），
    ///     用計數才能讓存檔次數與原本逐字相同。
    /// </summary>
    public static void SaveConfig()
    {
        if (deferralDepth > 0)
        {
            pendingConfigSaves++;
            return;
        }

        C.Save();
    }

    /// <summary>
    ///     寫檔（牌組快取）。在延後範圍內就收下來並回報 true；不在範圍內回 false，
    ///     由呼叫端自己當場寫 —— 漏包某一條持鎖呼叫鏈的後果是維持原本的行為。
    /// </summary>
    public static bool TryDeferFileWrite(Action write)
    {
        if (deferralDepth <= 0)
        {
            return false;
        }

        pendingFileWrites ??= new List<Action>();
        pendingFileWrites.Add(write);
        return true;
    }

    private static bool TryDeferLog(DeferredLogLevel level, string message)
    {
        if (deferralDepth <= 0)
        {
            return false;
        }

        pendingLog ??= new List<(DeferredLogLevel, string)>();
        pendingLog.Add((level, message));
        return true;
    }

    internal readonly struct Scope : IDisposable
    {
        public void Dispose()
        {
            if (--deferralDepth > 0)
            {
                // 還在外層的延後範圍內（持鎖呼叫鏈有巢狀），由最外層那個統一做完。
                return;
            }

            deferralDepth = 0;
            FlushLog();
            FlushChat();
            FlushConfigSave();
            FlushFileWrites();

            // 動作排最後:它們自己還會寫 log／送聊天,此時 deferralDepth 已歸零,
            // 所以會當場輸出 —— 排在前面三個之後才不會把先後順序倒過來。
            FlushActions();
        }

        // 每個 Flush 都先拍快照再清空：做的期間如果又有新的進來，不會跟這一輪混在一起。
        private static void FlushLog()
        {
            var pending = pendingLog;
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            var entries = pending.ToArray();
            pending.Clear();
            foreach (var (level, message) in entries)
            {
                switch (level)
                {
                    case DeferredLogLevel.Error:
                        Svc.Log.Error(message);
                        break;
                    case DeferredLogLevel.Warning:
                        Svc.Log.Warning(message);
                        break;
                    default:
                        Svc.Log.Info(message);
                        break;
                }
            }
        }

        private static void FlushChat()
        {
            var pending = pendingChat;
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            var messages = pending.ToArray();
            pending.Clear();
            foreach (var message in messages)
            {
                Svc.Chat.Print(message);
            }
        }

        private static void FlushConfigSave()
        {
            var count = pendingConfigSaves;
            pendingConfigSaves = 0;
            for (var i = 0; i < count; i++)
            {
                C.Save();
            }
        }

        private static void FlushFileWrites()
        {
            var pending = pendingFileWrites;
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            var writes = pending.ToArray();
            pending.Clear();
            foreach (var write in writes)
            {
                write();
            }
        }

        private static void FlushActions()
        {
            var pending = pendingActions;
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            var actions = pending.ToArray();
            pending.Clear();
            foreach (var action in actions)
            {
                action();
            }
        }
    }
}
