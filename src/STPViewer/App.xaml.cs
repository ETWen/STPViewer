using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace STPViewer;

/// <summary>
/// 全域例外處理：UI 執行緒未攔截例外 → 記 log + 訊息框後盡量存活（不閃退）；
/// 背景 Task / 非 UI 執行緒例外 → 記 log。log 位於 %LOCALAPPDATA%\STPViewer\error.log。
/// </summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "STPViewer", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI 執行緒（含 async void 回拋）的未攔截例外：記 log、提示、標記 Handled 讓程式存活。
        // 若後續狀態已壞使用者可自行重啟；至少量測結果 / 已載模型不會直接蒸發。
        DispatcherUnhandledException += (_, args) =>
        {
            LogException("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"發生未預期的錯誤，操作已中止（程式將嘗試繼續運作）。\n\n{args.Exception.Message}\n\n" +
                $"詳細記錄：{LogPath}",
                "STPViewer 錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        // 沒被 await 的背景 Task 例外（finalizer 階段才浮出）：記 log 並標記已觀察，避免行程終止
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // 其他非 UI 執行緒的致命例外：無法阻止終止，但至少留下記錄
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
        catch { /* logging 本身失敗不能再拋 */ }
    }
}
