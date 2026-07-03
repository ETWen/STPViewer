using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Wpf;
using Microsoft.Win32;
using STPViewer.Models;
using STPViewer.Services;

namespace STPViewer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly StepImportService _importService = new();
    private readonly MeasurementService _measureService = new();

    /// <summary>渲染物件 → B-rep Face 反查表（HitTest 用；overlay 不在表內所以自然被忽略）</summary>
    private readonly Dictionary<Model3D, FaceInfo> _faceMap = new();

    /// <summary>合併網格 → leaf 反查表（拖曳模式在瀏覽渲染下 hit-test 用）</summary>
    private readonly Dictionary<Model3D, ModelNodeViewModel> _mergedMap = new();

    /// <summary>合併網格 → (每面頂點起始邊界, 對應 FaceInfo)。命中合併網格時用三角形頂點 index 二分搜尋反查回是哪個面，
    /// 讓量測模式也能 render 合併網格（62 個 model）而非逐面（數萬個 model）。starts 長度 = faces+1，最後一格為總頂點數。
    /// 邊界由匯入時依面序串接而成，與 BuildMergedMesh 串接順序一致；平移不改各面頂點數，故邊界永久有效。</summary>
    private readonly Dictionary<Model3D, (int[] Starts, FaceInfo[] Faces)> _mergedFaceRanges = new();

    private HelixViewport3D? _viewport;
    private int _paletteIndex;
    private readonly Dictionary<MeasureMode, int> _counters = new();

    // 兩段式量測的第一次拾取暫存
    private Point3D? _pendingPoint;          // 距離
    private DirectionPick? _pendingDirection; // 角度
    private FaceInfo? _pendingFace;           // 面距
    private (ModelNodeViewModel Root, Point3D Point)? _pendingAlign; // 兩點對齊
    private readonly List<(ModelNodeViewModel Root, Point3D P)> _align3 = new(); // 三點對齊（前3=來源、後3=目標）
    private readonly List<Visual3D> _pendingOverlays = new();

    /// <summary>樹面板目前選取的節點（MainWindow SelectedItemChanged 轉發；旋轉指令的目標）</summary>
    [ObservableProperty]
    private ModelNodeViewModel? selectedNode;

    // 互動中暫停邊線：轉動/縮放/平移時隱藏 LinesVisual3D，停下再顯示 → 大組件流暢
    private readonly DispatcherTimer _interactionTimer;
    private bool _edgesSuspended;

    [ObservableProperty]
    private MeasureMode currentMode = MeasureMode.None;

    [ObservableProperty]
    private string statusText = "就緒 — 匯入 STP/STEP/STL/DXF（拖放亦可）。右鍵旋轉、滾輪縮放、中鍵或 Shift+左鍵平移";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportRecentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateRootCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckInterferenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportStepFileCommand))]
    private bool isBusy;

    [ObservableProperty]
    private bool useInch;

    /// <summary>樹面板名稱過濾字串（只影響樹顯示，不影響 3D 可見性）</summary>
    [ObservableProperty]
    private string? treeFilter;

    partial void OnTreeFilterChanged(string? value)
    {
        foreach (ModelNodeViewModel root in Roots) root.ApplyFilter(value);
    }

    /// <summary>最近開啟檔案（MRU，最多 10 筆；隨 settings.json 保存）</summary>
    public ObservableCollection<string> RecentFiles { get; } = new();

    public ObservableCollection<ModelNodeViewModel> Roots { get; } = new();
    public ObservableCollection<MeasurementResult> Measurements { get; } = new();

    // ─── 使用者設定（視窗由 MainWindow 處理；VM 管單位 + MRU）─────

    public void LoadSettings(AppSettings s)
    {
        UseInch = s.UseInch;
        RecentFiles.Clear();
        foreach (string f in s.RecentFiles.Where(File.Exists).Take(SettingsService.MaxRecentFiles))
            RecentFiles.Add(f);
    }

    public void SaveSettingsInto(AppSettings s)
    {
        s.UseInch = UseInch;
        s.RecentFiles = RecentFiles.ToList();
    }

    private void TouchRecent(string path)
    {
        string full = Path.GetFullPath(path);
        RecentFiles.Remove(full);
        RecentFiles.Insert(0, full);
        while (RecentFiles.Count > SettingsService.MaxRecentFiles)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }

    public MainViewModel()
    {
        _sectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _sectionTimer.Tick += (_, _) => { _sectionTimer.Stop(); ApplySection(); };

        // 相機停止移動 180ms 後恢復邊線
        _interactionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _interactionTimer.Tick += (_, _) => { _interactionTimer.Stop(); ResumeEdges(); };
    }

    private UnitSystem CurrentUnit => UseInch ? UnitSystem.Inch : UnitSystem.Millimeter;

    /// <summary>由 MainWindow 注入 viewport（桌面工具，不做過度抽象）</summary>
    public void Attach(HelixViewport3D viewport)
    {
        _viewport = viewport;
        // 相機一動就暫停邊線/逐面（轉動/縮放/平移皆觸發），停下 180ms 再恢復。
        // 兩個事件都訂閱，互補保底：
        //   1) viewport.CameraChanged（控制項層級 routed event）— 相機實例若被 Helix 內部換掉也接得住；
        //   2) Camera.Changed（Freezable，相機 Position/方向一被改就觸發）— 確保每次移動都收到。
        // 舊寫法只訂 Camera.Changed 且包在 null 檢查裡，萬一相機實例被換就永久失聯 → 暫停機制全失效。
        // 重複觸發 OnCameraMoved 無害（idempotent，內部有 _edgesSuspended guard）。
        viewport.CameraChanged += (_, _) => OnCameraMoved();
        if (viewport.Camera is not null)
            viewport.Camera.Changed += (_, _) => OnCameraMoved();
    }

    private void OnCameraMoved()
    {
        SyncOverlayCamera(); // 操作器疊圖層跟著主相機
        if (!_edgesSuspended) { _edgesSuspended = true; SetEdgesActive(false); }
        _interactionTimer.Stop();
        _interactionTimer.Start();
    }

    private void ResumeEdges()
    {
        _edgesSuspended = false;
        SetEdgesActive(true);
    }

    /// <summary>只切換 root 合併邊線的 viewport 掛載（不重建 Points）</summary>
    private void SetEdgesActive(bool active)
    {
        if (_viewport is null) return;
        foreach (ModelNodeViewModel root in Roots)
            if (root.EdgeVisual is not null)
                SyncVisual(root.EdgeVisual, active && root.EdgeVisual.Points.Count > 0);
    }

    /// <summary>IsBusy 時停用會改動幾何/場景的指令（匯入、旋轉、干涉），避免背景運算期間狀態被改走造成結果錯位</summary>
    private bool IsIdle() => !IsBusy;

    // ─── 匯入 ────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "匯入 CAD 檔案（可複選）",
            Filter = "CAD 檔案 (*.stp;*.step;*.stl;*.dxf)|*.stp;*.step;*.stl;*.dxf|" +
                     "STEP (*.stp;*.step)|*.stp;*.step|STL (*.stl)|*.stl|DXF (*.dxf)|*.dxf|所有檔案 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true)
            await ImportFilesAsync(dlg.FileNames);
    }

    /// <summary>MRU 下拉點選（檔案可能已被移走 → ImportFilesAsync 會報匯入失敗）</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ImportRecentAsync(string path) => ImportFilesAsync(new[] { path });

    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if (_viewport is null) return;
        // 拖放/命令列可繞過指令 CanExecute 直接進來 → 匯入或背景運算期間直接擋掉，
        // 避免兩批匯入交錯（StepImportService 有共享狀態，非重入安全）
        if (IsBusy) { StatusText = "忙碌中（匯入或運算進行中），請稍候再匯入"; return; }
        int ok = 0, fail = 0;
        IsBusy = true; // 整批匯入期間維持 busy（v0.4.0：不再逐檔開關，杜絕檔案之間的重入空窗）
        try
        {
            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                StatusText = $"匯入中：{name} …";
                // 匯入階段回報（從背景執行緒來 → 切回 UI 執行緒）
                _importService.Progress = msg =>
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        () => StatusText = $"匯入 {name}：{msg}");
                try
                {
                    ImportedFileData data = await Task.Run(() => _importService.Import(path));
                    BuildRoot(data);
                    TouchRecent(path);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    StatusText = $"匯入失敗：{name} — {ex.Message}";
                    MessageBox.Show($"{path}\n\n{ex.Message}", "匯入失敗",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
        if (ok > 0)
        {
            if (SectionEnabled) ApplySection();
            _viewport.ZoomExtents(500);
            StatusText = $"匯入完成：成功 {ok} 檔" + (fail > 0 ? $"、失敗 {fail} 檔" : "");
        }
    }

    private void BuildRoot(ImportedFileData data)
    {
        string baseName = Path.GetFileNameWithoutExtension(data.FilePath);
        string name = baseName;
        for (int i = 2; Roots.Any(r => r.Name == name); i++)
            name = $"{baseName} ({i})";

        ModelNodeViewModel root = BuildNode(data.Root, isRoot: true, data.FilePath, name);

        // 邊線總量過大 → 整檔預設關閉（cascade；使用者可再開）
        int totalSegments = root.Leaves().Sum(l => (l.OriginalEdgePoints?.Count ?? 0) / 2);
        if (totalSegments > 30_000)
            root.ShowEdges = false;

        Roots.Add(root);
        if (!string.IsNullOrWhiteSpace(TreeFilter)) root.ApplyFilter(TreeFilter); // 新檔套用目前過濾
        foreach (ModelNodeViewModel leaf in root.Leaves())
        {
            leaf.VisualStateChanged += _ => SyncLeafVisuals(root);
            if (leaf.BodyVisual is not null)
                _viewport!.Children.Add(leaf.BodyVisual);
        }

        // 整檔合併成「一條」LinesVisual3D（轉動時只重建 1 條，避免逐零件 N 條造成卡頓）
        if (root.Leaves().Any(l => l.OriginalEdgePoints is { Count: > 0 }))
        {
            root.EdgeVisual = new LinesVisual3D
            {
                Color = Color.FromRgb(0x30, 0x30, 0x30),
                Thickness = 0.6,
            };
            RefreshRootEdges(root);
        }
    }

    private ModelNodeViewModel BuildNode(ImportedNode n, bool isRoot, string? filePath, string? overrideName)
    {
        var vm = new ModelNodeViewModel(n.Faces.Count > 0 || n.EdgeSegments is not null ? _paletteIndex++ : _paletteIndex)
        {
            Name = overrideName ?? n.Name,
            FilePath = isRoot ? filePath : null,
            SolidCount = n.SolidCount,
            FaceCount = n.FaceCount,
            TriangleCount = n.TriangleCount,
            Bounds = n.Bounds,
            HasBrep = n.HasBrep,
        };

        if (n.Faces.Count > 0)
        {
            // leaf 共用材質（換色時只改 Brush）
            var diffuseBrush = new SolidColorBrush(vm.Color);
            diffuseBrush.Freeze();
            var diffuse = new DiffuseMaterial(diffuseBrush);
            var matGroup = new MaterialGroup();
            matGroup.Children.Add(diffuse);
            matGroup.Children.Add(new SpecularMaterial(Brushes.White, 60));
            var backMat = new DiffuseMaterial(Brushes.DimGray);
            backMat.Freeze();
            vm.SharedMaterial = diffuse;

            // 一個 B-rep Face = 一個 GeometryModel3D，登錄反查表（剖面模式逐面拾取用）。
            // 同時依面序記錄每面頂點起始邊界 + 對應 FaceInfo，供「命中合併網格反查面」（量測模式拾取用）。
            var group = new Model3DGroup();
            var faceInfos = new FaceInfo[n.Faces.Count];
            var vertStarts = new int[n.Faces.Count + 1];
            int vacc = 0;
            for (int i = 0; i < n.Faces.Count; i++)
            {
                ImportedFace f = n.Faces[i];
                var gm = new GeometryModel3D(f.Mesh, matGroup) { BackMaterial = backMat };
                group.Children.Add(gm);
                var info = new FaceInfo { BrepFace = f.BrepFace, Mesh = f.Mesh, Owner = vm };
                _faceMap[gm] = info;
                faceInfos[i] = info;
                vertStarts[i] = vacc;
                vacc += f.Mesh.Positions.Count;
            }
            vertStarts[n.Faces.Count] = vacc;
            vm.FacesContent = group;

            // 合併網格：整零件所有面併成 1 個 GeometryModel3D（draw call 從每面 1 個降為每檔 1 個）。
            // BuildMergedMesh 依 n.Faces 同序串接（baseIdx += positions.Count），與上面 vertStarts 對齊 → 命中頂點 index 可反查回面。
            var mergedModel = new GeometryModel3D(BuildMergedMesh(n.Faces), matGroup);
            // 封閉實體背面不可見 → 不設 BackMaterial（WPF 兩面渲染成本砍半）；開放殼/STL 仍需背面。
            // 拾取不受材質影響（WPF 3D hit-test 純幾何、不剔背面），故孔內壁仍可命中。
            if (n.SolidCount == 0 || !n.HasBrep)
                mergedModel.BackMaterial = backMat;
            vm.MergedContent = new Model3DGroup();
            vm.MergedContent.Children.Add(mergedModel);
            _mergedMap[mergedModel] = vm;
            _mergedFaceRanges[mergedModel] = (vertStarts, faceInfos);

            // 非剖面一律顯示合併網格（瀏覽/量測都是，量測靠 _mergedFaceRanges 反查面）；只有剖面才逐面（裁切後幾何）
            vm.BodyVisual = new ModelVisual3D
            {
                Content = SectionEnabled ? group : vm.MergedContent,
            };
        }

        // 邊線端點僅保存於 leaf（資料）；實際 LinesVisual3D 由 root 合併建立（見 BuildRoot）
        if (n.EdgeSegments is { Count: > 0 })
            vm.OriginalEdgePoints = n.EdgeSegments;

        foreach (ImportedNode c in n.Children)
            vm.Children.Add(BuildNode(c, isRoot: false, null, null));
        return vm;
    }

    private void SyncLeafVisuals(ModelNodeViewModel root)
    {
        if (_viewport is null) return;
        foreach (ModelNodeViewModel leaf in root.Leaves())
            if (leaf.BodyVisual is not null)
                SyncVisual(leaf.BodyVisual, leaf.IsVisible);
        RefreshRootEdges(root);
    }

    /// <summary>
    /// 重建整檔的合併邊線：收集所有「可見且開邊線」的 leaf 端點為單一 Point3DCollection。
    /// 剖面開啟時即時裁切。一檔只有一條 LinesVisual3D → 轉動視角不再逐零件重建。
    /// </summary>
    private void RefreshRootEdges(ModelNodeViewModel root)
    {
        if (_viewport is null || root.EdgeVisual is null) return;

        var merged = new Point3DCollection();
        foreach (ModelNodeViewModel leaf in root.Leaves())
        {
            if (!leaf.IsVisible || !leaf.ShowEdges || leaf.OriginalEdgePoints is null) continue;
            Point3DCollection pts = SectionEnabled
                ? SectionService.ClipSegments(leaf.OriginalEdgePoints, _sectionPlanePoint, _sectionNormal)
                : leaf.OriginalEdgePoints;
            foreach (Point3D p in pts) merged.Add(p);
        }

        root.EdgeVisual.Points = merged;
        SyncVisual(root.EdgeVisual, !_edgesSuspended && merged.Count > 0);
    }

    private void SyncVisual(Visual3D visual, bool shouldShow)
    {
        bool contains = _viewport!.Children.Contains(visual);
        if (shouldShow && !contains) _viewport.Children.Add(visual);
        else if (!shouldShow && contains) _viewport.Children.Remove(visual);
    }

    // ─── 合併網格（瀏覽模式降 draw call）────────────────────────

    /// <summary>把多個面網格依序併成單一 MeshGeometry3D（索引位移後串接；順序 = 呼叫端給的面序）</summary>
    private static MeshGeometry3D MergeMeshes(IReadOnlyList<MeshGeometry3D> meshes)
    {
        int nv = 0, nt = 0;
        foreach (MeshGeometry3D m in meshes) { nv += m.Positions.Count; nt += m.TriangleIndices.Count; }

        var pos = new Point3DCollection(nv);
        var nrm = new Vector3DCollection(nv);
        var idx = new Int32Collection(nt);
        int baseIdx = 0;
        bool hasNormals = true;
        foreach (MeshGeometry3D m in meshes)
        {
            foreach (Point3D p in m.Positions) pos.Add(p);
            if (m.Normals.Count == m.Positions.Count)
                foreach (Vector3D v in m.Normals) nrm.Add(v);
            else hasNormals = false;
            foreach (int ti in m.TriangleIndices) idx.Add(ti + baseIdx);
            baseIdx += m.Positions.Count;
        }

        var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx };
        if (hasNormals && nrm.Count == pos.Count) mesh.Normals = nrm;
        mesh.Freeze();
        return mesh;
    }

    /// <summary>匯入時建合併網格（依 faces 面序串接，與 _mergedFaceRanges 的頂點邊界對齊）</summary>
    private static MeshGeometry3D BuildMergedMesh(IReadOnlyList<ImportedFace> faces)
    {
        var meshes = new List<MeshGeometry3D>(faces.Count);
        foreach (ImportedFace f in faces) meshes.Add(f.Mesh);
        return MergeMeshes(meshes);
    }

    /// <summary>重建合併網格（位置變更後，如零件平移）。由 leaf 逐面 fi.Mesh（未剖切原始）依 FacesContent 子序串接，
    /// 與匯入時 BuildMergedMesh 的面序一致 → _mergedFaceRanges 頂點邊界維持有效。</summary>
    private void RebuildMerged(ModelNodeViewModel leaf)
    {
        if (leaf.MergedContent is null || leaf.FacesContent is null) return;
        var meshes = new List<MeshGeometry3D>(leaf.FacesContent.Children.Count);
        foreach (Model3D mm in leaf.FacesContent.Children)
            if (mm is GeometryModel3D g && _faceMap.TryGetValue(g, out FaceInfo? fi))
                meshes.Add(fi.Mesh);
        ((GeometryModel3D)leaf.MergedContent.Children[0]).Geometry = MergeMeshes(meshes);
    }

    /// <summary>
    /// 依目前狀態切換每個 leaf 的渲染內容：
    /// 非剖面（瀏覽 + 量測皆是）→ 合併網格（每檔 1 個 model，快）；量測拾取靠命中合併網格反查面（_mergedFaceRanges）。
    /// 剖面 → 逐面（要顯示各面裁切後的幾何）。
    /// </summary>
    private void ApplyRenderMode()
    {
        // 量測不再需要逐面渲染（拾取打合併網格反查面）→ 只有剖面才逐面。永不在量測模式掛數萬個 GeometryModel3D。
        bool merged = !SectionEnabled;
        foreach (ModelNodeViewModel leaf in Roots.SelectMany(r => r.Leaves()))
        {
            if (leaf.BodyVisual is null) continue;
            Model3DGroup? target = merged ? leaf.MergedContent : leaf.FacesContent;
            if (target is not null && !ReferenceEquals(leaf.BodyVisual.Content, target))
                leaf.BodyVisual.Content = target;
        }
    }

    // ─── 節點操作 ────────────────────────────────────────────────

    [RelayCommand]
    private void ZoomNode(ModelNodeViewModel node)
    {
        if (!node.Bounds.IsEmpty)
            _viewport?.ZoomExtents(node.Bounds, 400);
    }

    [RelayCommand]
    private void ZoomAll() => _viewport?.ZoomExtents(400);

    /// <summary>標準視圖（機構慣例 Z 向上）：Iso / Front / Back / Top / Bottom / Left / Right</summary>
    [RelayCommand]
    private void SetStandardView(string name)
    {
        if (_viewport?.Camera is not ProjectionCamera cam) return;
        (Vector3D dir, Vector3D up) = name switch
        {
            "Front"  => (new Vector3D(0, 1, 0),  new Vector3D(0, 0, 1)),  // 從 -Y 往 +Y 看
            "Back"   => (new Vector3D(0, -1, 0), new Vector3D(0, 0, 1)),
            "Top"    => (new Vector3D(0, 0, -1), new Vector3D(0, 1, 0)),
            "Bottom" => (new Vector3D(0, 0, 1),  new Vector3D(0, -1, 0)),
            "Right"  => (new Vector3D(-1, 0, 0), new Vector3D(0, 0, 1)),
            "Left"   => (new Vector3D(1, 0, 0),  new Vector3D(0, 0, 1)),
            _        => (new Vector3D(-1, -1, -1), new Vector3D(0, 0, 1)), // Iso
        };
        dir.Normalize();
        Rect3D b = UnionBounds();
        Point3D center = b.IsEmpty ? new Point3D()
            : new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        double dist = Math.Max(SceneDiagonal() * 1.8, 10);
        cam.Position = center - dir * dist;
        cam.LookDirection = dir * dist; // 目標點 = Position + LookDirection = 場景中心
        cam.UpDirection = up;
        _viewport.ZoomExtents(400);
        StatusText = $"視圖：{name}";
    }

    /// <summary>正交投影切換（平行投影，量尺寸不受透視變形；Helix 保持視角換相機型別）</summary>
    [ObservableProperty]
    private bool orthographicView;

    partial void OnOrthographicViewChanged(bool value)
    {
        if (_viewport is null) return;
        _viewport.Orthographic = value;
        StatusText = value ? "正交投影（平行投影，適合尺寸確認）" : "透視投影";
    }

    [RelayCommand]
    private void RemoveRoot(ModelNodeViewModel root)
    {
        if (_viewport is null || !root.IsRoot) return;

        if (ReferenceEquals(root, _gizmoTarget)) GizmoEnabled = false; // 目標被移除 → 收掉操作器

        // 量測 overlay 可能標在此檔模型上 → 一併清除（簡化：全清）
        if (Measurements.Count > 0) ClearMeasurements();

        foreach (ModelNodeViewModel leaf in root.Leaves())
        {
            if (leaf.BodyVisual is not null)
                _viewport.Children.Remove(leaf.BodyVisual);
            if (leaf.FacesContent is not null) // 逐面群組才在 _faceMap（與目前顯示哪種內容無關）
                foreach (Model3D m in leaf.FacesContent.Children)
                    _faceMap.Remove(m);
            if (leaf.MergedContent is not null)
                foreach (Model3D m in leaf.MergedContent.Children)
                {
                    _mergedMap.Remove(m);
                    _mergedFaceRanges.Remove(m);
                }
        }
        if (root.EdgeVisual is not null)
            _viewport.Children.Remove(root.EdgeVisual);
        Roots.Remove(root);
        StatusText = $"已移除：{root.Name}";
    }

    // ─── 顯示控制 / 體積（樹右鍵選單）────────────────────────────

    /// <summary>只顯示此節點（其餘全部隱藏；「全部顯示」復原）</summary>
    [RelayCommand]
    private void IsolateNode(ModelNodeViewModel node)
    {
        foreach (ModelNodeViewModel r in Roots) SetVisibleRecursive(r, false);
        SetVisibleRecursive(node, true);
        StatusText = $"只顯示「{node.Name}」（樹右鍵 → 全部顯示 復原）";
    }

    [RelayCommand]
    private void InvertVisibility()
    {
        foreach (ModelNodeViewModel l in Roots.SelectMany(r => r.Leaves()))
            l.IsVisible = !l.IsVisible;
        StatusText = "已反轉顯示";
    }

    [RelayCommand]
    private void ShowAll()
    {
        foreach (ModelNodeViewModel r in Roots) SetVisibleRecursive(r, true);
        StatusText = "已全部顯示";
    }

    /// <summary>逐節點顯式遞迴設定可見性。不能只設 root（setter 同值不觸發 cascade，
    /// 之前的隔離操作可能留下「父關子開」的混合狀態）</summary>
    private static void SetVisibleRecursive(ModelNodeViewModel node, bool visible)
    {
        node.IsVisible = visible;
        foreach (ModelNodeViewModel c in node.Children) SetVisibleRecursive(c, visible);
    }

    /// <summary>體積/質心（網格 signed volume，封閉實體才可靠）→ 加進量測結果 + 質心標記</summary>
    [RelayCommand]
    private void MeasureVolume(ModelNodeViewModel node)
    {
        var meshes = node.Leaves()
            .Where(l => l.FacesContent is not null)
            .SelectMany(l => l.FacesContent!.Children)
            .OfType<GeometryModel3D>()
            .Where(g => _faceMap.ContainsKey(g))
            .Select(g => _faceMap[g].Mesh)
            .ToList();
        if (meshes.Count == 0)
        {
            StatusText = $"「{node.Name}」沒有可計算的面網格（線架構無體積）";
            return;
        }

        (double vol, Point3D centroid, bool reliable) = MeasurementService.MeshVolume(meshes);
        if (!reliable)
        {
            StatusText = $"「{node.Name}」非封閉網格（開放殼），無法可靠計算體積";
            return;
        }

        double diag = SceneDiagonal();
        double markerR = Math.Clamp(diag * 0.004, 0.01, 5.0);
        string label = NextLabel(MeasureMode.Volume, "V");
        string name = node.Name;
        Rect3D b = node.Bounds;
        var m = new MeasurementResult
        {
            Kind = MeasureMode.Volume,
            TitleFor = u => $"{label}  {name}  V ≈ {Units.V(vol, u)}",
            DetailFor = u => $"體積 ≈ {Units.V(vol, u)}（網格近似）\n質心 {Units.P(centroid, u)}\n" +
                             $"外形（AABB）{Units.C(b.SizeX, u)} × {Units.C(b.SizeY, u)} × {Units.C(b.SizeZ, u)} " +
                             (u == UnitSystem.Millimeter ? "mm" : "in"),
        };
        m.Overlays.Add(new SphereVisual3D { Center = centroid, Radius = markerR, Fill = Brushes.OrangeRed });
        var volLabel = new BillboardTextVisual3D
        {
            Position = centroid + new Vector3D(0, 0, markerR * 2),
            Text = $"{label} {Units.V(vol, UnitSystem.Millimeter)}",
            Foreground = Brushes.Black,
            Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 210)),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 14,
        };
        m.Overlays.Add(volLabel);
        m.DynamicLabels.Add((volLabel, u => $"{label} {Units.V(vol, u)}"));
        AddMeasurement(m);
    }

    // ─── 量測 ────────────────────────────────────────────────────

    partial void OnCurrentModeChanged(MeasureMode value)
    {
        CancelPending();
        ApplyRenderMode(); // 非剖面一律合併網格（量測拾取靠反查面）；不再因切量測模式而掛逐面
        StatusText = value switch
        {
            MeasureMode.None => "瀏覽模式（右鍵旋轉、滾輪縮放、中鍵或 Shift+左鍵拖曳平移）",
            MeasureMode.Point => "點量測：點擊模型表面（自動吸附鄰近頂點）",
            MeasureMode.Distance => "距離量測：點選第 1 點",
            MeasureMode.Edge => "邊量測：點擊靠近要量的邊",
            MeasureMode.Face => "面量測：點擊要量的面",
            MeasureMode.Circle => "圓量測：點擊圓孔內壁或圓邊附近",
            MeasureMode.Angle => "角度量測：點選第 1 個面（或靠近直線邊）",
            MeasureMode.FaceDistance => "面距量測：點選第 1 個面",
            MeasureMode.Align => "兩點對齊：先點「要移動零件」上的點，再點目標點（純平移，量測會清空）",
            MeasureMode.Align3 => "三點對齊：先在「要移動的檔案」點 3 個特徵點，再到目標檔案點 3 個對應點（旋轉+平移）",
            MeasureMode.Drag => "拖曳模式：左鍵按住零件拖動（沿螢幕平面），放開定位；深度方向先轉視角再拖。右鍵仍可轉視角",
            _ => StatusText,
        };
    }

    partial void OnUseInchChanged(bool value)
    {
        UnitSystem u = CurrentUnit;
        foreach (MeasurementResult m in Measurements) m.SetUnit(u);
        StatusText = value ? "顯示單位：inch" : "顯示單位：mm";
    }

    /// <summary>Esc（MainWindow 轉發）：先取消進行中的多段量測（保留模式），再按退回瀏覽模式；再退操作器</summary>
    public void OnEscape()
    {
        if (CancelSectionPickIfActive()) return; // 3點剖面定義中 → 先取消定義
        bool hadPending = _pendingPoint is not null || _pendingDirection is not null ||
                          _pendingFace is not null || _pendingAlign is not null || _align3.Count > 0;
        if (hadPending)
        {
            CancelPending();
            StatusText = "已取消目前量測起點（再按 Esc 退出量測模式）";
        }
        else if (CurrentMode != MeasureMode.None)
        {
            CurrentMode = MeasureMode.None;
        }
        else if (GizmoEnabled)
        {
            GizmoEnabled = false;
        }
    }

    /// <summary>量測模式快速鍵（MainWindow 轉發；無修飾鍵）。同鍵再按 = 退回瀏覽。回傳 false 表示非快速鍵。</summary>
    public bool OnHotKey(System.Windows.Input.Key key)
    {
        MeasureMode? m = key switch
        {
            System.Windows.Input.Key.P => MeasureMode.Point,
            System.Windows.Input.Key.D => MeasureMode.Distance,
            System.Windows.Input.Key.E => MeasureMode.Edge,
            System.Windows.Input.Key.F => MeasureMode.Face,
            System.Windows.Input.Key.C => MeasureMode.Circle,
            System.Windows.Input.Key.A => MeasureMode.Angle,
            System.Windows.Input.Key.M => MeasureMode.FaceDistance,
            _ => null,
        };
        if (m is null) return false;
        CurrentMode = CurrentMode == m ? MeasureMode.None : m.Value;
        return true;
    }

    /// <summary>MainWindow 滑鼠左鍵點擊轉發進來</summary>
    public void OnViewportClick(Point position)
    {
        if (_viewport is null || IsBusy) return;
        if (HandleSectionPlanePick(position)) return; // 3點剖面定義模式優先吃點擊（v0.5.0）
        if (CurrentMode is MeasureMode.None or MeasureMode.Drag) return;

        var hits = _viewport.Viewport.FindHits(position);
        FaceInfo? fi = null;
        Point3D hitPoint = default;
        Vector3D hitNormal = default;
        foreach (var h in hits)
        {
            if (h.Model is null) continue;
            // 剖面模式：渲染逐面 → 直接查 _faceMap
            if (_faceMap.TryGetValue(h.Model, out FaceInfo? f))
            {
                fi = f; hitPoint = h.Position; hitNormal = h.Normal;
                break;
            }
            // 量測/瀏覽模式：渲染合併網格 → 用命中三角形頂點 index 反查回是哪個面
            if (h.RayHit is not null && _mergedFaceRanges.TryGetValue(h.Model, out var range))
            {
                FaceInfo? mf = ResolveMergedFace(range, h.RayHit.VertexIndex1);
                if (mf is not null)
                {
                    fi = mf; hitPoint = h.Position; hitNormal = h.Normal;
                    break;
                }
            }
        }
        if (fi is null)
        {
            StatusText = "未命中模型表面";
            return;
        }

        try
        {
            HandleMeasureClick(fi, hitPoint, hitNormal);
        }
        catch (Exception ex)
        {
            StatusText = $"量測失敗：{ex.Message}";
        }
    }

    /// <summary>命中合併網格的某頂點 index → 反查回是哪個面（依面序的頂點起始邊界二分搜尋）。</summary>
    private static FaceInfo? ResolveMergedFace((int[] Starts, FaceInfo[] Faces) range, int vertexIndex)
    {
        int[] starts = range.Starts;
        FaceInfo[] faces = range.Faces;
        if (vertexIndex < 0 || faces.Length == 0 || vertexIndex >= starts[faces.Length]) return null;
        // 找最大的 k 使 starts[k] <= vertexIndex（face k 佔頂點 [starts[k], starts[k+1])）
        int lo = 0, hi = faces.Length - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (starts[mid] <= vertexIndex) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return faces[ans];
    }

    private void HandleMeasureClick(FaceInfo fi, Point3D hit, Vector3D hitNormal)
    {
        double diag = SceneDiagonal();
        double snapTol = diag * 0.02;
        double markerR = Math.Clamp(diag * 0.004, 0.01, 5.0);

        // 無 B-rep 來源（STL）只支援部分模式
        if (fi.BrepFace is null && CurrentMode is MeasureMode.Edge or MeasureMode.Circle or MeasureMode.Face)
        {
            StatusText = "此模型無 B-rep（STL/網格），僅支援 點 / 距離 / 角度 / 面距 量測";
            return;
        }

        switch (CurrentMode)
        {
            case MeasureMode.Point:
            {
                Point3D p = _measureService.Snap(fi, hit, snapTol);
                AddMeasurement(_measureService.MeasurePoint(p, NextLabel(MeasureMode.Point, "P"), markerR));
                break;
            }
            case MeasureMode.Distance:
            {
                Point3D p = _measureService.Snap(fi, hit, snapTol);
                if (_pendingPoint is null)
                {
                    _pendingPoint = p;
                    AddPendingMarker(p, markerR);
                    StatusText = $"第 1 點 ({p.X:F2}, {p.Y:F2}, {p.Z:F2}) — 點選第 2 點";
                }
                else
                {
                    var result = _measureService.MeasureDistance(
                        _pendingPoint.Value, p, NextLabel(MeasureMode.Distance, "D"), markerR);
                    CancelPending();
                    AddMeasurement(result);
                }
                break;
            }
            case MeasureMode.Edge:
            {
                var result = _measureService.MeasureEdge(fi, hit, NextLabel(MeasureMode.Edge, "E"), markerR, circlesOnly: false);
                if (result is null) { StatusText = "此面沒有可量測的邊"; UndoLabel(MeasureMode.Edge); }
                else AddMeasurement(result);
                break;
            }
            case MeasureMode.Face:
            {
                AddMeasurement(_measureService.MeasureFace(fi, hit, NextLabel(MeasureMode.Face, "F"), markerR));
                break;
            }
            case MeasureMode.Circle:
            {
                var result = _measureService.MeasureEdge(fi, hit, NextLabel(MeasureMode.Circle, "C"), markerR, circlesOnly: true);
                if (result is null) { StatusText = "此面沒有圓形邊（請點圓孔內壁或圓邊附近）"; UndoLabel(MeasureMode.Circle); }
                else AddMeasurement(result);
                break;
            }
            case MeasureMode.Angle:
            {
                DirectionPick pick = _measureService.PickDirection(fi, hit, hitNormal, snapTol);
                if (_pendingDirection is null)
                {
                    _pendingDirection = pick;
                    AddPendingMarker(pick.At, markerR);
                    StatusText = $"已選第 1 個方向（{pick.Desc}）— 點選第 2 個面或邊";
                }
                else
                {
                    var result = _measureService.MeasureAngle(
                        _pendingDirection, pick, NextLabel(MeasureMode.Angle, "∠"), markerR, diag * 0.08);
                    CancelPending();
                    AddMeasurement(result);
                }
                break;
            }
            case MeasureMode.FaceDistance:
            {
                if (_pendingFace is null)
                {
                    _pendingFace = fi;
                    AddPendingMarker(hit, markerR);
                    StatusText = "已選第 1 個面 — 點選另一個面";
                }
                else if (ReferenceEquals(_pendingFace, fi))
                {
                    StatusText = "兩次點到同一個面，請點選另一個面";
                }
                else
                {
                    var result = _measureService.MeasureFaceDistance(
                        _pendingFace, fi, NextLabel(MeasureMode.FaceDistance, "M"), markerR);
                    CancelPending();
                    AddMeasurement(result);
                }
                break;
            }
            case MeasureMode.Align:
            {
                Point3D p = _measureService.Snap(fi, hit, snapTol);
                ModelNodeViewModel? root = FindRootOf(fi.Owner);
                if (root is null) { StatusText = "找不到此零件所屬檔案"; break; }
                if (_pendingAlign is null)
                {
                    _pendingAlign = (root, p);
                    AddPendingMarker(p, markerR);
                    StatusText = $"將移動「{root.Name}」— 點選目標點（另一個檔案上）";
                }
                else if (ReferenceEquals(_pendingAlign.Value.Root, root))
                {
                    StatusText = "目標點要點在另一個檔案上（兩點同屬一個檔案無法對齊）";
                }
                else
                {
                    (ModelNodeViewModel moveRoot, Point3D from) = _pendingAlign.Value;
                    Vector3D offset = p - from;
                    CancelPending();
                    TranslateRoot(moveRoot, offset);
                    StatusText = $"已移動「{moveRoot.Name}」 Δ({offset.X:F3}, {offset.Y:F3}, {offset.Z:F3}) mm" +
                                 "（量測已清空；可用 ⇔面距 / 🧩干涉 驗證配合）";
                }
                break;
            }
            case MeasureMode.Align3:
            {
                Point3D p = _measureService.Snap(fi, hit, snapTol);
                ModelNodeViewModel? root = FindRootOf(fi.Owner);
                if (root is null) { StatusText = "找不到此零件所屬檔案"; break; }

                if (_align3.Count < 3) // 來源 3 點（要移動的檔案）
                {
                    if (_align3.Count > 0 && !ReferenceEquals(_align3[0].Root, root))
                    {
                        StatusText = $"來源 3 點要同一個檔案（要移動：{_align3[0].Root.Name}）";
                        break;
                    }
                    if (_align3.Count == 2 && RigidAlign.Collinear(_align3[0].P, _align3[1].P, p))
                    {
                        StatusText = "三點共線，請改選不共線的第 3 點";
                        break;
                    }
                    _align3.Add((root, p));
                    AddPendingMarker(p, markerR);
                    StatusText = _align3.Count < 3
                        ? $"來源點 {_align3.Count}/3 — 繼續點「{root.Name}」上的點"
                        : "來源 3 點完成 — 換點「目標檔案」上的對應點 1/3";
                }
                else // 目標 3 點（另一個檔案，順序對應來源點）
                {
                    if (ReferenceEquals(_align3[0].Root, root))
                    {
                        StatusText = "目標點要點在另一個檔案上";
                        break;
                    }
                    if (_align3.Count == 5 && RigidAlign.Collinear(_align3[3].P, _align3[4].P, p))
                    {
                        StatusText = "目標三點共線，請改選不共線的第 3 點";
                        break;
                    }
                    _align3.Add((root, p));
                    AddPendingMarker(p, markerR);
                    if (_align3.Count < 6)
                    {
                        StatusText = $"目標點 {_align3.Count - 3}/3 — 繼續點目標檔案上的對應點";
                        break;
                    }

                    ModelNodeViewModel moveRoot = _align3[0].Root;
                    Point3D p1 = _align3[0].P, p2 = _align3[1].P, p3 = _align3[2].P;
                    Point3D q1 = _align3[3].P, q2 = _align3[4].P, q3 = _align3[5].P;
                    CancelPending();
                    if (!RigidAlign.TryRigidTransform(p1, p2, p3, q1, q2, q3, out Matrix3D m))
                    {
                        StatusText = "三點對齊失敗（點位退化），請重新選點";
                        break;
                    }
                    TransformRoot(moveRoot, RigidAlign.ToModOp(m), m);
                    StatusText = $"已三點對齊「{moveRoot.Name}」（旋轉+平移；點1精確貼合，量測已清空，可用 🧩干涉 驗證）";
                }
                break;
            }
        }
    }

    private ModelNodeViewModel? FindRootOf(ModelNodeViewModel leaf) =>
        Roots.FirstOrDefault(r => r.Leaves().Contains(leaf));

    private void AddPendingMarker(Point3D p, double markerR)
    {
        var marker = new SphereVisual3D { Center = p, Radius = markerR, Fill = Brushes.OrangeRed };
        _pendingOverlays.Add(marker);
        _viewport!.Children.Add(marker);
    }

    private string NextLabel(MeasureMode mode, string prefix)
    {
        _counters.TryGetValue(mode, out int n);
        _counters[mode] = ++n;
        return $"{prefix}{n}";
    }

    private void UndoLabel(MeasureMode mode)
    {
        if (_counters.TryGetValue(mode, out int n) && n > 0) _counters[mode] = n - 1;
    }

    private void AddMeasurement(MeasurementResult result)
    {
        result.SetUnit(CurrentUnit);
        Measurements.Add(result);
        foreach (Visual3D v in result.Overlays)
            _viewport!.Children.Add(v);
        StatusText = $"✓ {result.Title}";
    }

    [RelayCommand]
    private void RemoveMeasurement(MeasurementResult result)
    {
        foreach (Visual3D v in result.Overlays)
            _viewport?.Children.Remove(v);
        Measurements.Remove(result);
    }

    [RelayCommand]
    private void ClearMeasurements()
    {
        foreach (MeasurementResult m in Measurements)
            foreach (Visual3D v in m.Overlays)
                _viewport?.Children.Remove(v);
        Measurements.Clear();
        _counters.Clear();
        CancelPending();
        StatusText = "已清除全部量測";
    }

    private void CancelPending()
    {
        foreach (Visual3D v in _pendingOverlays)
            _viewport?.Children.Remove(v);
        _pendingOverlays.Clear();
        _pendingPoint = null;
        _pendingDirection = null;
        _pendingFace = null;
        _pendingAlign = null;
        _align3.Clear();
    }

    // ─── 零件剛體變換（兩點對齊=平移、旋轉 90°、三點對齊=旋轉+平移）──

    private void TranslateRoot(ModelNodeViewModel root, Vector3D v)
    {
        if (v.LengthSquared == 0) return;
        var m = Matrix3D.Identity;
        m.Translate(v);
        TransformRoot(root, CADability.ModOp.Translate(v.X, v.Y, v.Z), m);
    }

    /// <summary>
    /// 對整個檔案（root）套剛體變換：B-rep 用 ModOp 整體 Modify（量測維持精確）、
    /// 渲染網格 / 合併網格 / 邊線端點同步重算、邊界重新計算。
    /// op 與 m 必須是同一個變換（CADability 與 WPF 兩種表示）。
    /// </summary>
    private void TransformRoot(ModelNodeViewModel root, CADability.ModOp op, Matrix3D m)
    {
        if (_viewport is null) return;
        ClearMeasurements(); // 量測標記位置已失效

        var leaves = root.Leaves().ToList();

        // B-rep：對 Solid/Shell 整體 Modify（避免共用邊被逐面重複變換）。
        // CADability 物件非執行緒安全 → 維持循序
        foreach (ModelNodeViewModel leaf in leaves)
            foreach (CADability.GeoObject.IGeoObject g in leaf.SourceGeos)
            {
                try { g.Modify(op); }
                catch { /* 個別物件變換失敗不中斷 */ }
            }

        // 渲染網格：面級平行變換（來源/輸出皆 frozen、各面獨立 → 安全；大檔 64k 面吃滿多核）
        var faceItems = new List<(GeometryModel3D Gm, FaceInfo Fi)>();
        foreach (ModelNodeViewModel leaf in leaves)
            if (leaf.FacesContent is not null)
                foreach (Model3D mm in leaf.FacesContent.Children)
                    if (mm is GeometryModel3D gm && _faceMap.TryGetValue(gm, out FaceInfo? fi))
                        faceItems.Add((gm, fi));

        var moved = new MeshGeometry3D[faceItems.Count];
        Parallel.For(0, faceItems.Count, i => moved[i] = TransformMesh(faceItems[i].Fi.Mesh, m));
        for (int i = 0; i < faceItems.Count; i++) // 視覺樹賦值回 UI 執行緒循序做
        {
            faceItems[i].Fi.Mesh = moved[i];
            faceItems[i].Gm.Geometry = moved[i];
        }

        foreach (ModelNodeViewModel leaf in leaves)
        {
            if (leaf.FacesContent is not null)
                RebuildMerged(leaf); // 合併網格同步

            // 邊線端點（資料）；實際合併線於迴圈後重建
            if (leaf.OriginalEdgePoints is not null)
                leaf.OriginalEdgePoints = TransformPoints(leaf.OriginalEdgePoints, m);
        }

        RecomputeBounds(root);
        if (SectionEnabled) ApplySection(); // 內含 RefreshRootEdges
        else RefreshRootEdges(root);
    }

    private static MeshGeometry3D TransformMesh(MeshGeometry3D src, Matrix3D m)
    {
        var positions = new Point3DCollection(src.Positions.Count);
        foreach (Point3D p in src.Positions) positions.Add(m.Transform(p));
        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = src.TriangleIndices, // frozen，可共用（法向量缺省由 WPF 自算）
        };
        mesh.Freeze();
        return mesh;
    }

    private static Point3DCollection TransformPoints(Point3DCollection src, Matrix3D m)
    {
        var pts = new Point3DCollection(src.Count);
        foreach (Point3D p in src) pts.Add(m.Transform(p));
        pts.Freeze();
        return pts;
    }

    /// <summary>旋轉後 AABB 不能只平移 → 由網格/邊線重新計算（bottom-up）</summary>
    private Rect3D RecomputeBounds(ModelNodeViewModel node)
    {
        Rect3D b = Rect3D.Empty;
        if (node.IsLeafNode)
        {
            if (node.FacesContent is not null)
                foreach (Model3D mm in node.FacesContent.Children)
                    if (mm is GeometryModel3D gm && _faceMap.TryGetValue(gm, out FaceInfo? fi))
                        b.Union(fi.Mesh.Bounds);
            if (node.OriginalEdgePoints is { Count: > 0 } pts)
                foreach (Point3D p in pts) b.Union(p);
        }
        else
        {
            foreach (ModelNodeViewModel c in node.Children)
                b.Union(RecomputeBounds(c));
        }
        node.Bounds = b;
        return b;
    }

    // ─── 旋轉 90°（樹面板選取的檔案，繞其中心）──────────────────

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void RotateRoot(string axisName)
    {
        ModelNodeViewModel? root = SelectedNode is not null ? RootContaining(SelectedNode)
            : Roots.Count == 1 ? Roots[0] : null;
        if (root is null)
        {
            StatusText = "請先在樹面板點選要旋轉的檔案（任一節點即可）";
            return;
        }
        Rect3D b = root.Bounds;
        if (b.IsEmpty) return;

        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        Vector3D axis = axisName switch
        {
            "X" => new Vector3D(1, 0, 0),
            "Y" => new Vector3D(0, 1, 0),
            _ => new Vector3D(0, 0, 1),
        };
        var m = Matrix3D.Identity;
        m.RotateAt(new Quaternion(axis, 90), center);
        TransformRoot(root, RigidAlign.ToModOp(m), m);
        StatusText = $"已旋轉「{root.Name}」繞 {axisName} 軸 +90°（再按可繼續轉）";
    }

    private ModelNodeViewModel? RootContaining(ModelNodeViewModel node) =>
        Roots.FirstOrDefault(r => ReferenceEquals(r, node) || ContainsNode(r, node));

    private static bool ContainsNode(ModelNodeViewModel parent, ModelNodeViewModel target) =>
        parent.Children.Any(c => ReferenceEquals(c, target) || ContainsNode(c, target));

    // 三點對齊的剛體變換數學在 Services/RigidAlign.cs（SmokeTest --align-test 驗證）

    // ─── 共用 ────────────────────────────────────────────────────

    private Rect3D UnionBounds()
    {
        Rect3D union = Rect3D.Empty;
        foreach (ModelNodeViewModel r in Roots)
            union.Union(r.Bounds);
        return union;
    }

    private double SceneDiagonal()
    {
        Rect3D union = Rect3D.Empty;
        foreach (ModelNodeViewModel r in Roots.Where(r => r.IsVisible))
            union.Union(r.Bounds);
        if (union.IsEmpty) return 100;
        var size = new Vector3D(union.SizeX, union.SizeY, union.SizeZ);
        return size.Length > 0 ? size.Length : 100;
    }
}
