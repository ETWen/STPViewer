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
using static STPViewer.Services.LocalizationService;

namespace STPViewer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly StepImportService _importService = new();
    private readonly MeasurementService _measureService = new();

    /// <summary>Maps rendered face models to their B-rep face data for hit testing.</summary>
    private readonly Dictionary<Model3D, FaceInfo> _faceMap = new();

    /// <summary>Maps merged meshes to leaves for drag hit testing.</summary>
    private readonly Dictionary<Model3D, ModelNodeViewModel> _mergedMap = new();

    /// <summary>Vertex boundaries and faces used to resolve hits on merged meshes.
    /// The order matches BuildMergedMesh; transforms do not change vertex counts.</summary>
    private readonly Dictionary<Model3D, (int[] Starts, FaceInfo[] Faces)> _mergedFaceRanges = new();

    private HelixViewport3D? _viewport;
    private int _paletteIndex;
    private readonly Dictionary<MeasureMode, int> _counters = new();

    // First picks for multi-step measurements.
    private Point3D? _pendingPoint;
    private DirectionPick? _pendingDirection;
    private FaceInfo? _pendingFace;
    private (ModelNodeViewModel Root, Point3D Point)? _pendingAlign;
    private readonly List<(ModelNodeViewModel Root, Point3D P)> _align3 = new();
    private readonly List<Visual3D> _pendingOverlays = new();

    /// <summary>Selected tree node used as the target for commands.</summary>
    [ObservableProperty]
    private ModelNodeViewModel? selectedNode;

    // Suspend edge rendering during camera interaction.
    private readonly DispatcherTimer _interactionTimer;
    private bool _edgesSuspended;

    [ObservableProperty]
    private MeasureMode currentMode = MeasureMode.None;

    [ObservableProperty]
    private string statusText = Get("Runtime.Ready");

    private Func<string> _statusTextFor = () => Get("Runtime.Ready");

    private void SetStatus(Func<string> textFor)
    {
        _statusTextFor = textFor;
        StatusText = textFor();
    }

    public void RefreshLocalizedText()
    {
        foreach (MeasurementResult result in Measurements) result.RefreshText();
        foreach (ModelNodeViewModel root in Roots) root.RefreshLocalizedText();
        if (!string.IsNullOrWhiteSpace(TreeFilter))
            foreach (ModelNodeViewModel root in Roots) root.ApplyFilter(TreeFilter);
        StatusText = _statusTextFor();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportRecentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateRootCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckInterferenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportStepFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportStlFileCommand))]
    private bool isBusy;

    [ObservableProperty]
    private bool useInch;

    /// <summary>Tree name filter; does not affect viewport visibility.</summary>
    [ObservableProperty]
    private string? treeFilter;

    partial void OnTreeFilterChanged(string? value)
    {
        foreach (ModelNodeViewModel root in Roots) root.ApplyFilter(value);
    }

    /// <summary>Recently opened files saved in settings.json.</summary>
    public ObservableCollection<string> RecentFiles { get; } = new();

    public ObservableCollection<ModelNodeViewModel> Roots { get; } = new();
    public ObservableCollection<MeasurementResult> Measurements { get; } = new();

    /// <summary>Quick bar button selections.</summary>
    public QuickBarViewModel QuickBar { get; } = new();

    // User settings

    public void LoadSettings(AppSettings s)
    {
        UseInch = s.UseInch;
        RecentFiles.Clear();
        foreach (string f in s.RecentFiles.Where(File.Exists).Take(SettingsService.MaxRecentFiles))
            RecentFiles.Add(f);
        QuickBar.Load(s.QuickBarKeys);
    }

    public void SaveSettingsInto(AppSettings s)
    {
        s.UseInch = UseInch;
        s.Language = SelectedLanguage;
        s.RecentFiles = RecentFiles.ToList();
        s.QuickBarKeys = QuickBar.ToKeys();
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

        // Restore edges after camera movement stops.
        _interactionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _interactionTimer.Tick += (_, _) => { _interactionTimer.Stop(); ResumeEdges(); };
    }

    private UnitSystem CurrentUnit => UseInch ? UnitSystem.Inch : UnitSystem.Millimeter;

    /// <summary>Attaches the viewport from MainWindow.</summary>
    public void Attach(HelixViewport3D viewport)
    {
        _viewport = viewport;
        // The viewport event also catches camera instance replacements by HelixToolkit.
        viewport.CameraChanged += (_, _) => OnCameraMoved();
        if (viewport.Camera is not null)
            viewport.Camera.Changed += (_, _) => OnCameraMoved();
    }

    private void OnCameraMoved()
    {
        SyncOverlayCamera();
        if (!_edgesSuspended) { _edgesSuspended = true; SetEdgesActive(false); }
        _interactionTimer.Stop();
        _interactionTimer.Start();
    }

    private void ResumeEdges()
    {
        _edgesSuspended = false;
        SetEdgesActive(true);
    }

    /// <summary>Shows or hides merged root edges without rebuilding points.</summary>
    private void SetEdgesActive(bool active)
    {
        if (_viewport is null) return;
        foreach (ModelNodeViewModel root in Roots)
            if (root.EdgeVisual is not null)
                SyncVisual(root.EdgeVisual, active && root.EdgeVisual.Points.Count > 0);
    }

    /// <summary>Disables scene-changing commands while background work runs.</summary>
    private bool IsIdle() => !IsBusy;

    // Import

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = Get("Runtime.ImportTitle"),
            Filter = Get("Runtime.ImportFilter"),
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true)
            await ImportFilesAsync(dlg.FileNames);
    }

    /// <summary>Imports a recently opened file.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ImportRecentAsync(string path) => ImportFilesAsync(new[] { path });

    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if (_viewport is null) return;
        // Reject overlapping imports because StepImportService has shared state.
        if (IsBusy) { SetStatus(() => Get("Runtime.ImportBusy")); return; }
        int ok = 0, fail = 0;
        IsBusy = true;
        try
        {
            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                SetStatus(() => Format("Runtime.Importing", name));
                // Marshal progress from the import thread to the UI thread.
                _importService.ProgressTextFor = textFor =>
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        () => SetStatus(() => Format("Runtime.ImportProgress", name, textFor())));
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
                    string error = ex.Message;
                    SetStatus(() => Format("Runtime.ImportFailed", name, error));
                    MessageBox.Show($"{path}\n\n{error}", Get("Runtime.ImportFailedTitle"),
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
            int succeeded = ok, failed = fail;
            SetStatus(() => Format("Runtime.ImportCompleted", succeeded, failed > 0 ? Format("Runtime.ImportFailedCount", failed) : ""));
        }
    }

    private void BuildRoot(ImportedFileData data)
    {
        string baseName = Path.GetFileNameWithoutExtension(data.FilePath);
        string name = baseName;
        for (int i = 2; Roots.Any(r => r.Name == name); i++)
            name = $"{baseName} ({i})";

        ModelNodeViewModel root = BuildNode(data.Root, isRoot: true, data.FilePath, name);

        // Disable edges by default for very large files.
        int totalSegments = root.Leaves().Sum(l => (l.OriginalEdgePoints?.Count ?? 0) / 2);
        if (totalSegments > 30_000)
            root.ShowEdges = false;

        Roots.Add(root);
        if (!string.IsNullOrWhiteSpace(TreeFilter)) root.ApplyFilter(TreeFilter);
        foreach (ModelNodeViewModel leaf in root.Leaves())
        {
            leaf.VisualStateChanged += _ => SyncLeafVisuals(root);
            if (leaf.BodyVisual is not null)
                _viewport!.Children.Add(leaf.BodyVisual);
        }

        // One merged edge visual per file avoids per-leaf rebuilds.
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
            NameKey = overrideName is null ? n.NameKey : null,
            NameNumber = n.NameNumber,
            FilePath = isRoot ? filePath : null,
            SolidCount = n.SolidCount,
            FaceCount = n.FaceCount,
            TriangleCount = n.TriangleCount,
            Bounds = n.Bounds,
            HasBrep = n.HasBrep,
        };

        if (n.Faces.Count > 0)
        {
            // Share material across leaf faces for color changes.
            var diffuseBrush = new SolidColorBrush(vm.Color);
            diffuseBrush.Freeze();
            var diffuse = new DiffuseMaterial(diffuseBrush);
            var matGroup = new MaterialGroup();
            matGroup.Children.Add(diffuse);
            matGroup.Children.Add(new SpecularMaterial(Brushes.White, 60));
            var backMat = new DiffuseMaterial(Brushes.DimGray);
            backMat.Freeze();
            vm.SharedMaterial = diffuse;

            // Keep one model per face for section picking and record merged-mesh face boundaries.
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

            // BuildMergedMesh concatenates faces in the same order as vertStarts.
            var mergedModel = new GeometryModel3D(BuildMergedMesh(n.Faces), matGroup);
            // Closed solids render front faces only; open shells and STL render both sides.
            if (n.SolidCount == 0 || !n.HasBrep)
                mergedModel.BackMaterial = backMat;
            vm.MergedContent = new Model3DGroup();
            vm.MergedContent.Children.Add(mergedModel);
            _mergedMap[mergedModel] = vm;
            _mergedFaceRanges[mergedModel] = (vertStarts, faceInfos);

            // Use merged meshes except in section mode, which needs separate clipped faces.
            vm.BodyVisual = new ModelVisual3D
            {
                Content = SectionEnabled ? group : vm.MergedContent,
            };
        }

        // Leaves hold edge data; roots hold the merged edge visual.
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
    /// Rebuilds one edge visual from visible leaves, clipping segments in section mode.
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

    // Merged meshes

    /// <summary>Concatenates face meshes in the given order.</summary>
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

    /// <summary>Builds the import mesh in face order for merged hit resolution.</summary>
    private static MeshGeometry3D BuildMergedMesh(IReadOnlyList<ImportedFace> faces)
    {
        var meshes = new List<MeshGeometry3D>(faces.Count);
        foreach (ImportedFace f in faces) meshes.Add(f.Mesh);
        return MergeMeshes(meshes);
    }

    /// <summary>Rebuilds a merged mesh after a transform without changing face vertex boundaries.</summary>
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
    /// Uses merged meshes for browsing and measurement; section mode uses separate clipped faces.
    /// </summary>
    private void ApplyRenderMode()
    {
        // Measurement resolves merged-mesh hits back to their source faces.
        bool merged = !SectionEnabled;
        foreach (ModelNodeViewModel leaf in Roots.SelectMany(r => r.Leaves()))
        {
            if (leaf.BodyVisual is null) continue;
            Model3DGroup? target = merged ? leaf.MergedContent : leaf.FacesContent;
            if (target is not null && !ReferenceEquals(leaf.BodyVisual.Content, target))
                leaf.BodyVisual.Content = target;
        }
    }

    // Node commands

    [RelayCommand]
    private void ZoomNode(ModelNodeViewModel node)
    {
        if (!node.Bounds.IsEmpty)
            _viewport?.ZoomExtents(node.Bounds, 400);
    }

    [RelayCommand]
    private void ZoomAll() => _viewport?.ZoomExtents(400);

    /// <summary>Standard views with Z up.</summary>
    [RelayCommand]
    private void SetStandardView(string name)
    {
        if (_viewport?.Camera is not ProjectionCamera cam) return;
        (Vector3D dir, Vector3D up) = name switch
        {
            "Front"  => (new Vector3D(0, 1, 0),  new Vector3D(0, 0, 1)),
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
        cam.LookDirection = dir * dist;
        cam.UpDirection = up;
        _viewport.ZoomExtents(400);
        SetStatus(() => Format("Runtime.View", Get($"Runtime.View{name}")));
    }

    /// <summary>Toggles orthographic projection while preserving the view angle.</summary>
    [ObservableProperty]
    private bool orthographicView;

    partial void OnOrthographicViewChanged(bool value)
    {
        if (_viewport is null) return;
        _viewport.Orthographic = value;
        SetStatus(() => Get(value ? "Runtime.Orthographic" : "Runtime.Perspective"));
    }

    [RelayCommand]
    private void RemoveRoot(ModelNodeViewModel root)
    {
        if (_viewport is null || !root.IsRoot) return;

        if (ReferenceEquals(root, _gizmoTarget)) GizmoEnabled = false;

        // Clear measurements that may be attached to the removed file.
        if (Measurements.Count > 0) ClearMeasurements();

        foreach (ModelNodeViewModel leaf in root.Leaves())
        {
            if (leaf.BodyVisual is not null)
                _viewport.Children.Remove(leaf.BodyVisual);
            if (leaf.FacesContent is not null)
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
        string removedName = root.Name;
        SetStatus(() => Format("Runtime.Removed", removedName));
    }

    // Visibility and volume

    /// <summary>Shows only this node until ShowAll is called.</summary>
    [RelayCommand]
    private void IsolateNode(ModelNodeViewModel node)
    {
        foreach (ModelNodeViewModel r in Roots) SetVisibleRecursive(r, false);
        SetVisibleRecursive(node, true);
        SetStatus(() => Format("Runtime.Isolated", node.Name));
    }

    [RelayCommand]
    private void InvertVisibility()
    {
        foreach (ModelNodeViewModel l in Roots.SelectMany(r => r.Leaves()))
            l.IsVisible = !l.IsVisible;
        SetStatus(() => Get("Runtime.VisibilityInverted"));
    }

    [RelayCommand]
    private void ShowAll()
    {
        foreach (ModelNodeViewModel r in Roots) SetVisibleRecursive(r, true);
        SetStatus(() => Get("Runtime.AllVisible"));
    }

    /// <summary>Sets visibility for every node, including mixed child states.</summary>
    private static void SetVisibleRecursive(ModelNodeViewModel node, bool visible)
    {
        node.IsVisible = visible;
        foreach (ModelNodeViewModel c in node.Children) SetVisibleRecursive(c, visible);
    }

    /// <summary>Adds a volume and centroid measurement for a closed mesh.</summary>
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
            SetStatus(() => Format("Runtime.VolumeNoMesh", node.Name));
            return;
        }

        (double vol, Point3D centroid, bool reliable) = MeasurementService.MeshVolume(meshes);
        if (!reliable)
        {
            SetStatus(() => Format("Runtime.VolumeOpenMesh", node.Name));
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
            DetailFor = u => Format("Runtime.VolumeDetail", Units.V(vol, u), Units.P(centroid, u), Units.C(b.SizeX, u), Units.C(b.SizeY, u), Units.C(b.SizeZ, u), u == UnitSystem.Millimeter ? "mm" : "in"),
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

    // Measurement

    partial void OnCurrentModeChanged(MeasureMode value)
    {
        CancelPending();
        ApplyRenderMode();
        string? key = value switch
        {
            MeasureMode.None => "Runtime.ModeBrowse",
            MeasureMode.Point => "Runtime.ModePoint",
            MeasureMode.Distance => "Runtime.ModeDistance",
            MeasureMode.Edge => "Runtime.ModeEdge",
            MeasureMode.Face => "Runtime.ModeFace",
            MeasureMode.Circle => "Runtime.ModeCircle",
            MeasureMode.Angle => "Runtime.ModeAngle",
            MeasureMode.FaceDistance => "Runtime.ModeFaceDistance",
            MeasureMode.Align => "Runtime.ModeAlign",
            MeasureMode.Align3 => "Runtime.ModeAlign3",
            MeasureMode.Drag => "Runtime.ModeDrag",
            _ => null,
        };
        if (key is not null) SetStatus(() => Get(key));
    }

    partial void OnUseInchChanged(bool value)
    {
        UnitSystem u = CurrentUnit;
        foreach (MeasurementResult m in Measurements) m.SetUnit(u);
        SetStatus(() => Get(value ? "Runtime.UnitInch" : "Runtime.UnitMillimeter"));
    }

    /// <summary>Cancels a pending pick, then exits measurement mode or closes the gizmo.</summary>
    public void OnEscape()
    {
        if (CancelSectionPickIfActive()) return;
        bool hadPending = _pendingPoint is not null || _pendingDirection is not null ||
                          _pendingFace is not null || _pendingAlign is not null || _align3.Count > 0;
        if (hadPending)
        {
            CancelPending();
            SetStatus(() => Get("Runtime.MeasurementStartCancelled"));
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

    /// <summary>Handles measurement hotkeys; repeating a key returns to browse mode.</summary>
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

    /// <summary>Handles left-clicks forwarded by MainWindow.</summary>
    public void OnViewportClick(Point position)
    {
        if (_viewport is null || IsBusy) return;
        if (HandleSectionPlanePick(position)) return;
        if (CurrentMode is MeasureMode.None or MeasureMode.Drag) return;

        var hits = _viewport.Viewport.FindHits(position);
        FaceInfo? fi = null;
        Point3D hitPoint = default;
        Vector3D hitNormal = default;
        foreach (var h in hits)
        {
            if (h.Model is null) continue;
            // Section mode renders individual faces.
            if (_faceMap.TryGetValue(h.Model, out FaceInfo? f))
            {
                fi = f; hitPoint = h.Position; hitNormal = h.Normal;
                break;
            }
            // Other modes resolve merged-mesh vertex indices to faces.
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
            SetStatus(() => Get("Runtime.NoSurfaceHit"));
            return;
        }

        try
        {
            HandleMeasureClick(fi, hitPoint, hitNormal);
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            SetStatus(() => Format("Runtime.MeasureFailed", error));
        }
    }

    /// <summary>Finds the source face for a merged-mesh vertex by binary search.</summary>
    private static FaceInfo? ResolveMergedFace((int[] Starts, FaceInfo[] Faces) range, int vertexIndex)
    {
        int[] starts = range.Starts;
        FaceInfo[] faces = range.Faces;
        if (vertexIndex < 0 || faces.Length == 0 || vertexIndex >= starts[faces.Length]) return null;
        // Find the largest face start not exceeding the hit vertex index.
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

        // STL has no B-rep edges or faces for these modes.
        if (fi.BrepFace is null && CurrentMode is MeasureMode.Edge or MeasureMode.Circle or MeasureMode.Face)
        {
            SetStatus(() => Get("Runtime.MeshMeasurementLimited"));
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
                    SetStatus(() => Format("Runtime.DistanceFirstPoint", p.X, p.Y, p.Z));
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
                if (result is null) { SetStatus(() => Get("Runtime.NoMeasurableEdge")); UndoLabel(MeasureMode.Edge); }
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
                if (result is null) { SetStatus(() => Get("Runtime.NoCircularEdge")); UndoLabel(MeasureMode.Circle); }
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
                    SetStatus(() => Format("Runtime.AngleFirstDirection", Get(pick.Desc)));
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
                    SetStatus(() => Get("Runtime.FaceDistanceFirstFace"));
                }
                else if (ReferenceEquals(_pendingFace, fi))
                {
                    SetStatus(() => Get("Runtime.SameFace"));
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
                if (root is null) { SetStatus(() => Get("Runtime.RootNotFound")); break; }
                if (_pendingAlign is null)
                {
                    _pendingAlign = (root, p);
                    AddPendingMarker(p, markerR);
                    SetStatus(() => Format("Runtime.AlignMoving", root.Name));
                }
                else if (ReferenceEquals(_pendingAlign.Value.Root, root))
                {
                    SetStatus(() => Get("Runtime.AlignOtherFile"));
                }
                else
                {
                    (ModelNodeViewModel moveRoot, Point3D from) = _pendingAlign.Value;
                    Vector3D offset = p - from;
                    CancelPending();
                    TranslateRoot(moveRoot, offset);
                    SetStatus(() => Format("Runtime.AlignMoved", moveRoot.Name, offset.X, offset.Y, offset.Z));
                }
                break;
            }
            case MeasureMode.Align3:
            {
                Point3D p = _measureService.Snap(fi, hit, snapTol);
                ModelNodeViewModel? root = FindRootOf(fi.Owner);
                if (root is null) { SetStatus(() => Get("Runtime.RootNotFound")); break; }

                if (_align3.Count < 3)
                {
                    if (_align3.Count > 0 && !ReferenceEquals(_align3[0].Root, root))
                    {
                        ModelNodeViewModel sourceRoot = _align3[0].Root;
                        SetStatus(() => Format("Runtime.Align3SameSource", sourceRoot.Name));
                        break;
                    }
                    if (_align3.Count == 2 && RigidAlign.Collinear(_align3[0].P, _align3[1].P, p))
                    {
                        SetStatus(() => Get("Runtime.Align3SourceCollinear"));
                        break;
                    }
                    _align3.Add((root, p));
                    AddPendingMarker(p, markerR);
                    int sourceCount = _align3.Count;
                    SetStatus(() => sourceCount < 3
                        ? Format("Runtime.Align3SourcePoint", sourceCount, root.Name)
                        : Get("Runtime.Align3SourceComplete"));
                }
                else
                {
                    if (ReferenceEquals(_align3[0].Root, root))
                    {
                        SetStatus(() => Get("Runtime.Align3OtherFile"));
                        break;
                    }
                    if (_align3.Count == 5 && RigidAlign.Collinear(_align3[3].P, _align3[4].P, p))
                    {
                        SetStatus(() => Get("Runtime.Align3TargetCollinear"));
                        break;
                    }
                    _align3.Add((root, p));
                    AddPendingMarker(p, markerR);
                    if (_align3.Count < 6)
                    {
                        int targetCount = _align3.Count - 3;
                        SetStatus(() => Format("Runtime.Align3TargetPoint", targetCount));
                        break;
                    }

                    ModelNodeViewModel moveRoot = _align3[0].Root;
                    Point3D p1 = _align3[0].P, p2 = _align3[1].P, p3 = _align3[2].P;
                    Point3D q1 = _align3[3].P, q2 = _align3[4].P, q3 = _align3[5].P;
                    CancelPending();
                    if (!RigidAlign.TryRigidTransform(p1, p2, p3, q1, q2, q3, out Matrix3D m))
                    {
                        SetStatus(() => Get("Runtime.Align3Failed"));
                        break;
                    }
                    TransformRoot(moveRoot, RigidAlign.ToModOp(m), m);
                    SetStatus(() => Format("Runtime.Align3Completed", moveRoot.Name));
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
        Func<UnitSystem, string> titleFor = result.TitleFor;
        SetStatus(() => $"✓ {titleFor(CurrentUnit)}");
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
        SetStatus(() => Get("Runtime.MeasurementsCleared"));
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

    // Rigid part transforms

    private void TranslateRoot(ModelNodeViewModel root, Vector3D v)
    {
        if (v.LengthSquared == 0) return;
        var m = Matrix3D.Identity;
        m.Translate(v);
        TransformRoot(root, CADability.ModOp.Translate(v.X, v.Y, v.Z), m);
    }

    /// <summary>
    /// Transforms B-rep objects, meshes, edges and bounds for one file.
    /// op and m must represent the same transform in CADability and WPF conventions.
    /// </summary>
    private void TransformRoot(ModelNodeViewModel root, CADability.ModOp op, Matrix3D m)
    {
        if (_viewport is null) return;
        ClearMeasurements();

        var leaves = root.Leaves().ToList();

        // Transform whole B-rep objects sequentially to avoid shared-edge changes.
        foreach (ModelNodeViewModel leaf in leaves)
            foreach (CADability.GeoObject.IGeoObject g in leaf.SourceGeos)
            {
                try { g.Modify(op); }
                catch { }
            }

        // Transform independent frozen face meshes in parallel.
        var faceItems = new List<(GeometryModel3D Gm, FaceInfo Fi)>();
        foreach (ModelNodeViewModel leaf in leaves)
            if (leaf.FacesContent is not null)
                foreach (Model3D mm in leaf.FacesContent.Children)
                    if (mm is GeometryModel3D gm && _faceMap.TryGetValue(gm, out FaceInfo? fi))
                        faceItems.Add((gm, fi));

        var moved = new MeshGeometry3D[faceItems.Count];
        Parallel.For(0, faceItems.Count, i => moved[i] = TransformMesh(faceItems[i].Fi.Mesh, m));
        for (int i = 0; i < faceItems.Count; i++)
        {
            faceItems[i].Fi.Mesh = moved[i];
            faceItems[i].Gm.Geometry = moved[i];
        }

        foreach (ModelNodeViewModel leaf in leaves)
        {
            if (leaf.FacesContent is not null)
                RebuildMerged(leaf);

            // Update edge data before rebuilding the merged visual.
            if (leaf.OriginalEdgePoints is not null)
                leaf.OriginalEdgePoints = TransformPoints(leaf.OriginalEdgePoints, m);
        }

        RecomputeBounds(root);
        if (SectionEnabled) ApplySection();
        else RefreshRootEdges(root);
    }

    private static MeshGeometry3D TransformMesh(MeshGeometry3D src, Matrix3D m)
    {
        var positions = new Point3DCollection(src.Positions.Count);
        foreach (Point3D p in src.Positions) positions.Add(m.Transform(p));
        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = src.TriangleIndices,
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

    /// <summary>Recomputes bounds from transformed meshes and edges.</summary>
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

    // Rotate selected file

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void RotateRoot(string axisName)
    {
        ModelNodeViewModel? root = SelectedNode is not null ? RootContaining(SelectedNode)
            : Roots.Count == 1 ? Roots[0] : null;
        if (root is null)
        {
            SetStatus(() => Get("Runtime.RotateSelectFile"));
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
        SetStatus(() => Format("Runtime.Rotated", root.Name, axisName));
    }

    private ModelNodeViewModel? RootContaining(ModelNodeViewModel node) =>
        Roots.FirstOrDefault(r => ReferenceEquals(r, node) || ContainsNode(r, node));

    private static bool ContainsNode(ModelNodeViewModel parent, ModelNodeViewModel target) =>
        parent.Children.Any(c => ReferenceEquals(c, target) || ContainsNode(c, target));

    // Three-point alignment math is in RigidAlign.

    // Shared helpers

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
