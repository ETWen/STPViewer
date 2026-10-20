using System.Windows.Media.Media3D;
using CADability.GeoObject;
using STPViewer.ViewModels;

namespace STPViewer.Models;

/// <summary>渲染物件（GeometryModel3D）反查 B-rep Face 的對照資料</summary>
public class FaceInfo
{
    /// <summary>B-rep 面；STL 等純網格來源為 null（僅支援點/距離/面距量測）</summary>
    public Face? BrepFace { get; init; }

    /// <summary>原始（未剖切）三角網格 — 面積計算與剖面還原用；零件平移後會換成新 mesh</summary>
    public required MeshGeometry3D Mesh { get; set; }

    public required ModelNodeViewModel Owner { get; init; }
}
