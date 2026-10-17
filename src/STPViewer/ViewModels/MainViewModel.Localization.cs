using CommunityToolkit.Mvvm.Input;
using STPViewer.Models;
using STPViewer.Services;

namespace STPViewer.ViewModels;

public partial class MainViewModel
{
    public string SelectedLanguage
    {
        get => LocalizationService.Instance.Language;
        set => LocalizationService.Instance.SetLanguage(value);
    }

    public IReadOnlyList<LanguageDefinition> Languages => LocalizationService.Instance.Languages;

    public void InitializeLocalization() =>
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

    public void DisposeLocalization() =>
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

    [RelayCommand]
    private void ChangeLanguage(string language) => SelectedLanguage = language;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedLanguage));
        QuickBar.RefreshText();
        RefreshLocalizedText();
    }
}
