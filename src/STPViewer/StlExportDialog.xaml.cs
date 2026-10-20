using System.Windows;
using STPViewer.ViewModels;

namespace STPViewer;

/// <summary>STL export settings dialog; the view model owns the options.</summary>
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
