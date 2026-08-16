using System.Globalization;
using Windows.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace AudioSwitch_WinUI;

internal static class Localization
{
    private static ResourceLoader? loader;

    public static string LanguageTag { get; private set; } = "en-US";
    public static string LanguageSetting { get; private set; } = "System";

    public static void Initialize(string? languageSetting = null)
    {
        try
        {
            LanguageSetting = NormalizeSetting(languageSetting);
            string systemLanguage = CultureInfo.InstalledUICulture.Name;
            if (string.IsNullOrWhiteSpace(systemLanguage))
            {
                systemLanguage = ApplicationLanguages.Languages.FirstOrDefault() ?? "en-US";
            }
            LanguageTag = LanguageSetting switch
            {
                "zh-CN" => "zh-CN",
                "en-US" => "en-US",
                _ => systemLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US"
            };
            ApplicationLanguages.PrimaryLanguageOverride = LanguageTag;
            loader = new ResourceLoader();
        }
        catch
        {
            LanguageSetting = NormalizeSetting(languageSetting);
            LanguageTag = "en-US";
            loader = null;
        }
    }

    private static string NormalizeSetting(string? languageSetting) =>
        languageSetting is "zh-CN" or "en-US" ? languageSetting : "System";

    public static string Get(string key)
    {
        try
        {
            string resourceId = key.Replace('.', '/');
            string value = loader?.GetString(resourceId) ?? string.Empty;
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch
        {
            return key;
        }
    }

    public static string Text(string key) => Get(key + ".Text");
    public static string Content(string key) => Get(key + ".Content");
    public static string Value(string key) => Get(key + ".Value");
    public static string FormatValue(string key, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, Value(key), arguments);
}
