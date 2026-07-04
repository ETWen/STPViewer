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

namespace STPViewer.ViewModels;

// ─── 剖面（CPU 網格裁切；v0.4.0 起裁切在背景執行緒平行計算，UI 不凍結）──
public partial class MainViewModel
{
    private readonly DispatcherTimer _sectionTimer;
    private RectangleVisual3D? _sectionPlaneVisual;
    private Point3D _sectionPlanePoint;   // SectionEnabled 時的剖切平面（合併邊線重建用）
    private Vector3D _sectionNormal;

    // 背景裁切 guard：進行中又有新變更（拉滑桿/換軸/零件平移）→ 完成後用最新參數重跑一輪。
    // 不要改回同步呼叫 ClipMesh —— 大檔（64k 面）整場景裁切會凍結 UI 數秒
    private bool _sectionApplying;
    private bool _sectionReapply;

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

    /// <summary>剖面參數列的「✕ 關閉」按鈕（參數列只在剖面開啟時顯示）</summary>
    [RelayCommand]
    private void CloseSection() => SectionEnabled = false;

    // ── 3點自訂剖切平面（SectionAxisIndex == 3，v0.5.0）──
    private const int CustomAxisIndex = 3;
    private Vector3D? _customNormal;                                  // null = 尚未定義（等使用者點 3 點）
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
                StatusText = "3點剖面：請在模型上點 3 個點定義剖切平面（Esc 取消）";
        }
        else
        {
            // 離開自訂平面 → 丟棄舊平面（再選「3點」即重新定義）
            _customNormal = null;
            ClearSectionPicks();
        }
        ScheduleSection();
    }

    partial void OnSectionPositionChanged(double value)
    {
        if (value < 0) { SectionPosition = 0; return; }   // 位置數值輸入框防呆
        if (value > 100) { SectionPosition = 100; return; }
        ScheduleSection();
    }

    partial void OnSectionFlipChanged(bool value) => ScheduleSection();

    /// <summary>3點剖面定義模式中的點擊（OnViewportClick 轉入）。回傳 true = 已消費此點擊。</summary>
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
            StatusText = $"3點剖面：未命中模型表面（已選 {_sectionPicks.Count}/3 點，Esc 取消）";
            return true;
        }
        if (_sectionPicks.Count == 2 &&
            Vector3D.CrossProduct(_sectionPicks[1] - _sectionPicks[0], hit.Value - _sectionPicks[0]).LengthSquared < 1e-12)
        {
            StatusText = "3點剖面：三點共線，請改選不共線的第 3 點";
            return true;
        }

        _sectionPicks.Add(hit.Value);
        double markerR = Math.Clamp(SceneDiagonal() * 0.004, 0.01, 5.0);
        var marker = new SphereVisual3D { Center = hit.Value, Radius = markerR, Fill = System.Windows.Media.Brushes.DodgerBlue };
        _sectionPickOverlays.Add(marker);
        _viewport.Children.Add(marker);

        if (_sectionPicks.Count < 3)
        {
            StatusText = $"3點剖面：已選 {_sectionPicks.Count}/3 點";
            return true;
        }

        Vector3D n = Vector3D.CrossProduct(_sectionPicks[1] - _sectionPicks[0], _sectionPicks[2] - _sectionPicks[0]);
        ClearSectionPicks();
        if (n.LengthSquared < 1e-12)
        {
            StatusText = "3點剖面：三點退化（共線），請重新點選";
            return true;
        }
        n.Normalize();
        _customNormal = n;
        StatusText = $"3點剖面：平面已定義（法向 {n.X:F2}, {n.Y:F2}, {n.Z:F2}）— 用位置滑桿掃剖面";
        ApplySection();
        return true;
    }

    /// <summary>取消 3 點平面定義（Esc / 關剖面）。回傳 true = 有取消動作。</summary>
    private bool CancelSectionPickIfActive(bool quiet = false)
    {
        bool picking = SectionEnabled && SectionAxisIndex == CustomAxisIndex && _customNormal is null;
        if (_sectionPicks.Count == 0 && !picking) return false;
        ClearSectionPicks();
        if (SectionAxisIndex == CustomAxisIndex && _customNormal is null)
            SectionAxisIndex = 0; // 回 X 軸
        if (!quiet) StatusText = "已取消 3 點剖面定義";
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
            // 還原原始幾何（換回 fi.Mesh 參照，便宜、同步即可）。
            // 若背景裁切還在跑，其 await 之後會看到 SectionEnabled=false 而放棄套用
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
                    if (_customNormal is null) return; // 平面尚未定義（等使用者點 3 點）
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
                // 平面位置：場景 AABB 8 個角投影到法向的 [dmin, dmax]，滑桿 t 內插
                //（軸向法向 = 與舊版逐軸計算等價；任意法向也成立）
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

                // UI 執行緒快照（原始 mesh 皆已 Freeze，可跨執行緒），背景平行裁切，回 UI 一次換上。
                // 裁切期間若參數又變 / 零件被平移（TransformRoot 會呼叫 ApplySection）→
                // _sectionReapply 讓迴圈用最新的 fi.Mesh 與平面重跑，最終狀態必為最新
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

                if (!SectionEnabled) return; // 裁切期間被關掉 → 還原分支已處理，丟棄結果

                for (int i = 0; i < models.Count; i++)
                    models[i].Geometry = clipped[i]; // 已移除檔案的殘留 model 賦值無害（不在視覺樹上）

                _sectionPlanePoint = planePoint;
                _sectionNormal = normal;
                foreach (ModelNodeViewModel root in Roots) RefreshRootEdges(root);

                ApplyRenderMode(); // 剖面開啟 → 改用逐面（裁切後幾何）
                UpdateSectionPlaneVisual(planePoint, axis, size);
                string axisName = SectionAxisIndex == CustomAxisIndex ? "自訂平面" : $"{"XYZ"[SectionAxisIndex]} 軸";
                StatusText = $"剖面：{axisName} {SectionPosition:F0}%" + (SectionFlip ? "（反向）" : "");
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
            _ => (AnyPerpendicular(axis), size.Length * 0.8, size.Length * 0.8), // 自訂平面
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

    /// <summary>取任一與 n 垂直的單位向量（自訂平面的指示矩形方向用）</summary>
    private static Vector3D AnyPerpendicular(Vector3D n)
    {
        Vector3D v = Math.Abs(n.Z) < 0.9
            ? Vector3D.CrossProduct(n, new Vector3D(0, 0, 1))
            : Vector3D.CrossProduct(n, new Vector3D(1, 0, 0));
        v.Normalize();
        return v;
    }
}
