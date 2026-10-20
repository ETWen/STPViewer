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
        // Read the version from the project at runtime.
        UpdateTitle();
        DataContext = _vm;
        _vm.Attach(viewport);
        _vm.AttachOverlay(gizmoOverlay);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        // Restore window settings here and units/recent files in the view model.
        _settings = SettingsService.Load();
        ApplyWindowSettings();
        _vm.LoadSettings(_settings);
        _vm.InitializeLocalization();
        LocalizationService.Instance.LanguageChanged += LanguageChanged;
        Closed += (_, _) =>
        {
            LocalizationService.Instance.LanguageChanged -= LanguageChanged;
            _vm.DisposeLocalization();
        };

        // The gizmo overlay needs handled mouse-up events from the manipulator.
        gizmoOverlay.AddHandler(MouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => _vm.OnGizmoMouseUp()), handledEventsToo: true);
    }

    private void LanguageChanged(object? sender, System.EventArgs e) => UpdateTitle();

    private void UpdateTitle()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version!;
        Title = LocalizationService.Format("App.WindowTitle", $"{v.Major}.{v.Minor}.{v.Build}");
    }

    private void ApplyWindowSettings()
    {
        if (_settings.WindowWidth >= 400 && _settings.WindowHeight >= 300)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }
        // Restore the position only when it is within the current virtual desktop.
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

    /// <summary>Open files passed on the command line: STPViewer.exe a.stp b.stl …</summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var files = System.Environment.GetCommandLineArgs().Skip(1)
            .Where(StepImportService.IsSupported).ToArray();
        if (files.Length > 0)
            await _vm.ImportFilesAsync(files);
    }

    /// <summary>Handle Esc and measurement shortcuts without intercepting text inputs.</summary>
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

    /// <summary>Build the recent-files menu from the view model's current list.</summary>
    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = PlacementMode.Bottom,
        };
        if (_vm.RecentFiles.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = LocalizationService.Get("App.NoRecentFiles"), IsEnabled = false });
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

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutDialog { Owner = this }.ShowDialog();

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as ModelNodeViewModel;

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Shift+left-click pans the view.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        if (_vm.CurrentMode == STPViewer.Models.MeasureMode.Drag)
        {
            if (_vm.OnDragStart(e.GetPosition(viewport.Viewport)))
            {
                viewport.CaptureMouse(); // Keep receiving move/up events outside the window.
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
        _vm.OnDragEnd(); // Commit the current position if capture is interrupted.

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
