namespace STPViewer.Models;

/// <summary>量測模式（None = 純瀏覽）</summary>
public enum MeasureMode
{
    None,
    Point,
    Distance,
    Edge,
    Face,
    Circle,
    Angle,        // 兩面 / 兩邊夾角
    FaceDistance, // 面到面最短距離（網格近似）
    Align,        // 兩點對齊：平移零件使點 1 貼到點 2
    Align3,       // 三點對齊：旋轉+平移（來源 3 點 → 目標 3 點）
    Interference, // 干涉檢查結果（非點擊模式，僅作為結果類型）
}
