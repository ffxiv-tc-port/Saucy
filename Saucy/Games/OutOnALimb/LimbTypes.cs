using System;
using System.Reflection;
namespace Saucy.OutOnALimb;

/// <summary>孤樹無援（Out on a Limb）難度＝第一階段「力量表」要停在哪一格。
/// 停得越準速度越快（泰坦最快、仙人掌怪最慢）。
///
/// ⚠️ 這三個名字對應的是**格子大小的排名**，不是固定的節點編號：
/// 用戶端把力量表切成三段，段落寬度由 <c>AtkValue[4]</c>／<c>AtkValue[5]</c> 在開場時決定
/// （離線反組譯 <c>AddonMiniGameAimg::OnSetup</c> 證實），最窄的一段就是最難停中、獎勵最好的那一格。
/// 因此判定一律用「當下量到的三段寬度排序」，不寫死任何節點編號或像素高度。</summary>
public enum LimbDifficulty
{
    Titan,
    Morbol,
    Cactuar
}

/// <summary>單次砍伐的「手感」回饋。遊戲以系統訊息回報（Addon 表 9710/9711/9712/9713），
/// 值越大代表離最佳位置越近；<see cref="Unobserved"/> 代表這個位置還沒試過。
/// 順序有意義——解題器直接拿列舉值當分數比大小。
///
/// 📌 **這是主要回饋來源**（2026-08-06 實機修正）。21 刀的實機 log 裡它 21/21 都拿得到，
/// 而原本被當成主來源的量表落差 21 刀只動過 1 次。
/// 這四級也正好對應 DailyRoutines 用的四級結果（Fail／Normal／Great／Perfect）。</summary>
[Obfuscation(Exclude = true)]
public enum HitPower
{
    Unobserved,
    Nothing,
    Weak,
    Strong,
    Maximum
}

/// <summary>刻度盤上某個位置（0–100）以及在該位置砍伐得到的結果。</summary>
public class HitResult(int position, HitPower power)
{
    public int Position = position;
    public HitPower Power = power;

    /// <summary>在這個位置砍下去，樹的量表（<c>AtkValue[12]</c>）掉了多少。
    /// null＝這個位置沒量到量表變化。
    /// 📌 2026-08-07 更正：舊註解寫「台服 7.20 實測幾乎永遠是 null」是**從壞掉的版本量到的**——
    /// 那時每一刀都沒手感、沒手感就是 0 傷害。解題器修好之後量表確實每刀在動
    /// （見 <see cref="LimbBoard.ReadGauge"/>）。
    /// ⚠️ 但它仍然只是 <see cref="Power"/> 的**補強**：盲掃階段砍不中就沒有傷害可量，
    /// 而四級手感每一刀都會到。</summary>
    public int? Damage;

    /// <summary>這個位置有沒有任何形式的觀測結果。</summary>
    public bool IsObserved => Damage != null || Power != HitPower.Unobserved;
}

/// <summary>手感等級的中文標籤（面板與 log 用）。</summary>
internal static class HitPowerText
{
    internal static string Of(HitPower power) => power switch
    {
        HitPower.Nothing => "沒手感",
        HitPower.Weak => "接觸到",
        HitPower.Strong => "很接近",
        HitPower.Maximum => "正中目標",
        var _ => "未觀測"
    };
}

/// <summary>力量表上的一格。座標一律是遊戲自己用的 0–10000 指針刻度，
/// 不是像素、也不是 0–100 的顯示刻度。</summary>
/// <param name="Slot">用戶端內部的段落序號（0＝指針高端那段，2＝低端那段）。診斷用。</param>
/// <param name="LowExclusive">下界（不含）。最低那格是 -1，因為遊戲的判定是 <c>pos &lt;= 累加值</c>。</param>
/// <param name="HighInclusive">上界（含）。</param>
internal readonly record struct LimbZone(int Slot, int LowExclusive, int HighInclusive)
{
    internal int Width => HighInclusive - LowExclusive;

    internal int Centre => LowExclusive + (Width / 2);

    internal bool Contains(int position) => position > LowExclusive && position <= HighInclusive;
}

[Serializable]
public class LimbSettings
{
    /// <summary>第一階段力量表要瞄準的格子。</summary>
    public LimbDifficulty Difficulty { get; set; } = LimbDifficulty.Titan;

    /// <summary>指針掃過目標位置時，容許的誤差（0–100 顯示刻度）。放寬＝比較容易按得到，
    /// 但砍的位置比較不精準。
    /// 📌 現在還會另外做「兩幀之間有沒有跨過目標」的判斷，所以就算誤差窗很窄、
    /// 畫面更新率不夠高，也不會像以前那樣整個掃過頭都按不到。</summary>
    public int Tolerance { get; set; } = 2;

    /// <summary>粗掃時兩個取樣點的**最小間距**（0–100 顯示刻度）。
    /// 📌 2026-08-06 起語意改了：舊版把它當成「盤面切成幾格」，收斂精度因此被它卡死；
    /// 現在盤面一律是 0–100 逐格，這個值只決定「還沒摸到任何手感時，粗掃要撒多開」。
    /// 一旦有手感，細修可以走到比這個值更細的刻度。預設值不變。</summary>
    public int Step { get; set; } = 10;

    /// <summary>是否自動停第一階段的力量表。⚠️ 力量表畫面與礦脈探索共用，
    /// 所以只有在附近確實認得出孤樹無援機台時才會動作。</summary>
    public bool AutoPowerMeter { get; set; } = true;

    /// <summary>是否自動回答「挑戰翻倍」確認框（預設關閉，交由玩家自己決定）。</summary>
    public bool AutoContinue { get; set; }

    /// <summary>啟用自動續戰時，剩餘秒數低於此值就按「否」收工。</summary>
    public int StopAtSecondsRemaining { get; set; } = 18;

    /// <summary>是否允許「連續遊玩」：一局結束後自動再跟機台開下一局。
    /// 🔴 預設關閉，而且**光是打開這個開關還不會動作**——一定要在面板上按下「開始連續遊玩」，
    /// 而且跑滿 <see cref="AutoReplayMaxGames"/> 局就會自己停。</summary>
    public bool AutoReplay { get; set; }

    /// <summary>一次「開始連續遊玩」最多自動開幾局。到達上限就停下來，需要再按一次才會繼續。</summary>
    public int AutoReplayMaxGames { get; set; } = 5;

    /// <summary>是否把機台面板的 AtkValue[0..15] 節流輸出到 log（Information 等級）。
    /// 用來驗證各欄位的意義，平常不需要開。</summary>
    public bool LogBoardDiagnostics { get; set; }
}
