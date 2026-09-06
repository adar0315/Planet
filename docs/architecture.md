# 架構設計筆記

## 星球規模取捨

原始規格「地球大小＋六角形邊長1公里＋約2000萬格」三者互相矛盾：地球（半徑約6371公里）＋1公里邊長，用 Goldberg多面體公式（總格數=10n²+2）反推，實際約產生1.96億格，而非2000萬格。

決定：保留「邊長1公里」與「總格數約2000萬」，星球縮小。做法：固定細分頻率 n=1415（總格數=10×1415²+2=20,022,252，其中12個五角形、20,022,240個六角形），再從這個頻率反推星球半徑，使六角形邊長維持約1公里：

- 在單位球面上取樣面中央的相鄰格點，量測其弦長，得到「單位球面上，相鄰Tile中心之間的距離」
- **注意**：這量到的是「中心距離」，不是六邊形本身的邊長。正六邊形的「中心到相鄰中心距離」＝邊長×√3（把六邊形拆成6個以邊長為外接半徑的正三角形即可推出），兩者相差約73%，一開始曾誤把中心距離當邊長直接換算，算出來的星球小了將近一半（約1069公里），後來才抓出來修正
- 星球半徑（公尺）= (目標邊長1000公尺 × √3) ÷ 量到的單位球中心距離
- 結果約 1,851 公里（小於2100公里的上限），介於冥王星與水星之間的大小

見 [GoldbergPlanetGenerator.cs](../backend/src/Planet.Generation/GoldbergPlanetGenerator.cs) 的 `CenterDistanceToEdgeLengthRatio` 常數與建構子內的換算邏輯。

見 [GoldbergPlanetGenerator.cs](../backend/src/Planet.Generation/GoldbergPlanetGenerator.cs) 的 `EstimateUnitSphereEdgeLength`／`PlanetRadiusMeters`。

## 測地線細分演算法

- 用正二十面體的12頂點20面（標準座標，來源：Andreas Kahler icosphere），先旋轉對齊讓其中一個頂點＝北極、其對蹠頂點自動對齊南極（正二十面體是中心對稱體，天然滿足「北極放一個五角形」＋南北極都需要五角形冰帽的需求）。
- 每個三角面用整數重心座標 (i,j,k)，k=n-i-j，格點 P(i,j)=normalize(k·A+i·B+j·C)，n=SubdivisionFrequency。
- 面內部格點（i,j,k皆不為0）保證只屬於該面，直接配發新TileId，不需查表。
- 邊界格點（i、j、k其中之一為0，即落在面與面共用的邊或頂點上）用去重字典（依量化後的3D座標為鍵）避免重複建立——同一個物理點從相鄰面算出的浮點數結果理論上是bit-for-bit相同（見程式內註解的推導），去重字典只需容納約30(n-1)+12筆（遠小於2000萬總格數），效能與記憶體都很輕量。
- 兩個以上零座標的格點（角點）＝原二十面體頂點＝五角形；恰好一個零座標＝邊上的六角形；三個都不為0＝面內部六角形。

## 地圖座標與鄰居計算

- Tile 以正二十面體的 `FaceIndex`（0-19）+ 面內細分座標 `Row/Col` 定位，不額外存經緯度以外的鄰接關係表。
- 六角形有6個鄰居、五角形（12個，含北極一個）有5個，鄰居由 `ITileNeighborResolver` 依 `FaceIndex/Row/Col` 即時算出，跨面時需處理二十面體面與面之間的座標轉換規則。
- 這個決定是為了避免 2000萬 Tile 對應上億筆鄰接關係資料造成的儲存與寫入成本。

## 地圖生成與寫入

- 服務啟動時檢查資料庫是否已有 Tile 資料（`ITileRepository.HasAnyTilesAsync`），沒有才執行生成並寫入。
- 寫入用 `SqlBulkCopy`（非 EF Core 逐筆/分批 SaveChanges），搭配自製的 [TileDataReader](../backend/src/Planet.Data/TileDataReader.cs)（實作 `IDataReader`）把 `IEnumerable<Tile>` 直接串流進 `SqlBulkCopy.WriteToServerAsync`，不會像 `DataTable` 那樣把2000萬筆全部先塞進記憶體。本機測試（SQL Server 2025 Developer Edition）約1~2分鐘可以寫完全部2000萬筆。
- Schema 用 EF Core Migrations 管理（`dotnet ef migrations add` / `dotnet ef database update`，見 [deploy/sql/README.md](../backend/deploy/sql/README.md)），`TileDataReader` 的欄位需要跟 migration 產生的資料表欄位對得上（目前是 TileId/Shape/FaceIndex/Row/Col/Latitude/Longitude/Terrain/Elevation，enum 用EF Core預設的int映射）。
- `PlanetSeeder` 是 `IHostedService`，但它本身註冊成 Singleton，而 `ITileRepository` 是 Scoped（依賴 Scoped 的 `PlanetDbContext`）——不能直接建構子注入，要透過 `IServiceScopeFactory` 自己開一個 scope 才能解析，否則 DI 驗證會直接在啟動時丟例外。
- 地形（陸地/海洋/山脈/河流等）採程序化生成：極區依緯度強制設為 `PolarIce`，其餘用雜訊演算法（如 Simplex noise）決定兩大洲與海洋分布，陸地內再細分平原/森林/叢林/高山等。不是手繪固定地形。

## 時間系統與日夜光照

- 遊戲時鐘（`stores/gameState.ts` 的 `gameTime`）從 2050/01/01 08:00:00 UTC 起算，三種操作模式（`mode`）決定時間怎麼走：`observe`＝靜止、`stepped`＝每移動一格或按S+1小時、`realtime`＝每3秒自動+1小時（跟移動無關）。
- 日夜光照（[sunPosition.ts](../frontend/src/map/sunPosition.ts)）：依 `gameTime` 算太陽直射點（subsolar point）——
  - 太陽赤緯（南北向季節擺動）：簡化公式 `-23.44° × cos(2π/365 × (dayOfYear+10))`，忽略均時差，誤差約在1°內，對遊戲用途夠用
  - 太陽直射經度：UTC 12:00時直射本初子午線，每小時往西移15°（地球自轉）
  - 換算成單位方向向量後，把 `DirectionalLight` 放在「星球中心沿這個方向，很遠的地方」（只有方向重要，距離只是確保光線近似平行），在 `PlanetScene.vue` 的 animate loop裡每幀更新
  - 已實測 UTC 08:00→14:00→20:00 三個時間點，畫面亮度確實隨太陽仰角正確變化（先變亮後變暗）

## 通訊

- REST API：地圖區塊查詢（`GET /api/tiles/nearby`）、單一 Tile 查詢，供前端初始載入與視野更新用。
- SignalR（`/hubs/movement`）：玩家即時移動同步。目前為單人原型，Hub 先建立基礎架構，之後若擴充多人可加入 Group／其他玩家位置廣播。

## 地形系統（2026-08-29 全面改版）

### 海拔分級

- `Tile.ElevationLevel`：整數，範圍 -500~+500，每級5公尺（實際公尺 = ElevationLevel × 5），對應 ±2500公尺範圍。正值＝海平面以上，負值＝海平面以下（海床深度）。
- `Tile.SeaLevel`：跟ElevationLevel同單位的整數，每格獨立欄位，目前全部統一為0。存成per-tile是為了以後如果要做「局部海平面變動」不用改schema；判斷「是否淹沒在水下」用 `ElevationLevel < SeaLevel`。
- `Tile.HeightCategory`：地表覆蓋物（植被/水）的高度或深度細分等級，意義依 `Terrain` 而定，是**跟ElevationLevel完全獨立的屬性**——同一格可以是「海拔100級（500公尺）的高地，上面長著腰部高的草」。沒有細分等級的地形（Mud/Loess/Rock等）固定為0。

### 地形分類（TerrainType，18種）

原本8種（Ocean/Plains/Forest/Jungle/Mountain/River/Lake/PolarIce）擴充成18種，並補上「地表覆蓋物高度/深度」的細分：

| Terrain | HeightCategory 意義 |
|---|---|
| Grassland 草地 | 0~4 = 腳板/小腿肚/腰部/肩膀/高過頭 |
| Shrubland 灌木叢 | 0~3 = 小腿肚/腰部/肩膀/高過頭 |
| Forest 林地 | 0~7 = (高度0~3：人高/一層樓/二層樓/三層樓) × 2 + (0疏/1密) |
| Freshwater 淡水 | 0~6 = 小腿肚/腰部/肩膀/過一人/過二人/過三人/過十人深 |
| Seawater 海水 | 0~6，同淡水 |
| Mud/Loess/Rock/Sand/SnowField/Glacier/Swamp/SaltFlat/VolcanicRock/CoralReef/Cliff/Tundra/Gravel | 固定0（無細分） |

舊分類轉換：Ocean→Seawater；Plains→Grassland；Jungle→Forest（高濕度→密林）；Mountain→Rock（高海拔）／視noise可能是Cliff或VolcanicRock；PolarIce→Glacier（極區，緯度≥82°）或SnowField（雪線以上，含高海拔雪線）；River/Lake→Freshwater（差別只在HeightCategory深淺，河較淺、湖較深，不再是不同的Terrain）。

### 生成流程（TerrainGenerator）

用3D Simplex noise（自實作，見 `SimplexNoise3D.cs`）直接在單位球方向向量上取樣，避免2D noise套經緯度時在極區/換日線出現的接縫或扭曲。共8組獨立noise：海岸線、海拔、河流、湖泊、濕度（決定植被/沙漠/沼澤）、岩性（決定礫灘/懸崖）、稀有特徵（火山岩/鹽鹼地/珊瑚礁的機率roll）、地表覆蓋高度。

1. **陸地/海洋**：沿用「兩個大陸中心＋noise海岸線」的做法（大陸中心依seed隨機選在中緯度、彼此角距離>110°，falloff+noise決定不規則海岸線）。
2. **海洋**：依緯度/深度決定 Seawater／Glacier（緯度≥82°）／CoralReef（低緯度淺海，稀有noise roll）。
3. **陸地**：先算ElevationLevel（noise決定），依序判斷：Glacier（極區）→Freshwater（河流/湖泊noise，沿用原本的窄帶/湖泊noise邏輯）→SnowField（雪線：緯度≥70°或海拔≥400級）→高海拔（≥320級）分Rock／Cliff（岩性noise高）／VolcanicRock（稀有roll）→依濕度noise分沙漠(Sand，可能是SaltFlat)／沼澤(Swamp，低地+高濕度)→苔原(Tundra，55~70°)→礫灘(Gravel，低地+高岩性)→泥地(Mud)／黃土地(Loess)→依濕度分林地/灌木叢/草原，林地的疏密由濕度決定、高度由地表覆蓋noise決定。

**踩過的坑（低地地形門檻）**：4個octave疊加的海拔FBM noise不是均勻分布在0~500，而是像常態分布一樣集中在中間值附近（實測seed=42時中位數約220~240級）。「低地」判斷（Swamp/SaltFlat/Gravel）原本設 `LowLandMaxLevel=80`（假設0~500均勻分布下的「後1/6」），但實際上<=80級只占陸地約0.2%，導致整個2000萬格生成後Swamp/SaltFlat剛好0筆——不是完全不可能，是機率低到2000萬次抽樣也没抽到。用小範圍取樣（1°×1°經緯度網格）畫出實際海拔分布histogram校準後，改成 `LowLandMaxLevel=190`（約佔陸地22~30%）、Mud另外用 `MudMaxElevationLevel=260`（約55%），重新生成後18種地形全部都有出現。教訓：地形noise門檻不能只憑理論上的數值範圍去假設機率，要實際取樣校準。
4. `GoldbergPlanetGenerator` 內部直接建立自己的 `TerrainGenerator`（固定種子42，非DI注入），與Tile的Lat/Lon一起算出後直接寫入 `Tile.Terrain`／`Tile.HeightCategory`／`Tile.ElevationLevel`。

### 前端地形起伏渲染

- [tileMeshBuilder.ts](../frontend/src/map/tileMeshBuilder.ts)：每個Tile的中心點依 `elevationLevel` 沿當地法向量做徑向偏移，做出視覺上的地形起伏（海拔越高越突出、海水越深越凹陷）。
- 實際比例（±2500公尺 vs 星球半徑1851公里）起伏small到肉眼看不出來，所以垂直方向乘了 `ELEVATION_EXAGGERATION`（目前8倍）誇張顯示，純視覺效果，不影響底層資料。
- 相鄰但海拔差異大的格子之間會有高低差斷層。**2026-08-31起改用共用角落＋側面牆**，
  斷層處會補上垂直的牆面而不再是看得穿的裂縫，見下方「格子改成真正密合的蜂窩」。

## 跨面鄰居計算（ITileNeighborResolver）

用「頂點權重」的角度思考，而不是直接對(i,j)做符號運算（後者在邊界/角點會有多個看似合理但其實不成立的候選公式，很容易寫錯）。核心概念：

- 面F的一個格點，可以表示成「在F的3個頂點(ia,ib,ic)上各自的權重(wA,wB,wC)」，wA+wB+wC=n。
- **面內部鄰居**：3個頂點兩兩一組，各自+1/-1轉移權重，共6種嘗試；哪個方向會讓某個權重變負，就代表那個方向其實跨出了目前這個面。
- **跨面鄰居**：對目前權重為0的頂點（=這個格點所在的那條邊），去查「這條邊的另一側是哪個面」，新面的第三個頂點（原本沒份的那個）權重設為1，兩個共用頂點裡的其中一個再各自嘗試-1（兩種組合各為一個候選）。這個公式是逐步用「總權重必須=n」＋「新面第三頂點權重必須=1（代表往新面走了剛好一步）」反推出來的，不是直接沿用面內部轉移的公式（兩者長得不一樣，這點容易搞混、要注意）。
- **五角形（原二十面體頂點）**：上述公式對角點只能推出4個鄰居（少1個），因為valence-5節點在「只跨1個面」的推導下天生會漏掉正對面那個鄰居（需要跨2層面才到得了）。所以五角形改用完全獨立、更簡單可靠的方法：直接查二十面體「這個頂點跟哪5個頂點相鄰」（`IcosahedronGeometry.VertexAdjacency`），對每個相鄰頂點直接算出「權重n-1/1」的邊界點。
- **正規化（canonicalize）**：任何算出來的候選鄰居，最終都要換算成「用哪個面表示、面索引最小的那個」——面內部的點只有唯一面能表示（沒有歧義）；邊界點恰有2個候選面；角點最多5個候選面；一律取面索引最小的，確保跟 `GoldbergPlanetGenerator` 生成、存進DB時的規則（掃描面0..19、第一個遇到的算數）完全一致，這樣算出來的座標才查得到DB裡真正存在的那筆Tile。

驗證：[TileNeighborResolverTests.cs](../backend/tests/Planet.Generation.Tests/TileNeighborResolverTests.cs) 用「鄰居關係必須對稱」（A的鄰居列表有B，B的鄰居列表也必須有A）作為主要正確性檢查——這是最能抓出公式錯誤的測試，因為手推這類跨面座標公式很容易寫出「看起來對、實際上不對稱」的結果。

## PlanetScene 的模組拆分（2026-09-02）

`PlanetScene.vue` 原本是一個953行的單一檔案，場景初始化、鏡頭定位、地塊載入、玩家移動、
鍵盤/滑鼠輸入、HUD數字計算全部混在一起。拆成 `frontend/src/scene/` 底下幾個各自獨立的
`.ts` 模組，`.vue` 檔案只留下**組裝**——依序建構每個模組、把它們串成 render loop：

| 檔案 | 負責什麼 | 對外介面的關鍵字 |
|---|---|---|
| `planetSceneConstants.ts` | 所有寫死的常數（RENDER_RADIUS、冷卻時間…） | 純常數，無邏輯 |
| `cameraRig.ts` | OrbitControls生命週期、面向朝上的俯視角定位、轉向動畫 | `applyTopDownView`／`tickFacingAnimation` |
| `patchManager.ts` | 地塊資料、合併mesh、鄰居外框、時段換色 | `loadPatch`／`highlightNeighbors` |
| `planetBase.ts` | 星球本體（填縫球體＋遠景貼圖＋日夜色調） | `applyTint` |
| `playerMovement.ts` | 玩家位置、方向鍵對應、移動與伺服器驗證 | `moveToTile`／`rebuildNeighborKeys` |
| `hudStats.ts` | 每幀算給HUD看的鏡頭數字 | 純「讀鏡頭狀態→寫store」，不持有鏡頭 |
| `inputHandlers.ts` | 鍵盤與滑鼠點擊，翻譯成上面幾個模組的呼叫 | `keydownHandler`／`clickHandler` |

**這是純粹的重新組織，刻意不改任何邏輯**——每個函式的程式碼幾乎逐行照搬，只是換了檔案、
換了呼叫方式（模組間用建構時傳入依賴，而不是共用同一個閉包裡的變數）。之所以要求「純搬移」：
這是全前端bug密度最高的檔案（見下面的踩過的坑1~3、東西鏡像坑），改邏輯跟改結構混在一次
改動裡，出問題時很難判斷是搬移搬錯了還是邏輯本身就改壞了。

拆分時有一條依賴順序**不能打亂**（否則會重現踩過的坑1那種「鏡頭飛到很遠的地方」）：
`cameraRig` 的縮放範圍（`setZoomLimits`）必須在 `patchManager` 載入完初始patch**之後**、
玩家第一次被定位（`playerMovement.setPlayerTile`，內部會呼叫觸發`controls.update()`的
`cameraRig.followPlayerTile`）**之前**設好——這條順序原本靠原始碼裡三個函式的呼叫順序隱含維持，
拆開後改成在 `PlanetScene.vue` 的 `onMounted` 裡明寫出來，順序本身沒變，只是從「藏在函式呼叫鏈裡」
變成「這裡看得到」。

拆完之後新增了 `cameraRig.spec.ts`：把轉向動畫最容易出錯的部分（该往哪個方向轉、跨過0/360度
接縫的處理）抽成兩個純函式（`shortestFacingDelta`／`smoothstep`）直接測，而不是只能在瀏覽器裡
目視動畫「看起來對不對」——這是原本完全沒有自動化測試覆蓋的一塊（`.vue`檔案不在vitest的
測試範圍內，見 vitest.config.ts 的說明），現在至少這一小塊邏輯有測試釘住方向的正負號。

驗證：type-check／lint（eslint＋oxlint）都是0；`npm test` 196個測試全過（含新增的13個）；
瀏覽器裡逐項重測：初始載入、鍵盤移動、六個面向的轉向方向（用後端真實座標算地理方位角驗證，
誤差跟拆分前一致）、轉向動畫平滑度（截圖比對）、點擊移動、Home／指北針復位、走到patch邊緣
觸發背景補載（87步觸發、之後移動仍正常）、走動↔總覽視角來回切換8次（驗證WebGL context
沒有漏，這是文件記載過的既有坑）、按鍵設定面板的輸入防呆——全部行為跟拆分前一致。

## 鏡頭視角設計（cameraRig.ts）

- **移動不改變視角方向**：一開始做過「鏡頭跟著移動方向轉」（第三人稱視角），但實際使用時容易失去方向感（每次移動畫面都轉一次，累積幾次後很難判斷自己面向哪）。改成移動時只**平移**鏡頭位置＋target（用新舊焦點的位移向量直接加到camera.position），完全不動 `camera.up`／不重新定向——相鄰格角距離只有約0.03度，球面切平面幾乎沒有旋轉，平移近似完全夠用。手動拖曳（OrbitControls）想怎麼轉都可以，但移動這個動作本身不會偷偷轉動畫面。
- **復位視角**：`resetView()` 把鏡頭重設成「目前所在格正上方、正北朝上」的俯視角（`camera.up = north`、鏡頭沿當地法向量在正上方），縮放距離維持使用者當下的值。指北針UI本身可以點擊觸發（透過 `gameState.resetViewSignal` 訊號，`PlanetScene.vue` watch這個值來呼叫 `cameraRig.resetView()`），解決手動旋轉/縮放後失去方向感的問題。
- **踩過的坑1**：`OrbitControls.update()` 會把目前鏡頭距離依 `minDistance`/`maxDistance` 夾住。一開始把「設定min/maxDistance」放在「呼叫會內部觸發update()的定位函式」之後，結果鏡頭被builder時設定的舊範圍（給整顆星球俯瞰用的大範圍）夾回去，畫面看起來像鏡頭飛到很遠的地方、Tile patch縮成一個小點——要先設好min/maxDistance，才能呼叫任何會觸發`controls.update()`的定位邏輯。
- **踩過的坑2（左右方向顛倒）**：`OrbitControls` 建構子當下就用`camera.up`算好一個「up軸→世界+Y」的轉換quaternion（`_quat`/`_quatInverse`），之後拖曳/縮放都靠這個轉換做球面座標運算，但**這個quaternion只在建構時算一次，之後改`camera.up`它不會跟著重算**。因為每個Tile的當地north方向都不一樣，`resetView()`／初始載入每次都要換一次`camera.up`，結果OrbitControls內部用的還是舊的（甚至是預設(0,1,0)的）轉換，造成左右、上下的方位對不起來（實際症狀：使用者回報「移動的左右方向相反了」）。修法：`camera.up`每次變動，就整個`controls.dispose()`＋重新`new OrbitControls(...)`，讓建構子用新的up重新算一次轉換（同時要記得手動搬移`enableDamping`/`minDistance`/`maxDistance`等自訂設定，因為重建會丟回預設值）。
- **踩過的坑3（東西鏡像，2026-08-30）**：坑2修好之後又出現一次「WEADZX六個方向鍵全部左右相反」，但這次跟OrbitControls無關，是[sphereCoordinates.ts](../frontend/src/map/sphereCoordinates.ts)的`latLonToVector3`本身。移動鍵位對應（[bearing.ts](../frontend/src/map/bearing.ts)）純粹用經緯度算真實方位角，跟渲染座標系無關，一直是對的；問題出在「經緯度→XYZ」這個embedding的handedness，跟「鏡頭在正上方、camera.up=當地north、looking inward」這組相機設定（`applyTopDownView`）搭配起來，Three.js的`lookAt`公式算出來的畫面右方向＝`cross(up, eye-target)`＝`cross(north, normal)`，用微積分/ENU右手系仔細驗證後這其實等於**西**方向，不是東方向——整個場景渲染出來是東西鏡像的（標準地圖是「北上東右」，這裡變成「北上東左」）。修法：把`latLonToVector3`的Z分量（`cosLat*sin(lon)`）加負號翻轉embedding的handedness，讓畫面跟真實地理方位對齊；`tangentBasisAt`裡`east = cross(north, normal)`那行不用改，翻轉`latLonToVector3`之後這個式子自動變成正確的東方向（兩個「錯誤」原本方向相反、疊在一起兩個負負得正，只改一處即可，不要兩處都改）。驗證方式：從河邊的Tile往東移動（`d`鍵）後，河流應該出現在畫面左側（往西邊遠離了）；往回按`a`鍵應該回到河上——實測位置、經緯度變化、畫面呈現三者一致才視為修復。

## 全星球總覽模式（2026-08-30）

### 效能評估：「顯示所有地形」不可行

改變視角前先評估過「畫面改成顯示所有格子」（2000萬格全部一次載入）的效能，在本機開發環境用暫時放寬查詢
範圍＋本機備用後端（沒有動到正式服務）實測，三個維度都不可行：

- **查詢時間非線性變慢**：放寬bounding box到不同範圍，同樣的查詢邏輯（bbox篩選＋依距離排序＋TOP N）：
  ±0.75度(1,130格)24ms → ±2度(7,758格)183ms → ±5度(47,374格)1,010ms → ±10度(184,524格)3,857ms。
  換算到全星球2,002萬格，單次查詢會落在分鐘等級。
- **傳輸量線性但基數太大**：實測900格的JSON回應167KB（約185 bytes/格），全星球換算下去約**3.7GB**，
  瀏覽器不可能一次接收/解析/留在記憶體。
- **前端draw call數＝Tile數**：[tileMeshBuilder.ts](../frontend/src/map/tileMeshBuilder.ts)每個Tile
  各自建立獨立`THREE.Mesh`（雖共用材質但沒有geometry合併/instancing），一格一次WebGL draw call，
  2000萬次draw call遠超任何瀏覽器/GPU能撐住互動幀率的範圍（這台機器的瀏覽器pane無法穩定量到即時FPS，
  rAF在這個環境下似乎只在需要畫面時才觸發合成，不是持續跑；但draw call數量本身已經是決定性證據，
  不需要FPS數字也能下結論）。

結論：字面上的「全部一次載入」在架構層級就不可行，不是效能調校能解決的。實際做法拆成兩條路：
**走動視角維持「填滿目前視角」的策略**（一次載入數量從900提高到2000，後端`GetTilesNearAsync`查詢框
從±0.75度放寬到±1.1度，赤道約2,400格、極區約3,400格，留安全邊界；實測±1.1度查詢約60ms量級，仍在可接受範圍）；
**「看整顆星球」則用完全獨立的低精度渲染路徑**，見下方。

### 低精度貼圖渲染（OverviewTextureGenerator + PlanetOverview.vue）

不查資料庫，改成後端直接用`ITerrainGenerator`依經緯度取樣算出一張1024×512的**等距柱狀投影**
（equirectangular）地形色彩貼圖（`OverviewTextureGenerator`，種子要跟`GoldbergPlanetGenerator.DefaultTerrainSeed`
一致，顏色才會對得上資料庫裡實際的地形）。524,288次取樣用`Parallel.For`平行化，實測第一次生成約0.6秒，
之後永久快取在記憶體（星球地形是靜態的，不會變動）。用`GET /api/tiles/overview-texture`回傳原始RGBA
位元組（不封裝PNG，前端直接用`THREE.DataTexture`讀，兩邊都不用額外處理圖片編解碼），單次傳輸2MB。

前端`PlanetOverview.vue`是跟走動視角（`PlanetScene.vue`）完全獨立的Three.js場景/render loop（只共用
「這是同一顆星球」這件事），把貼圖貼在一顆簡單`SphereGeometry`球體上，用普通的自由旋轉/縮放`OrbitControls`
（不像走動視角有north-lock/tilt那些限制）。`gameState.viewMode`（`'walk'|'overview'`）控制`App.vue`
要顯示哪一個場景，畫面上方按鈕切換，不持久化（重新整理一律回到walk）。

- **踩過的坑（DataTexture預設flipY跟一般Texture不一樣）**：`THREE.SphereGeometry`的UV映射是
  `uv.y=1`對應north pole（見其原始碼：`vertex.y = radius*cos(theta)`，`theta=0`時y最大＝north pole，
  同時`uv.y = 1-v = 1`）。一般用`TextureLoader`載入的`Texture`預設`flipY=true`，會把圖片第0列（視覺上的頂端）
  對應到`uv.y=1`，這樣north-up的世界地圖貼上球體才會正著顯示。但`THREE.DataTexture`預設`flipY=false`——
  沒有另外設定的話，貼圖buffer的第0列（我在後端定義成北極）會對應到`uv.y=0`（south pole），整顆星球
  南北顛倒。**解法**：`DataTexture`建好後手動設 `texture.flipY = true`。驗證方式：往北極方向拖曳旋轉，
  應該看到白色（雪地/冰原）色塊出現在球體「上方」，不是下方——已實測正確。
  經度（東西）方向沒有特別跟走動視角的世界座標軸對齊，因為兩個場景各自獨立的攝影機/OrbitControls，
  沒有像走動視角那樣「東西方向對不對」會直接影響移動鍵位判斷的功能性後果，只要旋轉起來自然、
  南北極位置正確即可。

## EXE打包與NSSM部署（2026-08-30）

- `Planet.Service.csproj` 加了 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` + `<SelfContained>true</SelfContained>`——包成免安裝.NET執行環境的獨立EXE，符合原本「打包成EXE」的需求。
- **踩過的坑（專案路徑裡的單引號讓`dotnet publish`直接炸掉）**：整個repo放在 `D:\Adar's novel stories\Planet\...`，路徑本身含一個英文單引號（`Adar's`）。`dotnet build`／`dotnet run`都完全正常，但`dotnet publish`會在 `_CopyResolvedFilesToPublishPreserveNewest` 這個內建target壞掉，噴 `error MSB3094: "DestinationFiles" 參考 1 個項目，"SourceFiles" 則參考 N 個項目`（N會隨build是否為self-contained變化，64或385都出現過）。原因：這個target內部用單引號包住路徑做item transform（`'$(PublishDir)%(RelativePath)'`），`$(PublishDir)`只要繼承自專案路徑（不管是預設的`bin/Release/.../publish`還是用`-o`指到專案樹下的相對路徑），只要字串裡混進那個單引號，MSBuild的transform expression就被提前截斷，整批destination path解析壞掉、全部項目坍縮成1個——與RID（win-x64與否）、self-contained與否都無關，純粹是路徑字串裡的單引號炸的。**解法**：`dotnet publish`的`-o`一定要指到一個路徑本身不含單引號的絕對路徑（例如 `C:\PlanetPublish`），不能用專案樹底下的相對輸出目錄。已驗證：`dotnet publish src/Planet.Service -c Release -o "C:\PlanetPublish"` 成功產出 `Planet.Service.exe`（約127MB，self-contained），直接執行可正常連線SQL Server、API回應正確。這是這個repo在Windows上long-term都要記得的雷，不是一次性bug。
- NSSM服務安裝（`backend/deploy/nssm/install-service.ps1`）：`nssm install`本身在非提權終端機下意外成功建立了服務，但後續`nssm set`（環境變數、log路徑、`Start`模式、`ObjectName`執行帳號）都需要`SERVICE_CHANGE_CONFIG`權限，必須用系統管理員權限的PowerShell執行。
- **踩過的坑（NSSM服務預設用LocalSystem帳號，Windows整合驗證行不通）**：`nssm install`沒指定`ObjectName`時預設用`LocalSystem`（`NT AUTHORITY\SYSTEM`）帳號執行，但DB連線原本是`Trusted_Connection=True`（Windows整合驗證），SQL Server只信任互動使用者自己的Windows帳號登入，導致服務啟動時一直是`Login failed for user 'NT AUTHORITY\SYSTEM'`（Error 4060），NSSM又會自動重啟，`Get-Service`看起來是「Running」但其實裡面的App一直crash-loop，容易誤判成功。**最終解法**：改用SQL Server帳號驗證（SQL Server本來就是Mixed Mode），連線字串形如 `Server=localhost;Database=PlanetDb;User Id=<帳號>;Password=<密碼>;TrustServerCertificate=True`，這樣服務不管用哪個Windows帳號執行都不受影響，可以放心用預設的`LocalSystem`（`nssm reset PlanetService ObjectName`還原）。**連線字串本身不寫在 appsettings.json**，見下方「機密設定」一節。已驗證：`nssm start`後log顯示正常啟動、API回應正確、`netstat`確認port 5202確實是服務的PID在監聽（而不是開發用`dotnet run`殘留的進程，這點也曾經誤判過一次——`Get-Service`顯示Running不保證真的在serving，一定要用`netstat -ano`核對port owner的PID）。
- NSSM服務目前是`SERVICE_DEMAND_START`（手動啟動）模式；確認穩定後可用 `nssm set PlanetService Start SERVICE_AUTO_START` 改成開機自動啟動。

## 地形樣式資料表與逐格日夜變色（2026-08-30）

地形的「長什麼樣子」從程式碼搬進資料庫（`TerrainStyles` 表，以 `TerrainType` 為主鍵），
欄位是中英文名稱＋四個時段的顏色（`ColorDawn`/`ColorDay`/`ColorDusk`/`ColorNight`，格式 `#rrggbb`）。
種子資料用 EF Core `HasData()` 寫進 migration：白天色沿用原本寫死的調色盤，其餘三個時段用統一的
亮度／色溫公式從白天色推算（清晨 0.85 亮度 +0.12 暖，黃昏 0.75 +0.20 暖，夜晚 0.30 −0.15 偏冷），
不必為18種地形手挑72個顏色。改顏色不需要重新編譯部署，直接改資料庫即可。

**時段是逐格判斷的，不是全星球統一**（[solarTime.ts](../frontend/src/map/solarTime.ts)）：
用標準日出日落時角公式 `cos(H) = −tan(緯度)×tan(赤緯)` 算出「那一格、那一天」實際的日出/日落時刻，
日出後一小時內＝清晨、日落前一小時內＝黃昏，其餘為白天/夜晚，並處理極晝極夜（`cos(H)` 超出 ±1）。
實測近赤道座標的逐時分類為 night(0–5.5) → dawn(6–6.5) → day(7–17) → dusk(17–17.5) → night(18–23)，
日長約12小時，符合赤道全年日夜等長的預期。

## 星球生成參數化與重新生成 API（2026-08-30）

`TerrainGenerationOptions`（immutable record）集中所有生成參數：種子、兩塊大陸各自的經緯度伸展倍率、
小島數量／聚集比例／火山機率。搭配 `/api/admin` 三個端點：讀參數、寫參數、觸發「清空並在背景重新生成」。
生成走 `PlanetRegenerationService`，與服務啟動時的自動播種共用同一套邏輯（背景執行 + 進度回報到
`SeedingStatus`），前端輪詢 `/api/status` 顯示寫入進度。

**踩過的坑（球冠面積是非線性的）**：把大陸的緯度伸展調到 1.6 時，兩塊大陸直接融合成一塊 170°×359° 的
巨型陸地。原因是球冠面積正比於 (1−cosθ)，falloff 半徑接近 90° 時面積會爆炸性成長，遠超直覺。
結論：**收窄（<1）永遠是面積安全的，拉長（>1）有融合風險**——所以預設改成只調經度方向
（大陸A 1.0、大陸B 0.5，做出寬窄不對稱），緯度維持 1.0。

## 前端效能重構（2026-08-31）

使用者回報「操作遲鈍」。實測單步成本鏈：SignalR驗證 → `nearby` 查詢 67ms → **同步重建2000個Mesh 77ms**
→ `neighbors` 查詢，全部序列，合計約200ms，而且期間用 `isMoving` 把後續按鍵**直接丟棄**（不是排隊），
所以連按方向鍵只會走一格。穩態則是每幀2000次 draw call。

### 合併 geometry（[tilePatchMesh.ts](../frontend/src/map/tilePatchMesh.ts)）

整個patch合併成**單一** `BufferGeometry`／單一 `Mesh`，實測建構 76.8ms → 11.6ms、draw call 2000 → 1、
全量重新上色從2000次GPU上傳變成1次。

- 頂點與三角形一律**依 tiles 陣列順序連續配置**，這是 picking 能成立的前提。
- **picking 用查表不用算式**：raycast 回傳的 `intersection.faceIndex` 是三角形序號，但六角格佔6個、
  五角格佔5個，**stride 不固定**，任何乘法推算都會在遇到第一個五角形之後整體錯位，而且是靜默的
  （玩家只會發現點A卻走到B）。改用 `triangleToTileIndex` 直接查表。
- 一定要**共用** `computeTilePolygon`（不是複製一份），否則鄰居高亮外框會跟地形對不齊。
- 不要用 `BufferGeometryUtils.mergeGeometries`（可能重排索引）、不要建 `geometry.groups`、
  不要呼叫 `computeVertexNormals()`（會把格子邊界平滑掉）。

### 本地先動、背景驗證（movement）

`neighborIds` 由後端隨 `nearby` 一起送來（見下），前端因此能自己決定「按這個鍵走到哪一格」，
不必等伺服器。按鍵當下**同步**完成位置更新＋鏡頭平移＋方向鍵重算，SignalR 驗證在背景跑，
不一致才回退（`moveGeneration` 讓過期的驗證結果失效）。**連不上伺服器不等於移動非法，不可以因此回退**，
否則離線時玩家會被彈來彈去。

`isMoving` 之所以能拿掉，是因為舊版在狀態轉換**中間** await 了三次網路來回，那段期間各項狀態互相矛盾，
閘門其實是在遮掩撕裂狀態、不是真的在防競態；新流程所有變更都在單一同步任務內完成。
拿掉之後長按會以作業系統連發速率（約30次/秒）觸發，在移動模式下等於每秒推進30個遊戲小時，
所以加了 90ms 冷卻（約11格/秒）。

### 邊緣觸發載入（[tilePatch.ts](../frontend/src/map/tilePatch.ts)）

一次載入從2000格提高到6000格，但**只在玩家接近已載入範圍邊緣時才補載**。
「離邊緣幾步」用鄰接圖做多源BFS（拓樸距離），不是用幾何距離——因為patch的形狀並不可靠
（高緯度會被經度換算拉扁、換日線附近甚至可能缺一邊），拓樸距離才是精確的。
補載期間**舊patch完全保持渲染與可用**（星球是靜態的，舊鄰接仍然有效），所以不會卡頓或破圖。

實測走90步（跨越4.38°經度）：**零停頓**、剛好觸發3次補載，平均每30步一次，與預估的34步相符。

- **踩過的坑（Int32Array 放不下 MAX_SAFE_INTEGER）**：BFS 走不到的格子要標成「無限深」，
  原本寫 `Number.MAX_SAFE_INTEGER`，但 `depth` 是 `Int32Array`，該值會被**靜默截斷成 −1**，
  結果「深度 > 門檻」永遠不成立、變成每走一步都補載。改用 Int32 上限 `0x7fffffff`。
  這個坑是補單元測試時才抓到的，肉眼完全看不出來。

### 後端配合

- **`TileDto` 帶 `NeighborIds`**：`nearby` 回傳時，在**記憶體**建 `(FaceIndex,Row,Col) → TileId` 字典，
  對每格呼叫現有的 `GetNeighborCoordinates`（純運算）再映射成 patch 內的 TileId，**零額外DB查詢**
  （[PatchNeighborLinker.cs](../backend/src/Planet.Generation/PatchNeighborLinker.cs)）。
  陣列長度固定為完整鄰居數，不在範圍內的位置用 `null`——刻意不壓縮成變長清單，否則就分不出
  「五角形本來就5個」與「六角形有一個鄰居在範圍外」。
  **關鍵決策：絕不把 `TileNeighborResolver` 移植到 TypeScript**——跨面/五角形邏輯是全專案最難的
  程式碼（已踩過三次 handedness 的坑），演算法只留伺服器一份，前端只查表。
- **查詢框依 count 反推**（`NearbyQueryOptions`）：原本寫死 ±1.1°，前端要6000格時框沒跟著變大，
  等於只是把同一批格子重排一次。實測校準約1950格/平方度，`delta = sqrt(count × 1.3 / 1950)`。
- **回應壓縮**：2000格 362KB → **122KB**（3倍），而且是在多帶了 `neighborIds` 之後。
  兩個地雷：(1) Brotli 的 `CompressionLevel.Optimal` 是 quality 11，對數百KB會吃掉數百毫秒CPU，
  要明確設 `Fastest`；(2) **不要擴充 `MimeTypes`**——`application/json` 已在預設清單，而
  `application/octet-stream` 不在，正好讓星球總覽貼圖（2MB、已記憶體快取）維持不壓縮。
- **批次查詢（效能預期落空，誠實記錄）**：`MoveTo` 由「最多7次序列查詢」改成1次、`GetNeighbors` 改成
  批次 `IN`，但實測 `/neighbors` 舊版 4.1ms vs 新版 4.4ms、`nearby` 76.1ms vs 73.2ms（含 `.AsNoTracking()`），
  **都在誤差範圍內**。原本估計的「30-50ms」是沒有量測的臆測——暖索引下7次PK/索引seek本來就只要4ms。
  改動保留（來回次數少、冷快取或遠端DB才有意義），但**不應記為效能收益**。

### 順手修掉的三個既有 bug

1. **換日線 patch 被砍半**：`GetTilesNearAsync` 用原始度數的 Δlon 排序，在經度179.9查詢時，
   對面 −179.9 的格子算出 Δ=359.8 排到最後、被 `Take(count)` 整批砍掉。修法是排序前把經度差
   繞回 [−180,180]（T-SQL 的 `%` 不吃 float，用 `Math.Floor` 做等效取模），同時把經度項乘上 cos²φ
   修正高緯度的橢圓扭曲。實測修正後在換日線的patch是 1008/992 平衡、各緯度的長寬比 0.99~1.00。
2. **`camera.near` 大於 `controls.minDistance`**：near=0.1 但 minDistance 只有 0.0405，
   拉到最近時近平面跑到目標點後面，把腳下的格子裁掉。**修法是調高 minDistance 而不是調低 near**
   （調低 near 會讓深度精度差約10倍，精度正比於 z²/(near·2²⁴)）。
3. **方向鍵誤指派**：六個鍵的角度不平均（45/90/135/225/270/315，間隔45,45,90,45,45,90），
   六角格鄰居卻是平均60度一個，舊版「逐配對貪婪指派」在某些旋轉相位下會把**正北的鄰居配給 d（正東）**，
   實測誤差達90度，而且沒有任何誤差上限或警示。改成「保持循環順序」的最佳比對：兩邊各自依方位排序，
   試所有旋轉對齊取總誤差最小者，保證六個鍵繞著玩家的順序跟實際地理順序一致。
   實測4,508格真實資料：最差誤差 90° → **45°**、零循環順序違規、且全部保留6個鍵。
   誤差門檻設50度而非40度是刻意的——設40會讓約12%的格子少掉2個方向鍵（那些格子變成走不到），
   反而比「方向偏45度」更難用；門檻只當作抓真正異常的保險，不用來修正正常的相位差。

### WebGL context 洩漏（既有問題，一併修掉）

`renderer.dispose()` **不會**歸還 WebGL context，而「走動視角↔星球總覽」是用 `v-if` 整個換掉元件、
每次切換都會新建一個 renderer——瀏覽器每個分頁的 context 上限約16個，切十幾次畫面就會壞掉。
另外兩個場景的釋放邏輯都必須建在 `onMounted` 的閉包裡（scene/controls/planetBase 都是區域變數，
`onUnmounted` 根本拿不到，這正是原本漏掉沒釋放的原因）。修法：`forceContextLoss()` + `dispose()`
+ 移除 canvas，並釋放 geometry/material/texture（總覽那張 DataTexture 有2MB）。
實測連續切換10次，建立了10個 context 但**存活數維持在1**。

### 前端單元測試（本次新增）

前端原本 0 測試、連 Vitest 都沒裝。這次把最容易錯又最難用肉眼發現的純邏輯固化成 82 個測試
（`src/map/*.spec.ts`）：合併geometry的三角形→tileId對應（含五角/六角交錯等會讓固定stride錯位的情況，
逐一窮舉每個三角形）、方向鍵在12種旋轉相位下的誤差上限與循環順序、邊緣BFS與大圓最近格。
不需要瀏覽器：Three.js 的 BufferGeometry/Vector3 純粹是記憶體運算，用預設 node 環境即可，不必裝 jsdom。

```bash
cd frontend
npm test
```

## 消除「同一份事實存在多個地方」（2026-08-31）

架構盤點時發現三個問題其實是同一個病根：**同一份事實散落在多處，而且沒有任何機制保證同步**。
共通症狀是「不一致時完全看不出來」——沒有錯誤、沒有警告，只是安靜地錯。

### 星球記錄（`PlanetMetadata`）：既成事實 vs 待套用設定

新增一張只有一列的 `PlanetMetadata` 表，記錄「資料庫裡這顆星球**實際上**是什麼」：
細分頻率、半徑、Tile邊長、地塊總數、**實際產生這批地塊的地形參數（JSON）**、生成時間。
只在整顆星球生成完成時更新。

關鍵在於把兩個概念拆開，它們以前被混為一談：

| | 意義 | 何時變動 |
|---|---|---|
| `TerrainOptionsStore` | **下次**重新生成要用的參數 | 隨時可改，改了不影響現況 |
| `PlanetMetadata.TerrainOptionsJson` | **實際產生出目前這批地塊**的參數 | 只在生成完成時 |

**修掉的 bug**：星球總覽貼圖是即時取樣演算法算出來的，而它讀的是「下次要用的參數」。
所以改了參數但還沒重新生成時，總覽畫的是一顆**資料庫裡並不存在的星球**，
跟走動視角看到的完全是兩個地方，而且沒有任何提示。現在改讀 `PlanetMetadata` 的已套用參數。

驗證方式（要注意貼圖有快取，不能只改參數就比對——那樣兩次都是同一份快取，會得到假的 PASS）：
先編輯任一地形樣式強制讓快取失效、記下貼圖 hash，再把待套用參數改成 seed=999／島嶼4000、
**再次強制失效並重算**，兩次 hash 必須完全相同。實測 `ba62e977de7f60cb` 前後一致。

既有資料庫沒有這筆記錄（地塊是在這功能之前生成的），啟動時由 `PlanetSeeder` 依預設參數補寫一筆
並標記 `IsInferred=true`，下次重新生成就會被真實值覆蓋。

### 前端不再寫死星球尺寸（`GET /api/planet`）

前端原本寫死 `PLANET_RADIUS_METERS = 1_851_000`，但這個值後端是從細分頻率**推導**出來的。
新增 `GET /api/planet` 提供半徑、Tile邊長、細分頻率、地塊總數、總覽貼圖寬高，
由 `PlanetLoadingGate` 在畫面顯示前載入（`stores/planetInfo.ts`），場景元件掛載時同步讀取。

**這個修正當場就抓到既有的漂移**：實際半徑是 **1,852,259.92** 公尺，跟寫死的 1,851,000 差了
**約 1,260 公尺**——前端一直用著略微錯誤的比例在畫格子，而且完全沒有任何跡象。
這正是「寫死第二份」最典型的下場：不會壞掉，只會安靜地不準。

`RENDER_RADIUS`（場景單位）與 `SPHERE_RADIUS` 留在前端是對的——那是純粹的顯示比例選擇，
跟後端無關，不屬於「同一份事實的第二份複本」。

### 地形顏色只留一份（刪掉前端的調色盤複本）

`terrainColors.ts` 原本有一份18種地形的硬編碼顏色當 fallback。問題是**它只在後端拿不到時才用得到**，
所以兩份資料漂移了平常完全看不出來，只有在後端剛好掛掉的那一刻，畫面才會突然變成另一組顏色。

改成只留單一中性色 `UNKNOWN_TERRAIN_COLOR`（跟後端 `OverviewTextureGenerator.FallbackColor`
的行為一致），並把載入失敗的原因記在 `terrainStyles.loadError` 由 UI 明確顯示——
以前是靜默 `catch {}` 吞掉，畫面看起來「正常」但其實顏色與名稱全都不是真的。

順帶修掉一個併發問題：`load()` 原本只靠 `isLoaded` 擋重複載入，但第一個請求還在路上時
`isLoaded` 仍是 false，所以 `PlanetScene` 與 `CoordinateHud` 會各發一次
（實測 `/api/terrain-styles` 每次載入被抓兩次）。改成共用同一個 in-flight promise。

## 機密設定與管理端點權限（2026-08-31）

架構盤點列為「會出事」等級的兩項，一併處理。

### 連線字串不再進版控

`appsettings.json` 裡原本躺著 `Password=...`，而 `.gitignore` 完全沒有排除它——這個 repo 目前還不是
git repo，但 `.gitignore` 存在代表遲早要 init，一旦 commit 密碼就永久留在歷史裡。

改成三種來源（依優先序）：user-secrets（開發）→ `appsettings.Production.json`（部署目錄，不在 repo 內）
→ 環境變數。**缺少連線字串時直接啟動失敗**並在訊息裡寫明設定方式，而不是帶著空字串跑到第一次查詢才爆。

順帶把散在 `README.md` 與本文件裡的明文密碼也清掉了——文件同樣會進版控，只清設定檔是不夠的。

### 管理端點加上金鑰（fail closed）

`POST /api/admin/regenerate` 會 TRUNCATE 掉 2,000 萬列且無法復原，在此之前**完全沒有任何保護**：
`Program.cs` 有 `app.UseAuthorization()`，但從來沒註冊過任何 authentication scheme，那行是 no-op。
加上 UI 面板之後，等於任何開著這個網頁的人點兩下就能清空整顆星球。

作法是共用金鑰（`X-Admin-Key` 標頭，`AdminApiKeyFilter`）。這個專案不需要完整的使用者系統——
要擋的是「不可復原的破壞性操作可以被任何人無條件觸發」，共用金鑰對這個威脅已經足夠。

**最關鍵的設計是 fail closed**：伺服器沒設定金鑰時，管理端點回 **503（功能停用）**，
而不是「沒設定就不檢查」。漏設定的後果必須是「功能用不了」，不是「大門敞開」——
後者正是修之前的狀態。金鑰比對用 `CryptographicOperations.FixedTimeEquals`，避免用回應時間差逐字猜。

實測（正式服務）：`POST /api/admin/regenerate` 無金鑰由 **202 變成 401**；
沒設定金鑰的實例則是所有管理端點都回 503（連帶正確金鑰也一樣），而一般端點完全不受影響。

- **驗證到的 OpenAPI 產生器行為（第4條，補充本文件既有的3條）**：
  `[ProducesResponseType]` 掛在**控制器層級**時，會正確套用到該控制器的每一個 action。
  實際打 `/openapi/v1.json` 確認過：三個 admin action 都列出了 401 與 503。
- **踩過的坑（全域 keydown 監聽會吃掉輸入框的按鍵）**：管理面板加了文字輸入框之後，
  在裡面打字會同時觸發 `PlanetScene` 掛在 `window` 上的移動監聽——金鑰裡只要含
  `a`/`d`/`w`/`e`/`z`/`x`/`s` 就會一邊打字一邊把玩家走掉（實測輸入金鑰時玩家真的移動了）。
  修法：keydown handler 先檢查 `event.target` 是不是 INPUT/TEXTAREA/SELECT 或 contentEditable，是就直接忽略。

## 右下角位置指示器改版（2026-08-31）

原本是純 SVG 的示意圖：固定「參考經度=90°」、夜側用一個通過球心的半平面近似。檢查時量到三個問題：

1. **三個標籤都被 viewBox 裁掉**（用 `getBBox()` 量出來的，目視看不太出來）：
   `換日線` 超出右緣 7.6 單位（末字切掉約28%）、`N` 超出上緣、`S` 超出下緣。
2. **正反面分不出來**：`x = CX + R·cos(lat)·sin(lon−90)` 讓 `lon` 與 `180−lon` 投影到同一點
   （實測 `lon=90` 與 `lon=270` 都是 `(60,60)`）。每個畫面位置對應兩個經度。
3. **tooltip 永遠不會出現**：有 `title` 卻同時設了 `pointer-events: none`，收不到 hover。

需求改成：圖跟著玩家轉（玩家永遠在正中央）、標示大致的陸海、動態顯示日照光影。

### 為什麼從 SVG 改成 canvas 逐像素

三個需求指向同一個做法，而且**舊的半平面畫法會被新需求直接擋死**：
半平面的邊界通過球心，玩家一旦固定在球心，就會**永遠正好落在分界線上**，
等於完全看不出自己是白天還是晚上。

改成 `<canvas>` 畫球面、SVG 疊在上層畫標記與標籤之後，三件事變成同一個迴圈裡的事，
而且光影是**逐像素精確**的（依實際太陽天頂角），不再是半平面近似：

```
對圓內每個像素：反向正射投影 → 經緯度 → 取樣陸海遮罩 → 依 cos(天頂角) 決定明暗
```

直徑100的圓約7,850個像素，只在**玩家位置或遊戲時間變動時**重畫（不是每幀），成本可忽略。
數學抽到 [globeIndicator.ts](../frontend/src/map/globeIndicator.ts) 並補了22個單元測試——
這段錯了不會壞掉，只會「看起來像個地球但方向不對」，是最需要測試釘住的那種程式碼。

### 陸海遮罩（新端點）

陸海分布來自後端地形演算法，**不可以在前端重算**（理由同鄰居座標，見 CLAUDE.md）。
現成的總覽貼圖是 1024×512 RGBA（2MB），為了120px的小圖示載2MB不合理。

新增 `GET /api/tiles/overview-water-mask`：128×64、**每像素1位元組**（1=水、0=陸），共 **8KB**。
刻意回傳「已分類好的遮罩」而不是地形代碼——分類規則（哪些地形算水）留在後端一份就好，
讓前端拿代碼自己判斷等於又把規則複製成兩份。尺寸由 `/api/planet` 提供，前端不寫死。

`OverviewTextureGenerator.Invalidate()` 要**同時清掉貼圖與遮罩兩份快取**。
顏色改變其實只影響貼圖，但重新生成星球會同時影響兩者，漏清遮罩的話小圖示會停在舊地形。
實測驗證：失效後遮罩 2.0ms→9.8ms、貼圖 3.7ms→340ms，兩份都確實重算。

### 驗證結果

- 裁切：`getBBox()` 走訪全部元素，超出 viewBox 的數量 **0**。
- 跟著轉：玩家標記 x 永遠 **=60**（圓心），y 隨緯度變化。
- 光影會動：推進24個遊戲小時，圓心亮度 115（日）→ 39（夜）→ 115，
  且**轉換時機與 `determineDayPeriod` 完全吻合**（兩者都源自 `computeSubsolarPoint`），
  太陽標記的實心/空心同步翻轉。
- 陸海：畫布上同時存在陸地與海洋像素（4,854 / 3,006），遮罩本身水佔比 69.2%。

（註：17:00 時 `determineDayPeriod` 已是 `dusk` 但畫面仍是全亮，這不是 bug——
前者是「日落前一小時內」，後者是實際太陽仰角，太陽那時確實還在地平線上。）

## 格子改成真正密合的蜂窩（2026-08-31）

使用者回報格子之間有縫隙。這是本文件長期記載的既有限制（「每格各自獨立的切平面近似多邊形，
不是依實際鄰居精算共邊頂點的Voronoi對偶網格」），這次補上了。

### 量測出來的成因跟直覺相反

| 量測項目 | 結果 |
|---|---|
| 實際中心間距 | 1624–1681 m（中位 1665） |
| 舊畫法假設的間距 | 1732 m（＝邊長1000×√3） |
| → 格子其實**畫太大**約4% | 會重疊，不是縫隙的來源 |
| **六邊形朝向誤差** | **±22°～29°**（最大可能才30°） |

真正的原因是**朝向**：程式把「角」畫在應該是「邊」的位置，
所以三格交會處對不齊、留下三角形小洞——畫面上那層點狀紋理就是這些洞。

而且**朝向誤差隨地區變化**（(12,40)+26.8°、(0,0)−29.1°、(70,20)−0.3°…），
所以不能用單一常數旋轉修掉，必須逐格依實際鄰居方向決定。

### 做法：角落由「共用它的三格」一起決定

Goldberg多面體是測地線球的對偶：格子中心＝測地線頂點，格子角落＝相鄰三頂點所圍三角面的中心。
而「哪三格互相相鄰」我們已經有了（`TileDto.neighborIds`，後端提供）。

```
cornerDir = normalize( unit(自己) + unit(鄰居ᵢ) + unit(鄰居ᵢ₊₁) )
```

**密合性的關鍵不是這個公式幾何上有多精確，而是三個共用這個角的格子都用同樣的公式、
同樣的三個輸入去算**，所以必定得到同一個點。接縫由建構方式保證，不是靠調參數湊出來的。

兩個容易出錯的地方：

- 後端 `neighborIds` 的順序來自推導過程，**不保證是繞著格子的角度順序**，一定要自己依切平面方位角排序，
  否則角落會連成星形而不是凸多邊形。
- 算角落方向要用**未加海拔**的單位向量，海拔最後才乘上半徑。否則相鄰高低不同的格子會算出不同方向，密合性就沒了。

### 側面牆

相鄰格海拔落差中位1級、最大4級，誇張8倍後是格子邊長的8~16%，共用角落之後側面會露出可看穿的裂縫。
每條邊若「自己比鄰居高」就補一個四邊形往下接到鄰居的表面高度。
**只有較高的那一格畫牆**，所以每面牆剛好畫一次、不會重複。牆的法向量設成水平朝外，在方向光下自然有明暗差。

### 驗證結果

- **密合性（核心保證）**：對真實API資料的 **3,600 組相鄰配對**檢查，
  **全部剛好共用2個角落**（完整一條邊），0組對不齊。
  單元測試另外釘住「三格互相相鄰時算出的共用角落方向完全相同（<1e-12），即使三格海拔不同」。
- 幾何量：2000格 → 27,444頂點／18,722三角形（平均9.36三角形/格，其中約3.4個是牆），
  **仍然只有1次draw call**。
- 效能：建構2000格 11.6ms → **14.8ms**（+28%，遠低於原本預估的30–50ms）。
- 視覺：點狀紋理消失，低角度俯視也不透光；黃色鄰居外框與格子邊界貼合
  （外框與地形mesh共用同一份角落數學，這點在 `tileMeshBuilder.ts` 有註解警告）。

邊緣格（鄰居不在patch內）自動退回舊的近似畫法。目前一次載入6000格但畫面只看得到約2000格的範圍，
那些格子在畫面外，不影響觀感。

## 遠景與載入範圍（2026-08-31）

預設視角改成傾斜60度之後，「地形只到一半就沒了」變得很明顯——60度、10公里高時**地平線在約193公里外**，
而當時的載入範圍只有64公里。兩件事一起處理：

### 載入格數 6000 → 50000

實測（同一位置、暖機後取中位）：

| 格數 | patch半徑 | 換patch卡頓 | 壓縮後傳輸 | 後端查詢 | 三角形 |
|---|---|---|---|---|---|
| 6,000 | 64 km | 30ms | 361KB | 55ms | 68k |
| 20,000 | 117 km | 112ms | 1.21MB | 262ms | 220k |
| **50,000** | **187 km** | **217ms** | **3.09MB** | **~1秒** | **530k** |

187公里已經接近地平線距離，遠處看到的幾乎都是真正的格子。
**仍然只有1次draw call**，但頂點緩衝區約67MB、換patch的停頓是同步的（會有可感的頓一下）。
再往上加不划算——查詢時間非線性上升（實測±10度／18萬格要3.8秒）。
（上表的「換patch卡頓」是當時量到的數字；後來在瀏覽器逐段量測時發現5萬格的建構實際約1秒，
見下面「換patch的停頓：把建mesh的迴圈切成多幀」——該節的數字才是實測分段結果。）

後端的 `NearbyQueryOptions` 上限配合放寬：`MaxCount` 2萬→6萬、`MaxDeltaDegrees` 4°→7°
（5萬格需要約5.8度的查詢框）。

`REFETCH_DEPTH_THRESHOLD` 也從10提高到30：patch半徑約112格，設10要走到只剩10格（16公里）才補載，
那時邊界在傾斜視角下已經很近了；設30代表每走約82步補一次、前方邊界永遠保持在50公里外。
實測走100步觸發1次補載、0次停頓。

### 換patch的停頓：把建mesh的迴圈切成多幀（2026-09-01）

補載雖然在背景進行，但**建mesh那段是同步的**——`await` 只讓出網路等待的時間，
typed-array 填充一旦開始就會一路跑到底，中間瀏覽器完全沒有機會繪製或處理輸入。

在瀏覽器裡逐段量測5萬格的換patch（用 `setInterval(10ms)` 看門狗量「主執行緒被佔住多久」，
而不是量總時間——使用者感覺到的是前者）：

| 階段 | 是否同步阻塞 | 5萬格實測 |
|---|---|---|
| `fetch` + 3MB `JSON.parse` | parse 的部分是 | 合計約3.4秒（絕大多數是等後端） |
| `createTilePatch`（建 tileId→索引 的Map） | **是** | **約296ms** |
| 配置 typed array（最壞情況約70MB） | 是 | 暖機後約1~2ms（首次載入約166ms） |
| **填充迴圈（每格算多邊形、寫頂點與牆）** | **原本是** | **約1秒的CPU工作量** |
| `finish()`（建 BufferGeometry、`computeBoundingSphere`） | 是 | 約83~95ms |

最大的一塊是填充迴圈，而它是純 typed-array 填充、天然可續，所以先切它
（`buildTilePatchMeshSliced`，`tilePatchMesh.ts`）：把建構器拆成 `fillRange(from,to)` / `finish()`，
每次做滿約5ms就交還控制權，下一個 task 再接著填。

交還控制權用 **`MessageChannel`**，不是：

- `requestAnimationFrame`——分頁在背景時不觸發，建構會整個停住（這台機器的瀏覽器pane
  本來就不穩定觸發rAF，量測時已經踩到）；
- `setTimeout(0)`——巢狀計時器被夾到最少4ms，切幾百段等於憑空多出上百毫秒；
- `Promise.resolve()`——microtask，根本不會讓瀏覽器有機會繪製。

一次填幾格也要選：實測每格約20µs，原本設的256格一塊就要約5ms、剛好把整個預算一次用完，
等於預算完全失效（實測193段≈196塊，每段都只做一塊）。改成**64格**（約1.3ms）預算才管得住。

還加了 `isCancelled`：元件卸載時中止並回傳 `null`。中止點在 `finish()` 之前，
所以還沒建立任何GPU資源，直接丟掉即可、不需要 dispose。

換完之後把 `colorsDirty` 立起來重新上色一次——切片期間遊戲時間可能剛好跨過時段，
先建好的格子會拿到舊顏色、後建的拿到新顏色。

**實測結果**（走約200步觸發一次補載，全程量看門狗間隔）：

| | 切片前 | 切片後 |
|---|---|---|
| 填充迴圈造成的最長阻塞 | 約1秒（整段一次做完） | **≤35ms** |
| 看門狗間隔中位數 | — | **10.1ms**（設定值10ms，等於沒被卡住） |
| 超過50ms的區塊 | — | **1個**（121ms，是 `JSON.parse`＋`createTilePatch`，不是建mesh） |

代價是牆鐘時間從約1秒拉長到約1.6~1.8秒——多出來的是每次交還控制權時瀏覽器順便繪製一幀。
這是刻意的取捨：**建構期間舊的patch仍然掛在場景上**，畫面完整、可以繼續走動，
所以總時間長一點使用者感覺不到，卡住一秒則一定感覺得到。

**還沒處理的**：`createTilePatch` 的約296ms 與 3MB 的 `JSON.parse` 仍是同步的
（上表那個121ms的區塊就是它們）。這兩處要各自處理——前者同樣可以切片，
後者得換成串流解析或二進位格式才有解。

### 轉向：面向與可自訂按鍵（2026-09-01）

玩家有**面向**（`gameState.facingDeg`）。六角格＝六個方向，所以每次轉向剛好60度，
只有 0/60/120/180/240/300 六個值（0=正北，順時針；使用者指定的說法是「面北右轉變成面東北」）。

轉向要同時做兩件事，只做一半就會壞：

1. **鏡頭轉成「面向朝畫面上方」**（`applyTopDownView`）。原本寫死用當地正北當 `camera.up`，
   現在改成正北繞地表法線轉 `facingDeg` 度：`facing = north·cos(φ) + east·sin(φ)`。
   方位角的定義本來就是「0=北、90=東、順時針」，所以這個式子直接成立，不必動用四元數
   （那反而容易把旋轉方向的正負號弄反——這個專案已經因為 handedness 踩過三次坑）。
   `tangentBasisAt` 的 `east` 確實是地理東方，這點是**實測驗證**過的，不是推論：
   在四個不同緯經度沿 `east` 走一小步再回推方位角，四個都是 90.0°。
2. **六個移動插槽依面向重新指派**（`assignNeighborsToSlots` 的 `facingDeg` 參數）：
   比對時用「地理方位角 − 面向」＝這一格在畫面上的方位角。少減這一項或正負號弄反，
   症狀就是「轉向之後按右卻往左走」。

只做1不做2 → 畫面轉了但按鍵沒跟著轉；只做2不做1 → 按鍵轉了但畫面沒轉。所以兩者由
`facingDeg` 的同一個 watch 統一觸發，不在 keydown 裡各做各的。

**面向0度時，整套行為跟加入轉向功能之前完全相同**——`CANONICAL_DIRECTIONS` 那六個角度
（45/90/135/225/270/315）數值一個都沒改，只是意義從「地理方位角」變成「相對面向的方位角」，
面向0度時兩者相同。這是刻意的：新功能不該讓既有行為有任何退化。

驗證（在瀏覽器對真實資料實測，經緯度取自後端而不是前端自己的狀態）：
六個面向各按一次「右」，走到的那一格的**地理**方位角與「面向+90度」的誤差是
4.7 / 3.1 / 2.9 / 4.7 / 3.1 / 2.8 度，六次右轉回到面向0度。

#### 轉向動畫：平滑過渡而不是瞬間跳過去（2026-09-02）

最初的版本是瞬間切換——`facingDeg` 一變，`applyTopDownView` 立刻用新角度重建一次鏡頭，
畫面直接跳到新朝向。使用者回饋要改成平滑過渡。

核心問題：`applyTopDownView` 每次呼叫都會整個重建 `OrbitControls`（見上面「
OrbitControls在建構子當下就把up軸的轉換quaternion算好存起來」那個坑），所以不能只是
「把最終角度算出來後用CSS transition」那種做法——旋轉本身必須是「每幀用一個介於起訖之間
的中間角度，重新呼叫 applyTopDownView」，讓鏡頭真的一步一步轉過去。做法：

- `applyTopDownView` 加一個可選的 `facingDegOverride` 參數，不傳就退回讀 `gameState.facingDeg`
  （既有呼叫點—resetView／resetToDefaultView／初次進場—全部不用改，行為不變）。
- `facingDeg` 的 watch 不再直接呼叫 `applyTopDownView`，改成只設定一組動畫狀態
  （`turnAnimFromDeg`/`turnAnimToDeg`/`turnAnimStartAt`），實際內插由 `animate()` 逐幀處理，
  每幀用 `performance.now()` 算進度、smoothstep（`3t²-2t³`）算緩動係數，再把中間角度傳給
  `applyTopDownView`。方向插槽（`rebuildNeighborKeys`）則**立刻**重算、不等動畫播完——
  操作邏輯要馬上生效，鏡頭轉不轉完純粹是視覺的事，兩者刻意分開。
- 顯示角度 `displayedFacingDeg` 刻意**不做360取模**（可以累加到120、180…一路下去），
  跟存檔用、永遠正規化在0~300的 `gameState.facingDeg` 是兩個獨立表示法。這是為了處理
  跨過0/360接縫的情況：如果每次都拿正規化後的角度內插，从300轉到0（往右轉一次）會被
  誤判成「從300倒退300度」而不是「往前60度」，動畫會反方向轉一大圈。做法是先在正規化空間
  算出「最短路徑」的signed delta（用 `((to-from+180)%360+360)%360-180`，這個算法在
  `bearing.ts` 的 `circularDiff` 已經驗證過，這裡是同一個數學問題），再把這個delta加回
  未取模的顯示角度上，方向就永遠正確、動畫永遠往按下的那個方向轉，不會繞遠路。
  動畫播完後才把顯示角度收斂回目標附近（差值收斂到±180度內），避免長時間遊玩後這個數字
  無限增長——收斂只改變累加的圈數，角度本身（`cos`/`sin`）不變。
- 長按連續轉向時（轉向鍵冷卻90ms，動畫時長220ms，後者比前者長）：新的一次轉向會在
  上一次動畫還沒播完時就進來，這時候「起點」用的是**當下顯示中的角度**（可能還在動畫途中），
  不是回到上一個目標值重新起跳——效果是動畫平滑地接續延伸，看起來像連續旋轉，
  不會在每次按鍵的瞬間卡一下再重新加速。
- 動畫期間 `applyTopDownView` 一樣會重建 `OrbitControls`（220ms約13~14幀，每幀重建一次）。
  這不是新風險，只是把「單次重建已經驗證沒問題」的既有模式在短時間內重複套用；
  重建本身很輕（主要是註冊/移除事件監聽），這個頻率下的成本可以忽略。

驗證：在瀏覽器對真實畫面連續截圖（起點、~90ms、~440ms三個時間點），確認中間確實存在一個
與起訖點都不同的過渡畫面（地形與黃色鄰居外框的形狀都跟兩端不一樣），而不是「兩張圖只有
瞬間切換的兩種樣子」；左轉、右轉都測過，方向與 `facingDeg` 的變化一致。

#### 可自訂按鍵

九個動作（六個移動插槽＋原地＋左右轉向）的按鍵都可以自訂，存在 localStorage
（`stores/keyBindings.ts`，預設 W/E/A/D/Z/X＋S＋`,`／`.`）。因此：

- `bearing.ts` 的插槽改名成 `upLeft`/`right`/… ——**不能**再叫 `w`/`d`，那在玩家改綁之後就是謊話；
  也不用地理方位命名，因為面向不是北的時候左上根本不是西北。
- 衝突一律**互換**而不是「把舊的清掉」：清掉會留下一個沒有按鍵的動作，玩家得自己發現。
- localStorage 的內容是**逐欄位驗證**的（使用者可以直接編輯它）。壞掉的值整包信任的話，
  症狀會是「某個動作永遠按不到」，很難查。
- 等待玩家按鍵時，全域移動監聽必須整個停手。這裡用了**兩道**保護，見 CLAUDE.md
  ——只靠 capture 階段的 `stopPropagation` 會在「事件直接派送到 window」時安靜失效
  （實測確認過），所以另外加了一個與事件順序無關的 `isCapturing` 旗標。

### 遠景改用總覽貼圖（簡易LOD）

載入範圍之外原本是一片純色。現在把「星球總覽」用的那張1024×512等距柱狀投影地形貼圖
貼到 `planetBase` 球體上，近處是精細六角格、遠處是低解析度貼圖。
要用格子填到地平線需要約5萬格以上且成本非線性上升，這個做法幾乎不花錢。

- **經緯度對齊**：`SphereGeometry` 的 `u = (經度+180)/360`，與後端產生貼圖的取樣方式完全一致，
  不需要額外旋轉。`DataTexture` 預設 `flipY=false`，必須設成 `true`（貼圖第0列是北極、
  而 `uv.y=1` 對應北極），否則南北顛倒。
  已實測驗證：7個座標（含換日線179°與近極80°）的貼圖取樣顏色與該處實際地形的白天色**完全相同**。
- **時段染色**：貼圖用的是各地形的「白天色」，近處格子卻會依時段換色，邊界會有亮度落差。
  用資料庫裡參考地形的「該時段色 ÷ 白天色」當倍率去染 `material.color`，
  倍率因此仍來自資料庫，不在前端另寫一組明暗參數。
- **2MB 的貼圖非同步載入**，不放進 `PlanetLoadingGate` 一起等，以免拖慢開場；未到位前維持純色。

### 順帶發現：海水一直都不是用格子畫的

海水地塊的海拔一律是 **-500級**（下限），誇張8倍後在半徑 49.46，
而 `planetBase` 在 49.95 —— **所有海水地塊其實都被這顆球蓋住、從來沒有真的畫出來過**，
一直是由那顆純色球代表。海本來就是平的，用球面表示反而合理，
貼上貼圖之後海的顏色也跟著資料庫的地形樣式走了。但要注意：海面上看不到六角格細節、
也不會有格子的時段換色（只有上面那層整體染色）。

## 待決定 / 尚未實作

- 真正的河流水文模擬（依地形高度、連通性計算水流路徑）——`ITileNeighborResolver` 已完成，理論上可以接上做「水往低處流」的連通模擬，但目前的 River 仍只是noise近似的視覺效果，尚未真正利用鄰接資料
  → 已納入**地質演化模擬模式**的規劃（2026-09-05），見
  [geological-simulation-plan.md](geological-simulation-plan.md) 階段4「外營力與地層堆積」。
  那個模式會從「平坦、全海洋的裸星球」開始跑板塊運動與各種侵蝕作用，河流是其中一環。
- ~~前端Tile渲染是「每格各自獨立的切平面近似多邊形」，不是依實際鄰居精算共邊頂點的Voronoi對偶網格~~
  → **已於2026-08-31完成**，見上方「格子改成真正密合的蜂窩」。角落改由相鄰三格共同決定，
  實測3,600組相鄰配對全部完整共用一條邊。只有patch最外圈（鄰居不在patch內）仍用近似畫法，且在畫面外。
