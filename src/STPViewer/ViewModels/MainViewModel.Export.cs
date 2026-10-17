using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using STPViewer.Models;
using STPViewer.Services;
using static STPViewer.Services.LocalizationService;

namespace STPViewer.ViewModels;

// CSV, STEP, STL, and screenshot export
public partial class MainViewModel
{
    [RelayCommand]
    private void ExportCsv()
    {
        if (Measurements.Count == 0)
        {
            SetStatus(() => Get("Runtime.CsvEmpty"));
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = Get("Runtime.CsvTitle"),
            Filter = Get("Runtime.CsvFilter"),
            FileName = $"measurements_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine(Get("Runtime.CsvHeader"));
        foreach (MeasurementResult m in Measurements)
        {
            static string Esc(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
            sb.AppendLine($"{Esc(m.Title)},{Esc(Get($"Runtime.Kind{m.Kind}"))},{Esc(m.Detail.Replace("\n", " | "))}");
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM for Excel.
        int count = Measurements.Count;
        string fileName = Path.GetFileName(dlg.FileName);
        SetStatus(() => Format("Runtime.CsvExported", count, fileName));
    }

    /// <summary>
    /// Exports visible B-rep Solid and Shell objects at their current positions.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ExportStepFile()
    {
        var geos = Roots.Where(r => r.IsVisible)
            .SelectMany(r => r.Leaves()).Where(l => l.IsVisible)
            .SelectMany(l => l.SourceGeos)
            .Where(g => g is CADability.GeoObject.Solid or CADability.GeoObject.Shell)
            .ToList();
        if (geos.Count == 0)
        {
            SetStatus(() => Get("Runtime.StepExportEmpty"));
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = Get("Runtime.StepExportTitle"),
            Filter = Get("Runtime.StepExportFilter"),
            FileName = $"assembly_{DateTime.Now:yyyyMMdd_HHmmss}.stp",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // B-rep objects already contain their transformed positions.
            CADability.Project project = CADability.Project.CreateSimpleProject();
            CADability.Model model = project.GetActiveModel();
            foreach (CADability.GeoObject.IGeoObject g in geos) model.Add(g);
            new CADability.ExportStep().WriteToFile(dlg.FileName, project);
            int count = geos.Count;
            string fileName = Path.GetFileName(dlg.FileName);
            SetStatus(() => Format("Runtime.StepExported", count, fileName));
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            SetStatus(() => Format("Runtime.StepExportFailed", error));
        }
    }

    /// <summary>
    /// Exports visible meshes as STL using the selected format, scale and quality.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ExportStlFile()
    {
        // Only visible leaves with meshes can be exported.
        var groups = Roots.Where(r => r.IsVisible)
            .Select(r => (Root: r, Leaves: r.Leaves()
                .Where(l => l.IsVisible && GetMergedMesh(l) is not null).ToList()))
            .Where(g => g.Leaves.Count > 0)
            .ToList();
        if (groups.Count == 0)
        {
            SetStatus(() => Get("Runtime.StlExportEmpty"));
            return;
        }

        long triangles = groups.SelectMany(g => g.Leaves)
            .Sum(l => GetMergedMesh(l)!.TriangleIndices.Count / 3L);
        bool hasBrep = groups.SelectMany(g => g.Leaves).Any(l => l.HasBrep && l.SourceGeos.Count > 0);

        // Collect export options.
        var vm = new StlExportViewModel(groups.Count, triangles, hasBrep);
        var dialog = new StlExportDialog(vm) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || vm.Scale is not double scale) return;

        var dlg = new SaveFileDialog
        {
            Title = Get(vm.PerFile ? "Runtime.StlExportPerFileTitle" : "Runtime.StlExportMergedTitle"),
            Filter = Get("Runtime.StlExportFilter"),
            FileName = vm.PerFile
                ? $"stl_{DateTime.Now:yyyyMMdd_HHmmss}.stl"
                : $"assembly_{DateTime.Now:yyyyMMdd_HHmmss}.stl",
        };
        if (dlg.ShowDialog() != true) return;

        // Snapshot WPF geometry on the UI thread before background export.
        var snapshot = groups
            .Select(g => (g.Root.Name, Leaves: g.Leaves
                .Select(l => (Mesh: GetMergedMesh(l)!, l.HasBrep, l.SourceGeos))
                .ToList()))
            .ToList();

        IsBusy = true;
        SetStatus(() => Get(vm.Quality == StlMeshQuality.Current
            ? "Runtime.StlExporting" : "Runtime.StlRetriangulating"));
        try
        {
            double factor = StlExportService.PrecisionFactor(vm.Quality);
            var written = await Task.Run(() =>
            {
                var results = new List<(string File, int Triangles)>();
                if (vm.PerFile)
                {
                    string dir = Path.GetDirectoryName(dlg.FileName)!;
                    string baseName = Path.GetFileNameWithoutExtension(dlg.FileName);
                    foreach ((string rootName, var leaves) in snapshot)
                    {
                        string file = Path.Combine(dir, $"{baseName}_{SafeFileName(rootName)}.stl");
                        int n = StlExportService.Write(file, CollectMeshes(leaves, vm.Quality, factor), vm.Ascii, scale);
                        results.Add((file, n));
                    }
                }
                else
                {
                    var meshes = CollectMeshes(snapshot.SelectMany(g => g.Leaves).ToList(), vm.Quality, factor);
                    int n = StlExportService.Write(dlg.FileName, meshes, vm.Ascii, scale);
                    results.Add((dlg.FileName, n));
                }
                return results;
            });

            long total = written.Sum(w => (long)w.Triangles);
            int fileCount = written.Count;
            string destination = fileCount == 1 ? Path.GetFileName(written[0].File) : Path.GetDirectoryName(dlg.FileName)!;
            SetStatus(() => fileCount == 1
                ? Format("Runtime.StlExportedOne", total, destination)
                : Format("Runtime.StlExportedMany", fileCount, total, destination));
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            SetStatus(() => Format("Runtime.StlExportFailed", error));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Current merged mesh for an exportable leaf.</summary>
    private static MeshGeometry3D? GetMergedMesh(ModelNodeViewModel leaf) =>
        leaf.MergedContent?.Children.Count > 0
            ? (leaf.MergedContent.Children[0] as GeometryModel3D)?.Geometry as MeshGeometry3D
            : null;

    /// <summary>
    /// Selects frozen current meshes or sequentially retessellates B-rep geometry.
    /// </summary>
    private static List<MeshGeometry3D> CollectMeshes(
        IReadOnlyList<(MeshGeometry3D Mesh, bool HasBrep, List<CADability.GeoObject.IGeoObject> SourceGeos)> leaves,
        StlMeshQuality quality, double precisionFactor)
    {
        var meshes = new List<MeshGeometry3D>();
        foreach ((MeshGeometry3D mesh, bool hasBrep, var sourceGeos) in leaves)
        {
            if (quality != StlMeshQuality.Current && hasBrep && sourceGeos.Count > 0)
            {
                List<MeshGeometry3D> tess = StlExportService.Tessellate(sourceGeos, precisionFactor);
                if (tess.Count > 0) { meshes.AddRange(tess); continue; }
                // Fall back to the current mesh if retessellation yields nothing.
            }
            meshes.Add(mesh);
        }
        return meshes;
    }

    private static string SafeFileName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem)) stem = name;
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        return stem;
    }

    [RelayCommand]
    private void SaveScreenshot()
    {
        if (_viewport is null || _viewport.ActualWidth < 1) return;
        var dlg = new SaveFileDialog
        {
            Title = Get("Runtime.ScreenshotTitle"),
            Filter = Get("Runtime.ScreenshotFilter"),
            FileName = $"stpviewer_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog() != true) return;

        const double scale = 2.0;
        var rtb = new RenderTargetBitmap(
            (int)(_viewport.ActualWidth * scale), (int)(_viewport.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(_viewport);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(dlg.FileName);
        encoder.Save(fs);
        string fileName = Path.GetFileName(dlg.FileName);
        SetStatus(() => Format("Runtime.ScreenshotSaved", fileName));
    }
}
