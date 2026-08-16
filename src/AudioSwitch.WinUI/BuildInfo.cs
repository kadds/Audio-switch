using System.Reflection;

namespace AudioSwitch_WinUI;

internal static class BuildInfo
{
    private static readonly Assembly CurrentAssembly = typeof(BuildInfo).Assembly;

    public static string Version =>
        CurrentAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? CurrentAssembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public static string BuildTime => GetMetadata("AudioSwitchBuildTime", "unknown");

    public static string Channel => GetMetadata("AudioSwitchBuildChannel", "local");

    public static string Commit => GetMetadata("AudioSwitchBuildCommit", "unknown");

    public static string CommitDisplay =>
        Commit.Length > 12 ? Commit[..12] : Commit;

    public static string BuildTimeDisplay
    {
        get
        {
            if (!DateTimeOffset.TryParse(BuildTime, out DateTimeOffset buildTime)) return BuildTime;
            return buildTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    private static string GetMetadata(string key, string fallback)
    {
        string? value = CurrentAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
