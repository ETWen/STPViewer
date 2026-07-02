using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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

    partial void OnSectionEnabledChanged(bool value) => ScheduleSection();
    partial void OnSectionAxisIndexChanged(int value) => ScheduleSection();
    partial void OnSectionPositionChanged(double value) => ScheduleSection();
    partial void OnSectionFlipChanged(bool value) => ScheduleSection();

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
                StatusText = $"剖面：{"XYZ"[SectionAxisIndex]} 軸 {SectionPosition:F0}%" + (SectionFlip ? "（反向）" : "");
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
}
