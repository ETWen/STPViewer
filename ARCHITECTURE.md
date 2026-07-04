# STPViewer — Architecture

> STP/STEP 3D 檢視器：多檔匯入、圖層管理、點/線/面/圓量測（C# .NET 8 WPF 桌面程式）

---

## Overview

STPViewer 是一套 Windows 桌面 3D CAD 檢視工具，給硬體 / SI 工程師快速開啟機構件的
STEP（`.stp` / `.step`）檔案做確認與量測，不需要安裝 SolidWorks / Creo 等重量級 CAD。

核心價值：

- **多檔匯入**：一次載入多個 CAD 檔（STEP / STL / DXF），每個檔案自成一棵樹
- **裝配樹**：STEP product structure 還原成樹狀節點（組件→零件），逐節點 顯示/隱藏、換色、Zoom-to；
  名稱搜尋過濾、隔離顯示（只顯示此節點/反轉/全顯）、tooltip 外形尺寸
- **量測**：點座標、兩點距離、邊長、面（面積/類型/法向量）、圓（心/半徑/直徑/周長）、
  兩面/兩邊夾角、面到面最短距離、體積/質心；吸附含**圓心**（量孔對孔 pitch）；
  快速鍵 P/D/E/F/C/A/M + Esc；單位 mm ⇄ inch 即時切換
- **剖面**：X/Y/Z 軸向 + **3點任意平面**，位置滑桿/數值輸入 + 反向，CPU 網格裁切（量測不受影響）
- **視圖**：標準視圖（等角/前/上/右）+ 正交⇄透視投影
- **匯出**：量測結果 CSV、3D 視圖 PNG 截圖（2x）、**目前對齊位置寫成新 STEP 檔**、
  **STL 網格檔**（參數對話框：合併/每檔、binary/ASCII、mm/inch/自訂縮放、精度，確認再匯出）
- 單機離線執行，無網路相依；視窗/單位/最近檔案自動保存（settings.json）

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

每個 Face 對應一個 `GeometryModel3D`（`FacesContent`，剖面模式渲染用），並登錄到 `_faceMap` 反查 B-rep Face / Edge。

效能：非剖面（瀏覽＋量測）渲染**整零件合併網格**（`MergedContent`，每檔 1 個 model），量測 HitTest 改打合併網格、
用命中三角形的頂點 index 經 `_mergedFaceRanges` 二分搜尋（`ResolveMergedFace`）反查回是哪個面 —
量測模式不再掛數萬個逐面 model，大組件量測下轉動視角與瀏覽同樣流暢（v0.3.2）。

---

## Project Structure

```
STPViewer/
│
├── CLAUDE.md                  # 專案記憶 & 給 Claude 的指令
├── README.md                  # 快速上手
├── ARCHITECTURE.md            # 本文件
├── .gitignore                 # For_AI/ + *.stp/*.step（客戶料號）規則
│
├── docs/
│   ├── decisions/             # 技術決策紀錄（ADR）
│   └── runbooks/              # 操作手冊
│
├── For_AI/                    # 🚫 gitignored — AI 協作素材 + 測試模型 *.stp（客戶料號不入 git）
│
├── STPViewer.sln
│
└── src/
    └── STPViewer/
        ├── STPViewer.csproj           # net8.0-windows, UseWPF
        ├── App.xaml / App.xaml.cs     # 全域例外處理（log + 訊息框存活；%LOCALAPPDATA%\STPViewer\error.log）
        ├── MainWindow.xaml / .cs      # 版面 + 滑鼠拾取事件
        │
        ├── Models/
        │   ├── FaceInfo.cs            # GeometryModel3D ↔ B-rep Face 對照（STL 為 null）
        │   ├── MeasureMode.cs         # enum: None/Point/Distance/Edge/Face/Circle/Angle/FaceDistance/Align/Align3/Drag/Interference/Volume
        │   ├── MeasurementResult.cs   # 量測結果（雙單位 lambda、3D 標籤同步）
        │   └── UnitSystem.cs          # mm/inch + Units 格式化
        │
        ├── StlExportDialog.xaml / .cs # STL 匯出參數對話框（範圍/格式/單位/精度 + 預估，確認再匯出）
        │
        ├── Services/
        │   ├── StepImportService.cs   # STEP/STL/DXF 讀檔 + 三角化 + 裝配樹
        │   ├── MeasurementService.cs  # 點/線/面/圓/角度/面距/體積質心 幾何計算（吸附含圓心）
        │   ├── InterferenceService.cs # 干涉檢查：三角形-三角形相交(區間法)+均勻網格加速；無干涉時近似最小間隙
        │   ├── RigidAlign.cs          # 三點對齊/旋轉的剛體變換數學（Matrix3D列向量 ↔ ModOp行向量 轉換）
        │   ├── SectionService.cs      # 剖面：網格/線段半空間裁切
        │   ├── SettingsService.cs     # 使用者設定 settings.json（視窗/單位/MRU；%LOCALAPPDATA%\STPViewer）
        │   └── StlExportService.cs    # STL 匯出：binary/ASCII 寫檔 + B-rep 重新三角化（精細）
        │
        └── ViewModels/                        # MainViewModel 為 partial class，依職責分檔
            ├── MainViewModel.cs               # 核心：匯入、裝配樹、量測、剛體變換、邊線、單位
            ├── MainViewModel.Drag.cs          # 拖曳模式
            ├── MainViewModel.Gizmo.cs         # 三軸操作器 + 疊圖層相機
            ├── MainViewModel.Section.cs       # 剖面（v0.4.0 起背景平行裁切）
            ├── MainViewModel.Interference.cs  # 干涉檢查指令
            ├── MainViewModel.Export.cs        # CSV / 截圖 / STEP / STL 匯出
            ├── StlExportViewModel.cs          # STL 匯出對話框 VM（選項 + 即時摘要）
            └── ModelNodeViewModel.cs          # 裝配樹節點（可見性/顏色 cascade）
```

---

## Data Models

桌面程式無資料庫；核心為記憶體內模型：

```csharp
// 裝配樹節點：root = 匯入檔、group = STEP 裝配、leaf = 零件幾何（可見性/顏色向下 cascade）
class ModelNodeViewModel
{
    string  Name;            // root = 檔名；group/leaf = STEP product 名
    string? FilePath;        // root 才有值
    bool    IsVisible;       // 切換 viewport 中的 ModelVisual3D（cascade）
    bool    IsFilterVisible; // 樹搜尋過濾結果（只影響樹面板顯示）
    Color   Color;           // 節點色（換色只改共用 Brush）
    int     SolidCount, FaceCount, TriangleCount;
    ModelVisual3D BodyVisual;    // leaf：面網格容器（合併/逐面兩種內容切換）
    LinesVisual3D EdgeVisual;    // root：整檔合併輪廓線
    Rect3D  Bounds;          // Zoom-to / 外形尺寸 / 旋轉中心
}

// HitTest 反查：渲染物件 → B-rep
class FaceInfo
{
    Face? BrepFace;          // CADability Face（量測用 B-rep；STL 為 null）
    ModelNodeViewModel Owner;
    MeshGeometry3D Mesh;     // 面積近似 / 頂點吸附
}

enum MeasureMode { None, Point, Distance, Edge, Face, Circle,
                   Angle, FaceDistance, Align, Align3, Drag, Interference, Volume }

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
| 干涉 | 工具列「🧩 干涉」（≥2 個可見檔案，兩兩配對檢查） | 每組配對：相交→紅色交線+相交三角形對數；無相交→最小間隙 gap（≈0 即配合 match） |
| 剖面 | 工具列「✂ 剖面」+ 軸向（X/Y/Z/**3點任意平面**）/位置（滑桿+數值輸入）/反向 | CPU 裁切渲染網格（原始幾何保留，量測仍精確）；3點=在模型上點 3 點定義平面 |
| 單位 | 工具列 mm ⇄ in（隨設定保存） | 既有量測（清單+3D 標籤）即時換算 |
| 匯出 | 💾 CSV / 📷 截圖 / 📤 STEP / 📐 STL | UTF-8 BOM CSV；2x PNG；可見檔案目前位置寫成**新** STEP 檔（對齊結果交接 CAD）；STL 先開參數對話框（範圍/格式/單位/精度 + 三角形數與大小預估）確認再匯出 |
| 視圖 | 滑鼠右鍵旋轉/滾輪縮放/中鍵平移（Helix 預設）、ViewCube、工具列 等角/前/上/右、正交⇄透視 | |
| 快速鍵 | P 點 / D 距離 / E 邊 / F 面 / C 圓 / A 角度 / M 面距；同鍵再按=退出；Esc=取消進行中量測→退出模式 | |
| 樹操作 | 搜尋框過濾節點名稱；右鍵：只顯示此節點 / 反轉顯示 / 全部顯示 / 量體積質心 | tooltip 含外形尺寸 L×W×H |
| 體積/質心 | 樹節點右鍵「⚖ 量體積/質心」 | signed volume（封閉實體）+ 質心標記 + AABB 外形尺寸 |
| 設定/MRU | 自動記住視窗位置大小、單位；「▾」最近 10 個檔案 | %LOCALAPPDATA%\STPViewer\settings.json |

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

- 純離線桌面工具，無網路、無帳號、無資料庫、無任何 runtime / compile-time secret（故未建 `secret/` 資料夾）
- `For_AI/` 收 AI 協作素材（截圖、筆記）+ 測試模型 `*.stp`（客戶料號），整夾 gitignore
- `*.stp` / `*.step` 全域 gitignore：客戶 CAD 料號檔不入 git，避免外流

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

## Development Phases — 第四輪（效能，全部完成）

### Phase 12 — 大組件量測流暢度（工作量：M）

**問題：** 大檔（39MB / 64k 面）瀏覽轉動順，但一進量測模式轉動就嚴重卡頓。
**根因：** 量測模式渲染逐面（每個 B-rep 面 1 個 `GeometryModel3D`），64k 面 = 64k draw call，
WPF retained-mode 每幀重走 visual tree → frame rate 崩。瀏覽模式因渲染合併網格（每檔 1 個 model）所以順。

- [x] 量測拾取改打**合併網格** + 三角形頂點 index 反查面（`_mergedFaceRanges` + `ResolveMergedFace` 二分搜尋）
- [x] `ApplyRenderMode()` 改為：非剖面（瀏覽＋量測）→ 合併網格；剖面 → 逐面。量測模式不再掛數萬個逐面 model
- [x] `BuildMergedMesh` 依面序串接（無焊接共用頂點），匯入時同步記錄每面頂點起始邊界 + `FaceInfo`
- [x] 相機暫停事件改訂 `HelixViewport3D.CameraChanged`（控制項層級）＋ `Camera.Changed` 保底，避免相機實例被換掉時訂閱孤兒化
- [x] 驗證：WPF 3D hit-test 純幾何不剔背面 → 打單面合併網格仍命中孔內壁；圓/面/邊/角度/面距/剖面量測數值與逐面一致

**驗收：** 大組件量測模式下轉動視角與瀏覽同樣流暢；圓孔/面/邊/角度/面距量測精度不變；剖面模式量測仍正常

---

## Development Phases — 第五輪（效能 + 穩定性 v0.4.0，全部完成）

### Phase 13 — 效能 + 穩定性（工作量：M）

**效能：**
- [x] 剖面裁切背景化 + 平行化 — `ApplySection` 改 async：UI 執行緒快照 (model, frozen mesh)、
      `Task.Run` + `Parallel.For` 全場景裁切、回 UI 一次換上。`_sectionApplying`/`_sectionReapply` guard：
      裁切期間的新變更（拉滑桿/換軸/零件平移）→ 完成後用最新參數重跑一輪，最終狀態必為最新。
      大檔（64k 面）拉剖面滑桿不再凍結 UI
- [x] `TransformRoot` 網格變換面級平行化 — 來源/輸出 mesh 皆 frozen、各面獨立所以安全；
      B-rep `Modify` 與視覺樹賦值維持循序（CADability 非執行緒安全 / WPF 執行緒親和）。
      大組件拖曳/操作器放開的烘焙時間大減
- [x] 干涉間隙精修加速 — `ApproxMinDistance` 精修改為 AABB 平方距離下界快速拒絕 + `Parallel.For`
      分段掃描（thread-local best，lock 只在合併時），大檔全三角形掃描成本大減
- [x] 合併網格邏輯去重複 — `BuildMergedMesh`/`RebuildMerged` 共用 `MergeMeshes`（面序不變，
      `_mergedFaceRanges` 頂點邊界維持有效）；`MeasurementService.ClosestPointOnTriangle`
      改用 `InterferenceService` 的同一實作

**穩定性：**
- [x] 全域例外處理（App.xaml.cs）— `DispatcherUnhandledException`（記 log + 訊息框 + Handled 讓程式存活）、
      `TaskScheduler.UnobservedTaskException`（SetObserved）、`AppDomain.UnhandledException`（致命前留記錄）；
      log 位於 `%LOCALAPPDATA%\STPViewer\error.log`
- [x] `IsBusy` 指令防護 — 匯入 / 旋轉 90° / 干涉檢查在背景運算期間停用（`CanExecute`），
      避免干涉檢查中零件被轉走造成結果錯位；`ImportFilesAsync` 整批維持 busy、
      重入（拖放/命令列）直接擋掉（StepImportService 有共享狀態、非重入安全）
- [x] `MainViewModel`（原 1,476 行）拆 partial class — Drag / Gizmo / Section / Interference / Export 各自成檔

**驗收：** ClipTest 5 項、AlignTest 8 項、InterferenceTest 3 情境全過；test.stp 匯入 smoke test 通過；建置 0 警告

---

## Development Phases — 第六輪（Future Extensions 實作 v0.5.0，全部完成）

### Phase 14 — 功能擴充（工作量：L）

**量測/操作：**
- [x] 圓心吸附 — `Snap` 點到圓形邊（容差內）吸附圓心，與頂點比誰離命中點近；距離模式可直接量兩孔 pitch
- [x] Esc 取消 — 先清進行中的多段量測（保留模式）→ 退回瀏覽 → 關操作器；量測快速鍵 P/D/E/F/C/A/M
  （同鍵再按=退出；焦點在輸入框時不攔截）
- [x] 體積/質心 — `MeasurementService.MeshVolume` signed volume + 加權質心；有向體積抵銷比例過高
  （開放殼）判為不可靠並拒算；樹右鍵「⚖ 量體積/質心」→ 量測結果 + 質心標記

**視圖/UI：**
- [x] 標準視圖（等角/前/上/右，Z 向上機構慣例）+ 正交⇄透視（`HelixViewport3D.Orthographic`）；
  gizmo 疊圖層相機「型別」跟隨主相機切換（透視/正交投影一致，操作器不錯位）
- [x] GridSplitter ×2 — 樹面板/量測面板可調寬（MinWidth 防拖到消失）
- [x] 節點外形尺寸 — `Bounds` setter 通知 `Stats`/`ToolTipText`，tooltip 顯示 L×W×H（變換後自動更新）
- [x] 樹搜尋/過濾 — 名稱過濾（符合者+祖先+子樹顯示、自動展開）；只影響樹面板，不影響 3D；新匯入檔套用現行過濾
- [x] 隔離顯示 — 樹右鍵：只顯示此節點/反轉顯示/全部顯示；`SetVisibleRecursive` 顯式遞迴
  （不能只設 root：setter 同值不觸發 cascade，會留下混合狀態）
- [x] 記住設定 + MRU — `SettingsService`（settings.json）：視窗位置大小（含虛擬桌面範圍檢查）、
  mm/inch、最近 10 檔；工具列「▾」下拉

**檔案/剖面：**
- [x] 干涉 ≥2 可見檔 — 兩兩配對逐組檢查（網格先快照），每組一筆結果，狀態列總結相交組數
- [x] 匯出 STEP — `CADability.ExportStep.WriteToFile` + `Project.CreateSimpleProject`；可見檔案的
  Solid/Shell（目前對齊位置）寫成新檔；SmokeTest `--export-test` 往返驗證（8 實體出→回讀 8 實體一致）
- [x] 3點任意剖面 — 軸向下拉加「3點」：模型上點 3 點定義平面（共線防呆、Esc 取消、換軸重置）；
  平面位置改為「場景 AABB 8 角投影到法向」內插（軸向=舊行為、任意法向也成立）；位置加數值輸入框（0–100 防呆）

**驗收：** ClipTest / AlignTest / InterferenceTest / ExportTest 全過；test.stp 匯入正常；建置 0 警告

---

## Development Phases — 第七輪（STL 匯出 v0.6.0，全部完成）

### Phase 15 — STL 匯出（工作量：M）

- [x] `StlExportService` — binary（80B header + placeholder 補三角形數）/ ASCII 寫檔，
      退化（零面積）三角形濾除、逐三角形自算法向量；`Tessellate` 由 B-rep 重新三角化
- [x] 參數對話框（`StlExportDialog` + `StlExportViewModel`）— 範圍（合併單檔/每檔一個）、
      格式（binary/ASCII）、單位縮放（mm / inch ÷25.4 / 自訂）、網格精度（目前網格/精細）、
      即時摘要（檔案數/三角形數/預估大小），**確認才選路徑匯出**
- [x] 背景執行緒寫檔 — UI 先快照 frozen mesh（`GeometryModel3D` 是 DispatcherObject
      不可跨執行緒，v0.6.0 修正），背景只碰 frozen `MeshGeometry3D` + CADability B-rep
- [x] SmokeTest `--stl-export-test [file.stp]`：binary 往返（含 inch 縮放）、ASCII 結構、
      退化三角形濾除、真檔精細重算三角形數 > 匯入預設

**CADability 三角化兩個實測特性（設計依據）：**
1. `GetTriangulation` 對「比快取粗」的精度要求直接回傳既有較細快取 → 「較粗」選項無效，誠實不提供
2. 三角形數對精度**非單調**（0.4× 重算反而比 1× 少 — 重算網格較有效率），
   要明顯更細需 ≤0.15×（test.stp：1× = 30,784 → 0.15× = 35,228）→ 精細係數定 0.15

**驗收：** StlExportTest 5 項全過；既有 ClipTest/AlignTest/InterferenceTest 回歸全過；建置 0 警告

---

## Future Extensions（下一輪）

- **大檔重開快取** — 匯入成功後把三角網格 + 裝配樹序列化成 sidecar 快取檔（以來源檔 hash 驗證），
  重開同檔免等 CADability 解析（39MB 實測 276 秒）。取捨：B-rep 無法快取 → 量測退化成網格模式
  或背景補載；是否值得取決於「重複開同一大檔」的頻率（v0.5.0 決議暫緩）

### 既有清單（維持）

- 剖切面封口（cap）填實（目前剖開處可見內部背面材質；需對裁切輪廓三角化補面，難度最高，建議最後做）
- 樹節點三態 checkbox（部分子節點隱藏時顯示中間態）
- IGES 支援（需引入其他幾何核心或自寫 reader）
- 量測結果匯出含截圖的 PDF 報告
- 兩邊最短距離、邊到面距離
- 視圖狀態（相機、圖層、量測）存檔/還原
