using System.Globalization;
using System.Windows;

namespace TelegramConsoleApp;

public static class LocalizationManager
{
    private const string DictionaryPrefix = "Resources/Strings.";

    public static string CurrentLanguage { get; private set; } = "zh-CN";
    public static string LanguagePreference { get; private set; } = "";

    public static void ApplyLanguage(string? language)
    {
        LanguagePreference = NormalizePreference(language);
        var normalized = LanguagePreference.Length > 0
            ? LanguagePreference
            : ResolveSystemLanguage(CultureInfo.InstalledUICulture);
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CurrentLanguage = normalized;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var old = dictionaries.FirstOrDefault(x =>
            x.Source?.OriginalString.StartsWith(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"{DictionaryPrefix}{normalized}.xaml", UriKind.Relative)
        };
        if (old is null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(old)] = replacement;
    }

    public static string Text(string key) =>
        Application.Current.TryFindResource(key)?.ToString() ?? key;

    public static string Get(string key) => Text(key);

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), args);

    private static string NormalizePreference(string? language)
    {
        if (language?.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) == true) return "zh-CN";
        if (language?.Equals("en-US", StringComparison.OrdinalIgnoreCase) == true) return "en-US";
        return "";
    }

    private static string ResolveSystemLanguage(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";
}
