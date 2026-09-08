# Planet

星球地圖走動系統。Goldberg 多面體（12個五角形＋約2,002萬個六角形）星球地圖，後端 .NET 10 /
ASP.NET Core（REST + SignalR，SQL Server），前端 Vue 3 + TypeScript + Three.js。

完整架構、決策推導、踩過的坑，見 [README.md](README.md) 與 [docs/architecture.md](docs/architecture.md)——
這份 CLAUDE.md 只放「容易在下一次修改時又踩一次」的高風險慣例，不重複那兩份文件已經寫的內容。

## 建置與測試

```bash
cd backend
dotnet build
dotnet test
```

```bash
cd frontend
npm test          # Vitest，只測 src/map 底下的純邏輯（不需要瀏覽器，node 環境即可）
npm run type-check
```

全部 public/internal 成員都要有 XML 文件註解（各 csproj 已開 `GenerateDocumentationFile`，
`dotnet build` 應該要是 0 警告——CS1591 代表漏寫）。Migrations（`backend/src/Planet.Data/Migrations/`）
是 EF Core 工具產生的骨架，不需要手動補文件註解。

## ⚠️ 鄰居座標的推導只准存在後端一份

跨面／五角形的鄰居座標推導（`TileNeighborResolver`）是全專案最難的一段程式碼，
這個專案已經因為座標 handedness 踩過**三次**坑（見 docs/architecture.md 的「踩過的坑1~3」）。

前端需要知道鄰接關係時（例如「按下方向鍵要走到哪一格」），**一律由後端算好、隨 `TileDto.NeighborIds`
一起送過來**，前端只做查表。`nearby` 端點是在記憶體裡建 `(FaceIndex,Row,Col) → TileId` 字典再映射，
零額外DB查詢（`PatchNeighborLinker`）。

**不要為了「前端自己算比較快」而把這套演算法移植成 TypeScript**——那等於把最容易寫錯、
又最難用肉眼發現錯誤的邏輯複製成兩份，而且兩份會各自漂移。

## 正式運作：PlanetService（NSSM）＋ DCIM_nginx（2026-09-03起）

開發階段已經告一段落，現在**正式運作**是兩個 Windows 服務：

| 服務 | 內容 | 埠 |
|---|---|---|
| `PlanetService`（NSSM） | 後端，`C:\PlanetPublish\Planet.Service.exe`，`ASPNETCORE_ENVIRONMENT=Production` | 5050 |
| `DCIM_nginx`（NSSM） | Nginx，serve `frontend/dist`（複製到 `C:\DCIM\Nginx\html\planet`），這台機器上其他 DCIM 服務共用的同一個 Nginx | 8080 |

`C:\DCIM\Nginx\conf\nginx.conf` 內建的 `listen 80` 那個 server block**曾經被停用過又恢復**：
一開始因為這台機器的 port 80 被 IIS 的 `DefaultAppPool` 佔用（`netsh http show servicestate`
查到的，這台機器上還跑著其他跟這個repo無關的IIS站台）而bind失敗過——nginx只要任何一個
listen socket bind失敗，**整個process就完全不會啟動**，所以曾經整段註解掉port 80讓8080能先動。
後來使用者自己處理好IIS那邊的port 80衝突，把設定改回原本沒註解的樣子，兩個port現在都正常
（`listen 80;` 與 `listen 127.0.0.1:8080; listen [::1]:8080;` 都在用）。**這段歷史記著是因為
如果之後又出現「nginx整個起不來」，第一個該查的就是port 80有沒有又被別的東西搶走。**

**8080只允許本機存取**（2026-09-03，使用者明確要求）：`listen 127.0.0.1:8080; listen [::1]:8080;`，
不是 `listen 8080;`（那樣會綁 `0.0.0.0`，區網其他機器也連得到）。這是在socket層面擋、
不是靠防火牆規則，比較不會因為之後防火牆設定被改掉而露餡。已用
`Get-NetTCPConnection -LocalPort 8080` 實測確認過只綁了 `::1`／`127.0.0.1`兩個loopback位址。
後端（`ASPNETCORE_URLS=http://localhost:5050`）**不需要另外改**——Kestrel對host欄位是
`localhost`的URL會特殊處理成只綁loopback（不是綁0.0.0.0），已經實測驗證過
（`dotnet run --urls "http://localhost:5299"`後查`Get-NetTCPConnection`同樣只有`::1`/`127.0.0.1`
兩筆），這點跟前端的nginx設定是巧合地一致，不是特地去對齊的。**之後如果要改成允許區網存取，
兩邊都要一起改**（nginx的listen位址＋Kestrel的ASPNETCORE_URLS改成`http://0.0.0.0:5050`或
具體的區網IP），只改一邊會變成「網頁進得去但API連不到」或反過來。

**8080的server block同時反向代理後端**（2026-09-03起）：`location /api/` 與 `location /hubs/`
都 `proxy_pass http://127.0.0.1:5050`（PlanetService），瀏覽器因此只需要打8080一個origin，
REST／SignalR都是同源請求。兩個proxy_pass**刻意不在位址後面加路徑或斜線**——nginx這種寫法
會把location比對到的完整原始URI原封不動轉過去，跟後端Controller/Hub實際掛的路徑
（`/api/...`、`/hubs/movement`）一致；加了斜線nginx會把location字首砍掉，後端就收不到
`/api`這一段。`/hubs/` 那個location另外設了`proxy_http_version 1.1`＋`Upgrade`/`Connection`
header（SignalR走WebSocket需要）跟`proxy_read_timeout 3600s`（預設60秒對SignalR的長連線太短，
會被nginx主動切斷）。**前端因此要用同源網址build**（見下面`VITE_API_BASE_URL`），
不是後端自己的埠——這是這次改動最容易漏掉的地方：只加proxy設定、前端沒重新build的話，
瀏覽器還是會直接打5050，不會走這個proxy。CORS允許清單保留`http://localhost:8080`是給
「萬一有人繞過proxy直接打5050」當保險，不是必要條件（同源請求本來就不會觸發CORS）。

改後端要跟著兩步：

1. 重新 publish：`dotnet publish backend/src/Planet.Service -c Release -o C:\PlanetPublish`
   （`-o` 一定要是這個不含單引號的路徑，見下面`install-service.ps1`同一個註解）。
   不會動到 `C:\PlanetPublish\appsettings.Production.json`（來源沒有同名檔案）。
2. 用系統管理員權限重啟服務：`C:\DCIM\nssm.exe restart PlanetService`
   （這步需要admin，這個環境的一般shell做不到，要請使用者自己執行）。

改前端要跟著：build指令前**要手動設定**這兩個環境變數（不是已經存在系統環境裡、不設也會生效
的東西——只是這個shell的一次性變數，每次重新開一個shell/每次要重打的話都要重設）。
**注意這兩個值是nginx同源的網址（8080），不是後端自己聽的埠（5050）**——前端是透過nginx
的proxy才碰得到後端，直接寫後端的埠會繞過proxy：
```powershell
$env:VITE_API_BASE_URL = "http://localhost:8080"
$env:VITE_HUB_URL = "http://localhost:8080/hubs/movement"
npm run build
```
→ 複製 `frontend/dist/*` 到 `C:\DCIM\Nginx\html\planet\` → 不需要重啟nginx
（靜態檔案，nginx直接讀新內容，除非改了`nginx.conf`本身）。

CORS（`Program.cs` 的 `AddCors`）目前允許兩個來源：`http://localhost:5173`（dev server，
沒有nginx幫忙proxy，直接跨源打後端，CORS是必要的）與 `http://localhost:8080`（這個部署，
理論上同源請求不需要CORS，留著只是保險）。之後如果前端換了部署網域/埠，這裡才要跟著加。

### 開發時仍然可以照舊：後端跑在 VS Code

正式站（`PlanetService`）改到5050埠之後，跟開發用的VS Code（固定5202，見下表）**不會再互搶埠**，
可以兩個同時開著跑，不用像以前那樣「要改後端就得先停掉正式服務」：

- 需要在不打擾正式服務的情況下驗證後端改動時，可以自己在**另一個埠**跑一份臨時實例
  （`dotnet run --project src/Planet.Service --urls "http://localhost:5299"`），驗完記得停掉。
- 開發模式是 `Development`，機密來自 **user-secrets**（不是部署目錄的 `appsettings.Production.json`），
  兩者都已設定好，見下一節。
- `Development` 才會掛載 Scalar API 文件：`http://localhost:5202/scalar/v1`。

兩條啟動路徑與其陷阱：

| 方式 | 埠 | 說明 |
|---|---|---|
| VS Code 工作 `backend: run Planet.Service`（`dotnet run`） | 5202 | 讀 `Properties/launchSettings.json` 的 `applicationUrl` |
| VS Code 偵錯 F5 `Planet.Service (.NET)` | 5202 | **直接啟動 DLL，不會讀 launchSettings**，靠 `.vscode/launch.json` 裡明寫的 `ASPNETCORE_URLS` 才會綁對埠 |

`launch.json` 的 `program` 路徑必須是 `bin/Debug/net10.0/**win-x64**/Planet.Service.dll`——
`Planet.Service.csproj` 設了 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`＋`<SelfContained>true</SelfContained>`
（自封裝部署用），輸出就會多一層 RID 目錄。少了那一段 F5 會直接跳
「program ... does not exist」，而且訊息看起來像沒建置，實際上是路徑錯（2026-09-01 踩過）。
順帶驗證過：自封裝的 DLL 一樣可以用 `dotnet Planet.Service.dll` 啟動，不必改成指向 `.exe`。

前端把後端位址寫死成 `http://localhost:5202`（`apiClient.ts` 的 `API_BASE_URL`，可用 `VITE_API_BASE_URL` 覆寫），
所以後端一定要在 5202，否則畫面會卡在「連線中」。

## ⚠️ 地質模擬（`Planet.Simulation`）：只看統計量一定會被騙

規劃與完整的踩坑紀錄見 [docs/geological-simulation-plan.md](docs/geological-simulation-plan.md)。
這裡只放最容易再踩一次的三條。

**1. 改完模型一定要把貼圖畫出來看，不能只看陸地比例／厚度分布。**
這個階段每一個坑的症狀都是「數字完全正常、畫面是垃圾」：曾經有「陸地5%、厚度是漂亮的雙峰」
這種毫無異狀的統計量，畫出來卻是滿天散落的斑點與弧形細絲。驗證方式是打
`GET /api/simulation/texture?field=ElevationWithPlateBoundaries` 存成 PNG 來看。

**2. 地殼的性質掛在**標記**上，不是掛在格子上（marker-in-cell）。**
不要「為了簡單」改回「累積位移超過一格就把整格的資料搬過去」——球面剛體旋轉的位移在板塊上
是**連續變化**的（正比於 sin(離歐拉極的角距)），而整格跳躍是量化的，兩者一定會在
「跳了」與「還沒跳」的區帶之間撕出裂縫，然後那些裂縫被當成張裂/聚合，憑空生出中洋脊與隱沒帶。
同理，**板塊歸屬是由旋轉中的 Voronoi 種子幾何決定的**，不要改成跟著標記走
（取樣錯配會累積成胡椒鹽，94%的格子都會變成「板塊邊界」）。

**判準是「這個量是不是地殼柱的一部分」，不是「以前有沒有踩過坑」。**
2026-09-07 就是漏掉這一步：珊瑚礁的 `OrganicThickness` 掛在格子上、又沒有任何移除機制，
於是變成「這一格歷史上每一次剛好淺過」的化石紀錄——海岸線一直在移動，每一格遲早都輪到，
礁體因此無上限累積（300 Myr 後最厚10,671公尺）。**厚度分布本身完全看不出異常**，
露餡的是交叉檢查：那時真正還在透光帶裡的礁體只剩12格，1,386格躺在深海、1,255格在水面上。
現在礁灰岩存在 `_markerOrganic`。沉積物（`SedimentThickness`）也還掛在格子上，
目前只因為侵蝕會把它撿回來、有去路才沒失控，階段4做水系時要一併檢查。

而且**掛到標記上還不夠，每個累加的量都要有去路**。礁體另外補了溶蝕
（露出水面的碳酸鈣被雨水溶掉），少了它只是從「每一格都會累積」變成
「被抬起來的那些格會累積」，一樣沒有上限。

**3. 氣候是「解穩態」不是「往前推進」。**
大氣把水氣送到內陸只要幾天，而一步是5萬年。`Climate.Solve` 重新解出當下地形對應的穩態，
不是把水氣往前推一格。改成時間積分的話，內陸永遠是沙漠，而且不會有任何錯誤訊息。
反過來說它**不需要每步都解**（地形5萬年幾乎沒變），目前每200萬年一次。

**3.4 板塊是「岩石圈」，不是「地殼」。**
海洋地殼（2,900）比地函（3,300）**輕**——光靠地殼，洋殼永遠不會隱沒。
會沉下去的是整個岩石圈：冷卻後增厚的**地函部分**（`Isostasy.LithosphereBuoyancyMeters`）
把上面較輕的地殼一起拖下去。所以**判斷誰會隱沒要用 `SubductionBuoyancyOf`，
不是 `SurfaceLiftOf`**——地表高度只會單調變深，表達不出「年輕洋殼抗拒隱沒」那個符號翻轉。
兩者混用是 2026-09-07 修掉的概念錯誤。

那三個岩石圈常數（厚度成長率、上限、密度超出量）**彼此耦合**，合起來決定了浮力翻轉的年齡
（約18百萬年，地球觀測是10~30）。`LithosphereTests` 會抓到單獨調動其中任何一個。

**3.45 `record` 一律用 `with` 複製，不要用位置參數 `new Plate(...)` 重建。**
2026-09-08 踩過：`PlateSet.RemoveNetRotation` 用 `new Plate(p, ..., plates[p].DensityAnomalyKgPerM3)`
重建，只手動帶了密度異常。後來加了 `IsOceanicOnly`，那個旗標**每次扣淨旋轉都被預設值蓋掉**。

**失效方式特別陰險**：因為密度異常有被手動帶過去，症狀是「設定看起來只生效一半」
（`anomaly=250` 但 `oceanicOnly=False`），第一反應會是懷疑自己的編輯沒寫對、或組件過期，
而不是懷疑幾百行外的另一個方法。而且**那行上面就有註解在警告同一個風險**——
註解只能提醒讀到它的人，擋不住在別的檔案加欄位的人。`with` 才擋得住。

**3.5 「參數改了結果不動」＝這條路沒接上，不是這個參數不重要。**
已經踩過三次：`oceanDepthMeters` 曾經是死的（3,000調到5,000，海面佔比一個百分點都沒動）、
`FissureVolcanism.VentStrengthKmPerMyr` 是死的（強度掃3.2倍、甚至整個關成0，重板塊厚度
都停在25公里）、聚合判定從年齡改成浮力也量不出差別。正確的反應是**先確認這條路有沒有在跑**
（印出它這一步實際做了多少），不是繼續調數值——不然會像那個噴發強度一樣被「校準」三次，
而三次都是在調一個沒接線的旋鈕。

**3.6 平均值會騙人，分布不會；分布也會騙人，交叉檢查不會。**
重板塊「平均25公里」看不出異常——要跟一般海洋的分布並排，看到「一般海洋在25公里以上
一格都沒有」（過門檻就被改類別、離開統計群），才知道那是分類規則造成的假象。
珊瑚礁的厚度分布也完全正常，是「礁體所在的水深」才露餡。
**追到最後真正解決問題的，往往是印出某一個具體個體的完整軌跡**，不是再多一個統計量。

**4. 凡是「幾格」的參數都要改寫成物理量。**
模擬跑在 n=150（格距14.9公里），但測試多半跑在 n=24（格距93公里）——差了6倍。
寫死格數的參數在兩邊代表完全不同的距離，症狀是**粗網格的測試全過、正式解析度卻壞掉**
（水氣輸送就是這樣：寫死60次迭代在 n=24 是5,580公里、在 n=150 只剩894公里，
於是一半的陸地降水掛零）。要寫成距離再除以 `AverageCellAreaSquareMeters` 換算回來。

2026-09-07 又踩到更嚴重的一次：`FissureVolcanism` 的 `VentSpacing = 40`（每40格一個噴發口）
在 n=150 抽出13座、在 n=24 抽出 **0** 座——符合條件的格子只有47個，1/40 的期望值1.2，
而雜湊由格子索引決定、不會每步重擲，所以是**永遠**0。
**整個裂縫火山機制在所有測試裡從來沒有執行過一次**，守它的測試卻一直是綠的
（把機制整個關掉，重板塊上照樣有232座島——那些島是聚合處的標記堆疊頂出來的）。
現在改成面積密度 `AreaPerVentSquareKm`。
同理，**校準過的常數要註明是在哪個解析度上量的**（`DefaultOceanDepthMeters` 就是 n=150）。

另外，`SimulationEngine` 與其下的模型**都不是執行緒安全的**（大量重複使用的暫存緩衝），
`SimulationService` 是 Singleton，所有取用都必須在同一把鎖裡。模擬大量使用 `Parallel.For`，
**平行迴圈裡不可以一邊寫自己一邊讀鄰居的同一個欄位**——要先拍快照（`ApplyConvergence` 就是這樣做的）。
這類競爭不會拋例外，只會讓「同種子跑出不同的星球」，而且因為執行緒分割在同一台機器上通常固定，
**失敗位置每次都一樣、看起來完全不像競爭**。守門的測試是 `Step_SameSeedProducesIdenticalEvolution`。

## ⚠️ 機密與管理端點

連線字串／管理金鑰**不要寫回 `appsettings.json`**（會進版控）。用 user-secrets（開發）或
部署目錄的 `appsettings.Production.json`／環境變數（服務），見 README「設定機密」。
寫文件時也不要把明文密碼貼進 `README.md`／`docs/`——那些同樣會進版控。

`/api/admin/*` 需要 `X-Admin-Key`。**沒設定金鑰時這些端點回 503（停用），絕不能改成「沒設定就不檢查」**——
漏設定的後果必須是功能用不了，不是大門敞開（`POST /api/admin/regenerate` 會不可復原地清空2,000萬列）。

另外注意：`PlanetScene` 的移動監聽掛在 `window` 上，新增任何輸入框時要確認它有跳過
INPUT/TEXTAREA/SELECT，否則在輸入框打字會把玩家一起走掉（已踩過）。

同一個坑的變形：按鍵設定面板（`KeyBindingPanel.vue`）等待玩家按鍵時用的是 `<button>`，
不在那個白名單裡。它靠**兩道**保護：capture 階段的 `stopPropagation`，加上
`keyBindings.isCapturing` 旗標。**兩道都要留著**——capture 能擋住是因為 window 的 capture
監聽比 bubble 監聽早跑，但事件如果是直接 `window.dispatchEvent` 派送的，順序就變成註冊順序、
保護會安靜失效（2026-09-01 實測確認過這個失效情境，只有旗標那道擋得住）。

## `PlanetScene.vue` 拆成了模組，改動前先找對檔案

`frontend/src/scene/PlanetScene.vue` 只剩組裝邏輯（2026-09-02拆分，見 docs/architecture.md
「PlanetScene 的模組拆分」）。實際邏輯在同目錄底下：

| 要改的東西 | 去這個檔案 |
|---|---|
| 鏡頭定位、俯視角、轉向動畫 | `cameraRig.ts` |
| 地塊載入、合併mesh、鄰居外框、時段換色 | `patchManager.ts` |
| 星球本體（填縫球體、遠景貼圖、日夜色調） | `planetBase.ts` |
| 地質演化視角（模擬貼圖球體） | `scene/PlanetEvolution.vue`＋`ui/EvolutionControls.vue`＋`stores/simulation.ts` |
| 玩家位置、方向鍵對應、移動、伺服器驗證 | `playerMovement.ts` |
| HUD的鏡頭數字（指北針/傾斜/距離/高度/縮放%） | `hudStats.ts` |
| 鍵盤/滑鼠輸入 | `inputHandlers.ts` |

三個視角（走動／總覽／地質演化）各自是**完全獨立**的 Three.js 場景與 render loop，
用 `v-if` 互相切換。三者的 `dispose` 都要呼叫 `forceContextLoss()`——`dispose()` 不會歸還
WebGL context，切換幾十次就會超過瀏覽器的 context 上限。

**不要把邏輯搬回 `PlanetScene.vue`**——這個檔案曾經953行、混雜六種不同關注點，
是全前端bug密度最高的地方（見 docs/architecture.md「踩過的坑1~3」）。改動時只加到
對應的模組，`.vue` 只保留 `onMounted` 裡的建構順序與 `animate()` 的逐幀orchestration。

## 移動方向是「相對面向」的，不要寫死字母或地理方位

玩家有面向（`gameState.facingDeg`，六角格所以只有 0/60/120/180/240/300），鏡頭會轉成
「面向朝畫面上方」，六個移動插槽也依面向指派（`assignNeighborsToSlots` 的 `facingDeg` 參數）。
兩件事必須一起改，只做一半就會變成「畫面轉了但按右還是往原本的右走」。

因此：

- `bearing.ts` 的插槽叫 `upLeft`/`right`/… 而**不是** `w`/`d`——按鍵可以自訂
  （`stores/keyBindings.ts`），把插槽叫做「w」在玩家改綁成 i 之後就變成謊話。
- 也不要改回用地理方位（NW/NE…）命名：面向不是北的時候，左上那一格根本不是西北。
- `keydown` 裡**不要寫死字母**，一律查 `keyBindings.actionFor(key)`。寫死的症狀是
  「改了設定但沒反應」，很難查。
- 面向 0 度時整套行為跟加入轉向功能之前完全相同，這是刻意的（不能有退化）。

## ⚠️ 星球的尺寸與地形參數只有一個權威來源

前端**不要**再寫死星球半徑、Tile邊長、總覽貼圖寬高之類的值——這些後端是從細分頻率推導的，
一律打 `GET /api/planet`（`stores/planetInfo.ts`，由 `PlanetLoadingGate` 在畫面顯示前載入）。
2026-08-31 就是因為前端寫死 1,851,000 而實際是 1,852,259.92，**安靜地差了1,260公尺**才被發現。

同理，需要「產生目前這批地塊的地形參數」時（例如星球總覽貼圖取樣），要用
`PlanetMetadataService.GetAppliedTerrainOptionsAsync()`，**不要用 `TerrainOptionsStore.Current`**——
後者是「下次重新生成要用的」，拿它去畫現況會畫出一顆資料庫裡不存在的星球。

地形顏色的權威來源是資料庫 `TerrainStyles` 表。前端刻意只保留單一中性 fallback 色，
**不要再加回一份完整調色盤**：那種複本只在後端掛掉時才用得到，所以漂移了也看不出來。

## ⚠️ `backend/deploy/` 腳本編碼慣例（重要）

`.bat` 與 `.ps1` 正確的編碼**不一樣**，而且都不是這個環境 Write/Edit 工具的預設輸出
（UTF-8 無 BOM）能直接安全使用的：

| 檔案類型 | 正確編碼 | 用錯的症狀 | 這個專案已實際發生過 |
|---|---|---|---|
| `.ps1` | **UTF-8 with BOM** | Windows PowerShell 5.1讀取沒有BOM的檔案一律當系統ANSI codepage解碼，中文變亂碼，而且亂碼裡常常混進引號/反引號，**直接造成語法錯誤、腳本完全跑不起來**（不是「能跑但顯示亂碼」而已） | 是（2026-08-30，`install-service.ps1`加中文註解後，使用者在系統管理員PowerShell執行直接報`ParserError: AmpersandNotAllowed`，因為亂碼字元把字串/引號配對弄亂了） |
| `.bat` | **UTF-8 without BOM**，檔頭加一行 `chcp 65001 >nul` | 中文`echo`亂碼；若不小心存成有BOM，某些cmd.exe版本連第一行`@echo off`都解析失敗 | 是（見下面「修改時的地雷」，不是編碼設定錯，是修改過程中用錯工具把檔案讀壞又寫回去） |

**不要憑經驗假設某台機器的codepage是什麼，要實際查**（這個結論在不同locale/設定的機器上可能不一樣）：

```powershell
chcp                                                                    # 目前作用中的codepage
Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage -Name OEMCP,ACP  # 登錄檔的legacy ANSI設定
```

已驗證：這台機器（Windows 11、系統語言 zh-TW）`chcp`（含全新開的`cmd.exe`）預設就是65001，
代表已經開了「Beta: 全球語言支援使用Unicode UTF-8」，但登錄檔的`OEMCP`/`ACP`仍是950（Big5）——
兩者不一致是正常的，`chcp 65001`看的是前者。`.bat`檔頭仍保留顯式`chcp 65001`，這樣即使換到
沒開那個Beta設定的機器上也能正常顯示中文。

`.ps1` 寫完後務必用
`[System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)`
驗一次語法（不用真的執行也能驗出編碼造成的錯誤，`$errors.Count`應該是0）。

### 修改這兩類檔案時的地雷（已實際踩過，不只是理論風險）

- **Write/Edit工具**：預設輸出UTF-8無BOM。`.ps1`寫完/改完後如果檔案還沒有BOM，要額外用
  `Set-Content -Path $path -Value $content -Encoding UTF8`（Windows PowerShell 5.1下這樣寫就會補上BOM）
  重新存一次。已經有BOM的`.ps1`之後可以繼續正常用Edit工具修改，Edit不會弄丟既有的BOM。
- **PowerShell的`Get-Content`會踩到同一類雷，而且更陰險，因為它會把壞掉的內容寫回去覆蓋原檔**：
  `Get-Content -Raw`（不指定`-Encoding`）讀一個沒有BOM的UTF-8檔案（例如`.bat`）時，Windows
  PowerShell 5.1一樣會用系統ANSI codepage解碼、把中文讀成亂碼字串——如果接著把這個已經壞掉的字串
  做`-replace`之類加工後再寫回同一個檔案，就等於用亂碼覆蓋掉原本正確的內容，比「沒發現問題」更糟，
  而且是不可逆的（原始正確內容已經不在磁碟上了）。
  2026-08-30實際發生過：想幫兩支`.bat`統一收斂成CRLF換行，用`Get-Content -Raw`讀近來時中文就已經
  變亂碼、多行的`::`註解還被吃掉合併成一行，寫回去後檔案整個壞掉，最後是用Write工具依對話紀錄裡
  還留著的正確內容整份覆寫回去才復原。
  **要嘛明確指定 `-Encoding UTF8`（`.bat`）或讓它照BOM自動偵測（`.ps1`，通常不用特別指定），
  要嘹乾脆不要用PowerShell文字處理去讀取＋加工這些檔案的內容——直接用Write工具整份覆寫最安全。**
- 判斷`.ps1`目前是不是正確編碼：檔案前3個bytes是不是 `EF BB BF`
  （`Get-Content -Encoding Byte -TotalCount 3` 或直接用 `xxd`/`Format-Hex`）。

## Controller Action 的 XML 文件（OpenAPI / Scalar）

`Planet.Service` 用 ASP.NET Core 內建的 OpenAPI 產生器（`AddOpenApi()` + `Scalar.AspNetCore`，
`/scalar/v1`，只在 Development 環境掛載）。.NET 10 會把 XML 文件註解自動轉成 OpenAPI 內容，
但轉換規則有幾個不直覺的地方——以下3條已經在這個專案的 `TilesController` 上實際加
`[ProducesResponseType]` 屬性、重新build、打 `/openapi/v1.json` 驗證過（不是憑印象/抄別的專案）：

1. `<response code="...">` **只會替已經用 `[ProducesResponseType]` 宣告過的狀態碼補描述**，
   沒有對應屬性的話會被靜默丟棄，不會憑空新增一個狀態碼。
2. `<returns>` **只在這個action的response只有一種時才會被採用**（例如只有預設的200）。
   一旦掛了2個以上 `[ProducesResponseType]`（例如200+404），`<returns>` 就會被忽略——
   這時候Scalar上實際顯示的說明要寫在對應狀態碼的 `<response>` 裡才看得到。
   實務上兩個都寫：`<returns>` 給IDE的hover提示用（不受OpenAPI產生規則影響），
   `<response code="200">` 才是Scalar頁面上真正顯示的內容，兩邊敘述要一致。
3. 同一個 `<summary>` 如果不小心寫兩次，只有第一段生效，第二段被靜默丟棄，編譯不會警告。

目前 `TilesController` 的3個action都只回200/404（沒有auth、沒有rate limiting、沒有檔案下載、
沒有`[FromBody]`複雜驗證——這個專案目前完全沒有認證機制），照上面3條規則正確標
`[ProducesResponseType]` + `<returns>`/`<response>` 就夠了。**如果之後幫這個API加了認證、
rate limiting、檔案下載之類的功能，要幫對應的401/403/429/415等狀態碼補文件時，
先重複上面的驗證流程（實際加attribute、build、打openapi端點確認），不要直接假設這幾個功能在
別的專案上量出來的規則可以照搬**——那些規則往往綁定在該專案的auth filter／middleware
的實際實作細節上（例如「401是不是有body」取決於那個專案的authentication handler怎麼寫的），
換一個專案的實作就可能不成立。
