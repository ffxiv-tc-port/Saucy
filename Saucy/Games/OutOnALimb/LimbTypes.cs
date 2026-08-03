using System;
using System.Reflection;
namespace Saucy.OutOnALimb;

/// <summary>孤樹無援（Out on a Limb）難度＝第一階段「力量表」要停在哪一格。
/// 停得越準速度越快（泰坦最快、仙人掌怪最慢），對應 GoldSaucerArcadeMachine#2359302 的說明文字。</summary>
public enum LimbDifficulty
{
    Titan,
    Morbol,
    Cactuar
}

/// <summary>單次砍伐的「手感」回饋。遊戲以系統訊息回報（Addon 表 9710/9711/9712/9713），
/// 值越大代表離最佳位置越近；<see cref="Unobserved"/> 代表這個位置還沒試過。
/// 順序有意義——解題器用 &lt; 比較判斷「這次比上次差」。</summary>
[Obfuscation(Exclude = true)]
public enum HitPower
{
    Unobserved,
    Nothing,
    Weak,
    Strong,
    Maximum
}

/// <summary>刻度盤上某個位置（0–100）以及在該位置砍伐得到的手感。</summary>
public class HitResult(int position, HitPower power)
{
    public int Position = position;
    public HitPower Power = power;
}

[Serializable]
public class LimbSettings
{
    /// <summary>第一階段力量表要瞄準的格子。</summary>
    public LimbDifficulty Difficulty { get; set; } = LimbDifficulty.Titan;

    /// <summary>指針掃過目標位置時，容許的誤差（刻度單位）。放寬＝比較容易按得到，
    /// 但砍的位置比較不精準；收緊＝需要更高的畫面更新率才追得上指針。</summary>
    public int Tolerance { get; set; } = 2;

    /// <summary>解題器把 0–100 的刻度盤切成幾格（步進值）。</summary>
    public int Step { get; set; } = 10;

    /// <summary>是否自動停第一階段的力量表。⚠️ 力量表畫面與礦脈探索共用，
    /// 所以只有在附近確實認得出孤樹無援機台時才會動作。</summary>
    public bool AutoPowerMeter { get; set; } = true;

    /// <summary>是否自動回答「挑戰翻倍」確認框（預設關閉，交由玩家自己決定）。</summary>
    public bool AutoContinue { get; set; }

    /// <summary>啟用自動續戰時，剩餘秒數低於此值就按「否」收工。</summary>
    public int StopAtSecondsRemaining { get; set; } = 18;
}
