using System.Windows;
using STPViewer.ViewModels;

namespace STPViewer;

/// <summary>STL 匯出參數對話框 — 選項邏輯在 StlExportViewModel，這裡只做確認/取消</summary>
public partial class StlExportDialog : Window
{
    public StlExportViewModel ViewModel { get; }

    public StlExportDialog(StlExportViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
