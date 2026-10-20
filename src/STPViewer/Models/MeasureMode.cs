namespace STPViewer.Models;

/// <summary>Measurement modes; None is the browsing mode.</summary>
public enum MeasureMode
{
    None,
    Point,
    Distance,
    Edge,
    Face,
    Circle,
    Angle,        // Angle between two faces or edges.
    FaceDistance, // Shortest face-to-face distance (mesh approximation).
    Align,        // Translate a part so the first point meets the second.
    Align3,       // Rotate and translate from three source and target points.
    Drag,         // Move a part in the screen plane; commit on release.
    Interference, // Result type rather than a picking mode.
    Volume,       // Volume/centroid result triggered from the tree menu.
}
