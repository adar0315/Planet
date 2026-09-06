# Planet

星球地圖走動系統。以 Goldberg 多面體（12個五角形＋約2000萬個六角形，邊長約1公里）建構星球地圖，前端在球面地圖上移動並顯示視野內約2000格；另有全星球低解析度總覽模式可以一次看到整顆星球。

## 技術棧

- 後端：.NET 10 / ASP.NET Core（Controllers + Scalar API 文件 + SignalR），可 `dotnet publish` 成 EXE，透過 NSSM 安裝為 Windows 服務
- 前端：Vue 3 + TypeScript + Vite + Three.js
- 資料庫：SQL Server，服務啟動時若無地圖資料則自動生成並寫入
- 開發工具：Visual Studio Code

## 目錄結構

```
backend/
  src/
    Planet.Service/     # 進入點：Web API + SignalR + Scalar，打包成EXE
    Planet.Domain/       # 核心模型：Tile、TerrainType、TileShape
    Planet.Generation/    # Goldberg多面體生成、地形程序化生成、鄰居即時計算
    Planet.Data/          # EF Core + SQL Server存取層
    Planet.Shared/        # 前後端共用DTO
  tests/
  deploy/
    nssm/                 # Windows服務安裝腳本
    sql/                  # DB migration操作說明
frontend/
  src/
    scene/                # Three.js場景與星球渲染
    map/                  # 六角/五角格mesh產生
    network/               # REST + SignalR client
    stores/                 # Pinia狀態（玩家位置、時間倍率）
    ui/                     # HUD等UI元件
    types/                  # 對應後端DTO的型別
docs/                       # 架構與設計筆記
```

## 設定機密

連線字串與管理金鑰**不放在 `appsettings.json`**（那個檔案會進版控）。缺少連線字串時服務會直接啟動失敗並說明設定方式。

開發（存在 `%APPDATA%\Microsoft\UserSecrets`，不在 repo 內）：

```bash
cd backend/src/Planet.Service && dotnet user-secrets set "ConnectionStrings:PlanetDb" "Server=localhost;Database=PlanetDb;User Id=<帳號>;Password=<密碼>;TrustServerCertificate=True"
```

```bash
cd backend/src/Planet.Service && dotnet user-secrets set "Admin:ApiKey" "<自行產生的長亂數>"
```

部署（NSSM 服務）：在部署目錄放 `appsettings.Production.json`（不在 repo 內，`dotnet publish` 也不會覆蓋它）：

```json
{
  "ConnectionStrings": { "PlanetDb": "..." },
  "Admin": { "ApiKey": "..." }
}
```

或改用環境變數 `ConnectionStrings__PlanetDb` / `Admin__ApiKey`（`deploy/nssm/install-service.ps1` 的 `-ConnectionString`／`-AdminApiKey` 參數會設定它們）。

**管理金鑰沒設定時，`/api/admin` 會整個停用（回 503），不會退化成不用驗證。**

## 開發

後端：

```bash
cd backend
dotnet run --project src/Planet.Service
```

啟動後 Scalar API 文件在 `https://localhost:<port>/scalar/v1`。

前端：

```bash
cd frontend
npm run dev
```

## 星球規模

原始規格（地球大小＋1公里邊長＋2000萬格）三者互相衝突（地球大小＋1公里邊長實際上會產生約1.96億格）。已依決定改為：固定總格數（細分頻率 n=1415 → 20,022,252格，其中12個五角形），反推星球半徑以維持六邊形邊長約1公里 → 半徑約1851公里（小於2100公里，比地球小很多，介於冥王星與水星之間）。見 [docs/architecture.md](docs/architecture.md)。

## 目前狀態 / 待完成

### 後端 — 已完成

- `GoldbergPlanetGenerator`：正二十面體測地線細分、南北極自動對齊五角形、星球半徑推算
- `TerrainGenerator`：18種地形分類（草地/灌木叢/林地/淡水/海水/泥地/黃土地/岩地/沙地/雪地/冰原/沼澤/鹽鹼地/火山岩地/珊瑚礁/懸崖/苔原/礫灘），8組獨立noise決定，草地/灌木叢/林地/淡水/海水另有高度或深度細分等級（HeightCategory）
- `ITileNeighborResolver`：六角形6鄰居／五角形5鄰居，跨面座標轉換與正規化，已用對稱性測試驗證
- `TileRepository.BulkInsertAsync`：`SqlBulkCopy`＋自製 `TileDataReader` 串流寫入
- `GetTilesNearAsync`：bounding box＋(Latitude,Longitude)索引，查詢框±1.1度（赤道約2400格、極區約3400格，供前端一次載入2000格用），赤道~極區都驗證過約60ms量級（原本全表掃描要7.8秒）
- `OverviewTextureGenerator`：全星球低解析度（1024×512）等距柱狀投影地形色彩貼圖，直接用`ITerrainGenerator`取樣（不查資料庫），第一次請求約0.6秒生成後永久快取，供前端「星球總覽」模式用
- `PlanetSeeder`：啟動時自動生成＋寫入，已接上本機 SQL Server（`PlanetDb`，Windows驗證）跑過完整2000萬筆
- `MovementHub`（SignalR）：`MoveTo(fromTileId,toTileId)` 用 `ITileNeighborResolver` 驗證合法性才回傳目標Tile
- Schema 用 EF Core Migrations 管理（`backend/src/Planet.Data/Migrations/`）

### 前端 — 已完成

- 球面六角/五角格渲染，格子之間真正密合（角落由相鄰三格共同決定，見下方「蜂窩密合」）
- 三種操作模式（時間靜止／移動模式+1小時／即時模式每3秒+1小時），畫面可切換，時間從 2050/01/01 08:00:00 UTC 起算
- 移動：鍵盤 WEADZX（依方位角動態對應鄰居）＋滑鼠點擊鄰居格，皆透過SignalR請後端驗證
- 移動時鏡頭只平移跟著玩家，視角方向/縮放完全不變（相鄰格角距離極小，切平面幾乎沒有旋轉，用平移取代重新定向）
- 指北針UI（依鏡頭角度即時算「正北」在螢幕上的方向），可點擊復位視角（回到目前所在格正上方、正北朝上的俯視角）
- 鄰居高亮外框（點擊/移動的視覺提示）
- 日夜光照系統：依 `gameTime`（UTC）算太陽直射點（含地軸傾角23.44°的簡化季節性declination＋依UTC時刻算太陽直射經度），换算成方向光位置，晝夜/季節都會反映在光照上（已測試 08:00→14:00→20:00 亮度正確隨時間變化）
- 移動鍵語意化配置（[bearing.ts](frontend/src/map/bearing.ts)）：W/E/A/D/Z/X對應NW/NE/W/E/SW/SE六個方位，用「每個(鄰居,方位)配對的角度差」貪婪指派，而不是單純依方位角順時針排序——確保鍵位跟實際地理方向對得上
- 玩家狀態持久化：位置/模式/時間存瀏覽器localStorage（`stores/gameState.ts`），重新整理頁面會從上次的位置＋時間繼續，已實測position/mode/time三者都正確保存
- 地形起伏視覺化：Tile中心依 `ElevationLevel` 沿法向量偏移（誇張8倍顯示），海拔越高越突出、水越深越凹陷

### 地形系統全面改版（2026-08-29）

- 海拔改用500級分（`ElevationLevel`，範圍-500~+500，每級5公尺，即±2500公尺），`SeaLevel`跟著改成同單位的整數，每格獨立欄位（目前統一為0，方便之後做局部海平面變動）
- 地形分類從8種擴充到18種，草地/灌木叢/林地/淡水/海水另外用 `HeightCategory` 記錄高度或深度細分（例如林地=高度×2+疏密，共8種組合）。詳見 [docs/architecture.md](docs/architecture.md) 的對照表
- 全星球2000萬格已用新系統重新生成（migration `TerrainSystemOverhaul` + TRUNCATE + 重新seed）

### 部署（2026-08-30，已完成並驗證）

- **EXE打包**：`Planet.Service.csproj` 設定 `RuntimeIdentifier=win-x64` + `SelfContained=true`，`dotnet publish src/Planet.Service -c Release -o C:\PlanetPublish` 已驗證成功（**`-o` 一定要指到路徑本身不含單引號的絕對路徑**——這個repo放在 `D:\Adar's novel stories\Planet\...`，路徑裡的單引號會讓 `dotnet publish` 內建target壞掉噴MSB3094，詳見 [docs/architecture.md](docs/architecture.md)）。
- **DB連線改用SQL帳密**：改用SQL Server帳號驗證取代Windows整合驗證，這樣NSSM服務不管用哪個Windows帳號執行都能連上DB，不用額外處理服務帳號權限。連線字串本身不進版控，見下方「設定機密」。
- **NSSM服務安裝**：`deploy/nssm/install-service.ps1` 已驗證成功——服務裝好、啟動、API透過 `http://localhost:5202` 正常回應，`netstat`確認port確實是服務進程在監聽。目前為`SERVICE_DEMAND_START`（手動啟動），確認長期穩定後可改`SERVICE_AUTO_START`開機自動啟動。

### 全星球總覽模式（2026-08-30）

- **效能評估**：畫面改成「顯示所有格子」（2000萬格一次載入）在查詢時間（分鐘等級）、傳輸量（約3.7GB）、
  前端draw call數（一格一個Mesh，2000萬次）三個維度都不可行，實測數據見
  [docs/architecture.md](docs/architecture.md)。走動視角改成一次載入2000格（原本900格），
  後端查詢框放寬到±1.1度。
- **星球總覽**：另外做一套低精度渲染路徑，不查地塊資料——後端`OverviewTextureGenerator`直接用地形演算法
  取樣算出一張1024×512的等距柱狀投影貼圖，貼到前端一顆簡單球體（`PlanetOverview.vue`）上，可自由旋轉/縮放
  看整顆星球。跟走動視角（`PlanetScene.vue`）是完全獨立的Three.js場景，畫面上方按鈕切換（`gameState.viewMode`）。

### 地形樣式表與逐格日夜變色（2026-08-30）

- 地形的顯示樣式（中英文名稱＋清晨/白天/黃昏/夜晚四組顏色）改存資料庫 `TerrainStyles` 表，
  改顏色不用重新編譯部署。UI 顯示「英文 中文」（例如 `Mud 泥地`）。
- 時段是**逐格**依該格經緯度的實際日出日落判斷，不是全星球統一——用標準時角公式，
  含極晝極夜處理。詳見 [docs/architecture.md](docs/architecture.md)。

### 星球生成參數化（2026-08-30）

- `TerrainGenerationOptions` 集中管理種子、大陸伸展倍率、小島數量/聚集/火山機率。
- `/api/admin` 提供讀寫參數與「清空並在背景重新生成」，前端右上角有管理面板（二段式確認）。

### 前端效能重構（2026-08-31）

原本每走一格約 200ms、且期間按鍵被直接丟棄（連按只會走一格），穩態每幀 2000 次 draw call。

- **合併 geometry**：整個patch變成單一 Mesh，建構 76.8ms → 11.6ms、draw call 2000 → **1**。
- **本地先動、背景驗證**：後端把鄰接關係（`TileDto.neighborIds`）隨地塊一起送來，前端按鍵當下就能
  決定走到哪一格，SignalR 驗證改在背景跑。實測 **20次按鍵＝20步**（舊版20次只會走幾步）。
- **邊緣觸發載入**：一次載6000格，只在接近已載入範圍邊緣時補載。實測走90步只觸發3次補載、零停頓。
- **回應壓縮**：2000格 362KB → **122KB**。
- 順手修掉三個既有 bug：換日線 patch 被砍半、`camera.near` 大於 `minDistance` 導致腳下格子被裁掉、
  方向鍵在特定相位會誤指派（最差誤差 90° → 45°）。
- 修掉 WebGL context 洩漏（切換走動/總覽視角每次漏一個，約16次後畫面會壞）。

推導過程與踩過的坑見 [docs/architecture.md](docs/architecture.md)。

### 轉向與自訂按鍵（2026-09-01，轉向動畫 2026-09-02）

玩家有**面向**，六角格所以每次轉60度（0/60/120/180/240/300，0＝正北）。轉向會同時把
鏡頭轉成「面向朝畫面上方」，並依面向重新指派六個移動方向——兩者一起做，畫面上看到的右邊
才會是按「右」會走到的那一格。面向0度時行為跟加入這個功能之前完全相同。

實測驗證：六個面向各按一次「右」，走到那一格的地理方位角與「面向+90度」的誤差 2.8~4.7 度。

轉向後來改成**平滑過渡**而不是瞬間跳過去：鏡頭每幀用中間角度重新定位、以smoothstep緩動，
220ms轉完60度。長按連續轉向時新的一次會接著上一次還沒播完的角度繼續轉，不會卡頓重來；
跨過0°/360°接縫時一律走最短路徑，不會反方向繞一大圈。方向鍵對應的插槽仍然立刻重算，
不等動畫播完——操作邏輯即時生效，只有鏡頭轉動的過程是漸進的。細節見
[docs/architecture.md](docs/architecture.md)。

九個動作（六個移動方向＋原地＋左右轉向）的按鍵都可以在資訊欄的「按鍵設定」裡自訂，
預設 W/E/A/D/Z/X＋S＋`,`／`.`，設定存在 localStorage。按鍵重複時兩個動作自動互換，
不會有動作變成沒有按鍵。

### 消除重複的事實來源（2026-08-31）

架構盤點後處理三個同源問題：同一份事實散落多處、不一致時完全看不出來。

- **新增 `PlanetMetadata` 表**記錄「資料庫裡這顆星球實際上是什麼」（尺寸＋實際使用的地形參數）。
  修掉「改了參數但還沒重新生成時，星球總覽會畫出一顆不存在的星球」。
- **新增 `GET /api/planet`**，前端不再寫死星球半徑等常數。這個修正當場抓到既有漂移：
  實際半徑 1,852,259.92 公尺 vs 前端寫死的 1,851,000，**差了約1,260公尺**。
- **刪掉前端的地形調色盤複本**，只留單一中性色；載入失敗改為明確顯示警告，不再靜默吞掉。

### 右下角位置指示器改版（2026-08-31）

- **圖跟著玩家轉**：參考經度改成玩家經度，玩家永遠在正中央。舊版固定視角導致
  `lon` 與 `180−lon` 投影到同一點，正反面完全分不出來。
- **標示大致的陸地與海洋**：新增 `GET /api/tiles/overview-water-mask`（128×64、每像素1位元組、
  共8KB），跟2MB的總覽貼圖分開——為了120px的小圖示載2MB不合理。
- **動態日照光影**：改用 canvas 逐像素繪製，依實際太陽天頂角決定明暗，
  含柔和的晨昏過渡帶。實測推進24小時，圓心亮度與 `determineDayPeriod` 的判斷完全吻合。
- 順便修掉：三個標籤被 viewBox 裁掉、`title` 因 `pointer-events:none` 永遠不顯示。

### 蜂窩密合（2026-08-31）

格子之間原本有點狀縫隙。量測後發現成因不是格子畫太小（實際上還大了4%），而是**朝向錯了** ——
程式把「角」畫在應該是「邊」的位置，朝向誤差達 ±22°~29°，且**隨地區變化**所以無法用固定旋轉修正。

改成**角落由共用它的三格一起決定**（`normalize(自己+鄰居ᵢ+鄰居ᵢ₊₁)`）：三格用同樣的公式與輸入，
必定算出同一點，密合由建構方式保證。高低差處補上側面牆（只由較高的那格畫，每面牆剛好一次）。

實測 **3,600 組相鄰配對全部完整共用一條邊**，0 組對不齊；建構成本 11.6ms → 14.8ms，仍是 1 次 draw call。

### 遠景與載入範圍（2026-08-31）

預設視角改成傾斜60度後，地平線在約193公里外，遠比當時64公里的載入範圍遠，「地形只到一半」很明顯。

- **一次載入 6000 → 50000 格**：patch半徑 64 → **187公里**，已接近地平線。
  仍然只有1次draw call；代價是換patch時的同步停頓、傳輸3.1MB、後端查詢約1秒（背景進行）。
  後端 `MaxCount`／`MaxDeltaDegrees` 配合放寬到 6萬／7°。
- **遠景改用總覽貼圖**：載入範圍外原本是純色，現在把星球總覽那張地形貼圖貼在底層球體上，
  形成「近處精細格子、遠處低解析度貼圖」的簡易LOD。經緯度對齊已用7個座標實測驗證。
- **補載提早觸發**（threshold 10 → 30）：前方邊界永遠保持在50公里外，不必走到快出界才補。

順帶發現：**海水地塊的海拔一律是下限 -500級，其實一直被底層球體蓋住、從未真的畫出來過** ——
海面一直是由那顆球代表的（海是平的，這樣其實合理），貼上貼圖後海的顏色也跟著資料庫走了。

### 建 mesh 的迴圈改成分多幀完成（2026-09-01）

補載雖然在背景進行，但**建mesh那段是同步的**：5萬格的填充迴圈約1秒的CPU工作量，
一次做完就是整整1秒畫面與輸入都沒反應。改成 `buildTilePatchMeshSliced`——
建構器拆成 `fillRange`／`finish`，每次做滿約5ms就用 `MessageChannel` 交還控制權。

實測（`setInterval(10ms)` 看門狗量主執行緒被佔住多久，走約200步觸發一次補載）：
建mesh造成的最長阻塞 **約1秒 → ≤35ms**，看門狗間隔中位數 **10.1ms**（＝完全沒被卡住）。
牆鐘時間拉長到約1.6~1.8秒，但**建構期間舊的patch仍掛在場景上**，畫面完整、可以繼續走，
所以感覺不到；卡住一秒則一定感覺得到。

同一次量測也釘出了**剩下的兩處同步阻塞**：`createTilePatch` 建索引約296ms、
3MB回應的 `JSON.parse`——換patch時唯一還超過50ms的區塊（121ms）就是它們，不是建mesh。
推導與各階段數字見 [docs/architecture.md](docs/architecture.md)。

### 前端測試

```bash
cd frontend
npm test
```

196 個單元測試（Vitest），涵蓋合併 geometry 的三角形→地塊對應（五角/六角交錯等會讓固定 stride
錯位的情況逐一窮舉）、**蜂窩密合性**（三格互相相鄰時算出的共用角落必須完全相同）、
方向鍵在各旋轉相位的誤差與循環順序、patch 邊緣 BFS 與大圓最近格、
地形樣式 store 的失敗退回行為、位置指示器的正射投影／反投影／日照明暗、
**切片建構的等價性**（切成多段之後的 geometry 必須與一次做完的逐位元組相同——
任何「靠迴圈一路跑到底」的隱含假設都會在這裡露餡，而症狀是幾何靜默錯位）、
**轉向**（六個面向的方向指派、右轉整體+60度而不是−60度）與自訂按鍵的衝突互換／壞資料退回，
以及轉向動畫的內插方向（`cameraRig.ts` 的 `shortestFacingDelta`，跨過0/360度接縫時
要走最短路徑、不能反方向繞一大圈）。

### 正式部署（2026-09-03）

後端與前端都以 Windows 服務運作，只允許本機存取（見下方「只允許本機存取」）：

- **後端** `PlanetService`（NSSM，`C:\PlanetPublish\Planet.Service.exe`，`http://localhost:5050`）。
  改動後端要重新 `dotnet publish backend/src/Planet.Service -c Release -o C:\PlanetPublish`
  再重啟服務。**5050 是刻意跟開發用的 VS Code（固定 5202）分開的**——兩者不會互搶埠，
  可以同時開著跑，改後端不用先停正式服務（2026-09-03 從5202改過來，見CLAUDE.md）。
- **前端** `DCIM_nginx`（Nginx，這台機器上其他 DCIM 服務共用的同一個 Nginx實例，
  `http://localhost:8080`，serve `frontend/dist` 複製到 `C:\DCIM\Nginx\html\planet`）。
  nginx同時把 `/api/`、`/hubs/` 反向代理到後端5050，所以**前端build時用的是nginx的同源網址
  （`http://localhost:8080`），不是後端自己的埠**，換後端網址要重新 `npm run build`。
- 後端 CORS（`Program.cs`）允許 `http://localhost:5173`（dev server，直接跨源打後端，
  CORS是必要的）與 `http://localhost:8080`（這個部署，理論上同源請求不需要CORS，留著當保險）。

**只允許本機存取**：nginx的8080 server block用 `listen 127.0.0.1:8080; listen [::1]:8080;`
而不是 `listen 8080;`（會綁到所有網卡，區網其他機器也連得到）；後端的
`ASPNETCORE_URLS=http://localhost:5050` 則是Kestrel對`localhost`這個host的既有行為，
本來就只綁loopback，不用另外設定。兩者都已用 `Get-NetTCPConnection` 實測確認只綁了
`127.0.0.1`／`::1`。之後要開放區網存取，兩邊都要一起改，只改一邊會變成半套（見CLAUDE.md）。

詳細指令、nginx反向代理的proxy_pass寫法、以及 `listen 80` 曾經因為跟這台機器上IIS的
`DefaultAppPool`衝突而短暫停用又復原的過程，見 [CLAUDE.md](CLAUDE.md)
「正式運作：PlanetService（NSSM）＋ DCIM_nginx」一節。

### 尚未實作

- **真正的河流水文模擬**：`ITileNeighborResolver` 已完成，理論上可以做「水往低處流」的連通模擬，但目前Freshwater的河流/湖泊區別仍只是noise近似的視覺效果
- **多人連線**：SignalR Hub架構留了擴充空間，但目前沒有Group管理、沒有廣播其他玩家位置——單人原型範圍內故意不做
- 測試只涵蓋純邏輯：後端42個（`Planet.Generation.Tests`）＋前端122個（`src/**/*.spec.ts`）都是不碰
  資料庫／不碰瀏覽器的單元測試；Controller、SignalR Hub、Vue 元件與實際渲染結果仍只靠手動驗證
- CORS來源目前是逐一列舉允許的網址（見上方「正式部署」），不是從設定檔讀——換部署網址要改程式碼

見 [docs/architecture.md](docs/architecture.md) 了解各項決策的詳細推導過程。
