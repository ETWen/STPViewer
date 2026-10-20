using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Wpf;
using STPViewer.Models;
using STPViewer.Services;
using static STPViewer.Services.LocalizationService;

namespace STPViewer.ViewModels;

// Section clipping
public partial class MainViewModel
{
    private readonly DispatcherTimer _sectionTimer;
    private RectangleVisual3D? _sectionPlaneVisual;
    private Point3D _sectionPlanePoint;
    private Vector3D _sectionNormal;

    // Reapply clipping if parameters change while background work is running.
    private bool _sectionApplying;
    private bool _sectionReapply;

    [ObservableProperty]
    private bool sectionEnabled;

    /// <summary>0=X 1=Y 2=Z</summary>
    [ObservableProperty]
    private int sectionAxisIndex;

    /// <summary>Section position along the selected axis, from 0 to 100%.</summary>
    [ObservableProperty]
    private double sectionPosition = 50;

    [ObservableProperty]
    private bool sectionFlip;

    /// <summary>Closes the section parameter bar.</summary>
    [RelayCommand]
    private void CloseSection() => SectionEnabled = false;

    // Three-point custom cutting plane
    private const int CustomAxisIndex = 3;
    private Vector3D? _customNormal;
    private readonly List<Point3D> _sectionPicks = new();
    private readonly List<Visual3D> _sectionPickOverlays = new();

    partial void OnSectionEnabledChanged(bool value)
    {
        if (!value) CancelSectionPickIfActive(quiet: true);
        ScheduleSection();
    }

    partial void OnSectionAxisIndexChanged(int value)
    {
        if (value == CustomAxisIndex)
        {
            if (_customNormal is null)
                SetStatus(() => Get("Runtime.SectionPickPrompt"));
        }
        else
        {
            // Leaving custom mode discards the old plane.
            _customNormal = null;
            ClearSectionPicks();
        }
        ScheduleSection();
    }

    partial void OnSectionPositionChanged(double value)
    {
        if (value < 0) { SectionPosition = 0; return; }
        if (value > 100) { SectionPosition = 100; return; }
        ScheduleSection();
    }

    partial void OnSectionFlipChanged(bool value) => ScheduleSection();

    /// <summary>Handles a custom plane pick; returns true when the click is consumed.</summary>
    public bool HandleSectionPlanePick(System.Windows.Point position)
    {
        if (!SectionEnabled || SectionAxisIndex != CustomAxisIndex || _customNormal is not null) return false;
        if (_viewport is null) return true;

        Point3D? hit = null;
        foreach (var h in _viewport.Viewport.FindHits(position))
        {
            if (h.Model is null) continue;
            if (_faceMap.ContainsKey(h.Model) || _mergedFaceRanges.ContainsKey(h.Model))
            {
                hit = h.Position;
                break;
            }
        }
        if (hit is null)
        {
            int selected = _sectionPicks.Count;
            SetStatus(() => Format("Runtime.SectionNoHit", selected));
            return true;
        }
        if (_sectionPicks.Count == 2 &&
            Vector3D.CrossProduct(_sectionPicks[1] - _sectionPicks[0], hit.Value - _sectionPicks[0]).LengthSquared < 1e-12)
        {
            SetStatus(() => Get("Runtime.SectionCollinear"));
            return true;
        }

        _sectionPicks.Add(hit.Value);
        double markerR = Math.Clamp(SceneDiagonal() * 0.004, 0.01, 5.0);
        var marker = new SphereVisual3D { Center = hit.Value, Radius = markerR, Fill = System.Windows.Media.Brushes.DodgerBlue };
        _sectionPickOverlays.Add(marker);
        _viewport.Children.Add(marker);

        if (_sectionPicks.Count < 3)
        {
            int selected = _sectionPicks.Count;
            SetStatus(() => Format("Runtime.SectionPicked", selected));
            return true;
        }

        Vector3D n = Vector3D.CrossProduct(_sectionPicks[1] - _sectionPicks[0], _sectionPicks[2] - _sectionPicks[0]);
        ClearSectionPicks();
        if (n.LengthSquared < 1e-12)
        {
            SetStatus(() => Get("Runtime.SectionDegenerate"));
            return true;
        }
        n.Normalize();
        _customNormal = n;
        SetStatus(() => Format("Runtime.SectionDefined", n.X, n.Y, n.Z));
        ApplySection();
        return true;
    }

    /// <summary>Cancels custom plane picking; returns true if it was active.</summary>
    private bool CancelSectionPickIfActive(bool quiet = false)
    {
        bool picking = SectionEnabled && SectionAxisIndex == CustomAxisIndex && _customNormal is null;
        if (_sectionPicks.Count == 0 && !picking) return false;
        ClearSectionPicks();
        if (SectionAxisIndex == CustomAxisIndex && _customNormal is null)
            SectionAxisIndex = 0;
        if (!quiet) SetStatus(() => Get("Runtime.SectionCancelled"));
        return true;
    }

    private void ClearSectionPicks()
    {
        foreach (Visual3D v in _sectionPickOverlays) _viewport?.Children.Remove(v);
        _sectionPickOverlays.Clear();
        _sectionPicks.Clear();
    }

    private void ScheduleSection()
    {
        _sectionTimer.Stop();
        _sectionTimer.Start();
    }

    private async void ApplySection()
    {
        if (_viewport is null) return;

        if (!SectionEnabled)
        {
            // Restore original meshes. Background clipping checks SectionEnabled before applying.
            foreach ((Model3D model, FaceInfo fi) in _faceMap)
                if (model is GeometryModel3D gm && !ReferenceEquals(gm.Geometry, fi.Mesh))
                    gm.Geometry = fi.Mesh;
            foreach (ModelNodeViewModel root in Roots) RefreshRootEdges(root);
            if (_sectionPlaneVisual is not null)
            {
                _viewport.Children.Remove(_sectionPlaneVisual);
                _sectionPlaneVisual = null;
            }
            ApplyRenderMode();
            SetStatus(() => Get("Runtime.SectionClosed"));
            return;
        }

        if (_sectionApplying) { _sectionReapply = true; return; }
        _sectionApplying = true;
        try
        {
            do
            {
                _sectionReapply = false;

                Rect3D bounds = UnionBounds();
                if (bounds.IsEmpty) return;

                Vector3D axis;
                if (SectionAxisIndex == CustomAxisIndex)
                {
                    if (_customNormal is null) return;
                    axis = _customNormal.Value;
                }
                else
                {
                    axis = SectionAxisIndex switch
                    {
                        0 => new Vector3D(1, 0, 0),
                        1 => new Vector3D(0, 1, 0),
                        _ => new Vector3D(0, 0, 1),
                    };
                }
                double t = SectionPosition / 100.0;
                Point3D min = bounds.Location;
                var size = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ);
                Point3D center = min + size / 2;
                // Interpolate along the projected bounds of the scene AABB.
                double dmin = double.MaxValue, dmax = double.MinValue;
                for (int ci = 0; ci < 8; ci++)
                {
                    var corner = new Point3D(
                        (ci & 1) == 0 ? min.X : min.X + size.X,
                        (ci & 2) == 0 ? min.Y : min.Y + size.Y,
                        (ci & 4) == 0 ? min.Z : min.Z + size.Z);
                    double d = Vector3D.DotProduct((Vector3D)corner, axis);
                    if (d < dmin) dmin = d;
                    if (d > dmax) dmax = d;
                }
                double dStar = dmin + (dmax - dmin) * t;
                Point3D planePoint = center + axis * (dStar - Vector3D.DotProduct((Vector3D)center, axis));
                Vector3D normal = SectionFlip ? -axis : axis;

                // Clip frozen meshes in the background, then apply on the UI thread.
                var models = new List<GeometryModel3D>(_faceMap.Count);
                var sources = new List<MeshGeometry3D>(_faceMap.Count);
                foreach ((Model3D model, FaceInfo fi) in _faceMap)
                    if (model is GeometryModel3D gm)
                    {
                        models.Add(gm);
                        sources.Add(fi.Mesh);
                    }

                MeshGeometry3D[] clipped = await Task.Run(() =>
                {
                    var result = new MeshGeometry3D[sources.Count];
                    Parallel.For(0, sources.Count, i =>
                        result[i] = SectionService.ClipMesh(sources[i], planePoint, normal));
                    return result;
                });

                if (!SectionEnabled) return;

                for (int i = 0; i < models.Count; i++)
                    models[i].Geometry = clipped[i];

                _sectionPlanePoint = planePoint;
                _sectionNormal = normal;
                foreach (ModelNodeViewModel root in Roots) RefreshRootEdges(root);

                ApplyRenderMode();
                UpdateSectionPlaneVisual(planePoint, axis, size);
                int axisIndex = SectionAxisIndex;
                double position = SectionPosition;
                bool flip = SectionFlip;
                SetStatus(() => Format("Runtime.SectionStatus",
                    axisIndex == CustomAxisIndex ? Get("Runtime.CustomPlane") : Format("Runtime.Axis", "XYZ"[axisIndex]),
                    position, flip ? Get("Runtime.Reversed") : ""));
            } while (_sectionReapply);
        }
        finally
        {
            _sectionApplying = false;
        }
    }

    private void UpdateSectionPlaneVisual(Point3D planePoint, Vector3D axis, Vector3D size)
    {
        if (_sectionPlaneVisual is not null)
            _viewport!.Children.Remove(_sectionPlaneVisual);

        (Vector3D lenDir, double len, double wid) = SectionAxisIndex switch
        {
            0 => (new Vector3D(0, 1, 0), size.Y, size.Z),
            1 => (new Vector3D(1, 0, 0), size.X, size.Z),
            2 => (new Vector3D(1, 0, 0), size.X, size.Y),
            _ => (AnyPerpendicular(axis), size.Length * 0.8, size.Length * 0.8),
        };
        lenDir.Normalize();
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

    /// <summary>Returns a unit vector perpendicular to n for the custom plane visual.</summary>
    private static Vector3D AnyPerpendicular(Vector3D n)
    {
        Vector3D v = Math.Abs(n.Z) < 0.9
            ? Vector3D.CrossProduct(n, new Vector3D(0, 0, 1))
            : Vector3D.CrossProduct(n, new Vector3D(1, 0, 0));
        v.Normalize();
        return v;
    }
}
