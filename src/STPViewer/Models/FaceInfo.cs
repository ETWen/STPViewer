using System.Windows.Media.Media3D;
using CADability.GeoObject;
using STPViewer.ViewModels;

namespace STPViewer.Models;

/// <summary>Maps a rendered GeometryModel3D back to a B-rep face.</summary>
public class FaceInfo
{
    /// <summary>B-rep face, or null for mesh-only sources such as STL.</summary>
    public Face? BrepFace { get; init; }

    /// <summary>Uncut mesh used for area and section restoration; replaced after translation.</summary>
    public required MeshGeometry3D Mesh { get; set; }

    public required ModelNodeViewModel Owner { get; init; }
}
