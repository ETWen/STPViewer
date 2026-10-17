using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using STPViewer.Services;

namespace STPViewer;

/// <summary>
/// Log unhandled UI and background exceptions to
/// %LOCALAPPDATA%\STPViewer\error.log and show a message for UI errors.
/// </summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "STPViewer", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Instance.SetLanguage(SettingsService.Load().Language);
        base.OnStartup(e);

        // Report UI exceptions and keep the window open.
        DispatcherUnhandledException += (_, args) =>
        {
            LogException("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                LocalizationService.Format("App.UnexpectedError", args.Exception.Message, LogPath),
                LocalizationService.Get("App.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        // Log unobserved task exceptions.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Log fatal exceptions on non-UI threads.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogException("AppDomainUnhandledException", args.ExceptionObject as Exception);
    }

    private static void LogException(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
            sb.AppendLine(ex?.ToString() ?? "(null exception)");
            sb.AppendLine(new string('-', 80));
            File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
        }
        catch { /* Logging must not throw from an exception handler. */ }
    }
}
