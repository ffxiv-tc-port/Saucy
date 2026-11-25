# Saucy

金碟遊樂園多款小遊戲的自動化與輔助插件。

原作者：[PunishXIV/Saucy](https://github.com/PunishXIV/Saucy)

## 功能

### 九宮幻卡（Triple Triad）
- 自動導航至九宮幻卡 NPC（含地圖連結、坐騎輔助）
- 自動開始對戰、自動出牌、自動要求重賽
- 可把幻卡 NPC 的解鎖任務交給 Questionable 執行
- 追蹤卡片收藏、缺卡清單、NPC 解鎖狀態
- 金碟幣與掉落卡片追蹤；「NPC 統計」視窗記下每場對戰（含手動打的）的勝負和與收穫

### 仙人微彩、仙人仙彩
- 仙人微彩（Mini Cactpot）：自動翻格並求解最佳選線；顯示今天還剩幾張彩券。可選擇站到發放員旁就自動對話並選「參加」（預設關），購票確認由 YesAlready 或你自己按
- 仙人微彩當天彩券打完後，可接著請 Questionable 開始友好部族日常任務（預設關；要做哪些部族先在 Questionable 勾好）
- 仙人仙彩（Jumbo Cactpot）：購票面板開啟時自動填入號碼並叫出購買確認框，扣款確認仍由你自己按下；每週三張可各自指定號碼，並顯示本週已買的號碼。從「導航」抵達發放員後可選擇自動對話（預設關）

### 快速賣重複卡
- 自動出售背包中重複的九宮幻卡卡片，可設定每種卡至少保留張數

### GATE 小遊戲自動化
- 暴風倖存者（Wind Blows）
- 空軍裝甲駕駛員（Air Force One）：自動瞄準與開槍；炸彈避讓圈、目標標靶圈與同一點連續開槍的最短間隔都可調；遊戲視窗不在前景時暫停
- 登高跳跳樂大挑戰（Leap of Faith）：除了自動移動與跳躍（實驗性，預設關），也可改成「入場後等一段時間直接退出任務」拿最低獎勵（預設關，秒數可調）
- 搶救小雛鳥大作戰（Cliffhanger）
- 必中一閃快刀斬魔（Slice is Right）：Saucy 只負責走到場地邊界，場內走位交給 BossModReborn
- 活動解說員排程：只在金碟遊樂園內運作。每個時段開始前走去找已記錄的活動解說員，GATE 開放時走去報名；每個 GATE 的自動報名各有開關
- GATE 結束後可自動離開場地，走回最近的活動解說員等下一場（暴風倖存者、必中一閃快刀斬魔、搶救小雛鳥大作戰各有開關）
- 登高跳跳樂與空軍裝甲駕駛員結束後，自動關閉金碟幣結算視窗（可關）

### 孤樹無援（Out on a Limb）
- 陸行鳥廣場伐木機小遊戲自動化

### 金碟遊樂園導航
- 從「導航」頁選擇活動地點自動前往，需要時先傳送進金碟遊樂園，並可搭城內乙太之晶（需 Lifestream）
- 沒裝 vnavmesh 時改在地圖上標出目的地
- 走路途中你自己移動時會先暫停讓你操作，停手後從目前位置繼續；進入戰鬥即停止

### 統計
- 累計、單場、每小時金碟幣收益等數據追蹤

## 指令

- `/saucy`：開啟主視窗
- `/saucy d`：除錯視窗
- `/saucy stop`：停止導航、傳送與所有自動化，並暫停目前這個 GATE 時段的排程（按任一開始鈕可提早恢復）
- `/saucy tt go`、`/saucy tt stop`：開始、停止九宮幻卡自動對戰
- `/saucy tt play <場數>`：九宮幻卡打指定場數
- `/saucy tt cards any`、`/saucy tt cards all [次數]`：打到掉任一張卡為止、打到該 NPC 的每張卡都掉過指定次數為止

## 台服版的修改

- 新增搶救小雛鳥大作戰與登高跳跳樂大挑戰的自動化，以及 GATE 自動前往 NPC 與參加的排程
- GATE 結束後自動走回活動解說員、登高跳跳樂入場後直接退出、金碟幣結算視窗自動關閉
- 新增金碟遊樂園導航，以及仙人微彩／仙人仙彩抵達發放員後的自動對話（預設關）
- 仙人仙彩每週三張可各自指定號碼；仙人微彩顯示今天剩餘張數、派彩金額直接讀面板數字，打完後可接著開始 Questionable 的友好部族日常任務（預設關）
- 九宮幻卡卡片結構改用台服實際的記憶體位置，打開幻卡收藏視窗不再崩潰
- 對正在關閉中的視窗、確認框不重複按下，降低崩潰風險；替你按 NPC 對話前先請 YesAlready 讓開，避免兩個插件同時按同一個對話框
- 自動導航遇到你手動移動時讓位；停止或到達時只停 Saucy 自己送出的路徑，不會取消其他插件正在走的路線
- `/saucy stop` 會把所有自動化一起停下；其他插件也可以透過 IPC `Saucy.StopAll` 要求停止
- 仙人微彩中獎與九宮幻卡獲勝時可請塔塔露念一句（需安裝 TataruPraise）
- 相依插件面板的安裝按鈕改指台服的插件倉庫
- 多處降低記憶體用量
- 介面繁體中文化

## 安裝

1. 在遊戲內輸入 `/xlsettings` 開啟「Dalamud 設定」，到「試驗性功能」分頁的「第三方插件倉庫」加入
   `https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
   並啟用、儲存。
2. 輸入 `/xlplugins` 開啟「插件管理員」，搜尋「Saucy」安裝。

同一個插件只裝這裡的版本，不要同時裝上游版。

## 需要搭配的插件

- 建議：[vnavmesh](https://github.com/ffxiv-tc-port/vnavmesh)（自動走路；沒裝時改在地圖標示目的地）
- 選用：[Lifestream](https://github.com/ffxiv-tc-port/Lifestream)（城內乙太之晶）、[Questionable](https://github.com/ffxiv-tc-port/Questionable)（幻卡解鎖任務、友好部族日常任務）、[YesAlready](https://github.com/ffxiv-tc-port/YesAlready)（仙人微彩購票確認）、[BossModReborn](https://github.com/ffxiv-tc-port/BossmodReborn)（必中一閃快刀斬魔場內走位）、[TataruPraise](https://github.com/ffxiv-tc-port/TataruPraise)（塔塔露語音）

授權：BSD-3-Clause（見 LICENCE.md）
