# STPViewer — Architecture

> STP/STEP 3D 檢視器：多檔匯入、圖層管理、點/線/面/圓量測（C# .NET 8 WPF 桌面程式）

---

## Overview

STPViewer 是一套 Windows 桌面 3D CAD 檢視工具，給硬體 / SI 工程師快速開啟機構件的
STEP（`.stp` / `.step`）檔案做確認與量測，不需要安裝 SolidWorks / Creo 等重量級 CAD。

核心價值：

- **多檔匯入**：一次載入多個 CAD 檔（STEP / STL / DXF），每個檔案自成一棵樹
- **裝配樹**：STEP product structure 還原成樹狀節點（組件→零件），逐節點 顯示/隱藏、換色、Zoom-to
- **量測**：點座標、兩點距離、邊長、面（面積/類型/法向量）、圓（心/半徑/直徑/周長）、
  兩面/兩邊夾角、面到面最短距離；單位 mm ⇄ inch 即時切換
- **剖面**：X/Y/Z 軸向剖切，位置滑桿 + 反向，CPU 網格裁切（量測不受影響）
- **匯出**：量測結果 CSV、3D 視圖 PNG 截圖（2x）
- 單機離線執行，無網路相依

使用者：單一桌面使用者（無登入 / 角色系統）。

---

## Tech Stack

| Layer | Technology | 說明 |
|---|---|---|
| Runtime | .NET 8（`net8.0-windows`） | 桌面 WPF |
| UI Framework | WPF + MVVM（CommunityToolkit.Mvvm） | |
| 3D 渲染 / 拾取 | **HelixToolkit.Wpf** | Viewport3D 封裝、相機操作、HitTest |
| STEP 解析 / 幾何核心 | **CADability**（純 C#，netstandard2.0） | STEP B-rep 匯入、Face 三角化、邊/面幾何查詢 |
| 量測幾何運算 | CADability（B-rep 精確值）+ 網格近似（面積） | |
| 打包 | `dotnet publish -c Release` | 免安裝、單資料夾 |

> 技術選型備註：OpenCASCADE 的 .NET wrapper（Macad.Occt 等）不在 NuGet 上，
> 自建 C++/CLI wrapper 成本過高；CADability 是純 C# 的 CAD kernel（MIT），
> 內建 STEP reader 與三角化，與 .NET 8 相容（netstandard2.0），故採用。

---

## Architecture Diagram

```
┌────────────────────────── MainWindow (WPF) ──────────────────────────┐
│  Toolbar(匯入/量測模式)   LayerPanel    HelixViewport3D    量測結果面板 │
└──────────────┬───────────────┬───────────────┬───────────────────────┘
               │ ICommand      │ binding       │ MouseDown(HitTest)
               ▼               ▼               ▼
┌──────────────────────── MainViewModel (MVVM) ────────────────────────┐
│  Layers: ObservableCollection<LayerItemViewModel>                    │
│  Measurements: ObservableCollection<MeasurementResult>               │
│  CurrentMode: None/Point/Distance/Edge/Face/Circle                   │
└───────┬──────────────────────────────┬───────────────────────────────┘
        ▼                              ▼
┌─ StepImportService ─────────┐  ┌─ MeasurementService ───────────────┐
│ CADability ImportStep.Read  │  │ Hit → GeometryModel3D → FaceInfo   │
│ Solid→Shell→Face 三角化     │  │ 點: 頂點吸附 / 表面點              │
│ Edge 取樣折線(輪廓線)       │  │ 邊: 最近 Edge → Line/Ellipse 判型   │
│ → MeshGeometry3D + FaceInfo │  │ 面: 面積(網格Σ)+Surface 類型        │
└─────────────────────────────┘  │ 圓: 圓形 Edge → 心/半徑/直徑/周長  │
                                 └────────────────────────────────────┘
```

資料流：`*.stp → CADability B-rep → 三角網格(渲染) + B-rep 參照(量測) → Helix Viewport`

每個 Face 對應一個 `GeometryModel3D`，並登錄到 `Dictionary<GeometryModel3D, FaceInfo>`，
HitTest 命中後可反查回 B-rep Face / Edge 做精確量測。

---

## Project Structure

```
STPViewer/
│
├── CLAUDE.md                  # 專案記憶 & 給 Claude 的指令
├── README.md                  # 快速上手
├── ARCHITECTURE.md            # 本文件
├── .gitignore                 # 含 secret/ 與 For_AI/ 規則
│
├── docs/
│   ├── decisions/             # 技術決策紀錄（ADR）
│   └── runbooks/              # 操作手冊
│
├── secret/                    # 🚫 gitignored（README.md / *.example 除外）
│   ├── README.md
│   ├── run-STPViewer.ps1.example
│   └── run-STPViewer.sh.example
│
├── For_AI/                    # 🚫 gitignored — AI 協作素材 + 測試模型 *.stp（客戶料號不入 git）
│
├── STPViewer.sln
│
└── src/
    └── STPViewer/
        ├── STPViewer.csproj           # net8.0-windows, UseWPF
        ├── App.xaml / App.xaml.cs
        ├── MainWindow.xaml / .cs      # 版面 + 滑鼠拾取事件
        │
        ├── Models/
        │   ├── FaceInfo.cs            # GeometryModel3D ↔ B-rep Face 對照（STL 為 null）
        │   ├── MeasureMode.cs         # enum: None/Point/Distance/Edge/Face/Circle/Angle/FaceDistance/Align/Align3/Drag/Interference
        │   ├── MeasurementResult.cs   # 量測結果（雙單位 lambda、3D 標籤同步）
        │   └── UnitSystem.cs          # mm/inch + Units 格式化
        │
        ├── Services/
        │   ├── StepImportService.cs   # STEP/STL/DXF 讀檔 + 三角化 + 裝配樹
        │   ├── MeasurementService.cs  # 點/線/面/圓/角度/面距 幾何計算
        │   ├── InterferenceService.cs # 干涉檢查：三角形-三角形相交(區間法)+均勻網格加速；無干涉時近似最小間隙
        │   ├── RigidAlign.cs          # 三點對齊/旋轉的剛體變換數學（Matrix3D列向量 ↔ ModOp行向量 轉換）
        │   └── SectionService.cs      # 剖面：網格/線段半空間裁切
        │
        └── ViewModels/
            ├── MainViewModel.cs       # 樹集合、量測集合、剖面、單位、匯出
            └── ModelNodeViewModel.cs  # 裝配樹節點（可見性/顏色 cascade）
```

---

## Data Models

桌面程式無資料庫；核心為記憶體內模型：

```csharp
// 一個匯入檔 = 一個圖層
class LayerItemViewModel
{
    string  Name;            // 檔名（不含路徑）
    string  FilePath;
    bool    IsVisible;       // 切換 viewport 中的 ModelVisual3D
    Color   Color;           // 圖層色（換色重建材質）
    int     SolidCount, FaceCount, TriangleCount;
    ModelVisual3D BodyVisual;    // 面網格
    ModelVisual3D EdgeVisual;    // 輪廓線
    Rect3D  Bounds;          // Zoom-to 用
}

// HitTest 反查：渲染物件 → B-rep
class FaceInfo
{
    object Face;             // CADability Face（量測用 B-rep）
    LayerItemViewModel Owner;
    MeshGeometry3D Mesh;     // 面積近似 / 頂點吸附
}

enum MeasureMode { None, Point, Distance, Edge, Face, Circle,
                   Angle, FaceDistance, Align, Align3, Drag, Interference }

class MeasurementResult
{
    MeasureMode Kind;
    string Title;            // "P1 (12.30, 4.50, 0.00)"
    string Detail;           // 多行明細（Δ、半徑、面積…）
    List<Visual3D> Overlays; // 視圖中的標記（刪除量測時一併移除）
}
```

單位：STEP 內部以 mm 為準（CADability 匯入時依檔內單位換算），UI 顯示 mm。

---

## Key Features

| 功能 | 操作 | 輸出 |
|---|---|---|
| 匯入 STP | 工具列「匯入」(可複選) / 拖放檔案 | 新圖層 + 自動 ZoomExtents |
| 圖層 | 面板勾選顯示、換色、Zoom-to、移除 | 即時反映於 3D 視圖 |
| 量測-點 | 模式「點」+ 點擊模型 | 座標（優先吸附頂點/邊端點） |
| 量測-距離 | 模式「距離」+ 點兩下 | 直線距離 + ΔX/ΔY/ΔZ + 視圖連線 |
| 量測-邊 | 模式「邊」+ 點擊邊附近 | 線段長/曲線長；圓弧附半徑 |
| 量測-面 | 模式「面」+ 點擊面 | 面積、曲面類型、平面法向量/圓柱半徑 |
| 量測-圓 | 模式「圓」+ 點擊圓孔邊緣 | 圓心、半徑、直徑、周長 + 視圖圓心標記 |
| 量測-角度 | 模式「∠」+ 點兩個面（或靠近直線邊） | 夾角 + 補角（面取法向量、邊取方向） |
| 量測-面距 | 模式「⇔」+ 點兩個面 | 面到面最短距離（網格近似）+ 最近點對連線 |
| 對齊 | 模式「對齊」+ 點「要移動零件」上一點、再點目標點 | 純平移該檔案使點1貼到點2（B-rep 用 `ModOp` 整體位移，量測清空） |
| 三點對齊 | 模式「三點」+ 來源檔 3 點、目標檔 3 對應點 | 旋轉+平移剛體變換（點1精確貼合、1→2 方向對齊、三點平面對齊；`RigidAlign`） |
| 旋轉 | 樹面板選檔案 + 工具列 ↻X/↻Y/↻Z | 繞檔案中心 +90°（連按累加；方向不合時先轉正再對齊） |
| 拖曳 | 模式「🖐 拖曳」（手形游標）+ 左鍵按住零件拖 | 沿螢幕平面移動該檔案，放開烘進 B-rep（拖曳中僅暫時 Transform，不卡） |
| 操作器 | 「⊹ 操作器」+ 樹面板選檔案 | XYZ 箭頭沿軸移動、旋轉環繞軸轉任意角度（與視角無關）；每次放開烘進 B-rep |
| 干涉 | 工具列「🧩 干涉」（需剛好 2 個可見檔案） | 相交→紅色交線+相交三角形對數；無相交→最小間隙 gap（≈0 即配合 match） |
| 剖面 | 工具列「✂ 剖面」+ 軸向/位置/反向 | CPU 裁切渲染網格（原始幾何保留，量測仍精確） |
| 單位 | 工具列 mm ⇄ in | 既有量測（清單+3D 標籤）即時換算 |
| 匯出 | 💾 CSV / 📷 截圖 | UTF-8 BOM CSV；2x PNG |
| 視圖 | 滑鼠右鍵旋轉/滾輪縮放/中鍵平移（Helix 預設）、ViewCube | |

支援格式：`.stp` / `.step`（B-rep + 裝配樹）、`.stl`（純網格，僅點/距離/角度/面距量測）、
`.dxf`（線架構檢視）。**IGES 不支援**（CADability 無 IGES reader）。

---

## Key Constraints & Business Rules

1. 每個匯入檔案 = 一個圖層；同檔重複匯入產生新圖層（後綴 `(2)`）
2. 量測一律以 **B-rep 幾何** 為準（邊長、圓半徑）；僅「面積」用網格加總近似（三角化精度內）
3. 隱藏圖層不參與 HitTest（量測不會打到看不見的東西）
4. 移除圖層時，其上的量測 overlay 一併清除
5. 三角化精度依物件尺寸自適應（對角線 × 0.0015，限 0.02–0.5 mm）；大型組件匯入走背景執行緒，UI 不凍結
6. 不寫入/修改原始 STP 檔（唯讀檢視器）

---

## Security Considerations

- 純離線桌面工具，無網路、無帳號、無資料庫
- 本專案目前無任何 runtime secret；`secret/` 仍依專案慣例建立：
  - `secret/*` 全部 gitignore，僅 `README.md`、`*.example` 進 git
  - `run-STPViewer.*.example` 為啟動腳本範本（本專案無 DB/ApiKey，腳本僅做 build+run）
  - 無 compile-time secret → 不需 `publish-*.example` 腳本
- `For_AI/` 收 AI 協作素材（截圖、筆記），整夾 gitignore

---

## Build & Setup Steps

```bash
cd E:/10_AI/STPViewer

# 還原 + 建置
dotnet build STPViewer.sln -c Debug

# 執行
dotnet run --project src/STPViewer

# 發佈（免安裝資料夾）
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

NuGet 相依（自動還原）：`CADability`、`HelixToolkit.Wpf`、`CommunityToolkit.Mvvm`

---

## Development Phases

### Phase 1 — 專案骨架 + 3D 視窗（工作量：S）
**目標：** 專案能跑起來，出現含 Helix 3D viewport 的主視窗
**包含：**
- [x] `STPViewer.sln` + `src/STPViewer/STPViewer.csproj`（net8.0-windows、UseWPF、NuGet 三件套）
- [x] `MainWindow.xaml`：工具列 / 左側圖層面板 / 中央 `HelixViewport3D`（含 ViewCube、預設光源）/ 右側量測面板 / 底部狀態列
- [x] `MainViewModel.cs` 空殼 + DataContext 接線
**驗收條件：** `dotnet run` 開出主視窗，3D 區可旋轉縮放（空場景 + 格線）

### Phase 2 — STEP 匯入與渲染（工作量：L）
**目標：** 可開啟單一 STP 並看到實體模型
**包含：**
- [x] `StepImportService.cs`：CADability `ImportStep` 讀檔 → Solid/Shell/Face 三角化 → `MeshGeometry3D`
- [x] Face→`GeometryModel3D` 一對一、建 `FaceInfo` 對照字典
- [x] Edge 取樣折線 → `LinesVisual3D` 輪廓線（CAD 外觀）
- [x] 匯入走 `Task.Run`，完成後 UI 執行緒組 Visual + `ZoomExtents`
- [x] 用根目錄 Amphenol STP 驗證
**驗收條件：** 匯入 Amphenol STP 顯示正確 3D 模型（含輪廓線），視角操作流暢

### Phase 3 — 圖層系統（工作量：M）
**目標：** 多檔匯入、各自成層、可管理
**包含：**
- [x] 「匯入」支援複選 + 檔案拖放
- [x] `LayerItemViewModel.cs`：名稱、可見性 checkbox、色塊（調色盤換色）、統計（Solid/Face/三角形數）
- [x] 圖層操作：顯示/隱藏（含 HitTest 排除）、Zoom-to、移除（連帶清 overlay）
**驗收條件：** 匯入 2+ 個 STP，逐層開關/換色/移除皆即時生效

### Phase 4 — 量測功能（工作量：L）
**目標：** 點 / 距離 / 邊 / 面 / 圓 五種量測可用
**包含：**
- [x] `MeasurementService.cs` + `MeasureMode` 工具列切換（互斥 toggle）
- [x] 點：HitTest 命中點 + 頂點/邊端點吸附；距離：兩點 + ΔXYZ + 視圖連線
- [x] 邊：命中面最近 Edge，`Line`→長度、`Ellipse(IsCircle)`→弧長+半徑、其他→曲線長
- [x] 面：網格面積加總 + Surface 類型（平面法向量 / 圓柱半徑）
- [x] 圓：搜尋最近圓形 Edge → 圓心/半徑/直徑/周長 + 圓心標記
- [x] 量測結果面板：清單 + 單筆刪除 + 全部清除（overlay 同步移除）
**驗收條件：** 對 Amphenol STP 可量出 pin 孔圓徑、殼體面積、兩點距離，數值合理

### Phase 5 — 整合收尾（工作量：S）
**目標：** 穩定可交付
**包含：**
- [x] 錯誤處理（壞檔/非 STEP → 訊息列提示不閃退）、匯入進度提示
- [x] 狀態列模式提示（「點選第 2 點…」）
- [x] `README.md`、`CLAUDE.md` 完稿
- [x] `dotnet publish -c Release` 驗證 + smoke test（無 UI 載檔驗證管線）
**驗收條件：** Release 發佈資料夾雙擊可用；載入壞檔不閃退

---

## Development Phases — 第二輪（Future Extensions 實作，全部完成）

### Phase 6 — Future Extensions（工作量：L）
- [x] 剖面（Section plane）檢視 — `SectionService` CPU 網格/線段裁切 + 軸向/位置/反向控制 + 半透明剖面指示
- [x] 角度量測（兩面/兩邊夾角，含補角）、面到面最短距離（頂點→三角形雙向，網格近似）
- [x] 量測結果匯出 CSV（UTF-8 BOM）/ 視圖 PNG 截圖（2x）
- [x] 裝配樹（STEP `HierarchyToBlocks` product structure）取代「一檔一層」，節點層級 顯示/換色/Zoom
- [x] STL / DXF 格式支援（IGES 落空：CADability 無 IGES reader，誠實不支援）
- [x] 量測單位切換 mm ⇄ inch（清單與 3D 標籤即時換算，內部一律存 mm）

**驗收：** ClipTest 裁切數學 5 項全過；STEP/STL/DXF 三格式 smoke test 通過；UI 端到端存活

---

## Development Phases — 第三輪（裝配驗證，全部完成）

### Phase 7 — 配合 / 干涉驗證（工作量：M）
- [x] 兩點對齊（`Align`）— 點「要移動零件」一點 + 目標點 → 純平移整個檔案使其貼合；
      B-rep 用 `CADability.ModOp.Translate` 對 Solid/Shell 整體 `Modify`，網格/邊線/邊界同步重建，量測清空
- [x] 干涉檢查（`InterferenceService`）— 三角形-三角形相交（區間法回傳交線段）+ 均勻網格空間加速；
      相交→紅色交線 overlay；無相交→近似最小間隙 gap（共面貼合不算穿透）
- [x] SmokeTest `--interference-test`：相交 / 分離(gap≈20) / 貼合(gap≈0) 三情境數學驗證

**驗收：** InterferenceTest 3 情境全過；兩件 STEP 勾選後可判定干涉或回報配合間隙

### Phase 8 — 旋轉對齊（工作量：M）
- [x] 軸向旋轉 — 樹面板選檔案 + 工具列 ↻X/↻Y/↻Z，繞檔案 Bounds 中心 +90°（方向不合先轉正）
- [x] 三點對齊（`Align3`）— 來源檔 3 特徵點 → 目標檔 3 對應點，解旋轉+平移剛體變換一次貼合
- [x] `RigidAlign` 數學服務 — `TryRigidTransform`（座標架法）、`ToModOp`（WPF Matrix3D 列向量 ↔ CADability ModOp 行向量轉置）
- [x] 通用 `TransformRoot`（取代平移專用路徑）：B-rep ModOp Modify + 網格/合併網格/邊線重算 + `RecomputeBounds`
- [x] SmokeTest `--align-test`：已知變換還原（誤差 ~1e-15）、ModOp↔Matrix3D 一致、共線拒絕

**驗收：** AlignTest 8 項全過；公母連接器可旋轉擺正後三點對齊插合，再用干涉檢查驗證配合

### Phase 9 — 拖曳模式（工作量：M）
- [x] 🖐 拖曳模式（`MeasureMode.Drag`）— 手形游標，左鍵按住零件沿螢幕平面拖動（Helix `UnProject` 投影到過錨點、面向相機的平面）
- [x] 拖曳中僅掛暫時 `TranslateTransform3D`（GPU 端，大組件不卡），放開一次性 `TranslateRoot` 烘進 B-rep（量測精度不受影響）
- [x] `_mergedMap`（合併網格 → leaf 反查）支援瀏覽渲染下的 hit-test；拖曳中邊線暫停
- [x] 滑鼠捕捉（拖出視窗持續）、意外中斷以當前位置定格

**驗收：** 大組件手形拖曳流暢；放開後干涉/量測結果與拖曳位置一致；右鍵視角操作不受影響

> v0.2.1 修正：拖曳放開時清除暫時位移誤用 `Transform = null`，導致下次 `FindHits` 命中該檔案時
> HelixToolkit `GetTransform` 對 null transform `Add` 而 crash。改用 `Transform3D.Identity`。

### Phase 10 — Gizmo 三軸操作器（工作量：M）
- [x] 「⊹ 操作器」toggle — 對樹面板選取的檔案顯示 Helix `TranslateManipulator` ×3（XYZ 紅綠藍箭頭）+ `RotateManipulator` ×3（旋轉環），尺寸隨檔案 Bounds 自適應
- [x] 操作器綁定代理 `ModelVisual3D`，拖動中代理 Transform 即時套到目標所有 BodyVisual（暫時、GPU 端）；邊線暫停
- [x] 放開滑鼠（`handledEventsToo` 捕捉）→ `Dispatcher.BeginInvoke` 延後 `TransformRoot` 烘進 B-rep、操作器歸零並移到新中心
- [x] 換樹選取自動換目標；目標被移除自動收掉；暫時 Transform 清除用 `Identity`（非 null）

**驗收：** 拖 X 箭頭零件僅沿世界 X 移動（與視角無關）；拖環任意角度旋轉；放開後量測/干涉與顯示位置一致

### Phase 11 — Gizmo always-on-top 疊圖層（工作量：M）
- [x] 操作器移到獨立透明 `Viewport3D`（`gizmoOverlay`）疊在主視窗上 → 不在主場景，**永不被實體遮擋、永遠可抓**
- [x] `_overlayCamera` 在主相機 `Changed` 時同步（Position/方向/FOV/near-far）；同尺寸 → 投影一致、操作器精準貼合
- [x] raw Viewport3D 空白處不吃滑鼠 → 穿透回主視窗（orbit/縮放/量測不受影響）；`IsHitTestVisible` 綁 `GizmoEnabled`
- [x] 放開事件改掛 overlay（`handledEventsToo`）；`_gizmoBakePending` 防同次重複烘焙

**驗收：** 操作器從任何視角都浮在零件上可見可抓；開啟操作器時右鍵 orbit / 量測仍正常（穿透）

---

## Future Extensions（下一輪）

- 剖切面封口（cap）填實（目前剖開處可見內部背面材質）
- 樹節點三態 checkbox（部分子節點隱藏時顯示中間態）
- IGES 支援（需引入其他幾何核心或自寫 reader）
- 量測結果匯出含截圖的 PDF 報告
- 兩邊最短距離、邊到面距離
- 視圖狀態（相機、圖層、量測）存檔/還原
