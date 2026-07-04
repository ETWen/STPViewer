# STPViewer — Claude 專案記憶

## 專案簡介

CAD 3D 檢視器（Windows 桌面 WPF, .NET 8）：STEP/STL/DXF 匯入、STEP 裝配樹（搜尋/隔離顯示）、
點/距離/邊/面/圓/角度/面距/體積質心量測（吸附含圓心）、兩點對齊（平移）、三點對齊（旋轉+平移）、
軸向旋轉 90°、拖曳模式、干涉檢查（≥2 檔兩兩配對）、剖面（X/Y/Z + 3點任意平面）、
標準視圖+正交投影、量測快速鍵+Esc、mm⇄inch、CSV/截圖/STEP/STL 匯出（STL 有參數對話框）、設定與 MRU 保存。
詳細設計見 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 技術棧

- .NET 8 WPF（`net8.0-windows`）、MVVM（CommunityToolkit.Mvvm）
- **CADability**（純 C# CAD kernel）：STEP 匯入、B-rep 幾何、Face 三角化
- **HelixToolkit.Wpf**：3D viewport、相機、HitTest

## 常用指令

```bash
dotnet build STPViewer.sln
dotnet run --project src/STPViewer
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

測試模型放 `For_AI/`（gitignored，**客戶料號不入 git**）：`For_AI/test.stp`（Amphenol RA PHD 座端，小檔）與
`For_AI/Amphenol PHD to PHD Cable 10201248 1.stp`（39MB 大組件，效能測試用）。`*.stp`/`*.step` 已全域 gitignore

## 開發慣例

- MVVM：邏輯寫 ViewModel/Service，code-behind 只做純 View 事件（滑鼠拾取轉發）
- `MainViewModel` 是 **partial class 分檔**（v0.4.0）：主檔（匯入/樹/量測/變換/邊線）+
  `.Drag` / `.Gizmo` / `.Section` / `.Interference` / `.Export`。加新功能先看職責放哪個檔，勿全塞回主檔
- 全域例外處理在 `App.xaml.cs`：UI 執行緒未攔截例外 → 記 log（`%LOCALAPPDATA%\STPViewer\error.log`）+
  訊息框 + `Handled=true` 存活；背景 Task 例外記 log。個別功能仍應自行 try/catch 給出有意義的狀態列訊息
- 一個 B-rep Face = 一個 `GeometryModel3D`，用 `Dictionary<Model3D, FaceInfo>` (`_faceMap`) 反查（剖面模式逐面拾取靠這個，**不可移除逐面結構**）
- 渲染雙模式（降 draw call）：每個 leaf 同時持有 `FacesContent`（逐面，剖面用）與 `MergedContent`（整零件合併成 1 個 `GeometryModel3D`）。
  `ApplyRenderMode()` 依狀態切 `BodyVisual.Content`：**非剖面（瀏覽＋量測皆是）→ 合併網格**；**剖面 → 逐面**（要顯示各面裁切後幾何）。
  合併網格只代表「未剖切、目前位置」幾何；平移後呼叫 `RebuildMerged(leaf)` 同步。剖面/干涉/`RebuildMerged` 一律走 `FacesContent`，與目前顯示哪種內容無關
- ⚡ **量測拾取打合併網格、用三角形頂點 index 反查面**（`_mergedFaceRanges`：合併 Model → (每面頂點起始邊界, FaceInfo[])）。
  `BuildMergedMesh` 依面序串接（`baseIdx += positions.Count`，無焊接共用頂點），故命中 `RayHit.VertexIndex1` 可二分搜尋（`ResolveMergedFace`）回是哪個面。
  **這是 v0.3.2 修大檔量測卡頓的關鍵**：量測模式不再掛數萬個逐面 `GeometryModel3D`（64k 面實測 = 64k draw call，轉動/停下重繪爆量）→ 改成每檔 1 個 model，量測模式 orbit 與瀏覽同樣順。
  邊界由面頂點數決定、平移不改 → 永久有效，`RebuildMerged` 不需重算。**不要因為「量測要逐面」而把渲染改回逐面**
- 合併網格的 `BackMaterial`：封閉實體（`SolidCount≥1` 且 `HasBrep`）**不設**（WPF 兩面渲染成本砍半）；開放殼/STL 才設。
  **拾取不受材質影響**（WPF 3D hit-test 純幾何、不剔背面），故打合併網格仍命中孔內壁等背向面。逐面 `FacesContent` 一律保留 BackMaterial（剖切要看內部）
- 量測值以 B-rep 為準（圓半徑、邊長、角度），面積/面距/體積/質心用網格近似
- `Snap` 吸附順序（v0.5.0）：頂點與「圓形邊」都在容差內比誰離命中點近，圓邊勝出時回傳**圓心**
  （量孔對孔 pitch 靠這個）。不要把圓心吸附拿掉或改成永遠優先頂點
- 鍵盤快速鍵在 `MainWindow.Window_PreviewKeyDown`：**焦點在 TextBoxBase/ComboBox 時直接 return**
  （樹搜尋框、剖面數值框要能打字），新增輸入控件不用再各自處理
- UI 三層（v0.7.0）：**選單列**（全部功能，分類下拉）+ **快速列**（`QuickBarViewModel` 註冊表，
  使用者勾選常用按鈕，XAML 索引子綁定 `QuickBar[Key].IsChecked` 控 Visibility，按鈕本體維持靜態綁定）+
  **剖面參數列**（只在 SectionEnabled 顯示）。新增功能要三處都接：選單分類、快速列註冊表
  （含預設值）+ 對應 XAML 按鈕；勾選清單存 settings.json `QuickBarKeys`（null = 預設）
- 使用者設定 `SettingsService`（%LOCALAPPDATA%\STPViewer\settings.json）：視窗（MainWindow 管）+
  單位/MRU（VM `LoadSettings`/`SaveSettingsInto`）；壞檔回預設、儲存失敗靜默
- 匯出 STEP 走 `CADability.ExportStep.WriteToFile(file, Project.CreateSimpleProject()+Model.Add)`；
  只收 Solid/Shell（STL/DXF 無 B-rep 進不了）；改動要跑 `SmokeTest --export-test` 往返驗證
- 匯出 STL（`StlExportService` + 參數對話框）：**背景寫檔前必須在 UI 執行緒快照 mesh** —
  `GeometryModel3D`/`Model3DGroup` 是 DispatcherObject 跨執行緒會炸（v0.6.0 踩過），
  背景只能碰 frozen `MeshGeometry3D` 與 CADability B-rep。精度只有「目前網格/精細」兩檔：
  CADability `GetTriangulation` 對比快取粗的精度回傳既有快取（粗化無效），且三角形數對精度
  **非單調**（0.4× 反而比 1× 少），精細 = 0.15×（匯入 clamp 後再乘，保證嚴格更細）。
  改動要跑 `SmokeTest --stl-export-test For_AI/test.stp`
- 3點剖面（SectionAxisIndex==3）：`_customNormal` null = 拾取中（`HandleSectionPlanePick` 在
  OnViewportClick 最前面攔點擊）；換軸/關剖面/Esc 會重置。平面位置一律用「AABB 8 角投影到法向」內插，
  軸向與任意法向共用同一段程式，不要改回逐軸特化
- 量測文字一律 `Func<UnitSystem,string>` 延後產生（mm⇄inch 即時切換）；內部數值永遠存 mm
- 裝配樹節點（`ModelNodeViewModel`）的可見性/邊線/顏色向下 cascade
- 剖面只換 `GeometryModel3D.Geometry`（`FaceInfo.Mesh` 保留原始 frozen mesh 供還原與量測）
- ⚡ 剖面裁切是**背景平行**（v0.4.0）：`ApplySection` 在 UI 快照 (model, frozen mesh) → `Task.Run`+`Parallel.For`
  裁切 → 回 UI 一次換上；`_sectionApplying`/`_sectionReapply` guard 保證裁切中的新變更（滑桿/換軸/`TransformRoot`）
  完成後用最新參數重跑。**不要改回同步呼叫 ClipMesh**（64k 面整場景裁切會凍結 UI 數秒）；
  await 之後一定要檢查 `SectionEnabled`（期間可能被關掉，還原分支已處理）
- 剛體變換（兩點對齊/旋轉 90°/三點對齊）統一走 `TransformRoot(root, ModOp, Matrix3D)`：B-rep 用 ModOp 對 Solid/Shell 整體 `Modify`
  （勿逐面位移，會重複位移共用邊），網格/合併網格/邊線/邊界同步重算；變換後量測已失效要 `ClearMeasurements()`。
  **op 與 m 必須是同一個變換** — 數學在 `Services/RigidAlign.cs`：WPF `Matrix3D` 是「列向量」約定、CADability `ModOp` 是「行向量」約定，
  `ToModOp` 負責轉置轉換，改動務必跑 `SmokeTest --align-test` 驗證兩種表示一致，否則 B-rep 與顯示網格會悄悄分家。
  `TransformRoot` 的網格變換是**面級平行**（v0.4.0；來源/輸出 mesh 皆 frozen 所以安全）；
  B-rep `Modify` 與視覺樹賦值必須維持循序（CADability 非執行緒安全 / WPF 執行緒親和）
- IsBusy 期間匯入/旋轉 90°/干涉檢查指令停用（`CanExecute = nameof(IsIdle)` + isBusy 的
  `NotifyCanExecuteChangedFor`）；`ImportFilesAsync` 整批維持 busy 且開頭擋重入
  （拖放/命令列會繞過 CanExecute）。新增「會改動幾何的指令」時記得比照辦理
- 拖曳模式（`MeasureMode.Drag`）：拖曳中只掛**暫時 `TranslateTransform3D`**（GPU 免費），放開才一次性 `TranslateRoot` 烘進 B-rep —
  **不要改成拖曳中逐幀 TransformRoot**（大檔每幀重建網格會卡死）。2D→3D 用 Helix `UnProject`（過錨點、法向=相機 LookDirection 的平面）。
  合併網格的 hit-test 走 `_mergedMap`（合併 Model → leaf）；拖曳中邊線用 `_edgesSuspended` 暫停
- ⚠️ **`Visual3D.Transform` 永遠不要設成 `null`，清除要用 `Transform3D.Identity`**。HelixToolkit `Viewport3DHelper.GetTransform`
  對 `child.Transform` 沒做 null 檢查（`GeneralTransform3DGroup.Children.Add(null)` → 拋「無法新增空值到集合中」），
  之後任何 `FindHits` 都會 crash。v0.2.1 修的就是拖曳放開時把 BodyVisual.Transform 設 null（拖過一次後再點擊就炸）。
  Gizmo 的暫時 Transform 清除同理一律用 Identity
- Gizmo 操作器：Helix `TranslateManipulator`/`RotateManipulator` ×6 `Bind` 到代理 `ModelVisual3D`，
  代理 Transform 變更（`DependencyPropertyDescriptor.AddValueChanged`）即時套到目標 BodyVisual（暫時）；
  **放開滑鼠才 `TransformRoot` 烘焙** — MouseUp 被 manipulator 標 handled，MainWindow 用 `AddHandler(..., handledEventsToo: true)` 才收得到。
  烘焙用 `Dispatcher.BeginInvoke` 延後到 manipulator 自身事件處理完，避免 reentrancy；`_gizmoBaking` 旗標防 Transform 歸零的回呼重入
- Gizmo always-on-top（v0.3.1）：操作器放在獨立透明 `Viewport3D`（`gizmoOverlay`）疊在主視窗上，**不在主場景所以永不被實體遮擋**。
  `_overlayCamera` 在主 `Camera.Changed` 時同步主相機（Position/方向/FOV/near-far）；raw Viewport3D 空白處不吃滑鼠 → 穿透回主視窗（orbit/量測正常）；
  `IsHitTestVisible` 綁 `GizmoEnabled`。Manipulator 是 `UIElement3D`、用 `GetViewport3D()` 抓所在層相機，故在疊圖層用同步相機運作。
  放開事件靠 overlay 的 `AddHandler(MouseLeftButtonUp, handledEventsToo:true)`（manipulator 會標 handled），`_gizmoBakePending` 防同次重複烘焙
- 干涉/面距/對齊等運算在背景執行緒；`Freeze()` 幾何後才跨執行緒
- 匯入在背景執行緒；`Freeze()` 幾何後才跨執行緒
- 發新版：升 csproj `<Version>` 之外，記得在 `AboutDialog.xaml.cs` 的 `Changelog` 陣列**頂端**加一筆
  （口吻寫給一般使用者：「你會感覺到什麼」，不是技術 changelog）。
  ⚠️ WPF **頂層 MenuItem 直接掛 Click 不會觸發**（實測滑鼠/鍵盤都沒反應），要開視窗的入口一律放子選單（如 說明 → 關於）
- Commit 格式：Conventional Commits（`feat:` / `fix:` / `docs:` …）

## 注意事項 / 已知限制

- `Path` 在 service 會與 `CADability.GeoObject.Path` 撞名 → 用 `IOPath` alias
- CADability 解析大 STEP 慢（39MB/64k 面實測：解析約 276 秒 + 幾何處理），不要改成同步呼叫。
  解析（`ImportStep.Read`）單執行緒無解；三角化/邊取樣已按 **leaf 平行化**（`_leafWork` 收集 → `Parallel.ForEach`）。
  **平行粒度只能到 leaf**：同 leaf 的面共用 Edge 物件，面級平行會 race。空 leaf 由 `Prune` 收掉（延後三角化可能全失敗）。
  平行下 `GetTriangulation` 偶發失敗（實測 64k 面丟 ~8 面，跨 leaf 仍有共享狀態）→ 失敗面收進 `_retry`，平行結束後**循序重試**補回，
  該 leaf 的 `FinishLeaf` 也延到重試後才跑。**不要移除重試機制**，也不要把平行度開到面級
- `StepImportService.Progress` 回報匯入階段（解析/三角化耗時），UI 已接狀態列；訊息來自背景執行緒，要 `Dispatcher.BeginInvoke`
- `LinesVisual3D` 轉動視角逐幀重建，>30k 線段會卡 → 邊線自動關閉邏輯不要移除
- 邊線採「**一檔一條合併 `LinesVisual3D`**」（掛在 root `EdgeVisual`，由 `RefreshRootEdges` 收集各 leaf `OriginalEdgePoints` 重建）。
  **不要改回逐 leaf 一條** — 裝配樹零件多時，N 條線每幀重建會嚴重卡頓（實測主因）。leaf 只保留邊線「資料」，渲染統一在 root；
  可見性/ShowEdges/剖面/平移變更時呼叫 `RefreshRootEdges(root)` 重組合併線
- 互動中暫停邊線：`Attach` 掛 **`HelixViewport3D.CameraChanged`（控制項層級 routed event）＋ `Camera.Changed`（保底）** → `OnCameraMoved` 隱藏邊線、`_interactionTimer`(180ms) 停下後 `ResumeEdges` 顯示；
  `_edgesSuspended` 為真時 `RefreshRootEdges` 不把線掛回。轉動/縮放/平移時不付邊線重建成本。
  **只訂 `Camera.Changed` 不夠**：`Attach` 在建構式呼叫，相機實例若被 Helix 換掉訂閱會孤兒化 → 暫停永不觸發；故加訂控制項層級事件保底（重複觸發 `OnCameraMoved` 無害，有 guard）
- CADability `ImportStep` 對少數 AP242 檔案支援不完整；匯入失敗要 catch 顯示訊息，不可閃退
- IGES 無 reader；STL 無 B-rep（FaceInfo.BrepFace == null 的分支要保留）
- 不寫入原始檔（唯讀工具）；WPF 限 Windows，不要嘗試移植 vbox/Linux
- 干涉檢查需剛好 2 個可見檔案（樹面板勾選）；共面貼合（無穿透）不算干涉、gap≈0 視為配合（match）
- SmokeTest 工具：`--tree`（裝配樹）、`--clip-test`（剖切數學）、`--interference-test`（干涉相交/分離/貼合）、
  `--align-test`（三點對齊剛體變換 + ModOp↔Matrix3D 一致性）、`--make-dxf`（產測試檔）、
  `--export-test <in.stp> <out.stp>`（STEP 匯出往返：寫出→回讀比對實體數）、
  `--stl-export-test [file.stp]`（STL binary/ASCII 往返 + 退化濾除；給 stp 加測 B-rep 精細重算）
- **絕不要用 PowerShell regex/Set-Content 改 .cs 檔** — Windows PowerShell 5.1 預設編碼會把 UTF-8 中文弄成亂碼（已踩過，靠反編譯 DLL 救回）。文字取代一律用 Edit 工具

## For_AI/

- `For_AI/`：AI 協作素材（截圖、草稿）+ 測試模型 `*.stp`（客戶料號），整夾 gitignored。
- 本專案無 runtime secret（離線桌面工具，無 DB/ApiKey），故未保留 `secret/` 資料夾。
