using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using System.Text;
using System.Text.RegularExpressions;

namespace Saucy.Framework;

/// <summary>
/// 把表裡帶數字巨集的句子轉成「整句比對」的樣板：文字段逐字、換行巨集去掉、數字巨集（Num／Kilo）只配數字與千分位逗號。
/// 比對前兩邊都經 <see cref="Normalize"/>（去掉換行與空白），其餘逐字、要整句相同，不是子字串比對。
/// </summary>
/// <remarks>只依賴 Lumina 與 BCL：離線驗收直接編譯這個檔。含其他巨集（名字、條件式、連結）的句子不建樣板。</remarks>
internal static class GameTextTemplate
{
    /// <returns>null＝這一列不能當樣板（有其他巨集，或沒有任何固定文字）。</returns>
    internal static Regex? Build(ReadOnlySeString row)
    {
        var pattern = new StringBuilder(@"\A");
        var hasFixedText = false;
        foreach (var payload in row)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                var literal = Normalize(Encoding.UTF8.GetString(payload.Body.Span));
                if (literal.Length == 0)
                {
                    continue;
                }

                pattern.Append(Regex.Escape(literal));
                hasFixedText = true;
                continue;
            }

            if (payload.Type != ReadOnlySePayloadType.Macro)
            {
                return null;
            }

            switch (payload.MacroCode)
            {
                case MacroCode.NewLine:
                    break;
                case MacroCode.Num:
                case MacroCode.Kilo:
                    pattern.Append("[0-9,]+");
                    break;
                default:
                    return null;
            }
        }

        return hasFixedText
            ? new Regex(pattern.Append(@"\z").ToString(), RegexOptions.CultureInvariant)
            : null;
    }

    /// <summary>去掉換行（CR／LF 各種組合）、半形空白與不換行空白，再修頭尾。</summary>
    internal static string Normalize(string text) =>
        text.ReplaceLineEndings(string.Empty).Replace(" ", string.Empty).Replace("\u00A0", string.Empty).Trim();
}
