using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using STPViewer.Models;
using static STPViewer.Services.LocalizationService;

namespace STPViewer.ViewModels;

// Drag mode
public partial class MainViewModel
{
    // Preview with a transform, then commit to B-rep on release.
    private ModelNodeViewModel? _dragRoot;
    private Point3D _dragAnchor;
    private Vector3D _dragApplied;
    private TranslateTransform3D? _dragTransform;

    /// <summary>Starts dragging a hit part; returns true when the mouse should be captured.</summary>
    public bool OnDragStart(Point position)
    {
        if (_viewport is null || IsBusy || CurrentMode != MeasureMode.Drag || _dragRoot is not null)
            return false;

        // Hits can come from merged meshes or section faces.
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

        // Hide edges while moving to avoid rebuilding them each frame.
        _edgesSuspended = true;
        SetEdgesActive(false);
        SetStatus(() => Format("Runtime.Dragging", root.Name));
        return true;
    }

    /// <summary>Projects pointer movement onto the camera-facing plane through the anchor.</summary>
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

    /// <summary>Commits the temporary transform to B-rep on release.</summary>
    public bool OnDragEnd()
    {
        if (_dragRoot is null) return false;
        ModelNodeViewModel root = _dragRoot;
        Vector3D delta = _dragApplied;

        // HelixToolkit hit testing requires Identity rather than a null transform.
        foreach (ModelNodeViewModel l in root.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = Transform3D.Identity;
        _dragRoot = null;
        _dragTransform = null;
        _dragApplied = default;
        _edgesSuspended = false;
        SetEdgesActive(true);

        if (delta.LengthSquared > 1e-12)
        {
            TranslateRoot(root, delta);
            SetStatus(() => Format("Runtime.Dragged", root.Name, delta.X, delta.Y, delta.Z));
        }
        return true;
    }
}
