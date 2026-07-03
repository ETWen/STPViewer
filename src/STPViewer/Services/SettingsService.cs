using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace STPViewer.Services;

/// <summary>使用者設定（%LOCALAPPDATA%\STPViewer\settings.json）</summary>
public class AppSettings
{
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }
    public bool UseInch { get; set; }
    public List<string> RecentFiles { get; set; } = new();
}

/// <summary>設定讀寫：壞檔/缺檔一律回預設值，儲存失敗靜默（設定不值得讓程式閃退）</summary>
public static class SettingsService
{
    public const int MaxRecentFiles = 10;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "STPViewer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { /* 壞檔回預設 */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 儲存失敗靜默 */ }
    }
}
