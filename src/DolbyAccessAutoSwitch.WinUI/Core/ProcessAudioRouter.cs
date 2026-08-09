using System.Runtime.InteropServices;

namespace DolbyAccessAutoSwitch_WinUI;

/// <summary>
/// Changes the Windows system default render endpoint.
///
/// The public Core Audio interfaces expose reading the default endpoint and
/// controlling endpoint volume, but not setting the system default endpoint.
/// Windows' PolicyConfig COM interface is therefore kept private to this
/// implementation.
/// </summary>
public static class ProcessAudioRouter
{
    private static readonly Guid PolicyConfigClientClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    public static string SetSystemDefaultOutputDevice(string endpointPath, bool apply)
    {
        if (string.IsNullOrWhiteSpace(endpointPath)) return "Default output switch skipped: endpoint is empty";
        if (!apply) return $"C# dry-run: default output -> {endpointPath}";

        object? policyObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyConfigClientClsid, throwOnError: true)!);
            var policy = (IPolicyConfig)policyObject!;
            string coreAudioEndpointId = ResolveCoreAudioEndpointId(endpointPath);
            string policyEndpointId = ExtractPolicyEndpointId(endpointPath);
            bool usedPolicyEndpointId = false;
            foreach (PolicyConfigRole role in Enum.GetValues<PolicyConfigRole>())
            {
                int hr = policy.SetDefaultEndpoint(coreAudioEndpointId, role);
                if (hr < 0 && !string.Equals(endpointPath, coreAudioEndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    hr = policy.SetDefaultEndpoint(endpointPath, role);
                }

                if (hr < 0 && !string.Equals(policyEndpointId, endpointPath, StringComparison.OrdinalIgnoreCase))
                {
                    hr = policy.SetDefaultEndpoint(policyEndpointId, role);
                    usedPolicyEndpointId = hr >= 0;
                }

                if (hr < 0) return $"Default output switch failed for {role}: {FormatHresult(hr)}";
            }
            return usedPolicyEndpointId
                ? $"C# default output switched -> {endpointPath} (policy endpoint id)"
                : $"C# default output switched -> {endpointPath}";
        }
        catch (Exception ex)
        {
            return $"Default output switch failed: {ex.Message}";
        }
        finally
        {
            if (policyObject != null && Marshal.IsComObject(policyObject)) Marshal.FinalReleaseComObject(policyObject);
        }
    }

    private static string ResolveCoreAudioEndpointId(string endpointPath)
    {
        return AudioEndpointChoice.NormalizeCoreAudioEndpointId(endpointPath);
    }

    private static string FormatHresult(int hr) => $"0x{hr:X8}";

    private static string ExtractPolicyEndpointId(string endpointPath)
    {
        const string marker = "MMDEVAPI#";
        int markerIndex = endpointPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            string deviceId = endpointPath[(markerIndex + marker.Length)..];
            int interfaceIndex = deviceId.IndexOf("#{", StringComparison.Ordinal);
            if (interfaceIndex > 0) return deviceId[..interfaceIndex];
        }

        return endpointPath;
    }

    private enum PolicyConfigRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint format, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long period, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint key, nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PolicyConfigRole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
