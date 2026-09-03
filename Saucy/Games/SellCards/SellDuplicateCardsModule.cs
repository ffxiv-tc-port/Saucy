using Dalamud.Bindings.ImGui;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static ECommons.GenericHelpers;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Saucy.SellCards;

/// <summary>
/// 九宮幻卡「快速賣重複卡」輔助。移植自 DailyRoutines <c>AutoSellCards</c>，但**只保留顯示、丟掉自動化**。
///
/// <para>DR 版本做三件我方不做的事：①用 KamiToolKit 往 <c>TripleTriadCoinExchange</c> addon 注入按鈕
/// （🔴 全艦隊零 KamiToolKit 相依，不可用）；②自動迴圈點交換＋自動確認 <c>ShopCardDialog</c>
/// （🔴 使用者裁決：賣卡的送出與確認一律由人按，照 Saucy 仙彩／仙人微彩「錢包動作留給人」的先例）；
/// ③發 <c>EventStartPackt</c> 遠端開啟交換視窗（🔴 封包偽造紅線，丟棄）。</para>
///
/// <para>本模組保留的是唯一乾淨的部分：**讀取遊戲已經解析好、正顯示在交換視窗裡的卡片清單**，
/// 在旁邊開一個 ImGui 小視窗把「持有重複、可換 MGP」的卡整理出來（依單張 MGP 價值排序，並標出
/// 目前用於牌組的卡），方便玩家一眼看出該賣哪些、別誤賣了牌組裡的卡。**選取、交換、確認三步全部
/// 由玩家在遊戲原生視窗自己按**——本視窗只顯示，不送任何 callback、不發任何封包。</para>
///
/// <para>「至少留幾張」由 <see cref="Configuration.SellCardsKeepAtLeast"/> 控制（預設 1）：只有持有數
/// 超過這個保底值的卡才會被列為可賣，確保每種卡（含牌組用的那張）至少留下設定的張數。</para>
///
/// <para>資料來源是 addon 自己的 <c>AtkValues</c>（欄位排列沿用 ECommons
/// <c>ReaderTripleTriadCoinExchange</c> 已知的欄位版面，但改成型別容錯、逐格邊界檢查的讀法，
/// 避免 Int/UInt 型別差異在台服版本上直接擲例外）。全程零 hook、零 sig、零封包：讀不到就靜默降級
/// 成「讀取失敗」提示並寫一行 Information log 給玩家回報，不會崩潰。</para>
///
/// <para>模組未啟用時不掛任何 Draw 監聽；停用時立刻取消訂閱並清空快取，不留殘骸。</para>
/// </summary>
public unsafe class SellDuplicateCardsModule : Module
{
    /// <summary>addon 內部名，跨語言用戶端一致（非在地化字串）。</summary>
    private const string AddonName = "TripleTriadCoinExchange";

    /// <summary>重讀 addon 的最短間隔（毫秒）。UiBuilder.Draw 每幀都會呼叫，但卡片清單變動很慢，
    /// 沒必要每幀重讀原生記憶體＋重配置清單。</summary>
    private const int RereadIntervalMs = 250;

    /// <summary>entryCount 的理智上限——九宮幻卡總卡數三百多，這裡留大一點的餘裕當防呆，
    /// 讀到明顯離譜的值（記憶體版面對不上）就不會跑失控迴圈。</summary>
    private const int MaxEntries = 512;

    // AtkValues 的欄位版面（絕對索引，entry i）：沿用 ReaderTripleTriadCoinExchange 的已知位移——
    // 每個欄位是一整條跨 entry 連續排列的陣列，stride 為 1。
    //   entryCount 在索引 1；entry i 的欄位起點是 4 + i，欄位間距 40。
    private const int EntryCountIndex = 1;
    private const int EntryBase = 4;
    private const int OffName = 40;
    private const int OffValue = 80;
    private const int OffCount = 120;
    private const int OffInDeck = 200;
    private const int OffId = 160;

    private readonly List<CardRow> rows = [];
    private DateTime lastReadUtc = DateTime.MinValue;
    private bool readFailedLogged;
    private int lastEntryCount;

    public override string Name => "Sell Cards";

    /// <summary>給設定面板顯示的最近狀態。</summary>
    public string LastAction { get; private set; } = "等待開啟幻卡交換視窗";

    private readonly record struct CardRow(string Name, uint Id, uint Count, uint Value, bool InDeck);

    public override void Enable()
    {
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    public override void Disable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        rows.Clear();
        lastReadUtc = DateTime.MinValue;
        readFailedLogged = false;
        LastAction = "等待開啟幻卡交換視窗";
    }

    private void DrawOverlay()
    {
        // 🔴 每幀重新解析，絕不跨幀保存原生指標。
        var addon = GetExchangeAddon();
        if (addon == null)
        {
            if (rows.Count > 0)
            {
                rows.Clear();
            }

            LastAction = "等待開啟幻卡交換視窗";
            return;
        }

        if ((DateTime.UtcNow - lastReadUtc).TotalMilliseconds >= RereadIntervalMs)
        {
            lastReadUtc = DateTime.UtcNow;
            RefreshRows(addon);
        }

        DrawWindow();
    }

    private void RefreshRows(AtkUnitBase* addon)
    {
        rows.Clear();
        try
        {
            var count = (int)Math.Min(ReadNum(addon, EntryCountIndex) ?? 0u, MaxEntries);
            lastEntryCount = count;

            for (var i = 0; i < count; i++)
            {
                var inDeckIndex = EntryBase + i + OffInDeck;
                if (inDeckIndex >= addon->AtkValuesCount)
                {
                    // 版面比宣稱的短：讀不到這一筆的完整欄位就停手，不越界。
                    break;
                }

                var id = ReadNum(addon, EntryBase + i + OffId) ?? 0u;
                var name = ReadStr(addon, EntryBase + i + OffName);
                if (id == 0 && string.IsNullOrEmpty(name))
                {
                    // 已越過真正的清單尾端。
                    break;
                }

                var cardCount = ReadNum(addon, EntryBase + i + OffCount) ?? 0u;
                var value = ReadNum(addon, EntryBase + i + OffValue) ?? 0u;
                var inDeck = ReadBool(addon, inDeckIndex);

                rows.Add(new CardRow(
                    string.IsNullOrEmpty(name) ? $"#{id}" : name,
                    id, cardCount, value, inDeck));
            }

            readFailedLogged = false;
            LastAction = $"幻卡交換視窗開啟中，讀到 {rows.Count} 種可交換卡";
        }
        catch (Exception ex)
        {
            rows.Clear();
            LastAction = "讀取幻卡交換視窗失敗（見 log）";
            if (!readFailedLogged)
            {
                readFailedLogged = true;
                // 要玩家回報的診斷寫 Information（使用者跑 LogLevel 1）。
                Log($"讀取幻卡交換視窗失敗，欄位版面可能與此台服版本不符：{ex.Message}");
            }
        }
    }

    private void DrawWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(420f, 360f), ImGuiCond.FirstUseEver);
        // ⚠️ ImGui.Begin 必須無條件配對 ImGui.End（與 BeginTable/BeginChild 的規則相反）。
        // 這個視窗可被玩家收合，收合時 Begin 回 false，此處以手動 try/finally 保證 End 一定被呼叫；
        // ImGuiScopes.Window 現已無條件 End、兩者等價，此處維持手動寫法不動。
        var open = ImGui.Begin("重複幻卡 · 可換 MGP 一覽###SaucySellCards", ImGuiWindowFlags.None);
        try
        {
            if (open)
            {
                DrawWindowBody();
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawWindowBody()
    {
        var keepAtLeast = Math.Clamp(C.SellCardsKeepAtLeast, 0, Configuration.SellCardsMaxKeepAtLeast);

        if (rows.Count == 0)
        {
            ImGui.TextWrapped(readFailedLogged
                ? "讀取交換視窗失敗（欄位版面可能對不上，詳見 log）。"
                : "交換視窗裡目前沒有可交換的卡。");
            return;
        }

        // 可賣 = 持有數超過保底值的部分；至少留 keepAtLeast 張（含牌組用的那張）。
        var sellable = rows
            .Select(r => (Row: r, Extra: r.Count > (uint)keepAtLeast ? r.Count - (uint)keepAtLeast : 0u))
            .Where(x => x.Extra > 0)
            .OrderByDescending(x => x.Row.Value)
            .ThenByDescending(x => x.Extra)
            .ToList();

        ImGui.TextWrapped($"以下是你持有 {keepAtLeast + 1} 張以上、可換 MGP 的重複卡（每種至少留 {keepAtLeast} 張）。" +
                          "選取、交換、確認請在下方遊戲原生視窗自行按下——本視窗只顯示，不會替你送出交換。");
        ImGui.Dummy(new Vector2(0, 4));

        if (sellable.Count == 0)
        {
            SaucyTheme.TextMuted($"沒有持有數超過保底（{keepAtLeast} 張）的重複卡。");
            return;
        }

        ulong totalMgp = 0;
        uint totalCards = 0;

        using (var table = ImRaiiTable("##SellCardsTable"))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("卡片", ImGuiTableColumnFlags.WidthStretch, 0.42f);
                ImGui.TableSetupColumn("持有", ImGuiTableColumnFlags.WidthStretch, 0.12f);
                ImGui.TableSetupColumn("可賣", ImGuiTableColumnFlags.WidthStretch, 0.12f);
                ImGui.TableSetupColumn("單張 MGP", ImGuiTableColumnFlags.WidthStretch, 0.17f);
                ImGui.TableSetupColumn("小計 MGP", ImGuiTableColumnFlags.WidthStretch, 0.17f);
                ImGui.TableHeadersRow();

                foreach (var (row, extra) in sellable)
                {
                    var subtotal = (ulong)extra * row.Value;
                    totalMgp += subtotal;
                    totalCards += extra;

                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (row.InDeck)
                    {
                        // 牌組中的卡用警示色標出：保底值確保牌組那張不會被賣掉，但仍讓玩家一眼看見。
                        SaucyTheme.TextWarning($"{row.Name}  · 牌組中");
                    }
                    else
                    {
                        ImGui.TextUnformatted(row.Name);
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Count.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(extra.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{row.Value:N0}");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{subtotal:N0}");
                }
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.TextUnformatted($"可賣重複卡合計：{totalCards} 張　估計約 {totalMgp:N0} MGP");
        SaucyTheme.TextMuted("標「牌組中」的卡：保底張數已保護牌組用的那張，賣掉的只是多餘的重複；仍請自行確認。");
    }

    private static ImGuiTableScope ImRaiiTable(string id) => new(id);

    private readonly struct ImGuiTableScope : IDisposable
    {
        public bool Success { get; }

        public ImGuiTableScope(string id) =>
            Success = ImGui.BeginTable(id, 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 240f));

        public void Dispose()
        {
            if (Success)
            {
                ImGui.EndTable();
            }
        }
    }

    /// <summary>型別容錯的數值讀取：Int/UInt/Bool 都當數字讀，越界或型別不符回 null。全程邊界檢查，
    /// 不會越過 <c>AtkValuesCount</c>，所以不會踩到 AVE。</summary>
    private static uint? ReadNum(AtkUnitBase* addon, int index)
    {
        if (addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount)
        {
            return null;
        }

        var v = addon->AtkValues[index];
        return v.Type switch
        {
            ValueType.UInt => v.UInt,
            ValueType.Int => v.Int >= 0 ? (uint)v.Int : 0u,
            ValueType.Bool => v.Byte != 0 ? 1u : 0u,
            _ => null
        };
    }

    private static bool ReadBool(AtkUnitBase* addon, int index)
    {
        if (addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount)
        {
            return false;
        }

        var v = addon->AtkValues[index];
        return v.Type switch
        {
            ValueType.Bool => v.Byte != 0,
            ValueType.UInt => v.UInt != 0,
            ValueType.Int => v.Int != 0,
            _ => false
        };
    }

    private static string ReadStr(AtkUnitBase* addon, int index)
    {
        if (addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount)
        {
            return string.Empty;
        }

        var v = addon->AtkValues[index];
        if (!v.Type.EqualsAny(ValueType.String, ValueType.String8, ValueType.WideString, ValueType.ManagedString))
        {
            return string.Empty;
        }

        // 🔴 型別是字串不代表指標非空：型別對而指標為 null 時，從位址 0 掃 null 結尾＝AVE（攔不到）。
        if (v.String.Value == null)
        {
            return string.Empty;
        }

        return MemoryHelper.ReadStringNullTerminated((nint)v.String.Value);
    }

    /// <summary>🔴 每幀重新解析，絕不跨幀保存原生指標。</summary>
    private static AtkUnitBase* GetExchangeAddon()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !addon->IsVisible || !IsAddonReady(addon))
        {
            return null;
        }

        return addon;
    }
}
