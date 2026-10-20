using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Wpf;
using STPViewer.Models;
using STPViewer.Services;
using static STPViewer.Services.LocalizationService;

namespace STPViewer.ViewModels;

// ─── 干涉檢查 ────────────────────────────────────────────────────────
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task CheckInterferenceAsync()
    {
        if (_viewport is null) return;
        var visibleRoots = Roots.Where(r => r.IsVisible).ToList();
        if (visibleRoots.Count < 2)
        {
            int visibleCount = visibleRoots.Count;
            SetStatus(() => Format("Runtime.InterferenceNeedTwo", visibleCount));
            return;
        }

        List<MeshGeometry3D> MeshesOf(ModelNodeViewModel root) =>
            root.Leaves().Where(l => l.IsVisible && l.FacesContent is not null)
                .SelectMany(l => l.FacesContent!.Children)
                .Where(m => m is GeometryModel3D gm && _faceMap.ContainsKey(gm))
                .Select(m => _faceMap[(GeometryModel3D)m].Mesh)
                .ToList();

        // 網格快照（開跑後零件被改走也不會半途換料；v0.4.0 起運算期間指令已鎖，這裡是雙保險）
        var meshes = visibleRoots.ToDictionary(r => r, MeshesOf);

        IsBusy = true;
        try
        {
            int pairs = 0, intersecting = 0;
            for (int i = 0; i < visibleRoots.Count; i++)
                for (int j = i + 1; j < visibleRoots.Count; j++)
                {
                    pairs++;
                    string nameA = visibleRoots[i].Name, nameB = visibleRoots[j].Name;
                    int currentPair = pairs;
                    SetStatus(() => Format("Runtime.InterferenceChecking", currentPair, nameA, nameB));
                    var a = meshes[visibleRoots[i]];
                    var b = meshes[visibleRoots[j]];
                    InterferenceResult result = await Task.Run(() => InterferenceService.Check(a, b));
                    AddInterferenceResult(result, nameA, nameB);
                    if (result.Intersects) intersecting++;
                }
            if (pairs > 1)
            {
                int checkedPairs = pairs, hits = intersecting;
                SetStatus(() => Format("Runtime.InterferenceCompleted", checkedPairs, hits));
            }
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            SetStatus(() => Format("Runtime.InterferenceFailed", error));
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
                TitleFor = _ => Format("Runtime.InterferenceHitTitle", nameA, nameB),
                DetailFor = _ => Format("Runtime.InterferenceHitDetail", pairs, segCount),
            };
            var pts = new Point3DCollection(Math.Min(result.Segments.Count, 20000) * 2);
            foreach ((Point3D s0, Point3D s1) in result.Segments) { pts.Add(s0); pts.Add(s1); }
            m.Overlays.Add(new LinesVisual3D { Points = pts, Color = Colors.Red, Thickness = 3 });
            if (result.Segments.Count > 0)
            {
                Point3D at = result.Segments[0].A;
                var label = new BillboardTextVisual3D
                {
                    Position = at + new Vector3D(0, 0, markerR * 3),
                    Text = Get("Runtime.InterferenceLabel"),
                    Foreground = Brushes.White,
                    Background = Brushes.Red,
                    Padding = new Thickness(5, 2, 5, 2),
                    FontSize = 14,
                };
                m.Overlays.Add(label);
                m.DynamicLabels.Add((label, _ => Get("Runtime.InterferenceLabel")));
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
                TitleFor = u => Format("Runtime.InterferenceClearTitle", Units.L(gap, u)),
                DetailFor = u => Format("Runtime.InterferenceClearDetail", nameA, nameB, Units.L(gap, u), Units.P(ga, u), Units.P(gb, u)),
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
                Text = Format("Runtime.GapLabel", Units.L(gap, UnitSystem.Millimeter)),
                Foreground = Brushes.Black,
                Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 210)),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 14,
            };
            m.Overlays.Add(label);
            m.DynamicLabels.Add((label, u => Format("Runtime.GapLabel", Units.L(gap, u))));
        }
        AddMeasurement(m);
    }
}
