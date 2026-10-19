using System;

namespace Saucy.JumboCactpot;

/// <summary>
/// 仙人仙彩：單張彩券的號碼設定。
/// <para>拆成獨立類別（而不是在 <see cref="Configuration"/> 攤平成六個欄位），是為了讓「本次進場的第幾張」可以直接用索引取用。</para>
/// <para>🔴 EzConfig 的序列化對 <c>[DefaultValue]</c> 不生效（既有使用者的 JSON 一定會把值蓋回來），所以預設值一律寫在欄位初始式上；而且兩個欄位的預設都等同「不指定」，升級的既有使用者行為完全不變。</para>
/// </summary>
[Serializable]
public class JumboCactpotTicketNumber
{
    /// <summary>true＝這張用 <see cref="Number"/>；false（預設）＝這張照舊隨機。</summary>
    public bool UseSpecificNumber { get; set; } = false;

    /// <summary>指定號碼（0-9999）。<see cref="UseSpecificNumber"/> 為 false 時不使用。</summary>
    public int Number { get; set; } = 0;
}

/// <summary>
/// 三張彩券號碼設定的取用與正規化。
///
/// <para>🔴 設定檔是使用者可以手改的 JSON，陣列長度、元素是不是 null 都不能假設——
/// 每一個取用點都先過 <see cref="Normalize"/>，所以索引越界在這裡就被擋掉，
/// 呼叫端拿到的一定是剛好 <see cref="Configuration.JumboCactpotTicketsPerWeek"/> 個非 null 元素。</para>
/// </summary>
public static class JumboCactpotNumberPlan
{
    /// <summary>把設定裡的陣列修正成「長度正確、元素非 null」，必要時就地寫回設定物件。
    /// 已經合法時原樣回傳，不做任何配置。</summary>
    public static JumboCactpotTicketNumber[] Normalize(Configuration config)
    {
        var slots = config.JumboCactpotTicketNumbers;
        var expected = Configuration.JumboCactpotTicketsPerWeek;
        if (slots != null && slots.Length == expected)
        {
            var allPresent = true;
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                {
                    allPresent = false;
                    break;
                }
            }

            if (allPresent)
            {
                return slots;
            }
        }

        var repaired = new JumboCactpotTicketNumber[expected];
        for (var i = 0; i < expected; i++)
        {
            repaired[i] = slots != null && i < slots.Length && slots[i] != null
                ? slots[i]
                : new JumboCactpotTicketNumber();
        }

        config.JumboCactpotTicketNumbers = repaired;
        return repaired;
    }

    /// <summary>取第 <paramref name="ticketIndex"/> 張（0 起算）的設定。
    /// 超出本週張數（例如伺服器允許買第四張時）回 null，呼叫端照「沒有指定」處理。</summary>
    public static JumboCactpotTicketNumber? Get(Configuration config, int ticketIndex)
    {
        var slots = Normalize(config);
        return ticketIndex >= 0 && ticketIndex < slots.Length ? slots[ticketIndex] : null;
    }

    /// <summary>這張有沒有指定號碼。回 false 時呼叫端該自己隨機——
    /// 🔴 這是 fail-safe 的方向：讀不到設定就退回既有的隨機行為，不會拿一個猜出來的號碼去買。</summary>
    public static bool TryGetNumber(Configuration config, int ticketIndex, out int number)
    {
        number = 0;
        var slot = Get(config, ticketIndex);
        if (slot == null || !slot.UseSpecificNumber)
        {
            return false;
        }

        number = Math.Clamp(slot.Number, 0, Configuration.JumboCactpotMaxNumber);
        return true;
    }

    /// <summary>給設定面板顯示用：這張會用什麼號碼。</summary>
    public static string Describe(Configuration config, int ticketIndex) =>
        TryGetNumber(config, ticketIndex, out var number) ? $"{number:D4}" : "隨機";
}
