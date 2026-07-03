# STPViewer

[![English](https://img.shields.io/badge/English-2ea043.svg)](README.md) [![繁體中文](https://img.shields.io/badge/%E7%B9%81%E9%AB%94%E4%B8%AD%E6%96%87-lightgrey.svg)](README.zh-TW.md)

> A Windows desktop 3D viewer for STP/STEP CAD files — multi-file import, assembly tree, and point / distance / edge / face / circle measurement. Built with C# .NET 8 WPF.

![version](https://img.shields.io/badge/version-0.5.0-blue.svg) ![platform](https://img.shields.io/badge/platform-Windows-0078D6.svg) ![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg) ![UI](https://img.shields.io/badge/UI-WPF-blueviolet.svg)

---

## 📖 Table of Contents

- [✨ Features](#-features)
- [💻 System Requirements](#-system-requirements)
- [📥 Installation](#-installation)
- [🚀 Quick Start](#-quick-start)
- [📚 Usage Guide](#-usage-guide)
- [🔨 Building from Source](#-building-from-source)
- [📁 Project Structure](#-project-structure)
- [⚠️ Known Limitations](#️-known-limitations)
- [🤝 Contributing](#-contributing)
- [📜 Version History](#-version-history)
- [🙏 Acknowledgments](#-acknowledgments)

---

## ✨ Features

Open mechanical STEP parts for quick review and measurement without a heavyweight CAD suite (SolidWorks / Creo).

- **Multi-file import** — STEP / STL / DXF, via the toolbar (multi-select), drag-and-drop, command line (`STPViewer.exe a.stp b.stl`), or the recent-files dropdown (▾, last 10 files)
- **Assembly tree** — STEP product structure restored as a tree (assembly → part); per-node show/hide, recolor (cascades to children), zoom-to; file-level remove and outline-edge toggle; **name search filter**; right-click **Isolate / Invert visibility / Show all / Volume & centroid**; tooltips show each part's L×W×H
- **Measurement** (toolbar mode toggle or hotkey, then click the model):

  | Mode | Hotkey | Output |
  |---|---|---|
  | 📍 Point | P | XYZ coordinate (auto-snaps to nearby B-rep vertex **or circle center**) |
  | 📏 Distance | D | Straight-line distance + ΔX/ΔY/ΔZ — clicking two hole rims snaps to their **centers** = pin pitch |
  | 📐 Edge | E | Line length / curve length / arc length + radius |
  | ⬛ Face | F | Area (mesh approximation) + surface type (plane normal, cylinder radius/axis) |
  | ⭕ Circle | C | Center / radius / diameter / circumference |
  | ∠ Angle | A | Angle between two faces (normals) or two straight edges + supplement |
  | ⇔ Face distance | M | Shortest face-to-face distance (mesh approximation) + closest point pair |
  | ⚖ Volume | (tree right-click) | Mesh signed volume + centroid marker + bounding size (closed solids) |
  | ⤚ Align (2-pt) | | Pick a point on the moving part + a target point → pure translation so the two points coincide |
  | 🎯 Align (3-pt) | | 3 source points + 3 target points → rotation + translation in one shot |

  Press the same hotkey again to exit; **Esc** cancels an in-progress pick, then exits the mode.
- **Rotate** — select a file in the tree, then ↻X / ↻Y / ↻Z to rotate +90° about its center (for re-orienting; repeat to accumulate)
- **Drag** 🖐 — hand-cursor mode; hold the left button to drag a part along the screen plane, release to place (right-button view orbit unaffected)
- **Gizmo** ⊹ — select a file → XYZ tri-color arrows + rotation rings (Fusion 360 style); drag an arrow to move along that axis (view-independent), drag a ring to rotate. Always floats on top, never occluded
- **Interference check** 🧩 — with 2+ visible files, checks **every pair**: red intersection curves + intersecting triangle-pair count, or the minimum gap (gap ≈ 0 means a fit/match; coplanar contact is not interference)
- **Section plane** ✂ — X/Y/Z axis **or an arbitrary plane picked from 3 points on the model**; position slider + numeric input + flip; CPU mesh clipping (parallel, in the background), original geometry preserved (measurement stays exact)
- **Units** — one-click mm ⇄ inch; existing measurements (list + 3D labels) convert live
- **Export** — measurement results to CSV (UTF-8 BOM, no mojibake in Excel), a 2× PNG screenshot, and 📤 **STEP export**: writes all visible parts at their current aligned positions into a new STEP file (originals untouched)
- **View** — right-button orbit, wheel zoom, middle-button pan, ViewCube; one-click **Iso / Front / Top / Right** views and an **orthographic ⇄ perspective** toggle
- **Remembers your setup** — window size/position, unit choice and recent files persist across sessions; side panels are resizable

Measurement principle: edge length, circle radius and angles use **exact B-rep values**; area is a triangle-mesh sum approximation (triangulation precision adapts to model size, 0.02–0.5 mm).

---

## 💻 System Requirements

| Item | Requirement |
|------|-------------|
| OS | Windows 10 / 11 (x64) |
| Runtime | .NET 8 Desktop Runtime (framework-dependent build) — or none for the portable build |
| Build SDK | .NET 8 SDK (only to build from source) |

---

## 📥 Installation

Download a release build and run it — no install required.

- **Framework-dependent** (smaller): requires the .NET 8 Desktop Runtime. Run `STPViewer v0.5.0.exe`.
- **Portable** (self-contained): runtime bundled, no install / admin. Run `STPViewer v0.5.0.exe`.

Or build from source (see below).

```bash
git clone https://github.com/ETWen/STPViewer.git
cd STPViewer
dotnet build STPViewer.sln
```

---

## 🚀 Quick Start

```bash
# Build and run
dotnet build STPViewer.sln
dotnet run --project src/STPViewer

# Publish a no-install folder
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

Then import a `.stp` file (toolbar **Import**, drag-and-drop, or command-line argument), pick a measurement mode, and click the model.

---

## 📚 Usage Guide

1. **Import** one or more CAD files. Each file becomes a root in the assembly tree and the view zooms to fit.
2. **Navigate** the tree — search by name, toggle visibility (or right-click → Isolate), recolor, zoom to a node, or remove a file.
3. **Measure** — pick a mode on the toolbar or by hotkey (P/D/E/F/C/A/M), then click the model. Clicking near a hole rim snaps to the circle center — two clicks measure a pin pitch. Esc cancels. Results appear in the right-hand panel; delete individually or clear all.
4. **Assemble** — use Align (2-pt / 3-pt), Rotate, Drag, or the Gizmo to position parts; then run the Interference check (any number of visible files, all pairs) to verify fit.
5. **Section** — toggle ✂, choose an axis or pick 3 points for an arbitrary plane, and slide (or type a %) to cut through the model; measurement stays exact on the original geometry.
6. **Export** — save measurements to CSV, capture a 2× PNG, or write the aligned scene to a new STEP file for hand-off to CAD.

Headless import-pipeline and geometry-math verification (no UI):

```bash
dotnet run --project tools/SmokeTest -- "path\to\model.stp"   # import + assembly tree
dotnet run --project tools/SmokeTest -- --clip-test           # section clipping math
dotnet run --project tools/SmokeTest -- --interference-test   # interference: intersect / separate / contact
dotnet run --project tools/SmokeTest -- --align-test          # 3-point rigid-transform math
dotnet run --project tools/SmokeTest -- --export-test in.stp out.stp  # STEP export round-trip
```

---

## 🔨 Building from Source

```bash
dotnet build STPViewer.sln -c Debug
dotnet run --project src/STPViewer
dotnet publish src/STPViewer -c Release -o publish/STPViewer
```

NuGet dependencies (restored automatically): `CADability`, `HelixToolkit.Wpf`, `CommunityToolkit.Mvvm`.

---

## 📁 Project Structure

```
STPViewer/
├── ARCHITECTURE.md            # Design, data flow, development phases
├── CLAUDE.md                  # Project memory & engineering conventions
├── STPViewer.sln
├── src/STPViewer/
│   ├── STPViewer.csproj       # net8.0-windows, UseWPF, single-source <Version>
│   ├── MainWindow.xaml / .cs   # Layout + mouse-pick forwarding
│   ├── Models/                 # FaceInfo, MeasureMode, MeasurementResult, UnitSystem
│   ├── Services/               # StepImport, Measurement, Interference, Section, RigidAlign, Settings
│   └── ViewModels/             # MainViewModel (partial: Drag/Gizmo/Section/Interference/Export), ModelNodeViewModel
└── tools/SmokeTest/           # Headless import + geometry-math verification
```

---

## ⚠️ Known Limitations

- Large STEP files (thousands of faces) take tens of seconds to import (CADability parse cost); a progress indicator keeps the UI responsive.
- Files with more than 30,000 outline segments disable edges by default (WPF `LinesVisual3D` cost while orbiting); re-enable per file in the tree.
- **IGES is not supported** (CADability has no IGES reader). STL has no B-rep (point / distance / angle / face-distance only). DXF is wireframe view.
- Section cuts have no cap fill — the opened face shows the interior back material (dark gray).
- STEP export writes the solids/shells at their current positions as a flat list — the original assembly hierarchy and product names are not preserved. STL meshes and DXF wireframes are not exported (no B-rep).
- Area, face-distance and volume/centroid are mesh approximations; edge length / circle radius / angle are exact B-rep values.
- A few AP242 files are incompletely supported by CADability; failed imports show a message (no crash).
- Read-only viewer — never writes to or modifies the source file.

---

## 🤝 Contributing

1. Fork and create a feature branch: `git checkout -b feature/your-feature`
2. Follow [Conventional Commits](https://www.conventionalcommits.org/): `feat(scope): summary`
3. Push and open a Pull Request

---

## 📜 Version History

### v0.5.0

- **Feature round:** circle-center snap (measure hole-to-hole pitch in two clicks), measurement hotkeys (P/D/E/F/C/A/M) + Esc, standard views (Iso/Front/Top/Right) + orthographic projection, assembly-tree search / isolate / invert, volume & centroid, 3-point arbitrary section plane + numeric position input, interference check across 3+ files (all pairs), STEP export of the aligned scene, resizable panels, and persisted window / unit / recent-files settings.

### v0.4.0

- **Perf:** section clipping now runs parallel in the background (no more UI freeze while dragging the slider on large files); part-transform bake (drag/gizmo release) parallelized; interference gap refinement accelerated with AABB early rejection.
- **Stability:** global exception handling (log to `%LOCALAPPDATA%\STPViewer\error.log` + message box instead of crashing); geometry-mutating commands disabled while a background computation runs; import reentrancy guarded.

### v0.3.2

- **Perf:** large-assembly measurement no longer lags while orbiting. Measurement modes now render the merged mesh (one model per file) and resolve the picked face from the hit triangle's vertex index, instead of rendering tens of thousands of per-face models. Per-face rendering is kept only for section mode.
- Camera-interaction suspension now also subscribes to `HelixViewport3D.CameraChanged` so it can't be orphaned if the camera instance is replaced.

### v0.3.1

- Gizmo always-on-top overlay (manipulator floats above parts, never occluded).

### v0.3.0

- Rotation alignment: axis rotate (↻X/↻Y/↻Z), 3-point align, and the unified `TransformRoot` rigid-transform path.

### v0.2.x

- Drag mode, 2-point align, interference check, section plane, angle / face-distance measurement, assembly tree, STL / DXF support, mm ⇄ inch.

---

## 🙏 Acknowledgments

- [CADability](https://github.com/SOFAgh/CADability) — pure-C# CAD kernel: STEP import, B-rep geometry, face triangulation
- [HelixToolkit.Wpf](https://github.com/helix-toolkit/helix-toolkit) — 3D viewport, camera control, hit testing
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full design and development phases.
