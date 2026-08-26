using Lumina.Excel.Sheets;
using System.Collections.Generic;
namespace Saucy.OutOnALimb;

/// <summary>
/// 判斷一個確認框是不是「街機遊玩確認框」（＝按下去會扣金碟幣、開始新的一局）。
///
/// 【為什麼不寫死字串】repo 慣例是面向使用者的文字一律從 Lumina 讀，這裡也一樣：
/// 比對用的句子是**執行期**從 <c>Addon</c> 表第 <see cref="PlayConfirmAddonRowId"/> 列拆出來的，
/// 換語言、改譯名、甚至官方改寫句子都會自動跟上。寫死中文的失敗方式是靜默的。
///
/// 【為什麼這一列夠獨特】台服 7.20 的 <c>Addon</c> 表（1138 張表全掃）裡：
/// <list type="bullet">
/// <item>「要挑戰一下嗎？」**只有 9321 這一列**有；</item>
/// <item>「需要金碟幣：」也**只有 9321**有；</item>
/// <item>翻倍提示（9329／9333）用的是「要<b>嘗試</b>挑戰一下嗎？」——
///   多了「嘗試」兩個字，所以 <c>Contains("要挑戰一下嗎？")</c> 對它是 false，
///   兩個模板不會互相包含。</item>
/// </list>
/// 也就是說「文字命中 9321 的固定句」等價於「這是遊玩確認框」，
/// 不會誤中砍完一棵樹之後那個免費的續戰提示。
///
/// 🔴 拆不出句子時一律回 false（＝不按）。這條路徑每按一次就花掉真的金碟幣，
/// 「認不出來就不要碰」永遠比「猜對機率很高」正確。
/// </summary>
internal static class LimbPrompt
{
    /// <summary>街機遊玩確認框的模板列：
    /// 「**〈機台名〉**\n〈說明〉\n\n要挑戰一下嗎？\n需要金碟幣：N\n[所持金碟幣：N]」。</summary>
    private const uint PlayConfirmAddonRowId = 9321;

    /// <summary>太短的片段沒有鑑別力（「是」「否」這種），一律丟掉。</summary>
    private const int MinFragmentLength = 4;

    private static string[]? fragments;

    /// <summary>模板裡「不含參數的固定句」。第一次要用時才建表，
    /// 避免外掛載入當下（ModuleManager 建構所有模組）就去碰資料表。</summary>
    internal static string[] Fragments => fragments ??= BuildFragments();

    /// <summary>這段文字看起來像不像街機遊玩確認框。</summary>
    internal static bool LooksLikePlayConfirm(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        foreach (var fragment in Fragments)
        {
            if (prompt.Contains(fragment, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] BuildFragments()
    {
        var sheet = Svc.Data.GetExcelSheet<Addon>();
        var text = sheet?.GetRowOrDefault(PlayConfirmAddonRowId)?.Text.ExtractText();

        // ⚠️ 台服有「列存在但欄位是空字串＝該內容未開放」的情形，所以判定要看內容而不是列在不在。
        if (string.IsNullOrWhiteSpace(text))
        {
            Svc.Log.Warning($"[OutOnALimb] Addon#{PlayConfirmAddonRowId} (街機遊玩確認框模板) is empty on this " +
                            "client; auto-replay will not recognise the arcade prompt and will stay hands-off");
            return [];
        }

        var list = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < MinFragmentLength || !IsStableFragment(line))
            {
                continue;
            }

            if (!list.Contains(line))
            {
                list.Add(line);
            }
        }

        if (list.Count == 0)
        {
            Svc.Log.Warning($"[OutOnALimb] Addon#{PlayConfirmAddonRowId} yielded no stable text fragments; " +
                            "auto-replay will not recognise the arcade prompt and will stay hands-off");
        }
        else
        {
            Svc.Log.Information($"[OutOnALimb] arcade play-confirm fragments ({list.Count}): " +
                                $"{string.Join(" / ", list)}");
        }

        return [.. list];
    }

    /// <summary>只留下「模板固定、不會被參數代換掉」的行。
    /// 帶數字的行（金碟幣數量）與方括號行（[所持金碟幣：N]）在不同情境長得不一樣，
    /// 拿它們比對會時準時不準——那種失敗最難查。</summary>
    private static bool IsStableFragment(string line)
    {
        foreach (var c in line)
        {
            if (char.IsDigit(c) || c is '[' or ']' or '*')
            {
                return false;
            }
        }

        return true;
    }
}
