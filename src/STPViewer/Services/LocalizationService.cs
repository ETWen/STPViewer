using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using STPViewer.Models;

namespace STPViewer.Services;

public class LocalizationService : ObservableObject
{
    public static LocalizationService Instance { get; } = new(Path.Combine(AppContext.BaseDirectory, "i18n"));

    private LanguageDefinition? _selected;
    private readonly LanguageDefinition? _english;

    public IReadOnlyList<LanguageDefinition> Languages { get; }
    public string Language => _selected?.Id ?? "";
    public event EventHandler? LanguageChanged;

    public LocalizationService(string directory)
    {
        Languages = LoadLanguages(directory);
        _english = Languages.FirstOrDefault(language => language.Id == "en");
        _selected = Languages.FirstOrDefault(language => language.Id == ResolveLanguage(null));
        foreach (LanguageDefinition language in Languages)
            language.SetSelected(ReferenceEquals(language, _selected));
    }

    public string this[string key]
    {
        get
        {
            if (_selected?.Data.TryGetValue(key, out string? value) == true) return value;
            if (_english?.Data.TryGetValue(key, out value) == true) return value;
            return key;
        }
    }

    public static string Get(string key) => Instance[key];

    public static string Format(string key, params object[] args) => Instance.FormatText(key, args);

    public string FormatText(string key, params object[] args)
    {
        string text = this[key];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException ex)
        {
            Trace.TraceWarning($"Invalid translation format '{key}' in '{Language}': {ex.Message}");
            if (_english?.Data.TryGetValue(key, out string? fallback) == true && fallback != text)
            {
                try { return string.Format(CultureInfo.CurrentCulture, fallback, args); }
                catch (FormatException) { }
            }
            return key;
        }
    }

    public string ResolveLanguage(string? language)
    {
        string id = NormalizeId(language ?? CultureInfo.CurrentUICulture.Name);
        if (Languages.Any(item => item.Id == id)) return id;

        string[] parts = id.Split('_');
        if (parts[0] == "zh")
        {
            bool traditional = parts.Any(part => part is "hant" or "tw" or "hk" or "mo");
            string[] candidates = traditional ? ["zh_tw", "zh_hant", "zh"] : ["zh_cn", "zh_hans", "zh"];
            foreach (string candidate in candidates)
                if (Languages.Any(item => item.Id == candidate)) return candidate;
            return _english?.Id ?? Languages.FirstOrDefault()?.Id ?? "";
        }

        LanguageDefinition? match = Languages.FirstOrDefault(item => item.Id == parts[0]);
        return match?.Id ?? _english?.Id ?? Languages.FirstOrDefault()?.Id ?? "";
    }

    public void SetLanguage(string? language)
    {
        string id = ResolveLanguage(language);
        LanguageDefinition? selected = Languages.FirstOrDefault(item => item.Id == id);
        bool changed = !ReferenceEquals(_selected, selected);
        _selected = selected;

        foreach (LanguageDefinition item in Languages)
            item.SetSelected(ReferenceEquals(item, selected));

        if (selected is not null)
        {
            try
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(selected.Id.Replace('_', '-'));
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch (CultureNotFoundException) { }
        }

        if (!changed) return;
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeId(string id) => id.Trim().Replace('-', '_').ToLowerInvariant();

    private static IReadOnlyList<LanguageDefinition> LoadLanguages(string directory)
    {
        var languages = new List<LanguageDefinition>();
        if (!Directory.Exists(directory)) return languages;

        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Cannot read language directory '{directory}': {ex.Message}");
            return languages;
        }

        foreach (string path in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                LanguageFile? file = JsonSerializer.Deserialize<LanguageFile>(File.ReadAllText(path));
                string id = NormalizeId(Path.GetFileNameWithoutExtension(path));
                if (string.IsNullOrWhiteSpace(file?.Key) || file.Data is null || id.Length == 0 ||
                    file.Data.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
                    throw new JsonException("Expected a display key and a data object containing string translations.");
                if (languages.Any(language => language.Id == id))
                    throw new JsonException($"Duplicate language identifier '{id}'.");

                languages.Add(new LanguageDefinition(id, file.Key, file.Data));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning($"Cannot load language file '{path}': {ex.Message}");
            }
        }
        return languages;
    }

    private sealed class LanguageFile
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("data")]
        public Dictionary<string, string>? Data { get; set; }
    }
}
