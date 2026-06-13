using System.Linq;
using System.Windows;
using System.Windows.Input;
using STPViewer.Services;
using STPViewer.ViewModels;

namespace STPViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Attach(viewport);
        _vm.AttachOverlay(gizmoOverlay);
        Loaded += MainWindow_Loaded;

        // Gizmo 操作器放在疊圖層；manipulator 會把 MouseUp 標 handled，需 handledEventsToo 才收得到
        gizmoOverlay.AddHandler(MouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => _vm.OnGizmoMouseUp()), handledEventsToo: true);
    }

    /// <summary>支援命令列帶檔開啟：STPViewer.exe a.stp b.stl …</summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var files = System.Environment.GetCommandLineArgs().Skip(1)
            .Where(StepImportService.IsSupported).ToArray();
        if (files.Length > 0)
            await _vm.ImportFilesAsync(files);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as ModelNodeViewModel;

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Shift+左鍵 = 平移（PanGesture2），不觸發量測/拖曳
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        if (_vm.CurrentMode == STPViewer.Models.MeasureMode.Drag)
        {
            if (_vm.OnDragStart(e.GetPosition(viewport.Viewport)))
            {
                viewport.CaptureMouse(); // 拖出視窗外也持續收到 Move/Up
                e.Handled = true;
            }
            return;
        }
        _vm.OnViewportClick(e.GetPosition(viewport.Viewport));
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            _vm.OnDragMove(e.GetPosition(viewport.Viewport));
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm.OnDragEnd())
            viewport.ReleaseMouseCapture();
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e) =>
        _vm.OnDragEnd(); // 捕捉意外中斷（Alt+Tab 等）→ 以目前位置定格，不留懸空 Transform

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var supported = files.Where(StepImportService.IsSupported).ToArray();
        if (supported.Length > 0)
            await _vm.ImportFilesAsync(supported);
    }

    private static bool HasSupportedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        e.Data.GetData(DataFormats.FileDrop) is string[] files &&
        files.Any(StepImportService.IsSupported);
}
