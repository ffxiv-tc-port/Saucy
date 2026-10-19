using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace Saucy.TripleTriad.UI;

/// <summary>
/// 卡片收藏清單(GSInfoCardList)詳細資訊面板的節點取得層 —— 台服專用位移 + 成員資格證明。
/// </summary>
/// <remarks>
/// 🔴🔴 存在的理由:FFXIVClientStructs 的 <c>AddonGSInfoCardList</c> 欄位位移在台服客戶端是錯的,而其中 <c>SelectedCardName</c>：台服根本沒有這個欄位、連建構子都不會把它清零——讀到的是這塊堆積記憶體殘留的垃圾,再當成 <c>AtkTextNode*</c>解參考就是 AccessViolationException。
/// 🔴 <b>本層的安全性不依賴位移推導是否正確。</b>取到候選指標之後,在<b>任何解參考之前</b>先證明它是「這個 addon 的 ULD 節點清單裡的一員」(<c>UldManager.Objects->NodeList</c> 逐格比位址)。這個比對<b>完全不碰候選指標指向的記憶體</b>,所以候選值是垃圾時只會比不中被丟掉,不可能 AVE。⇒ 位移若推錯,結果是「拿到別的節點或拿不到」,<b>不會是崩潰</b>。
/// 🔴 AccessViolationException 在 .NET Core 是 corrupted-state exception,<c>try/catch</c> 與 <c>HookSafety.ExecuteSafe</c> 都攔不到,整個遊戲行程直接死 ——所以只能靠事前證明,不能靠例外隔離。
/// </remarks>
internal static unsafe class GSInfoCardListNodes
{
    /// <summary>台服 0x4F0(CS 誤標為 0x4E8 的 <c>SelectedCardName</c>)。</summary>
    private const int SelectedCardNameFieldOffset = 0x4F0;

    /// <summary>台服 0x500(CS 誤標為 0x4F8 的 <c>SelectedCardDescription</c>)。</summary>
    private const int SelectedCardDescriptionFieldOffset = 0x500;

    /// <summary>台服 0x510(CS 誤標為 0x508 的 <c>SelectedCardNumber</c>)。</summary>
    private const int SelectedCardNumberFieldOffset = 0x510;

    /// <summary>OnSetup 讀出來的節點 id,只用來做一致性診斷,<b>不</b>參與安全判定。</summary>
    private const uint SelectedCardNameNodeId = 58;

    private const uint SelectedCardNumberNodeId = 56;

    private static readonly uint[] SelectedCardDescriptionNodeIds = [60, 61];

    /// <summary>節點清單長度的理智上界;超過就當成結構對不上,整格放棄。</summary>
    private const int MaxNodeListCount = 4096;

    private static bool loggedLayoutMismatch;

    private static bool loggedNodeRejected;

    /// <summary>選取卡片的<b>名稱</b>文字節點;取不到或證明不了就回 <c>null</c>。</summary>
    public static AtkTextNode* SelectedCardName(AddonGSInfoCardList* addon) =>
        ResolveTextNode(addon, SelectedCardNameFieldOffset, SelectedCardNameNodeId, "卡片名稱");

    /// <summary>選取卡片的<b>編號</b>文字節點;取不到或證明不了就回 <c>null</c>。</summary>
    public static AtkTextNode* SelectedCardNumber(AddonGSInfoCardList* addon) =>
        ResolveTextNode(addon, SelectedCardNumberFieldOffset, SelectedCardNumberNodeId, "卡片編號");

    /// <summary>選取卡片的<b>說明</b>文字節點;取不到或證明不了就回 <c>null</c>。</summary>
    public static AtkTextNode* SelectedCardDescription(AddonGSInfoCardList* addon)
    {
        var node = ResolveNode(addon, SelectedCardDescriptionFieldOffset);
        if (node == null || node->Type != NodeType.Text)
        {
            return null;
        }

        var id = node->NodeId;
        var expected = false;
        foreach (var candidate in SelectedCardDescriptionNodeIds)
        {
            if (id == candidate)
            {
                expected = true;
                break;
            }
        }

        if (!expected)
        {
            ReportLayoutMismatch("卡片說明", SelectedCardDescriptionFieldOffset, id, SelectedCardDescriptionNodeIds[0]);
        }

        return (AtkTextNode*)node;
    }

    private static AtkTextNode* ResolveTextNode(
        AddonGSInfoCardList* addon,
        int fieldOffset,
        uint expectedNodeId,
        string label)
    {
        var node = ResolveNode(addon, fieldOffset);
        if (node == null || node->Type != NodeType.Text)
        {
            return null;
        }

        if (node->NodeId != expectedNodeId)
        {
            ReportLayoutMismatch(label, fieldOffset, node->NodeId, expectedNodeId);
        }

        return (AtkTextNode*)node;
    }

    /// <summary>
    /// 讀出欄位裡的候選指標,並在解參考之前先證明它屬於這個 addon 的 ULD 節點清單。
    /// </summary>
    private static AtkResNode* ResolveNode(AddonGSInfoCardList* addon, int fieldOffset)
    {
        if (addon == null)
        {
            return null;
        }

        var unitBase = &addon->AtkUnitBase;

        // 🔴 節點的生命週期由 UldManager 管:Unload() 會先把 LoadedState 設回 Unloaded 才釋放節點,
        // 所以這是唯一在「欄位還是非 null」時仍然正確的死活旗標。它是內嵌欄位,讀它不跳指標。
        if (unitBase->UldManager.LoadedState != AtkLoadState.Loaded)
        {
            return null;
        }

        var objects = unitBase->UldManager.Objects;
        if (objects == null)
        {
            return null;
        }

        var count = objects->NodeCount;
        var list = objects->NodeList;
        if (count <= 0 || count > MaxNodeListCount || list == null)
        {
            return null;
        }

        // 🔴 這一行是唯一會碰 addon 自己欄位區的讀取。它讀的是 addon 內部的位址,
        // addon 指標本身有效(每幀由 GetAddonByName 重查)⇒ 這個讀取安全,
        // 但讀出來的「值」完全不可信,下面必須先證明才准解參考。
        var candidate = *(AtkResNode**)((byte*)addon + fieldOffset);
        if (candidate == null)
        {
            return null;
        }

        for (var i = 0; i < count; i++)
        {
            if (list[i] == candidate)
            {
                return candidate;
            }
        }

        if (!loggedNodeRejected)
        {
            loggedNodeRejected = true;
            Svc.Log.Information(
                $"[卡片收藏] GSInfoCardList +0x{fieldOffset:X} 的值不在本視窗的 ULD 節點清單裡(共 {count} 個節點),已擋下解參考。這是台服結構位移對不上的徵兆,詳細面板本幀不讀。");
        }

        return null;
    }

    private static void ReportLayoutMismatch(string label, int fieldOffset, uint actualNodeId, uint expectedNodeId)
    {
        if (loggedLayoutMismatch)
        {
            return;
        }

        loggedLayoutMismatch = true;
        Svc.Log.Information(
            $"[卡片收藏] GSInfoCardList +0x{fieldOffset:X}({label})取到的節點 id 是 {actualNodeId},與離線驗證的 {expectedNodeId} 不同;節點本身已證明有效所以照用,但欄位對照可能要更新。");
    }
}
