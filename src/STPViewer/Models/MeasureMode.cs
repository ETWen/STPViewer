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
    Drag,         // 拖曳模式：左鍵按住零件沿螢幕平面拖動（放開才烘進 B-rep）
    Interference, // 干涉檢查結果（非點擊模式，僅作為結果類型）
    Volume,       // 體積/質心（樹右鍵選單觸發，非點擊模式，僅作為結果類型）
}
