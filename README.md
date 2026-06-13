# STPViewer

STP/STEP 3D 檢視器（Windows 桌面程式，C# .NET 8 WPF）— 多檔匯入、圖層管理、點/距離/邊/面/圓量測。

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg) ![Platform](https://img.shields.io/badge/platform-Windows-0078D6.svg) ![UI](https://img.shields.io/badge/UI-WPF-blueviolet.svg)

## 功能

- **多檔匯入**：STEP / STL / DXF — 工具列匯入（可複選）、拖放到視窗、或命令列 `STPViewer.exe a.stp b.stl`
- **裝配樹**：STEP product structure 還原成樹（組件→零件），逐節點 顯示/隱藏、換色（cascade）、Zoom-to；檔案層級可移除、開關輪廓邊線
- **量測**（工具列切換模式，點擊模型）：
  | 模式 | 輸出 |
  |---|---|
  | 📍 點 | XYZ 座標（自動吸附鄰近 B-rep 頂點） |
  | 📏 距離 | 兩點直線距離 + ΔX/ΔY/ΔZ |
  | 📐 邊 | 直線長 / 曲線長 / 圓弧長+半徑 |
  | ⬛ 面 | 面積（網格近似）+ 曲面類型（平面法向量、圓柱半徑/軸向） |
  | ⭕ 圓 | 圓心 / 半徑 / 直徑 / 周長 |
  | ∠ 角度 | 兩面（法向量）/ 兩直線邊 夾角 + 補角 |
  | ⇔ 面距 | 面到面最短距離（網格近似）+ 最近點對 |
  | ⤚ 對齊 | 點「要移動零件」一點 + 目標點 → 純平移該檔案使兩點貼合（B-rep 整體位移） |
  | 🎯 三點 | 來源檔 3 特徵點 + 目標檔 3 對應點 → 旋轉+平移一次貼合（方向不同也能對） |
- **旋轉**：樹面板選檔案 + 工具列 ↻X/↻Y/↻Z 繞中心 +90°（擺正方向用，連按累加）
- **拖曳** 🖐：手形游標模式，左鍵按住零件沿螢幕平面拖動、放開定位（粗定位用；精確貼合用對齊）；右鍵轉視角不受影響
- **干涉檢查** 🧩：勾選剛好 2 個可見檔案 → 相交時顯示紅色干涉交線 + 相交三角形對數；無相交時回報最小間隙 gap（gap≈0 即為配合 match，共面貼合不算干涉）
- **剖面**：✂ 開關 + X/Y/Z 軸 + 位置滑桿 + 反向；CPU 網格裁切，原始幾何保留（量測不受影響）
- **單位**：mm ⇄ inch 一鍵切換，既有量測（清單與 3D 標籤）即時換算
- **匯出**：量測結果 CSV（Excel 中文不亂碼）、3D 視圖 PNG 截圖（2x 解析度）
- **視角**：右鍵旋轉、滾輪縮放、中鍵平移、ViewCube

## 快速開始

```bash
dotnet build STPViewer.sln
dotnet run --project src/STPViewer

# 發佈免安裝資料夾
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

無 UI 匯入管線與幾何數學驗證：

```bash
dotnet run --project tools/SmokeTest -- "path\to\model.stp"   # 匯入 + 裝配樹
dotnet run --project tools/SmokeTest -- --clip-test           # 剖切裁切數學
dotnet run --project tools/SmokeTest -- --interference-test   # 干涉 相交/分離/貼合
dotnet run --project tools/SmokeTest -- --align-test          # 三點對齊剛體變換數學
```

## 技術棧

| 元件 | 用途 |
|---|---|
| [CADability](https://github.com/SOFAgh/CADability)（純 C#） | STEP 匯入、B-rep 幾何核心、面三角化 |
| [HelixToolkit.Wpf](https://github.com/helix-toolkit/helix-toolkit) | 3D viewport、相機操作、HitTest |
| CommunityToolkit.Mvvm | MVVM |

量測原則：邊長、圓半徑等取 **B-rep 精確值**；面積為三角網格加總近似（三角化精度依模型尺寸自適應 0.02–0.5 mm）。

## 已知限制

- 大型 STEP（數千面）匯入需數十秒（CADability 解析成本），匯入期間 UI 有進度提示不凍結
- 輪廓邊線超過 30,000 線段的檔案預設關閉邊線（WPF LinesVisual3D 轉動視角時效能限制），可在樹面板手動開啟
- **IGES 不支援**（CADability 無 IGES reader）；STL 無 B-rep，僅支援 點/距離/角度/面距 量測；DXF 為線架構檢視
- 剖切面無封口（cap），剖開處顯示內部背面材質（深灰）
- 面積與面距為三角網格近似值；邊長/圓半徑/角度為 B-rep 精確值
- 少數 AP242 檔案 CADability 支援不完整，匯入失敗會提示訊息（不閃退）
- 唯讀檢視器，不寫入/修改原始檔案

詳細設計與開發 Phase 見 [ARCHITECTURE.md](ARCHITECTURE.md)。
