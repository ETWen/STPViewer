using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using HelixToolkit.Wpf;

namespace STPViewer.ViewModels;

// ─── Gizmo 三軸操作器（XYZ 箭頭 + 旋轉環；每次放開滑鼠烘進 B-rep）──────
public partial class MainViewModel
{
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
    private ProjectionCamera? _overlayCamera; // 型別跟隨主相機（透視/正交切換，v0.5.0）

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

    /// <summary>
    /// 疊圖層相機跟隨主相機（每次主相機變更時呼叫，讓操作器疊在正確螢幕位置）。
    /// 主相機可能在透視/正交間切換（Orthographic 屬性）→ 疊圖層相機「型別」也要跟著換，投影才一致。
    /// </summary>
    private void SyncOverlayCamera()
    {
        if (_overlayViewport is null || _viewport?.Camera is not ProjectionCamera src) return;

        if (src is PerspectiveCamera p)
        {
            if (_overlayCamera is not PerspectiveCamera op)
            {
                op = new PerspectiveCamera();
                _overlayCamera = op;
                _overlayViewport.Camera = op;
            }
            op.FieldOfView = p.FieldOfView;
        }
        else if (src is OrthographicCamera o)
        {
            if (_overlayCamera is not OrthographicCamera oo)
            {
                oo = new OrthographicCamera();
                _overlayCamera = oo;
                _overlayViewport.Camera = oo;
            }
            oo.Width = o.Width;
        }
        if (_overlayCamera is null) return;

        _overlayCamera.Position = src.Position;
        _overlayCamera.LookDirection = src.LookDirection;
        _overlayCamera.UpDirection = src.UpDirection;
        _overlayCamera.NearPlaneDistance = src.NearPlaneDistance;
        _overlayCamera.FarPlaneDistance = src.FarPlaneDistance;
    }

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

        TransformRoot(root, Services.RigidAlign.ToModOp(m), m); // 烘進 B-rep（量測精度不受影響）

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
}
