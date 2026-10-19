using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using System;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Saucy.Framework;

/// <summary>問遊戲自己「站在這裡互動得到這個物件嗎」（遊戲內部互動前也呼叫同一支）。</summary>
/// <remarks>
/// 契約：只在 framework 執行緒呼叫；傳進來的物件必須是同一幀從物件表取得的，位址當幀取當幀用。
/// 特徵碼沒對上時 CS 的包裝是擲例外而不是回 null，所以先判函式指標；問不到就記住不再重試，回 null 由呼叫端退回距離判斷。
/// </remarks>
internal static unsafe class InteractRangeCheck
{
    private static bool unavailable;

    /// <returns>true／false＝遊戲的判定；null＝問不到。</returns>
    public static bool? IsWithinRange(IGameObject target, string logTag)
    {
        if (unavailable)
        {
            return null;
        }

        if (EventFramework.MemberFunctionPointers.CheckInteractRange is null)
        {
            MarkUnavailable(null, logTag);
            return null;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return null;
        }

        try
        {
            var eventFramework = EventFramework.Instance();
            if (eventFramework == null)
            {
                return null;
            }

            return eventFramework->CheckInteractRange(
                (NativeGameObject*)player.Address, (NativeGameObject*)target.Address, 1, false);
        }
        catch (InvalidOperationException ex)
        {
            MarkUnavailable(ex, logTag);
            return null;
        }
    }

    private static void MarkUnavailable(Exception? ex, string logTag)
    {
        unavailable = true;
        Svc.Log.Information(ex, $"[{logTag}] 問不到遊戲的互動範圍判定，改用水平距離判斷");
    }
}
