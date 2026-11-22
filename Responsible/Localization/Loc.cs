using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;
using Responsible.MoeViews;

namespace Responsible.Localization;

public static class Loc
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible",
        "language.json");

    private static readonly Uri ChineseUri =
        new("/Responsible;component/Localization/Strings.zh-CN.xaml", UriKind.Relative);

    private static readonly Uri EnglishUri =
        new("/Responsible;component/Localization/Strings.en-US.xaml", UriKind.Relative);

    private static string _language = Chinese;

    public static event EventHandler? LanguageChanged;

    public static string Language => _language;

    public static bool IsChinese => _language == Chinese;

    public static void Initialize()
    {
        Apply(Load());
    }

    public static void SetLanguage(string language)
    {
        var normalized = Normalize(language);
        if (normalized == _language) return;
        Save(normalized);
        Apply(normalized);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
        WeakReferenceMessenger.Default.Send(new LanguageChangedMessage());
        MoeText.NotifyLanguageChanged();
    }

    public static string T(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static void Apply(string language)
    {
        var normalized = Normalize(language);
        _language = normalized;
        if (Application.Current is not { } app) return;

        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Localization/Strings.", StringComparison.Ordinal) == true);
        var replacement = new ResourceDictionary
        {
            Source = normalized == English ? EnglishUri : ChineseUri,
        };
        if (existing is null)
            dictionaries.Add(replacement);
        else
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
    }

    private static string Normalize(string language) =>
        language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Chinese;

    private static string Load()
    {
        try
        {
            if (File.Exists(ConfigPath)
                && JsonSerializer.Deserialize<LanguageRecord>(File.ReadAllText(ConfigPath)) is { } record)
                return Normalize(record.Language);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return Chinese;
    }

    private static void Save(string language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new LanguageRecord(language)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record LanguageRecord(string Language);
}