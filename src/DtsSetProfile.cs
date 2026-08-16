using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windows.ApplicationModel;
using Windows.Foundation.Collections;
using Windows.Storage;

internal static class DtsSetProfile
{
    private const string RuntimeSettingsContainerName = "RuntimeSettings";
    private const string RuntimeSettingsBlobName = "RuntimeSettingsBLOBName";
    private const string AudioRendererIdName = "AudioRendererId";

    // These names are the actual Sound Unbound resource labels. The generic
    // Headphone:X page exposes Balanced/Spacious; the partner catalog exposes
    // the Gaming/Movies variants below. Do not invent Game/Movie aliases here.
    private static readonly IReadOnlyDictionary<string, string> ProfileBlobs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Balanced"] = "02-SPAC-HqHeightAndHgNf_SD1_Hp_Normal_v4_RC2.SPAC.crypt",
            ["Spacious"] = "04-SPAC-HqHeightAndHgNf_SD2_Hp_Normal_v4_RC2.SPAC.crypt",
            ["Gaming: Balanced"] = "2403-GamingBalanced.SPAC.crypt",
            ["Gaming: Neutral"] = "2403-GamingNeutral.SPAC.crypt",
            ["Gaming: Spacious"] = "2403-GamingSpacious.SPAC.crypt",
            ["Gaming: Spacious 2"] = "2403-GamingSpacious2.SPAC.crypt",
            ["Movies: Balanced"] = "2403-MoviesBalanced.SPAC.crypt",
            ["Movies: Spacious"] = "2403-MoviesSpacious.SPAC.crypt"
        };

    private static string Json(string? value)
    {
        if (value == null) return "null";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static string EncodeSetting(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string DecodeSetting(object? value)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
            return text;
        }
    }

    private static bool EndpointMatches(string requested, string stored)
    {
        string left = requested.Trim();
        string right = stored.Trim();
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
               left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
               right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationDataCompositeValue? FindRuntimeSettings(
        string endpointPath,
        out string? entryKey,
        out string? currentBlob,
        out string? currentDeviceProfileBlob)
    {
        entryKey = null;
        currentBlob = null;
        currentDeviceProfileBlob = null;

        ApplicationDataContainer root = ApplicationData.Current.LocalSettings;
        if (!root.Containers.TryGetValue(RuntimeSettingsContainerName, out ApplicationDataContainer? runtime))
        {
            return null;
        }

        foreach (KeyValuePair<string, object> pair in runtime.Values)
        {
            if (pair.Value is not IPropertySet values) continue;
            string storedEndpoint = DecodeSetting(values.TryGetValue(AudioRendererIdName, out object? renderer) ? renderer : null);
            if (!EndpointMatches(endpointPath, storedEndpoint)) continue;

            var copy = new ApplicationDataCompositeValue();
            foreach (KeyValuePair<string, object> value in values)
            {
                copy[value.Key] = value.Value;
            }

            entryKey = pair.Key;
            currentBlob = DecodeSetting(values.TryGetValue(RuntimeSettingsBlobName, out object? blob) ? blob : null);
            currentDeviceProfileBlob = DecodeSetting(values.TryGetValue("DeviceProfileSettingsBLOBName", out object? deviceBlob) ? deviceBlob : null);
            return copy;
        }

        return null;
    }

    private static string? FindProfileForBlob(string? blobName)
    {
        if (string.IsNullOrWhiteSpace(blobName)) return null;
        foreach (KeyValuePair<string, string> pair in ProfileBlobs)
        {
            if (string.Equals(pair.Value, blobName, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        }

        return null;
    }

    private static string FindPackageBlob(string blobName)
    {
        string path = Path.Combine(
            Package.Current.InstalledLocation.Path,
            "Data",
            "SAD",
            blobName);
        if (!File.Exists(path)) throw new FileNotFoundException("The DTS Sound Unbound SAD blob is missing.", path);
        return path;
    }

    private static void EnsureLocalBlob(string packageBlobPath)
    {
        string localPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Path.GetFileName(packageBlobPath));
        if (!File.Exists(localPath)) File.Copy(packageBlobPath, localPath);
    }

    private static string ReadOnly(string endpointPath)
    {
        ApplicationDataCompositeValue? settings = FindRuntimeSettings(
            endpointPath,
            out string? entryKey,
            out string? currentBlob,
            out string? currentDeviceProfileBlob);
        if (settings == null || string.IsNullOrWhiteSpace(entryKey))
        {
            return "{\"supported\":false,\"applied\":false,\"currentProfile\":null,\"currentBlob\":null}";
        }

        string? profile = FindProfileForBlob(currentBlob);
        return "{\"supported\":true,\"applied\":false,\"entryKey\":" + Json(entryKey) +
               ",\"currentProfile\":" + Json(profile) +
               ",\"currentBlob\":" + Json(currentBlob) +
               ",\"deviceProfileBlob\":" + Json(currentDeviceProfileBlob) + "}";
    }

    private static string Apply(string endpointPath, string profile)
    {
        if (!ProfileBlobs.TryGetValue(profile, out string? targetBlob))
        {
            throw new ArgumentException("The requested DTS Sound Unbound profile is not in the installed SAD catalog.", nameof(profile));
        }

        string packageBlobPath = FindPackageBlob(targetBlob);
        EnsureLocalBlob(packageBlobPath);

        ApplicationDataCompositeValue? settings = FindRuntimeSettings(
            endpointPath,
            out string? entryKey,
            out string? currentBlob,
            out string? currentDeviceProfileBlob);
        if (settings == null || string.IsNullOrWhiteSpace(entryKey))
        {
            return "{\"supported\":false,\"applied\":false,\"reason\":\"runtime settings entry for endpoint was not found\"}";
        }

        if (!string.Equals(currentBlob, targetBlob, StringComparison.OrdinalIgnoreCase))
        {
            settings[RuntimeSettingsBlobName] = EncodeSetting(targetBlob);
            ApplicationData.Current.LocalSettings.Containers[RuntimeSettingsContainerName].Values[entryKey] = settings;
        }

        // Read the same composite back from the package settings store. This
        // is the authoritative state that Sound Unbound uses for generic HPX;
        // CAPX/DSEC status is deliberately not consulted here.
        _ = currentDeviceProfileBlob;
        _ = FindRuntimeSettings(
            endpointPath,
            out string? readbackKey,
            out string? readbackBlob,
            out _);
        bool applied = string.Equals(readbackKey, entryKey, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(readbackBlob, targetBlob, StringComparison.OrdinalIgnoreCase);
        return "{\"supported\":true,\"applied\":" + (applied ? "true" : "false") +
               ",\"profile\":" + Json(profile) +
               ",\"blob\":" + Json(targetBlob) +
               ",\"previousBlob\":" + Json(currentBlob) + "}";
    }

    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: DtsSetProfile.exe <endpoint-path> <profile-or-> <output-json> [--apply]");
            return 2;
        }

        string outputPath = args[2];
        try
        {
            string result = args[1] == "-" || !args.Any(arg => string.Equals(arg, "--apply", StringComparison.OrdinalIgnoreCase))
                ? ReadOnly(args[0])
                : Apply(args[0], args[1]);
            File.WriteAllText(outputPath, result, Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            string result = "{\"errorType\":" + Json(ex.GetType().FullName) +
                            ",\"message\":" + Json(ex.Message) +
                            ",\"hresult\":\"0x" + ex.HResult.ToString("X8") + "\"}";
            try { File.WriteAllText(outputPath, result, Encoding.UTF8); } catch { }
            return 1;
        }
    }
}
