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
        Loaded += MainWindow_Loaded;
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
        // Shift+左鍵 = 平移（PanGesture2），不觸發量測
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        _vm.OnViewportClick(e.GetPosition(viewport.Viewport));
    }

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
