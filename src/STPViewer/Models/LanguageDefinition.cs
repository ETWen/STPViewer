using CommunityToolkit.Mvvm.ComponentModel;

namespace STPViewer.Models;

public class LanguageDefinition : ObservableObject
{
    public string Id { get; }
    public string Key { get; }
    public IReadOnlyDictionary<string, string> Data { get; }
    public bool IsSelected { get; private set; }

    internal LanguageDefinition(string id, string key, IReadOnlyDictionary<string, string> data)
    {
        Id = id;
        Key = key;
        Data = data;
    }

    internal void SetSelected(bool selected)
    {
        IsSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }
}
