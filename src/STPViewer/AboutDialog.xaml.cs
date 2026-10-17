using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using STPViewer.Services;

namespace STPViewer;

/// <summary>
/// The About window shows application information on the left and release history on the right.
/// Add each new release at the start of Changelog.
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        Version v = Assembly.GetExecutingAssembly().GetName().Version!;
        VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        ChangelogList.ItemsSource = Changelog.Select(e => new
        {
            Header = $"v{e.Version}  ·  {LocalizationService.Get(e.Title)}    ({e.Date:yyyy-MM-dd})",
            Changes = e.Changes.Select(LocalizationService.Get).ToArray(),
        }).ToList();
    }

    private record ChangelogEntry(string Version, DateOnly Date, string Title, string[] Changes);

    // Release history shown to users, newest first.
    private static readonly ChangelogEntry[] Changelog =
    {
        new("0.7.1", new DateOnly(2026, 7, 12), "Ui.About.Release071.Title",
        new[]
        {
            "Ui.About.Release071.Change1",
            "Ui.About.Release071.Change2",
        }),
        new("0.7.0", new DateOnly(2026, 7, 4), "Ui.About.Release070.Title",
        new[]
        {
            "Ui.About.Release070.Change1",
            "Ui.About.Release070.Change2",
            "Ui.About.Release070.Change3",
            "Ui.About.Release070.Change4",
        }),
        new("0.6.0", new DateOnly(2026, 7, 4), "Ui.About.Release060.Title",
        new[]
        {
            "Ui.About.Release060.Change1",
            "Ui.About.Release060.Change2",
            "Ui.About.Release060.Change3",
        }),
        new("0.5.0", new DateOnly(2026, 7, 3), "Ui.About.Release050.Title",
        new[]
        {
            "Ui.About.Release050.Change1",
            "Ui.About.Release050.Change2",
            "Ui.About.Release050.Change3",
            "Ui.About.Release050.Change4",
            "Ui.About.Release050.Change5",
            "Ui.About.Release050.Change6",
            "Ui.About.Release050.Change7",
        }),
        new("0.4.0", new DateOnly(2026, 7, 2), "Ui.About.Release040.Title",
        new[]
        {
            "Ui.About.Release040.Change1",
            "Ui.About.Release040.Change2",
            "Ui.About.Release040.Change3",
        }),
        new("0.3.2", new DateOnly(2026, 6, 18), "Ui.About.Release032.Title",
        new[]
        {
            "Ui.About.Release032.Change1",
        }),
        new("0.3.1", new DateOnly(2026, 6, 13), "Ui.About.Release031.Title",
        new[]
        {
            "Ui.About.Release031.Change1",
        }),
        new("0.3.0", new DateOnly(2026, 6, 13), "Ui.About.Release030.Title",
        new[]
        {
            "Ui.About.Release030.Change1",
        }),
        new("0.2.0", new DateOnly(2026, 6, 13), "Ui.About.Release020.Title",
        new[]
        {
            "Ui.About.Release020.Change1",
            "Ui.About.Release020.Change2",
        }),
        new("0.1.0", new DateOnly(2026, 6, 13), "Ui.About.Release010.Title",
        new[]
        {
            "Ui.About.Release010.Change1",
        }),
    };
}
