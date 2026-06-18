# STPViewer

[![English](https://img.shields.io/badge/English-lightgrey.svg)](README.md) [![繁體中文](https://img.shields.io/badge/%E7%B9%81%E9%AB%94%E4%B8%AD%E6%96%87-2ea043.svg)](README.zh-TW.md)

> STP/STEP 3D 檢視器（Windows 桌面程式，C# .NET 8 WPF）— 多檔匯入、裝配樹、點/距離/邊/面/圓量測。

![version](https://img.shields.io/badge/version-0.3.2-blue.svg) ![platform](https://img.shields.io/badge/platform-Windows-0078D6.svg) ![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg) ![UI](https://img.shields.io/badge/UI-WPF-blueviolet.svg)

---

## 📖 目錄

- [✨ 功能特色](#-功能特色)
- [💻 系統需求](#-系統需求)
- [📥 安裝](#-安裝)
- [🚀 快速開始](#-快速開始)
- [📚 使用指南](#-使用指南)
- [🔨 從原始碼建置](#-從原始碼建置)
- [📁 專案結構](#-專案結構)
- [⚠️ 已知限制](#️-已知限制)
- [🤝 貢獻](#-貢獻)
- [📜 版本紀錄](#-版本紀錄)
- [🙏 致謝](#-致謝)

---

## ✨ 功能特色

快速開啟機構件 STEP 檔做確認與量測，不需安裝 SolidWorks / Creo 等重量級 CAD。

- **多檔匯入** — STEP / STL / DXF，工具列匯入（可複選）、拖放到視窗，或命令列 `STPViewer.exe a.stp b.stl`
- **裝配樹** — STEP product structure 還原成樹（組件→零件）；逐節點 顯示/隱藏、換色（cascade）、Zoom-to；檔案層級可移除、開關輪廓邊線
- **量測**（工具列切換模式，點擊模型）：

  | 模式 | 輸出 |
  |---|---|
  | 📍 點 | XYZ 座標（自動吸附鄰近 B-rep 頂點） |
  | 📏 距離 | 兩點直線距離 + ΔX/ΔY/ΔZ |
  | 📐 邊 | 直線長 / 曲線長 / 圓弧長 + 半徑 |
  | ⬛ 面 | 面積（網格近似）+ 曲面類型（平面法向量、圓柱半徑/軸向） |
  | ⭕ 圓 | 圓心 / 半徑 / 直徑 / 周長 |
  | ∠ 角度 | 兩面（法向量）/ 兩直線邊 夾角 + 補角 |
  | ⇔ 面距 | 面到面最短距離（網格近似）+ 最近點對 |
  | ⤚ 對齊（兩點） | 點「要移動零件」一點 + 目標點 → 純平移使兩點貼合 |
  | 🎯 對齊（三點） | 來源檔 3 點 + 目標檔 3 對應點 → 旋轉+平移一次貼合 |

- **旋轉** — 樹面板選檔案 + 工具列 ↻X/↻Y/↻Z 繞中心 +90°（擺正方向用，連按累加）
- **拖曳** 🖐 — 手形游標模式，左鍵按住零件沿螢幕平面拖動、放開定位（右鍵轉視角不受影響）
- **操作器** ⊹ — 樹面板選檔案 → XYZ 三色箭頭 + 旋轉環（Fusion 360 風格）；拖箭頭沿該軸移動（與視角無關）、拖環繞軸轉。永遠浮在最上層、不被實體遮擋
- **干涉檢查** 🧩 — 勾選剛好 2 個可見檔案 → 相交時顯示紅色干涉交線 + 相交三角形對數；無相交時回報最小間隙 gap（gap≈0 即為配合 match，共面貼合不算干涉）
- **剖面** ✂ — X/Y/Z 軸 + 位置滑桿 + 反向；CPU 網格裁切，原始幾何保留（量測仍精確）
- **單位** — mm ⇄ inch 一鍵切換，既有量測（清單與 3D 標籤）即時換算
- **匯出** — 量測結果 CSV（UTF-8 BOM，Excel 中文不亂碼）、3D 視圖 2× PNG 截圖
- **視角** — 右鍵旋轉、滾輪縮放、中鍵平移、ViewCube

量測原則：邊長、圓半徑、角度取 **B-rep 精確值**；面積為三角網格加總近似（三角化精度依模型尺寸自適應 0.02–0.5 mm）。

---

## 💻 系統需求

| 項目 | 需求 |
|------|------|
| 作業系統 | Windows 10 / 11（x64） |
| 執行階段 | .NET 8 Desktop Runtime（框架相依版）— Portable 版則免裝 |
| 建置 SDK | .NET 8 SDK（僅從原始碼建置時需要） |

---

## 📥 安裝

下載發佈版直接執行，免安裝。

- **框架相依版**（檔案較小）：需 .NET 8 Desktop Runtime，執行 `STPViewer v0.3.2.exe`。
- **Portable 版**（self-contained）：內含 runtime，免安裝 / 免系統管理員，執行 `STPViewer v0.3.2.exe`。

或從原始碼建置（見下）。

```bash
git clone https://github.com/ETWen/STPViewer.git
cd STPViewer
dotnet build STPViewer.sln
```

---

## 🚀 快速開始

```bash
# 建置並執行
dotnet build STPViewer.sln
dotnet run --project src/STPViewer

# 發佈免安裝資料夾
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

接著匯入 `.stp` 檔（工具列「匯入」、拖放、或命令列帶檔），選一個量測模式，點擊模型即可。

---

## 📚 使用指南

1. **匯入** 一個或多個 CAD 檔，每個檔案成為裝配樹的一個 root，視角自動 Zoom 到全景。
2. **操作樹** — 顯示/隱藏、換色、Zoom-to 某節點，或移除檔案。
3. **量測** — 工具列選模式（點 / 距離 / 邊 / 面 / 圓 / 角度 / 面距），點擊模型；結果顯示於右側面板，可單筆刪除或全部清除。
4. **裝配** — 用 對齊（兩點/三點）、旋轉、拖曳、操作器擺位，再用干涉檢查驗證配合。
5. **剖面** — 開啟 ✂、選軸向、滑動剖切；量測仍以原始幾何精確計算。
6. **匯出** — 量測結果存 CSV，或截 2× PNG。

無 UI 匯入管線與幾何數學驗證：

```bash
dotnet run --project tools/SmokeTest -- "path\to\model.stp"   # 匯入 + 裝配樹
dotnet run --project tools/SmokeTest -- --clip-test           # 剖切裁切數學
dotnet run --project tools/SmokeTest -- --interference-test   # 干涉 相交/分離/貼合
dotnet run --project tools/SmokeTest -- --align-test          # 三點對齊剛體變換數學
```

---

## 🔨 從原始碼建置

```bash
dotnet build STPViewer.sln -c Debug
dotnet run --project src/STPViewer
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

NuGet 相依（自動還原）：`CADability`、`HelixToolkit.Wpf`、`CommunityToolkit.Mvvm`。

---

## 📁 專案結構

```
STPViewer/
├── ARCHITECTURE.md            # 設計、資料流、開發 Phase
├── CLAUDE.md                  # 專案記憶 & 開發慣例
├── STPViewer.sln
├── src/STPViewer/
│   ├── STPViewer.csproj       # net8.0-windows、UseWPF、單一版號來源 <Version>
│   ├── MainWindow.xaml / .cs   # 版面 + 滑鼠拾取轉發
│   ├── Models/                 # FaceInfo、MeasureMode、MeasurementResult、UnitSystem
│   ├── Services/               # StepImport、Measurement、Interference、Section、RigidAlign
│   └── ViewModels/             # MainViewModel、ModelNodeViewModel（裝配樹）
└── tools/SmokeTest/           # 無 UI 匯入 + 幾何數學驗證
```

---

## ⚠️ 已知限制

- 大型 STEP（數千面）匯入需數十秒（CADability 解析成本），匯入期間有進度提示、UI 不凍結。
- 輪廓邊線超過 30,000 線段的檔案預設關閉邊線（WPF `LinesVisual3D` 轉動視角時效能限制），可在樹面板手動開啟。
- **IGES 不支援**（CADability 無 IGES reader）；STL 無 B-rep，僅支援 點/距離/角度/面距 量測；DXF 為線架構檢視。
- 剖切面無封口（cap），剖開處顯示內部背面材質（深灰）。
- 面積與面距為三角網格近似值；邊長/圓半徑/角度為 B-rep 精確值。
- 少數 AP242 檔案 CADability 支援不完整，匯入失敗會提示訊息（不閃退）。
- 唯讀檢視器，不寫入/修改原始檔案。

---

## 🤝 貢獻

1. Fork 並建立 feature 分支：`git checkout -b feature/your-feature`
2. 遵循 [Conventional Commits](https://www.conventionalcommits.org/)：`feat(scope): summary`
3. Push 後開 Pull Request

---

## 📜 版本紀錄

### v0.3.2

- **效能：** 大組件量測模式下轉動視角不再卡頓。量測模式改為渲染合併網格（每檔 1 個 model），並用命中三角形的頂點 index 反查回是哪個面，不再掛數萬個逐面 model；逐面渲染只保留給剖面模式。
- 相機互動暫停事件加訂 `HelixViewport3D.CameraChanged`，避免相機實例被換掉時訂閱失效。

### v0.3.1

- 操作器 always-on-top 疊圖層（永遠浮在零件上、不被實體遮擋）。

### v0.3.0

- 旋轉對齊：軸向旋轉（↻X/↻Y/↻Z）、三點對齊，以及統一的 `TransformRoot` 剛體變換路徑。

### v0.2.x

- 拖曳模式、兩點對齊、干涉檢查、剖面、角度/面距量測、裝配樹、STL/DXF 支援、mm ⇄ inch。

---

## 🙏 致謝

- [CADability](https://github.com/SOFAgh/CADability) — 純 C# CAD kernel：STEP 匯入、B-rep 幾何、面三角化
- [HelixToolkit.Wpf](https://github.com/helix-toolkit/helix-toolkit) — 3D viewport、相機操作、HitTest
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM

詳細設計與開發 Phase 見 [ARCHITECTURE.md](ARCHITECTURE.md)。
