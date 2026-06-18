using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    // 拖曳模式：暫時 Transform 跟著滑鼠，放開才一次性烘進 B-rep（TranslateRoot）
    private ModelNodeViewModel? _dragRoot;
    private Point3D _dragAnchor;              // 命中點（世界座標），拖曳平面的錨點
    private Vector3D _dragApplied;            // 目前累計位移
    private TranslateTransform3D? _dragTransform;

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

    // 剖面
    private readonly DispatcherTimer _sectionTimer;
    private RectangleVisual3D? _sectionPlaneVisual;
    private Point3D _sectionPlanePoint;   // SectionEnabled 時的剖切平面（合併邊線重建用）
    private Vector3D _sectionNormal;

    // 互動中暫停邊線：轉動/縮放/平移時隱藏 LinesVisual3D，停下再顯示 → 大組件流暢
    private readonly DispatcherTimer _interactionTimer;
    private bool _edgesSuspended;

    [ObservableProperty]
    private MeasureMode currentMode = MeasureMode.None;

    [ObservableProperty]
    private string statusText = "就緒 — 匯入 STP/STEP/STL/DXF（拖放亦可）。右鍵旋轉、滾輪縮放、中鍵或 Shift+左鍵平移";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool useInch;

    [ObservableProperty]
    private bool sectionEnabled;

    /// <summary>0=X 1=Y 2=Z</summary>
    [ObservableProperty]
    private int sectionAxisIndex;

    /// <summary>剖面位置（沿軸 0~100%）</summary>
    [ObservableProperty]
    private double sectionPosition = 50;

    [ObservableProperty]
    private bool sectionFlip;

    public ObservableCollection<ModelNodeViewModel> Roots { get; } = new();
    public ObservableCollection<MeasurementResult> Measurements { get; } = new();

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

    /// <summary>注入操作器疊圖層（透明 Viewport3D，疊在主視窗上、永遠最上層）</summary>
    public void AttachOverlay(System.Windows.Controls.Viewport3D overlay)
    {
        _overlayViewport = overlay;
        _overlayCamera = new PerspectiveCamera();
        overlay.Camera = _overlayCamera;
        // 操作器材質需打光（manipulator 用 DiffuseMaterial）
        overlay.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(0x80, 0x80, 0x80)) });
        overlay.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Colors.White, new Vector3D(-1, -1, -3)) });
        SyncOverlayCamera();
    }

    /// <summary>疊圖層相機跟隨主相機（每次主相機變更時呼叫，讓操作器疊在正確螢幕位置）</summary>
    private void SyncOverlayCamera()
    {
        if (_overlayCamera is null || _viewport?.Camera is not ProjectionCamera src) return;
        _overlayCamera.Position = src.Position;
        _overlayCamera.LookDirection = src.LookDirection;
        _overlayCamera.UpDirection = src.UpDirection;
        _overlayCamera.NearPlaneDistance = src.NearPlaneDistance;
        _overlayCamera.FarPlaneDistance = src.FarPlaneDistance;
        if (src is PerspectiveCamera p) _overlayCamera.FieldOfView = p.FieldOfView;
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

    // ─── 匯入 ────────────────────────────────────────────────────

    [RelayCommand]
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

    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if (_viewport is null) return;
        int ok = 0, fail = 0;
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            IsBusy = true;
            StatusText = $"匯入中：{name} …";
            // 匯入階段回報（從背景執行緒來 → 切回 UI 執行緒）
            _importService.Progress = msg =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () => StatusText = $"匯入 {name}：{msg}");
            try
            {
                ImportedFileData data = await Task.Run(() => _importService.Import(path));
                BuildRoot(data);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                StatusText = $"匯入失敗：{name} — {ex.Message}";
                MessageBox.Show($"{path}\n\n{ex.Message}", "匯入失敗",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
            }
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

    /// <summary>把一個零件的所有面網格併成單一 MeshGeometry3D（索引位移後串接）</summary>
    private static MeshGeometry3D BuildMergedMesh(IReadOnlyList<ImportedFace> faces)
    {
        int nv = 0, nt = 0;
        foreach (ImportedFace f in faces) { nv += f.Mesh.Positions.Count; nt += f.Mesh.TriangleIndices.Count; }

        var pos = new Point3DCollection(nv);
        var nrm = new Vector3DCollection(nv);
        var idx = new Int32Collection(nt);
        int baseIdx = 0;
        bool hasNormals = true;
        foreach (ImportedFace f in faces)
        {
            MeshGeometry3D m = f.Mesh;
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

    /// <summary>重建合併網格（位置變更後，如零件平移）。由 leaf 逐面 fi.Mesh（未剖切原始）串接。</summary>
    private void RebuildMerged(ModelNodeViewModel leaf)
    {
        if (leaf.MergedContent is null || leaf.FacesContent is null) return;
        int nv = 0, nt = 0;
        foreach (Model3D mm in leaf.FacesContent.Children)
            if (mm is GeometryModel3D g && _faceMap.TryGetValue(g, out FaceInfo? fi))
            { nv += fi.Mesh.Positions.Count; nt += fi.Mesh.TriangleIndices.Count; }

        var pos = new Point3DCollection(nv);
        var nrm = new Vector3DCollection(nv);
        var idx = new Int32Collection(nt);
        int baseIdx = 0; bool hasNormals = true;
        foreach (Model3D mm in leaf.FacesContent.Children)
        {
            if (mm is not GeometryModel3D g || !_faceMap.TryGetValue(g, out FaceInfo? fi)) continue;
            MeshGeometry3D m = fi.Mesh;
            foreach (Point3D p in m.Positions) pos.Add(p);
            if (m.Normals.Count == m.Positions.Count) foreach (Vector3D v in m.Normals) nrm.Add(v);
            else hasNormals = false;
            foreach (int ti in m.TriangleIndices) idx.Add(ti + baseIdx);
            baseIdx += m.Positions.Count;
        }
        var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx };
        if (hasNormals && nrm.Count == pos.Count) mesh.Normals = nrm;
        mesh.Freeze();
        ((GeometryModel3D)leaf.MergedContent.Children[0]).Geometry = mesh;
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

    /// <summary>MainWindow 滑鼠左鍵點擊轉發進來</summary>
    public void OnViewportClick(Point position)
    {
        if (_viewport is null || IsBusy || CurrentMode is MeasureMode.None or MeasureMode.Drag) return;

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

        foreach (ModelNodeViewModel leaf in root.Leaves())
        {
            // B-rep：對 Solid/Shell 整體 Modify（避免共用邊被逐面重複變換）
            foreach (CADability.GeoObject.IGeoObject g in leaf.SourceGeos)
            {
                try { g.Modify(op); }
                catch { /* 個別物件變換失敗不中斷 */ }
            }

            // 渲染網格（逐面，與目前顯示哪種內容無關）
            if (leaf.FacesContent is not null)
            {
                foreach (Model3D mm in leaf.FacesContent.Children)
                {
                    if (mm is not GeometryModel3D gm || !_faceMap.TryGetValue(gm, out FaceInfo? fi)) continue;
                    MeshGeometry3D moved = TransformMesh(fi.Mesh, m);
                    fi.Mesh = moved;
                    gm.Geometry = moved;
                }
                RebuildMerged(leaf); // 合併網格同步
            }

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

    [RelayCommand]
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

    // ─── 拖曳模式（左鍵按住零件沿螢幕平面拖；放開才烘進 B-rep）──

    /// <summary>左鍵按下：命中零件則開始拖曳（回傳 true 表示要捕捉滑鼠）</summary>
    public bool OnDragStart(Point position)
    {
        if (_viewport is null || IsBusy || CurrentMode != MeasureMode.Drag || _dragRoot is not null)
            return false;

        // 兩種渲染內容都可能在場：合併網格查 _mergedMap、逐面（剖面時）查 _faceMap
        ModelNodeViewModel? leaf = null;
        Point3D anchor = default;
        foreach (var h in _viewport.Viewport.FindHits(position))
        {
            if (h.Model is null) continue;
            if (_faceMap.TryGetValue(h.Model, out FaceInfo? fi)) { leaf = fi.Owner; anchor = h.Position; break; }
            if (_mergedMap.TryGetValue(h.Model, out ModelNodeViewModel? ml)) { leaf = ml; anchor = h.Position; break; }
        }
        if (leaf is null) return false;
        ModelNodeViewModel? root = FindRootOf(leaf) ?? RootContaining(leaf);
        if (root is null) return false;

        _dragRoot = root;
        _dragAnchor = anchor;
        _dragApplied = default;
        _dragTransform = new TranslateTransform3D();
        foreach (ModelNodeViewModel l in root.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = _dragTransform;

        // 拖曳中隱藏邊線（LinesVisual3D 隨 Transform 變更逐幀重建會卡）
        _edgesSuspended = true;
        SetEdgesActive(false);
        StatusText = $"拖曳「{root.Name}」中…（放開定位）";
        return true;
    }

    /// <summary>滑鼠移動：把 2D 位移投影到「過錨點、面向相機」的平面上 → 暫時 Transform</summary>
    public void OnDragMove(Point position)
    {
        if (_viewport?.Camera is not System.Windows.Media.Media3D.ProjectionCamera cam ||
            _dragRoot is null || _dragTransform is null) return;

        Point3D? p = _viewport.Viewport.UnProject(position, _dragAnchor, cam.LookDirection);
        if (p is null) return;
        _dragApplied = p.Value - _dragAnchor;
        _dragTransform.OffsetX = _dragApplied.X;
        _dragTransform.OffsetY = _dragApplied.Y;
        _dragTransform.OffsetZ = _dragApplied.Z;
    }

    /// <summary>放開：移除暫時 Transform，一次性烘進 B-rep（量測精度不受拖曳影響）</summary>
    public bool OnDragEnd()
    {
        if (_dragRoot is null) return false;
        ModelNodeViewModel root = _dragRoot;
        Vector3D delta = _dragApplied;

        // 清除暫時位移：必須用 Identity，不可用 null —— HelixToolkit GetTransform 對 child.Transform
        // 不做 null 檢查（Children.Add(null) 會拋「無法新增空值到集合中」），下次 FindHits 即 crash
        foreach (ModelNodeViewModel l in root.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = Transform3D.Identity;
        _dragRoot = null;
        _dragTransform = null;
        _dragApplied = default;
        _edgesSuspended = false;
        SetEdgesActive(true); // 恢復所有檔案的邊線（被拖檔案的點隨後由 TranslateRoot 更新，期間不會渲染）

        if (delta.LengthSquared > 1e-12)
        {
            TranslateRoot(root, delta); // 烘進 B-rep + 網格 + 邊線 + 邊界（量測清空）
            StatusText = $"已拖曳「{root.Name}」 Δ({delta.X:F2}, {delta.Y:F2}, {delta.Z:F2}) mm（可用 🧩干涉 驗證）";
        }
        return true;
    }

    // ─── Gizmo 三軸操作器（XYZ 箭頭 + 旋轉環；每次放開滑鼠烘進 B-rep）──

    [ObservableProperty]
    private bool gizmoEnabled;

    private readonly List<HelixToolkit.Wpf.Manipulator> _gizmoParts = new();
    private ModelVisual3D? _gizmoProxy;        // 操作器綁定的代理 visual（其 Transform = 使用者拖出的變換）
    private ModelNodeViewModel? _gizmoTarget;
    private bool _gizmoDragActive;             // 拖動中（邊線已暫停）
    private bool _gizmoBaking;                 // 烘焙中，忽略 Transform 變更回呼
    private bool _gizmoBakePending;            // 已排程烘焙，防同一次放開重複觸發

    // 操作器疊圖層：另一個透明 Viewport3D 疊在主視窗上、相機同步、只放操作器 →
    // 操作器不在主場景，永不被實體遮擋（always-on-top）；空白處滑鼠穿透回主視窗
    private System.Windows.Controls.Viewport3D? _overlayViewport;
    private PerspectiveCamera? _overlayCamera;

    partial void OnGizmoEnabledChanged(bool value) => UpdateGizmo();

    partial void OnSelectedNodeChanged(ModelNodeViewModel? value)
    {
        if (GizmoEnabled) UpdateGizmo(); // 換選取 → 操作器跟著換目標
    }

    private void UpdateGizmo()
    {
        RemoveGizmo();
        if (!GizmoEnabled || _viewport is null || _overlayViewport is null) return;

        ModelNodeViewModel? root = SelectedNode is not null ? RootContaining(SelectedNode)
            : Roots.Count == 1 ? Roots[0] : null;
        if (root is null || root.Bounds.IsEmpty)
        {
            StatusText = "操作器：請先在樹面板點選要操作的檔案";
            GizmoEnabled = false;
            return;
        }
        _gizmoTarget = root;
        SyncOverlayCamera(); // 操作器出現前先對齊相機

        Rect3D b = root.Bounds;
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        double diag = new Vector3D(b.SizeX, b.SizeY, b.SizeZ).Length;

        _gizmoProxy = new ModelVisual3D();
        _overlayViewport.Children.Add(_gizmoProxy);
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(Visual3D.TransformProperty, typeof(Visual3D))
            .AddValueChanged(_gizmoProxy, GizmoTransformChanged);

        void AddPart(HelixToolkit.Wpf.Manipulator man)
        {
            man.Position = center;
            man.Bind(_gizmoProxy);
            _gizmoParts.Add(man);
            _overlayViewport!.Children.Add(man); // 放疊圖層 → 永不被實體遮擋
        }
        // 平移箭頭（X 紅 / Y 綠 / Z 藍 — 業界慣例）
        AddPart(new TranslateManipulator { Direction = new Vector3D(1, 0, 0), Color = Colors.Red,   Length = diag * 0.22, Diameter = diag * 0.016 });
        AddPart(new TranslateManipulator { Direction = new Vector3D(0, 1, 0), Color = Colors.Green, Length = diag * 0.22, Diameter = diag * 0.016 });
        AddPart(new TranslateManipulator { Direction = new Vector3D(0, 0, 1), Color = Colors.Blue,  Length = diag * 0.22, Diameter = diag * 0.016 });
        // 旋轉環
        AddPart(new RotateManipulator { Axis = new Vector3D(1, 0, 0), Color = Colors.Red,   Diameter = diag * 0.34, InnerDiameter = diag * 0.30, Length = diag * 0.012 });
        AddPart(new RotateManipulator { Axis = new Vector3D(0, 1, 0), Color = Colors.Green, Diameter = diag * 0.34, InnerDiameter = diag * 0.30, Length = diag * 0.012 });
        AddPart(new RotateManipulator { Axis = new Vector3D(0, 0, 1), Color = Colors.Blue,  Diameter = diag * 0.34, InnerDiameter = diag * 0.30, Length = diag * 0.012 });

        StatusText = $"操作器：拖箭頭沿軸移動「{root.Name}」、拖環繞軸旋轉；放開即定位";
    }

    private void RemoveGizmo()
    {
        if (_gizmoProxy is not null)
        {
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(Visual3D.TransformProperty, typeof(Visual3D))
                .RemoveValueChanged(_gizmoProxy, GizmoTransformChanged);
            _overlayViewport?.Children.Remove(_gizmoProxy);
        }
        foreach (HelixToolkit.Wpf.Manipulator man in _gizmoParts)
        {
            man.UnBind();
            _overlayViewport?.Children.Remove(man);
        }
        _gizmoParts.Clear();
        _gizmoProxy = null;

        if (_gizmoTarget is not null) // 清掉殘留的暫時 Transform（用 Identity，不可 null → 否則 FindHits crash）
            foreach (ModelNodeViewModel l in _gizmoTarget.Leaves())
                if (l.BodyVisual is not null)
                    l.BodyVisual.Transform = Transform3D.Identity;
        _gizmoTarget = null;
        if (_gizmoDragActive)
        {
            _gizmoDragActive = false;
            _edgesSuspended = false;
            SetEdgesActive(true);
        }
    }

    /// <summary>操作器拖動中：把代理的 Transform 套到目標檔案的所有 BodyVisual（暫時、GPU 端）</summary>
    private void GizmoTransformChanged(object? sender, EventArgs e)
    {
        if (_gizmoBaking || _gizmoTarget is null || _gizmoProxy is null) return;
        Transform3D t = _gizmoProxy.Transform;
        if (!_gizmoDragActive && t is not null && !t.Value.IsIdentity)
        {
            _gizmoDragActive = true;
            _edgesSuspended = true;
            SetEdgesActive(false); // 邊線逐幀重建會卡，拖動中暫停
        }
        foreach (ModelNodeViewModel l in _gizmoTarget.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = t;
    }

    /// <summary>滑鼠放開（MainWindow 轉發，handledEventsToo）：把累積變換烘進 B-rep 並重置操作器</summary>
    public void OnGizmoMouseUp()
    {
        if (_gizmoTarget is null || _gizmoProxy is null || _gizmoBakePending) return;
        Matrix3D m = _gizmoProxy.Transform?.Value ?? Matrix3D.Identity;
        if (m.IsIdentity) return; // 只是點一下、沒拖操作器 → 不烘焙

        // 延後到 manipulator 自身的 mouse-up 處理（釋放捕捉等）完成後再烘焙，避免在其事件中改動視覺樹造成 reentrancy
        _gizmoBakePending = true;
        _viewport?.Dispatcher.BeginInvoke(new Action(() => BakeGizmo(m)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BakeGizmo(Matrix3D m)
    {
        _gizmoBakePending = false;
        if (_gizmoTarget is null) return;
        ModelNodeViewModel root = _gizmoTarget;

        _gizmoBaking = true; // 防 proxy 歸零觸發 GizmoTransformChanged 重入
        foreach (ModelNodeViewModel l in root.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = Transform3D.Identity; // 清暫時位移（用 Identity，不可 null）
        if (_gizmoProxy is not null) _gizmoProxy.Transform = Transform3D.Identity; // 綁定 → 操作器歸零
        _gizmoBaking = false;
        _gizmoDragActive = false;
        _edgesSuspended = false;
        SetEdgesActive(true);

        TransformRoot(root, RigidAlign.ToModOp(m), m); // 烘進 B-rep（量測精度不受影響）

        // 操作器移到變換後的新中心
        Rect3D b = root.Bounds;
        if (!b.IsEmpty)
        {
            var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            foreach (HelixToolkit.Wpf.Manipulator man in _gizmoParts)
                man.Position = center;
        }
        StatusText = $"已套用操作器變換「{root.Name}」（可用 🧩干涉 驗證）";
    }

    // ─── 干涉檢查 ────────────────────────────────────────────────

    [RelayCommand]
    private async Task CheckInterferenceAsync()
    {
        if (_viewport is null) return;
        var visibleRoots = Roots.Where(r => r.IsVisible).ToList();
        if (visibleRoots.Count != 2)
        {
            StatusText = $"干涉檢查需要剛好 2 個可見檔案（目前 {visibleRoots.Count} 個）— 用樹面板勾選";
            return;
        }

        List<MeshGeometry3D> MeshesOf(ModelNodeViewModel root) =>
            root.Leaves().Where(l => l.IsVisible && l.FacesContent is not null)
                .SelectMany(l => l.FacesContent!.Children)
                .Where(m => m is GeometryModel3D gm && _faceMap.ContainsKey(gm))
                .Select(m => _faceMap[(GeometryModel3D)m].Mesh)
                .ToList();

        var a = MeshesOf(visibleRoots[0]);
        var b = MeshesOf(visibleRoots[1]);
        string nameA = visibleRoots[0].Name, nameB = visibleRoots[1].Name;

        IsBusy = true;
        StatusText = $"干涉檢查中：{nameA} ⟷ {nameB} …";
        try
        {
            InterferenceResult result = await Task.Run(() => InterferenceService.Check(a, b));
            AddInterferenceResult(result, nameA, nameB);
        }
        catch (Exception ex)
        {
            StatusText = $"干涉檢查失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddInterferenceResult(InterferenceResult result, string nameA, string nameB)
    {
        double diag = SceneDiagonal();
        double markerR = Math.Clamp(diag * 0.004, 0.01, 5.0);
        MeasurementResult m;

        if (result.Intersects)
        {
            int pairs = result.PairCount;
            int segCount = result.Segments.Count;
            m = new MeasurementResult
            {
                Kind = MeasureMode.Interference,
                TitleFor = _ => $"🧩 干涉！{nameA} ⟷ {nameB}",
                DetailFor = _ => $"兩零件相交（不 match）\n相交三角形對 = {pairs:N0}\n" +
                                 $"紅色線 = 干涉交線（{segCount:N0} 段）\n（共面貼合不會被算為干涉）",
            };
            var pts = new Point3DCollection(Math.Min(result.Segments.Count, 20000) * 2);
            foreach ((Point3D s0, Point3D s1) in result.Segments) { pts.Add(s0); pts.Add(s1); }
            m.Overlays.Add(new LinesVisual3D { Points = pts, Color = Colors.Red, Thickness = 3 });
            if (result.Segments.Count > 0)
            {
                Point3D at = result.Segments[0].A;
                m.Overlays.Add(new BillboardTextVisual3D
                {
                    Position = at + new Vector3D(0, 0, markerR * 3),
                    Text = "干涉",
                    Foreground = Brushes.White,
                    Background = Brushes.Red,
                    Padding = new Thickness(5, 2, 5, 2),
                    FontSize = 14,
                });
            }
        }
        else
        {
            Point3D ga = result.GapA, gb = result.GapB;
            Vector3D d = gb - ga;
            double gap = result.GapDistance;
            m = new MeasurementResult
            {
                Kind = MeasureMode.Interference,
                TitleFor = u => $"🧩 無干涉  gap ≈ {Units.L(gap, u)}",
                DetailFor = u => $"{nameA} ⟷ {nameB} 無相交\n最小間隙 ≈ {Units.L(gap, u)}（網格近似）\n" +
                                 $"gap ≈ 0 即為貼合（match）\n點 1 {Units.P(ga, u)}\n點 2 {Units.P(gb, u)}",
            };
            m.Overlays.Add(new SphereVisual3D { Center = ga, Radius = markerR, Fill = Brushes.OrangeRed });
            m.Overlays.Add(new SphereVisual3D { Center = gb, Radius = markerR, Fill = Brushes.OrangeRed });
            m.Overlays.Add(new LinesVisual3D
            {
                Points = new Point3DCollection { ga, gb },
                Color = Colors.OrangeRed,
                Thickness = 2,
            });
            var label = new BillboardTextVisual3D
            {
                Position = ga + d / 2 + new Vector3D(0, 0, markerR * 2),
                Text = $"gap {Units.L(gap, UnitSystem.Millimeter)}",
                Foreground = Brushes.Black,
                Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 210)),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 14,
            };
            m.Overlays.Add(label);
            m.DynamicLabels.Add((label, u => $"gap {Units.L(gap, u)}"));
        }
        AddMeasurement(m);
    }

    // ─── 剖面 ────────────────────────────────────────────────────

    partial void OnSectionEnabledChanged(bool value) => ScheduleSection();
    partial void OnSectionAxisIndexChanged(int value) => ScheduleSection();
    partial void OnSectionPositionChanged(double value) => ScheduleSection();
    partial void OnSectionFlipChanged(bool value) => ScheduleSection();

    private void ScheduleSection()
    {
        _sectionTimer.Stop();
        _sectionTimer.Start();
    }

    private void ApplySection()
    {
        if (_viewport is null) return;

        if (!SectionEnabled)
        {
            // 還原原始幾何
            foreach ((Model3D model, FaceInfo fi) in _faceMap)
                if (model is GeometryModel3D gm && !ReferenceEquals(gm.Geometry, fi.Mesh))
                    gm.Geometry = fi.Mesh;
            foreach (ModelNodeViewModel root in Roots) RefreshRootEdges(root);
            if (_sectionPlaneVisual is not null)
            {
                _viewport.Children.Remove(_sectionPlaneVisual);
                _sectionPlaneVisual = null;
            }
            ApplyRenderMode(); // 剖面關閉 → 瀏覽模式可切回合併網格
            StatusText = "剖面已關閉";
            return;
        }

        Rect3D bounds = UnionBounds();
        if (bounds.IsEmpty) return;

        Vector3D axis = SectionAxisIndex switch
        {
            0 => new Vector3D(1, 0, 0),
            1 => new Vector3D(0, 1, 0),
            _ => new Vector3D(0, 0, 1),
        };
        double t = SectionPosition / 100.0;
        Point3D min = bounds.Location;
        var size = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ);
        double planeCoord = SectionAxisIndex switch
        {
            0 => min.X + size.X * t,
            1 => min.Y + size.Y * t,
            _ => min.Z + size.Z * t,
        };
        Point3D center = min + size / 2;
        Point3D planePoint = SectionAxisIndex switch
        {
            0 => new Point3D(planeCoord, center.Y, center.Z),
            1 => new Point3D(center.X, planeCoord, center.Z),
            _ => new Point3D(center.X, center.Y, planeCoord),
        };
        Vector3D normal = SectionFlip ? -axis : axis;

        foreach ((Model3D model, FaceInfo fi) in _faceMap)
            if (model is GeometryModel3D gm)
                gm.Geometry = SectionService.ClipMesh(fi.Mesh, planePoint, normal);

        _sectionPlanePoint = planePoint;
        _sectionNormal = normal;
        foreach (ModelNodeViewModel root in Roots) RefreshRootEdges(root);

        ApplyRenderMode(); // 剖面開啟 → 改用逐面（裁切後幾何）
        UpdateSectionPlaneVisual(planePoint, axis, size);
        StatusText = $"剖面：{"XYZ"[SectionAxisIndex]} 軸 {SectionPosition:F0}%" + (SectionFlip ? "（反向）" : "");
    }

    private void UpdateSectionPlaneVisual(Point3D planePoint, Vector3D axis, Vector3D size)
    {
        if (_sectionPlaneVisual is not null)
            _viewport!.Children.Remove(_sectionPlaneVisual);

        (Vector3D lenDir, double len, double wid) = SectionAxisIndex switch
        {
            0 => (new Vector3D(0, 1, 0), size.Y, size.Z),
            1 => (new Vector3D(1, 0, 0), size.X, size.Z),
            _ => (new Vector3D(1, 0, 0), size.X, size.Y),
        };
        _sectionPlaneVisual = new RectangleVisual3D
        {
            Origin = planePoint,
            Normal = axis,
            LengthDirection = lenDir,
            Length = len * 1.05,
            Width = wid * 1.05,
            Fill = new SolidColorBrush(Color.FromArgb(40, 30, 120, 255)),
        };
        _viewport!.Children.Add(_sectionPlaneVisual);
    }

    // ─── 匯出 ────────────────────────────────────────────────────

    [RelayCommand]
    private void ExportCsv()
    {
        if (Measurements.Count == 0)
        {
            StatusText = "沒有量測結果可匯出";
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "匯出量測結果",
            Filter = "CSV 檔案 (*.csv)|*.csv",
            FileName = $"measurements_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("標籤,類型,明細");
        foreach (MeasurementResult m in Measurements)
        {
            static string Esc(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
            sb.AppendLine($"{Esc(m.Title)},{m.Kind},{Esc(m.Detail.Replace("\n", " | "))}");
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM：Excel 中文不亂碼
        StatusText = $"已匯出 {Measurements.Count} 筆量測 → {Path.GetFileName(dlg.FileName)}";
    }

    [RelayCommand]
    private void SaveScreenshot()
    {
        if (_viewport is null || _viewport.ActualWidth < 1) return;
        var dlg = new SaveFileDialog
        {
            Title = "儲存視圖截圖",
            Filter = "PNG 圖片 (*.png)|*.png",
            FileName = $"stpviewer_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog() != true) return;

        const double scale = 2.0; // 2x 解析度
        var rtb = new RenderTargetBitmap(
            (int)(_viewport.ActualWidth * scale), (int)(_viewport.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(_viewport);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(dlg.FileName);
        encoder.Save(fs);
        StatusText = $"已儲存截圖 → {Path.GetFileName(dlg.FileName)}";
    }

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
