using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using HelixToolkit.Wpf;
using static STPViewer.Services.LocalizationService;

namespace STPViewer.ViewModels;

// Gizmo translation and rotation
public partial class MainViewModel
{
    [ObservableProperty]
    private bool gizmoEnabled;

    private readonly List<HelixToolkit.Wpf.Manipulator> _gizmoParts = new();
    private ModelVisual3D? _gizmoProxy;
    private ModelNodeViewModel? _gizmoTarget;
    private bool _gizmoDragActive;
    private bool _gizmoBaking;
    private bool _gizmoBakePending;

    // A synchronized overlay viewport keeps the gizmo above scene geometry.
    private System.Windows.Controls.Viewport3D? _overlayViewport;
    private ProjectionCamera? _overlayCamera;

    /// <summary>Attaches the transparent gizmo overlay viewport.</summary>
    public void AttachOverlay(System.Windows.Controls.Viewport3D overlay)
    {
        _overlayViewport = overlay;
        _overlayCamera = new PerspectiveCamera();
        overlay.Camera = _overlayCamera;
        // Manipulator materials need lighting.
        overlay.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(0x80, 0x80, 0x80)) });
        overlay.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Colors.White, new Vector3D(-1, -1, -3)) });
        SyncOverlayCamera();
    }

    /// <summary>
    /// Keeps the overlay camera in sync with the main camera and projection mode.
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
        if (GizmoEnabled) UpdateGizmo();
    }

    private void UpdateGizmo()
    {
        RemoveGizmo();
        if (!GizmoEnabled || _viewport is null || _overlayViewport is null) return;

        ModelNodeViewModel? root = SelectedNode is not null ? RootContaining(SelectedNode)
            : Roots.Count == 1 ? Roots[0] : null;
        if (root is null || root.Bounds.IsEmpty)
        {
            SetStatus(() => Get("Runtime.GizmoSelectFile"));
            GizmoEnabled = false;
            return;
        }
        _gizmoTarget = root;
        SyncOverlayCamera();

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
            _overlayViewport!.Children.Add(man);
        }
        // Translation arrows: X red, Y green, Z blue.
        AddPart(new TranslateManipulator { Direction = new Vector3D(1, 0, 0), Color = Colors.Red,   Length = diag * 0.22, Diameter = diag * 0.016 });
        AddPart(new TranslateManipulator { Direction = new Vector3D(0, 1, 0), Color = Colors.Green, Length = diag * 0.22, Diameter = diag * 0.016 });
        AddPart(new TranslateManipulator { Direction = new Vector3D(0, 0, 1), Color = Colors.Blue,  Length = diag * 0.22, Diameter = diag * 0.016 });
        // Rotation rings
        AddPart(new RotateManipulator { Axis = new Vector3D(1, 0, 0), Color = Colors.Red,   Diameter = diag * 0.34, InnerDiameter = diag * 0.30, Length = diag * 0.012 });
        AddPart(new RotateManipulator { Axis = new Vector3D(0, 1, 0), Color = Colors.Green, Diameter = diag * 0.34, InnerDiameter = diag * 0.30, Length = diag * 0.012 });
        AddPart(new RotateManipulator { Axis = new Vector3D(0, 0, 1), Color = Colors.Blue,  Diameter = diag * 0.34, InnerDiameter = diag * 0.30, Length = diag * 0.012 });

        SetStatus(() => Format("Runtime.GizmoHint", root.Name));
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

        if (_gizmoTarget is not null)
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

    /// <summary>Applies the proxy transform to target visuals during dragging.</summary>
    private void GizmoTransformChanged(object? sender, EventArgs e)
    {
        if (_gizmoBaking || _gizmoTarget is null || _gizmoProxy is null) return;
        Transform3D t = _gizmoProxy.Transform;
        if (!_gizmoDragActive && t is not null && !t.Value.IsIdentity)
        {
            _gizmoDragActive = true;
            _edgesSuspended = true;
            SetEdgesActive(false);
        }
        foreach (ModelNodeViewModel l in _gizmoTarget.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = t;
    }

    /// <summary>Commits the accumulated transform and resets the gizmo on mouse release.</summary>
    public void OnGizmoMouseUp()
    {
        if (_gizmoTarget is null || _gizmoProxy is null || _gizmoBakePending) return;
        Matrix3D m = _gizmoProxy.Transform?.Value ?? Matrix3D.Identity;
        if (m.IsIdentity) return;

        // Commit after the manipulator completes its mouse-up handling.
        _gizmoBakePending = true;
        _viewport?.Dispatcher.BeginInvoke(new Action(() => BakeGizmo(m)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BakeGizmo(Matrix3D m)
    {
        _gizmoBakePending = false;
        if (_gizmoTarget is null) return;
        ModelNodeViewModel root = _gizmoTarget;

        _gizmoBaking = true;
        foreach (ModelNodeViewModel l in root.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = Transform3D.Identity;
        if (_gizmoProxy is not null) _gizmoProxy.Transform = Transform3D.Identity;
        _gizmoBaking = false;
        _gizmoDragActive = false;
        _edgesSuspended = false;
        SetEdgesActive(true);

        TransformRoot(root, Services.RigidAlign.ToModOp(m), m);

        // Move the gizmo to the transformed center.
        Rect3D b = root.Bounds;
        if (!b.IsEmpty)
        {
            var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            foreach (HelixToolkit.Wpf.Manipulator man in _gizmoParts)
                man.Position = center;
        }
        SetStatus(() => Format("Runtime.GizmoApplied", root.Name));
    }
}
