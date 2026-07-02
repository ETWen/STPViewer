using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using STPViewer.Models;

namespace STPViewer.ViewModels;

// ─── 拖曳模式（左鍵按住零件沿螢幕平面拖；放開才烘進 B-rep）──────────
public partial class MainViewModel
{
    // 拖曳模式：暫時 Transform 跟著滑鼠，放開才一次性烘進 B-rep（TranslateRoot）
    private ModelNodeViewModel? _dragRoot;
    private Point3D _dragAnchor;              // 命中點（世界座標），拖曳平面的錨點
    private Vector3D _dragApplied;            // 目前累計位移
    private TranslateTransform3D? _dragTransform;

    /// <summary>左鍵按下：命中零件則開始拖曳（回傳 true 表示要捕捉滑鼠）</summary>
    public bool OnDragStart(Point position)
    {
        if (_viewport is null || IsBusy || CurrentMode != MeasureMode.Drag || _dragRoot is not null)
            return false;

        // 兩種渲染內容都可能在場：合併網格查 _mergedMap、逐面（剖面時）查 _faceMap
        ModelNodeViewModel? leaf = null;
        Point3D anchor = default;
        foreach (var h in _viewport.Viewport.FindHits(position))
        {
            if (h.Model is null) continue;
            if (_faceMap.TryGetValue(h.Model, out FaceInfo? fi)) { leaf = fi.Owner; anchor = h.Position; break; }
            if (_mergedMap.TryGetValue(h.Model, out ModelNodeViewModel? ml)) { leaf = ml; anchor = h.Position; break; }
        }
        if (leaf is null) return false;
        ModelNodeViewModel? root = FindRootOf(leaf) ?? RootContaining(leaf);
        if (root is null) return false;

        _dragRoot = root;
        _dragAnchor = anchor;
        _dragApplied = default;
        _dragTransform = new TranslateTransform3D();
        foreach (ModelNodeViewModel l in root.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = _dragTransform;

        // 拖曳中隱藏邊線（LinesVisual3D 隨 Transform 變更逐幀重建會卡）
        _edgesSuspended = true;
        SetEdgesActive(false);
        StatusText = $"拖曳「{root.Name}」中…（放開定位）";
        return true;
    }

    /// <summary>滑鼠移動：把 2D 位移投影到「過錨點、面向相機」的平面上 → 暫時 Transform</summary>
    public void OnDragMove(Point position)
    {
        if (_viewport?.Camera is not System.Windows.Media.Media3D.ProjectionCamera cam ||
            _dragRoot is null || _dragTransform is null) return;

        Point3D? p = _viewport.Viewport.UnProject(position, _dragAnchor, cam.LookDirection);
        if (p is null) return;
        _dragApplied = p.Value - _dragAnchor;
        _dragTransform.OffsetX = _dragApplied.X;
        _dragTransform.OffsetY = _dragApplied.Y;
        _dragTransform.OffsetZ = _dragApplied.Z;
    }

    /// <summary>放開：移除暫時 Transform，一次性烘進 B-rep（量測精度不受拖曳影響）</summary>
    public bool OnDragEnd()
    {
        if (_dragRoot is null) return false;
        ModelNodeViewModel root = _dragRoot;
        Vector3D delta = _dragApplied;

        // 清除暫時位移：必須用 Identity，不可用 null —— HelixToolkit GetTransform 對 child.Transform
        // 不做 null 檢查（Children.Add(null) 會拋「無法新增空值到集合中」），下次 FindHits 即 crash
        foreach (ModelNodeViewModel l in root.Leaves())
            if (l.BodyVisual is not null)
                l.BodyVisual.Transform = Transform3D.Identity;
        _dragRoot = null;
        _dragTransform = null;
        _dragApplied = default;
        _edgesSuspended = false;
        SetEdgesActive(true); // 恢復所有檔案的邊線（被拖檔案的點隨後由 TranslateRoot 更新，期間不會渲染）

        if (delta.LengthSquared > 1e-12)
        {
            TranslateRoot(root, delta); // 烘進 B-rep + 網格 + 邊線 + 邊界（量測清空）
            StatusText = $"已拖曳「{root.Name}」 Δ({delta.X:F2}, {delta.Y:F2}, {delta.Z:F2}) mm（可用 🧩干涉 驗證）";
        }
        return true;
    }
}
