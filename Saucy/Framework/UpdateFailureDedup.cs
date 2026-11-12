using System;
using System.Collections.Generic;
namespace Saucy.Framework;

/// <summary>每幀重試的例外只記一次：同一種（例外型別＋擲出的方法）第一次回 true，之後回 false。</summary>
/// <remarks>本檔契約：純邏輯、不碰 Dalamud；不是執行緒安全（呼叫端只在 framework 執行緒用）。
/// 刻意不用 EzThrottler：它是全插件共用的鍵表，首次必放行且鍵永久留著。訊息文字不進鍵（常帶索引值，會讓「同一種」永遠不同）。</remarks>
internal sealed class UpdateFailureDedup
{
    /// <summary>記過的種類上限；超過後新種類也不再記（避免每幀換一種的病態情形洗版）。</summary>
    public const int MaxKinds = 8;

    private readonly HashSet<string> seen = [];

    public int ReportedKinds => seen.Count;

    public bool ShouldReport(Exception ex)
    {
        if (seen.Count >= MaxKinds)
        {
            return false;
        }

        var site = ex.TargetSite is { } m ? $"{m.DeclaringType?.FullName}.{m.Name}" : "?";
        return seen.Add($"{ex.GetType().FullName}@{site}");
    }
}
