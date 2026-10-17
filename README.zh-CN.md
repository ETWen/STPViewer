# STPViewer

[![English](https://img.shields.io/badge/English-lightgrey.svg)](README.md) [![简体中文](https://img.shields.io/badge/%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-2ea043.svg)](README.zh-CN.md) [![繁體中文](https://img.shields.io/badge/%E7%B9%81%E9%AB%94%E4%B8%AD%E6%96%87-lightgrey.svg)](README.zh-TW.md)

> STP/STEP 3D 查看器（Windows 桌面程序，C# .NET 8 WPF）— 多文件导入、装配树、点/距离/边/面/圆测量。

![version](https://img.shields.io/badge/version-0.5.0-blue.svg) ![platform](https://img.shields.io/badge/platform-Windows-0078D6.svg) ![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg) ![UI](https://img.shields.io/badge/UI-WPF-blueviolet.svg)

---

## 📖 目录

- [✨ 功能特性](#-功能特性)
- [💻 系统需求](#-系统需求)
- [📥 安装](#-安装)
- [🚀 快速开始](#-快速开始)
- [📚 使用指南](#-使用指南)
- [🔨 从源代码构建](#-从源代码构建)
- [📁 项目结构](#-项目结构)
- [⚠️ 已知限制](#️-已知限制)
- [🤝 贡献](#-贡献)
- [📜 版本记录](#-版本记录)
- [🙏 致谢](#-致谢)

---

## ✨ 功能特性

快速打开机械零件 STEP 文件进行查看和测量，无需安装 SolidWorks / Creo 等大型 CAD 软件。

- **多文件导入** — STEP / STL / DXF，工具栏导入（可多选）、拖放到窗口、命令行 `STPViewer.exe a.stp b.stl`，或最近文件下拉菜单（▾，最近 10 项）
- **装配树** — STEP product structure 还原为树（组件→零件）；逐节点显示/隐藏、更改颜色（作用于子节点）、缩放至节点；文件层级可移除、开关轮廓边线；**按名称搜索过滤**；右键 **只显示此节点 / 反转显示 / 全部显示 / 计算体积与质心**；提示信息显示外形尺寸 L×W×H
- **测量**（工具栏切换模式或快捷键，点击模型）：

  | 模式 | 快捷键 | 输出 |
  |---|---|---|
  | 📍 点 | P | XYZ 坐标（自动吸附邻近 B-rep 顶点**或圆心**） |
  | 📏 距离 | D | 两点直线距离 + ΔX/ΔY/ΔZ — 点击两个孔缘自动吸附**圆心**，可直接测量孔中心距 |
  | 📐 边 | E | 直线长 / 曲线长 / 圆弧长 + 半径 |
  | ⬛ 面 | F | 面积（网格近似）+ 曲面类型（平面法向量、圆柱半径/轴向） |
  | ⭕ 圆 | C | 圆心 / 半径 / 直径 / 周长 |
  | ∠ 角度 | A | 两面（法向量）/ 两条直线边的夹角 + 补角 |
  | ⇔ 面距 | M | 面到面最短距离（网格近似）+ 最近点对 |
  | ⚖ 体积 | （树右键） | 网格有符号体积 + 质心标记 + 外形尺寸（封闭实体） |
  | ⤚ 对齐（两点） | | 点「要移动零件」一点 + 目标点 → 纯平移使两点贴合 |
  | 🎯 对齐（三点） | | 源文件 3 点 + 目标文件 3 对应点 → 旋转+平移一次贴合 |

  再次按下同一快捷键退出模式；**Esc** 先取消当前选择、再按退出模式。
- **旋转** — 树面板选文件 + 工具栏 ↻X/↻Y/↻Z 绕中心 +90°（用于调整朝向，可重复旋转）
- **拖动** 🖐 — 手形光标模式，左键按住零件沿屏幕平面拖动、放开定位（右键转视角不受影响）
- **操作器** ⊹ — 树面板选文件 → XYZ 三色箭头 + 旋转环（Fusion 360 风格）；拖箭头沿该轴移动（与视角无关）、拖环绕轴转。永远浮在最上层、不被实体遮挡
- **干涉检查** 🧩 — 2 个以上可见文件**两两配对**检查：相交时显示红色干涉交线 + 相交的三角形对数；无相交时报告最小间隙（gap≈0 即为配合，共面贴合不算干涉）
- **剖面** ✂ — X/Y/Z 轴或**在模型上点 3 点定义任意平面**；位置滑块 + 数值输入 + 反向；CPU 网格裁切（背景平行运算），原始几何保留（测量仍精确）
- **单位** — mm ⇄ inch 一键切换，已有测量（列表与 3D 标签）实时换算
- **语言** — 在 **视图 → 语言** 中选择语言；启动时从 `i18n/` 的 JSON 文件读取可用语言，并记住你的选择
- **导出** — 测量结果 CSV（UTF-8 BOM，Excel 中文不乱码）、3D 视图 2× PNG 截图、📤 **STEP 导出**：可见零件以当前对齐位置写成新 STEP 文件（源文件不动）
- **视角** — 右键旋转、滚轮缩放、中键平移、ViewCube；一键 **等角/前/上/右** 标准视图与 **正交⇄透视** 切换
- **设置保存** — 窗口位置和大小、单位、最近文件自动保存；左右面板可拖动调宽

测量原则：边长、圆半径、角度取 **B-rep 精确值**；面积为三角网格求和近似（三角化精度根据模型尺寸自适应 0.02–0.5 mm）。

---

## 💻 系统需求

| 项目 | 需求 |
|------|------|
| 操作系统 | Windows 10 / 11（x64） |
| 运行环境 | .NET 8 Desktop Runtime（依赖框架版）— Portable 版则免装 |
| 构建 SDK | .NET 8 SDK（仅从源代码构建时需要） |

---

## 📥 安装

下载发布版直接执行，免安装。

- **依赖框架版**（文件较小）：需 .NET 8 Desktop Runtime，执行 `STPViewer v0.5.0.exe`。
- **Portable 版**（self-contained）：内置运行环境，免安装 / 无需管理员权限，执行 `STPViewer v0.5.0.exe`。

或从源代码构建（见下）。

```bash
git clone https://github.com/ETWen/STPViewer.git
cd STPViewer
dotnet build STPViewer.sln
```

---

## 🚀 快速开始

```bash
# 构建并执行
dotnet build STPViewer.sln
dotnet run --project src/STPViewer

# 发布免安装文件夹
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

然后导入 `.stp` 文件（工具栏「导入」、拖放或命令行传入文件路径），选一个测量模式，点击模型即可。

---

## 📚 使用指南

1. **导入** 一个或多个 CAD 文件，每个文件成为装配树的一个根节点，视角自动缩放至适合窗口。
2. **操作树** — 按名称搜索、显示/隐藏（或右键隔离显示）、换色、缩放至某节点，或移除文件。
3. **测量** — 在工具栏选择模式或按快捷键（P/D/E/F/C/A/M），点击模型；点击孔缘自动吸附圆心，点击两次即可测得孔中心距；Esc 取消。结果显示在右侧面板，可逐条删除或全部清除。
4. **装配** — 使用对齐（两点/三点）、旋转、拖动、操作器摆位，再用干涉检查对所有可见文件两两检查，验证装配配合。
5. **剖面** — 打开 ✂、选轴向或点 3 点定义任意平面、滑动（或输入 %）剖切；测量仍以原始几何精确计算。
6. **导出** — 测量结果保存为 CSV、截取 2× PNG，或把对齐后的场景写成新 STEP 文件交接给 CAD。

无需启动界面的导入流程与几何运算验证：

```bash
dotnet run --project tools/SmokeTest -- "path\to\model.stp"   # 导入 + 装配树
dotnet run --project tools/SmokeTest -- --clip-test           # 剖切裁切数学
dotnet run --project tools/SmokeTest -- --interference-test   # 干涉 相交/分离/贴合
dotnet run --project tools/SmokeTest -- --align-test          # 三点对齐刚体变换数学
dotnet run --project tools/SmokeTest -- --localization-test   # 语言资源与实时切换
dotnet run --project tools/SmokeTest -- --export-test in.stp out.stp  # STEP 导出往返验证
```

---

## 🔨 从源代码构建

```bash
dotnet build STPViewer.sln -c Debug
dotnet run --project src/STPViewer
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

NuGet 依赖（自动还原）：`CADability`、`HelixToolkit.Wpf`、`CommunityToolkit.Mvvm`。

---

## 🌐 添加语言

程序启动时扫描可执行文件旁的 `i18n/*.json`。文件名去掉 `.json` 后就是语言 ID，例如 `en.json` 对应 `en`，`zh_cn.json` 对应 `zh_cn`。JSON 顶层的 `key` 是语言菜单中的显示名，`data` 是资源键到译文的映射：

```json
{
  "key": "Français",
  "data": {
    "Ui.Language": "Langue",
    "Ui.Main.MenuImport": "📂 Importer…"
  }
}
```

参考 `src/STPViewer/i18n/en.json` 中的资源键，翻译其 `data` 内容，将新文件以 UTF-8 编码放入可执行文件旁的 `i18n/`。重启 STPViewer 后，新语言便会出现在菜单和快捷栏中，无需重新编译。

---

## 📁 项目结构

```
STPViewer/
├── ARCHITECTURE.md            # 设计、数据流、开发 阶段
├── CLAUDE.md                  # 项目记忆 & 开发规范
├── STPViewer.sln
├── src/STPViewer/
│   ├── STPViewer.csproj       # net8.0-windows、UseWPF、统一的版本号来源 <Version>
│   ├── MainWindow.xaml / .cs   # 布局 + 鼠标拾取转发
│   ├── i18n/                  # 复制到可执行文件旁的语言 JSON 文件
│   ├── Models/                 # FaceInfo、MeasureMode、MeasurementResult、UnitSystem
│   ├── Services/               # StepImport、Measurement、Interference、Section、RigidAlign、Settings
│   └── ViewModels/             # MainViewModel（partial：Drag/Gizmo/Section/Interference/Export）、ModelNodeViewModel
└── tools/SmokeTest/           # 无需 UI 导入 + 几何数学验证
```

---

## ⚠️ 已知限制

- 大型 STEP（数千面）导入需数十秒（CADability 解析成本），导入期间有进度提示、UI 不会卡住。
- 轮廓边线超过 30,000 线段的文件会默认关闭边线（WPF `LinesVisual3D` 转动视角时性能限制），可在树面板手动打开。
- **IGES 不支持**（CADability 无 IGES reader）；STL 无 B-rep，仅支持 点/距离/角度/面距 测量；DXF 为线框查看。
- 剖切面无封口（cap），剖开处显示内部背面材质（深灰）。
- STEP 导出为「当前位置的 Solid/Shell 扁平列表」，不保留原始装配层级与 product 名称；STL 网格、DXF 线框无 B-rep 不导出。
- 面积、面距、体积/质心为三角网格近似值；边长/圆半径/角度为 B-rep 精确值。
- 少数 AP242 文件 CADability 支持不完整，导入失败会提示消息（不会崩溃）。
- 只读查看器，不写入/修改原始文件。

---

## 🤝 贡献

1. Fork 并建立 feature 分支：`git checkout -b feature/your-feature`
2. 遵循 [Conventional Commits](https://www.conventionalcommits.org/)：`feat(scope): summary`
3. Push 后开 Pull Request

---

## 📜 版本记录

### v0.5.0

- **功能扩展：** 圆心吸附（点击两次测量孔中心距）、测量快捷键（P/D/E/F/C/A/M）+ Esc、标准视图（等角/前/上/右）+ 正交投影、装配树搜索/隔离显示/反转、体积与质心、3 点任意剖切平面 + 位置数值输入、干涉检查支持 3 个以上文件（两两配对）、对齐后场景导出 STEP、面板可调宽、窗口/单位/最近文件自动保存。

### v0.4.0

- **性能：** 剖面裁切改为后台并行运算（大文件拖动滑块时不再卡住 UI）；零件变换烘焙（拖动/操作器放开）并行化；干涉间隙精修以 AABB 快速排除加速。
- **稳定性：** 全域例外处理（记 log 至 `%LOCALAPPDATA%\STPViewer\error.log` + 消息框，不会崩溃）；背景运算期间停用会改动几何的指令；导入防重入。

### v0.3.2

- **性能：** 大组件测量模式下转动视角不再卡顿。测量模式改为渲染合并网格（每个文件 1 个 model），并用命中三角形的顶点 index 反查回是哪个面，不再挂数万个逐面 model；逐面渲染只保留给剖面模式。
- 相机交互暂停事件额外监听 `HelixViewport3D.CameraChanged`，避免相机实例被换掉时失去订阅。

### v0.3.1

- 操作器 always-on-top 叠图层（永远浮在零件上、不被实体遮挡）。

### v0.3.0

- 旋转对齐：轴向旋转（↻X/↻Y/↻Z）、三点对齐，以及统一的 `TransformRoot` 刚体变换路径。

### v0.2.x

- 拖动模式、两点对齐、干涉检查、剖面、角度/面距测量、装配树、STL/DXF 支持、mm ⇄ inch。

---

## 🙏 致谢

- [CADability](https://github.com/SOFAgh/CADability) — 纯 C# CAD kernel：STEP 导入、B-rep 几何、面三角化
- [HelixToolkit.Wpf](https://github.com/helix-toolkit/helix-toolkit) — 3D viewport、相机操作、HitTest
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM

详细设计与开发 阶段 见 [ARCHITECTURE.md](ARCHITECTURE.md)。
