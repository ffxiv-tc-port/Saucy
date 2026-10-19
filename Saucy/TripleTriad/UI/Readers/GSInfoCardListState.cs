using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace Saucy.TripleTriad.UI;

/// <summary>卡片收藏清單(GSInfoCardList)的「共幾頁／第幾頁／第幾格」整數欄位讀取層 —— 台服專用位移。</summary>
/// <remarks>
/// 🔴 FFXIVClientStructs 的 SelectedPage／SelectedCardIndex／FilterMode 三個位移在台服全部指到
/// 別的東西(前兩個各差一格語意,第三個落在節點指標上)。推導見 commit 訊息。
/// 🔑 讀的全是 addon 配置範圍內的 int、不跳指標 ⇒ 讀取本身不可能 AVE,風險只有「值不對」;
/// 所以一律回 bool 並帶值域檢查,讓呼叫端讀不到時 fail-closed。
/// </remarks>
internal static unsafe class GSInfoCardListState
{
    /// <summary>頁數總量。遊戲拿它設定頁籤數,也拿它當翻頁請求的上界。</summary>
    private const int PageCountOffset = 0x528;

    /// <summary>目前頁索引。遊戲拿它設定頁籤位置,與格子內容是同一批資料送來的。</summary>
    private const int PageIndexOffset = 0x52C;

    /// <summary>目前選取的卡片格索引。遊戲拿它當 30 格卡片按鈕陣列的索引。</summary>
    private const int CellIndexOffset = 0x534;

    /// <summary>頁數總量的理智上界;超過就當成結構對不上。</summary>
    private const int MaxSanePageCount = 64;

    private static bool loggedRejected;

    /// <summary>讀出頁數總量;還沒收到資料或值不合理就回 <c>false</c>。</summary>
    public static bool TryGetPageCount(AddonGSInfoCardList* addon, out int pageCount)
    {
        if (!TryReadInt(addon, PageCountOffset, out pageCount))
        {
            return false;
        }

        // 0 = 還沒收到第一批資料,是正常狀態,不算對不上。
        if (pageCount == 0)
        {
            return false;
        }

        if (pageCount < 0 || pageCount > MaxSanePageCount)
        {
            Report("頁數總量", PageCountOffset, pageCount);
            pageCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>讀出目前頁索引(已對頁數總量做上界檢查);讀不到就回 <c>false</c>。</summary>
    public static bool TryGetPageIndex(AddonGSInfoCardList* addon, out int pageIndex)
    {
        pageIndex = 0;
        if (!TryGetPageCount(addon, out var pageCount))
        {
            return false;
        }

        if (!TryReadInt(addon, PageIndexOffset, out pageIndex))
        {
            return false;
        }

        if (pageIndex < 0 || pageIndex >= pageCount)
        {
            Report("頁索引", PageIndexOffset, pageIndex);
            pageIndex = 0;
            return false;
        }

        return true;
    }

    /// <summary>讀出目前選取的卡片格索引;讀不到或超出格數就回 <c>false</c>。</summary>
    public static bool TryGetCellIndex(AddonGSInfoCardList* addon, out int cellIndex)
    {
        if (!TryReadInt(addon, CellIndexOffset, out cellIndex))
        {
            return false;
        }

        if (cellIndex < 0 || cellIndex >= GameCardDB.MaxGridCells)
        {
            Report("卡片格索引", CellIndexOffset, cellIndex);
            cellIndex = 0;
            return false;
        }

        return true;
    }

    private static bool TryReadInt(AddonGSInfoCardList* addon, int fieldOffset, out int value)
    {
        value = 0;
        if (addon == null)
        {
            return false;
        }

        // 節點還沒建好時這幾個欄位是建構子留下的 0,與「真的在第 0 頁第 0 格」分不出來。
        if (addon->AtkUnitBase.UldManager.LoadedState != AtkLoadState.Loaded)
        {
            return false;
        }

        value = *(int*)((byte*)addon + fieldOffset);
        return true;
    }

    private static void Report(string label, int fieldOffset, int value)
    {
        if (loggedRejected)
        {
            return;
        }

        loggedRejected = true;
        Svc.Log.Information(
            $"[卡片收藏] GSInfoCardList +0x{fieldOffset:X}({label})讀到 {value},超出合理值域;本幀不採用,「帶我去這張卡」不會判定完成。這是台服結構位移對不上的徵兆。");
    }
}
