using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using STPViewer.Services;
using STPViewer.ViewModels;

namespace STPViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();
        // 版號單一來源 = csproj <Version>；標題執行時讀，避免寫死
        var v = Assembly.GetExecutingAssembly().GetName().Version!;
        Title = $"STPViewer V{v.Major}.{v.Minor}.{v.Build} — STP/STEP 3D 檢視器";
        DataContext = _vm;
        _vm.Attach(viewport);
        _vm.AttachOverlay(gizmoOverlay);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        // 使用者設定：視窗位置大小（View 職責）+ 單位/MRU（VM 職責）
        _settings = SettingsService.Load();
        ApplyWindowSettings();
        _vm.LoadSettings(_settings);

        // Gizmo 操作器放在疊圖層；manipulator 會把 MouseUp 標 handled，需 handledEventsToo 才收得到
        gizmoOverlay.AddHandler(MouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => _vm.OnGizmoMouseUp()), handledEventsToo: true);
    }

    private void ApplyWindowSettings()
    {
        if (_settings.WindowWidth >= 400 && _settings.WindowHeight >= 300)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }
        // 位置要落在目前虛擬桌面內才還原（避免拔掉外接螢幕後視窗開在螢幕外）
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop) &&
            _settings.WindowLeft >= SystemParameters.VirtualScreenLeft - 50 &&
            _settings.WindowTop >= SystemParameters.VirtualScreenTop - 50 &&
            _settings.WindowLeft <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 200 &&
            _settings.WindowTop <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 200)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        Rect r = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        _settings.WindowLeft = r.Left;
        _settings.WindowTop = r.Top;
        _settings.WindowWidth = r.Width;
        _settings.WindowHeight = r.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _vm.SaveSettingsInto(_settings);
        SettingsService.Save(_settings);
    }

    /// <summary>支援命令列帶檔開啟：STPViewer.exe a.stp b.stl …</summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var files = System.Environment.GetCommandLineArgs().Skip(1)
            .Where(StepImportService.IsSupported).ToArray();
        if (files.Length > 0)
            await _vm.ImportFilesAsync(files);
    }

    /// <summary>Esc / 量測模式快速鍵（焦點在輸入框時不攔截，樹搜尋、剖面數值可正常打字）</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox) return;
        if (e.Key == Key.Escape)
        {
            _vm.OnEscape();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (_vm.OnHotKey(e.Key))
            e.Handled = true;
    }

    /// <summary>最近檔案下拉（▾）：即時從 VM 的 MRU 清單組選單</summary>
    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = PlacementMode.Bottom,
        };
        if (_vm.RecentFiles.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（沒有最近開啟的檔案）", IsEnabled = false });
        }
        else
        {
            foreach (string f in _vm.RecentFiles)
                menu.Items.Add(new MenuItem
                {
                    Header = System.IO.Path.GetFileName(f),
                    ToolTip = f,
                    Command = _vm.ImportRecentCommand,
                    CommandParameter = f,
                });
        }
        menu.IsOpen = true;
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
