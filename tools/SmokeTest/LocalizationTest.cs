using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Media3D;
using CADability.GeoObject;
using STPViewer.Models;
using STPViewer.Services;
using STPViewer.ViewModels;
using IOPath = System.IO.Path;

namespace SmokeTest;

internal static class LocalizationTest
{
    private static readonly Regex FormatIndex = new(@"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})", RegexOptions.Compiled);

    public static int Run()
    {
        int failures = 0;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { failures = RunCore(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            Console.WriteLine($"LocalizationTest: {error}");
            return 1;
        }
        return failures;
    }

    private static int RunCore()
    {
        int failures = CheckTemporaryPacks() + CheckBundledPacks() + CheckViewModel() + CheckSettings();
        Console.WriteLine(failures == 0 ? "LocalizationTest: all passed" : $"LocalizationTest: {failures} failed");
        return failures;
    }

    private static int CheckTemporaryPacks()
    {
        int failures = 0;
        string directory = IOPath.Combine(IOPath.GetTempPath(), "stpviewer_i18n_" + Guid.NewGuid().ToString("N"));
        try
        {
            failures += Check("missing directory loads without languages", new LocalizationService(directory).Languages.Count == 0);
            Directory.CreateDirectory(directory);
            failures += Check("empty directory loads without languages", new LocalizationService(directory).Languages.Count == 0);
            WritePack(directory, "en", "English", new()
            {
                ["Ui.Sample"] = "View", ["Ui.Fallback"] = "Fallback {0}",
                ["Ui.BadBrace"] = "English {0}", ["Ui.BadIndex"] = "English {0}",
                ["Ui.BothBad"] = "English {"
            });
            WritePack(directory, "zh_cn", "Simplified", new()
            {
                ["Ui.Sample"] = "CN_VIEW", ["Ui.BadBrace"] = "Chinese {",
                ["Ui.BadIndex"] = "Chinese {1}", ["Ui.BothBad"] = "Chinese {"
            });
            WritePack(directory, "zh_tw", "Traditional", new() { ["Ui.Sample"] = "TW_VIEW" });
            WritePack(directory, "pirate_dialect", "Pirate", new() { ["Ui.Sample"] = "ARR_VIEW" });
            File.WriteAllText(IOPath.Combine(directory, "broken.json"), "{invalid json");
            File.WriteAllText(IOPath.Combine(directory, "no_key.json"), "{\"data\":{\"Ui.Sample\":\"unused\"}}");
            File.WriteAllText(IOPath.Combine(directory, "no_data.json"), "{\"key\":\"Missing data\"}");
            File.WriteAllText(IOPath.Combine(directory, "non_string.json"), "{\"key\":\"Invalid\",\"data\":{\"Ui.Sample\":42}}");

            CultureInfo oldUi = CultureInfo.CurrentUICulture;
            LocalizationService service;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                service = new LocalizationService(directory);
            }
            finally { CultureInfo.CurrentUICulture = oldUi; }

            failures += Check("valid packs isolated from malformed files", service.Languages.Count == 4 &&
                service.Languages.Select(l => l.Id).Order().SequenceEqual(new[] { "en", "pirate_dialect", "zh_cn", "zh_tw" }));
            failures += Check("display names and data loaded", service.Languages.Any(l =>
                l.Id == "pirate_dialect" && l.Key == "Pirate" && l.Data["Ui.Sample"] == "ARR_VIEW"));
            failures += Check("English selected from system culture", service.Language == "en");
            failures += Check("legacy Chinese IDs resolved", service.ResolveLanguage("zh-CN") == "zh_cn" &&
                service.ResolveLanguage("zh-TW") == "zh_tw");
            failures += Check("Chinese script and region aliases resolved", service.ResolveLanguage("zh-Hans") == "zh_cn" &&
                service.ResolveLanguage("zh-SG") == "zh_cn" && service.ResolveLanguage("zh-Hant") == "zh_tw" &&
                service.ResolveLanguage("zh-HK") == "zh_tw" && service.ResolveLanguage("zh-MO") == "zh_tw");
            failures += Check("custom language ID resolved", service.ResolveLanguage("pirate-dialect") == "pirate_dialect");
            failures += Check("unknown language falls back", service.ResolveLanguage("unknown") == "en");
            failures += Check("invalid custom ID falls back", service.ResolveLanguage("pirate_other") == "en");

            CultureInfo beforeNull = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-HK");
                failures += Check("null language uses system UI culture", service.ResolveLanguage(null) == "zh_tw");
            }
            finally { CultureInfo.CurrentUICulture = beforeNull; }

            LanguageDefinition chinese = service.Languages.Single(l => l.Id == "zh_cn");
            bool selectedNotified = false, indexerNotified = false;
            chinese.PropertyChanged += (_, e) => selectedNotified |= e.PropertyName == nameof(LanguageDefinition.IsSelected);
            ((INotifyPropertyChanged)service).PropertyChanged += (_, e) => indexerNotified |= e.PropertyName == "Item[]";
            service.SetLanguage("zh-CN");
            failures += Check("selection and indexer notify", service.Language == "zh_cn" &&
                chinese.IsSelected && selectedNotified && indexerNotified);
            failures += Check("selected data and fallback", service["Ui.Sample"] == "CN_VIEW" &&
                service["Ui.Fallback"] == "Fallback {0}" && service["Ui.Missing"] == "Ui.Missing");
            failures += Check("malformed brace uses English format", service.FormatText("Ui.BadBrace", "value") == "English value");
            failures += Check("missing format argument uses English format", service.FormatText("Ui.BadIndex", "value") == "English value");
            failures += Check("invalid English format returns key", service.FormatText("Ui.BothBad", "value") == "Ui.BothBad");
            service.SetLanguage("pirate_dialect");
            failures += Check("custom language selected", service.Language == "pirate_dialect" &&
                service["Ui.Sample"] == "ARR_VIEW");

            WritePack(directory, "new_language", "New language", new() { ["Ui.Sample"] = "NEW_VIEW" });
            WritePack(directory, "en", "English", new() { ["Ui.Sample"] = "Updated view", ["Ui.Fallback"] = "Fallback {0}" });
            failures += Check("existing instance does not hot reload", service.Languages.Count == 4 &&
                service.Languages.Single(l => l.Id == "en").Data["Ui.Sample"] == "View");
            var reloaded = new LocalizationService(directory);
            failures += Check("new instance rescans files and values", reloaded.Languages.Count == 5 &&
                reloaded.Languages.Single(l => l.Id == "en").Data["Ui.Sample"] == "Updated view");

            File.Delete(IOPath.Combine(directory, "zh_tw.json"));
            var withoutTraditional = new LocalizationService(directory);
            failures += Check("missing language not listed", withoutTraditional.Languages.All(l => l.Id != "zh_tw"));
            failures += Check("missing legacy ID falls back", withoutTraditional.ResolveLanguage("zh-TW") == "en");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        return failures;
    }

    private static void WritePack(string directory, string id, string name, Dictionary<string, string> data) =>
        File.WriteAllText(IOPath.Combine(directory, id + ".json"), JsonSerializer.Serialize(new { key = name, data }));

    private static int CheckBundledPacks()
    {
        int failures = 0;
        LocalizationService service = LocalizationService.Instance;
        failures += Check("bundled languages available", new[] { "en", "zh_cn", "zh_tw" }
            .All(id => service.Languages.Any(l => l.Id == id)));
        LanguageDefinition? english = service.Languages.FirstOrDefault(l => l.Id == "en");
        if (english is null) return failures;
        failures += Check("bundled English data nonempty", english.Data.Count > 0 &&
            english.Data.Values.All(v => !string.IsNullOrWhiteSpace(v)));
        int formatFailures = 0;
        foreach (LanguageDefinition language in service.Languages)
            foreach ((string key, string value) in language.Data)
            {
                if (string.IsNullOrWhiteSpace(value)) formatFailures++;
                int[] indices = Indices(value);
                if (english.Data.TryGetValue(key, out string? fallback) && !Indices(fallback).SequenceEqual(indices))
                {
                    Console.WriteLine($"  FAIL: {language.Id} format arguments differ for {key}");
                    formatFailures++;
                }
                object[] args = Enumerable.Range(0, indices.DefaultIfEmpty(-1).Max() + 1)
                    .Select(i => (object)i).ToArray();
                try { _ = string.Format(CultureInfo.InvariantCulture, value, args); }
                catch (FormatException)
                {
                    Console.WriteLine($"  FAIL: {language.Id} format syntax for {key}");
                    formatFailures++;
                }
            }
        failures += Check("bundled data and format strings valid", formatFailures == 0);
        failures += Check("missing key returns key", service["__missing_localization_key__"] == "__missing_localization_key__");
        return failures;
    }

    private static int CheckViewModel()
    {
        int failures = 0;
        LocalizationService service = LocalizationService.Instance;
        string originalLanguage = service.Language;
        CultureInfo numericCulture = CultureInfo.CurrentCulture;
        try
        {
            service.SetLanguage("en");
            var vm = new MainViewModel();
            vm.InitializeLocalization();
            try
            {
                string oldLabel = vm.QuickBar["Import"].Label;
                bool labelChanged = false;
                vm.QuickBar["Import"].PropertyChanged += (_, e) =>
                    labelChanged |= e.PropertyName == nameof(QuickBarItemViewModel.Label);

                MeasurementResult result = new MeasurementService().MeasureDistance(
                    new Point3D(0, 0, 0), new Point3D(25.4, 0, 0), "D1", 0.2);
                vm.Measurements.Add(result);
                vm.UseInch = true;
                string oneInch = 1d.ToString("F4", numericCulture) + " in";
                string beforeTitle = result.Title, beforeDetail = result.Detail;
                string beforeLabel = result.DynamicLabels.Single().Label.Text;
                var (distanceLabel, distanceTextFor) = result.DynamicLabels.Single();
                int labelRefreshes = 0;
                result.DynamicLabels[0] = (distanceLabel, u =>
                {
                    labelRefreshes++;
                    return distanceTextFor(u);
                });
                bool titleChanged = false, detailChanged = false;
                result.PropertyChanged += (_, e) =>
                {
                    titleChanged |= e.PropertyName == nameof(MeasurementResult.Title);
                    detailChanged |= e.PropertyName == nameof(MeasurementResult.Detail);
                };

                vm.ChangeLanguageCommand.Execute("zh_tw");
                failures += Check("VM selects language", service.Language == "zh_tw" &&
                    service.Languages.Single(l => l.Id == "zh_tw").IsSelected);
                failures += Check("existing distance detail translated", beforeDetail != result.Detail &&
                    titleChanged && detailChanged && result.Detail.Contains(oneInch, StringComparison.Ordinal));
                failures += Check("distance and number culture preserved", vm.UseInch &&
                    CultureInfo.CurrentCulture.Equals(numericCulture) && beforeTitle == result.Title &&
                    result.Title.Contains(oneInch, StringComparison.Ordinal));
                failures += Check("3D distance label refreshed", labelRefreshes == 1 &&
                    beforeLabel == oneInch && result.DynamicLabels.Single().Label.Text == beforeLabel);
                failures += Check("quick bar label refreshed", labelChanged && vm.QuickBar["Import"].Label != oldLabel);

                LanguageDefinition selected = service.Languages.Single(l => l.Id == "zh_tw");
                bool reselected = false;
                selected.PropertyChanged += (_, e) => reselected |= e.PropertyName == nameof(LanguageDefinition.IsSelected);
                vm.ChangeLanguageCommand.Execute("zh_tw");
                failures += Check("reselecting current language restores check", reselected && selected.IsSelected);
            }
            finally { vm.DisposeLocalization(); }
            failures += CheckImportedNames(service);
        }
        finally { service.SetLanguage(originalLanguage); }
        return failures;
    }

    private static int CheckImportedNames(LocalizationService service)
    {
        int failures = 0;
        MethodInfo blockNode = typeof(StepImportService).GetMethod("BlockNode", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var importer = new StepImportService();
        service.SetLanguage("en");

        ImportedNode Import(Block block) => (ImportedNode)blockNode.Invoke(importer, [block])!;
        var namedSolidBlock = Block.Construct();
        namedSolidBlock.Name = "Solid";
        namedSolidBlock.Add(Solid.Construct());
        ImportedNode namedSolid = Import(namedSolidBlock);
        var namedShellBlock = Block.Construct();
        namedShellBlock.Name = "Shell";
        namedShellBlock.Add(Shell.Construct());
        ImportedNode namedShell = Import(namedShellBlock);
        var defaultSolidBlock = Block.Construct();
        defaultSolidBlock.Add(Solid.Construct());
        ImportedNode defaultSolid = Import(defaultSolidBlock);
        var defaultShellBlock = Block.Construct();
        defaultShellBlock.Add(Shell.Construct());
        ImportedNode defaultShellLeaf = Import(defaultShellBlock).Children.Single();
        var collisionBlock = Block.Construct();
        var realComponentShell = Shell.Construct();
        realComponentShell.Name = "Component";
        collisionBlock.Add(realComponentShell);
        ImportedNode collision = Import(collisionBlock);

        ModelNodeViewModel ViewModel(ImportedNode node) => new(0)
        {
            Name = node.Name,
            NameKey = node.NameKey,
            NameNumber = node.NameNumber
        };
        var namedSolidVm = ViewModel(namedSolid);
        var namedShellVm = ViewModel(namedShell);
        var defaultSolidVm = ViewModel(defaultSolid);
        var defaultShellVm = ViewModel(defaultShellLeaf);
        var collisionVm = ViewModel(collision);
        service.SetLanguage("zh_cn");
        failures += Check("explicit solid name retained", namedSolid.NameKey is null && namedSolidVm.Name == "Solid");
        failures += Check("real block name retained", namedShell.NameKey is null && namedShellVm.Name == "Shell");
        failures += Check("default solid name translated", defaultSolid.NameKey == "Runtime.SolidName" &&
            defaultSolidVm.Name == service["Runtime.SolidName"]);
        failures += Check("default shell name translated", defaultShellLeaf.NameKey == "Runtime.ShellName" &&
            defaultShellVm.Name == service["Runtime.ShellName"]);
        failures += Check("real child name wins collision", collision.NameKey is null && collisionVm.Name == "Component");
        return failures;
    }

    private static int CheckSettings()
    {
        var settings = new AppSettings
        {
            Language = "zh-CN", WindowWidth = 1200, WindowHeight = 800, WindowLeft = 100, WindowTop = 100
        };
        AppSettings? restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        return Check("legacy ID round trips through settings JSON", restored?.Language == "zh-CN" &&
            LocalizationService.Instance.ResolveLanguage(restored.Language) == "zh_cn");
    }

    private static int[] Indices(string format) => FormatIndex.Matches(format)
        .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
        .Distinct().Order().ToArray();

    private static int Check(string name, bool ok)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}");
        return ok ? 0 : 1;
    }
}
