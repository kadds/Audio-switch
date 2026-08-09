using System.Runtime.InteropServices;

namespace DolbyAccessAutoSwitch_WinUI;

/// <summary>
/// Routes a process's future audio streams to a persisted Windows endpoint.
/// AudioPolicyConfig is an internal Windows interface used by the system volume mixer.
/// </summary>
public static class ProcessAudioRouter
{
    private const string AudioPolicyConfigClass = "Windows.Media.Internal.AudioPolicyConfig";
    private static readonly Guid AudioPolicyConfigFactoryIid = new("ab3d4648-e242-459f-b02f-541c70306324");
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

    public static string SetOutputDevice(int processId, string endpointPath, bool apply)
    {
        if (processId <= 0) return "Audio route skipped: invalid process id";
        if (string.IsNullOrWhiteSpace(endpointPath)) return "Audio route skipped: endpoint is empty";
        if (!apply) return $"C# dry-run: process {processId} output -> {endpointPath}";

        nint className = 0;
        nint factoryPointer = 0;
        nint deviceId = 0;
        object? factoryObject = null;
        try
        {
            int hr = WindowsCreateString(AudioPolicyConfigClass, AudioPolicyConfigClass.Length, out className);
            if (hr < 0) return $"Audio route failed at WindowsCreateString: {FormatHresult(hr)}";

            Guid iid = AudioPolicyConfigFactoryIid;
            hr = RoGetActivationFactory(className, ref iid, out factoryPointer);
            if (hr < 0) return $"Audio route failed at RoGetActivationFactory: {FormatHresult(hr)}";

            factoryObject = Marshal.GetObjectForIUnknown(factoryPointer);
            var factory = (IAudioPolicyConfigFactory21H2)factoryObject;

            string coreAudioEndpointId = ResolveCoreAudioEndpointId(endpointPath);
            hr = WindowsCreateString(coreAudioEndpointId, coreAudioEndpointId.Length, out deviceId);
            if (hr < 0) return $"Audio route failed at WindowsCreateString(endpoint): {FormatHresult(hr)}";

            uint result = factory.SetPersistedDefaultAudioEndpoint(
                processId,
                AudioDataFlow.Render,
                AudioRole.Multimedia,
                deviceId);

            return result == 0
                ? $"C# process {processId} output routed -> {endpointPath} (Core Audio endpoint id)"
                : $"Audio route failed for process {processId}: {FormatHresult(result)}";
        }
        catch (Exception ex)
        {
            return $"Audio route failed for process {processId}: {ex.Message}";
        }
        finally
        {
            if (deviceId != 0) WindowsDeleteString(deviceId);
            if (factoryObject != null && Marshal.IsComObject(factoryObject)) Marshal.FinalReleaseComObject(factoryObject);
            if (factoryPointer != 0) Marshal.Release(factoryPointer);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    private static string FormatHresult(int hr) => $"0x{hr:X8}";
    private static string FormatHresult(uint hr) => $"0x{hr:X8}";

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

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    private enum AudioDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum AudioRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    private enum PolicyConfigRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsContext, nint activationParams, out nint interfacePointer);
        [PreserveSig] int OpenPropertyStore(uint access, out nint propertyStore);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IAudioPolicyConfigFactory21H2
    {
        [PreserveSig] int AddCtxVolumeChange();
        [PreserveSig] int RemoveCtxVolumeChanged();
        [PreserveSig] int SetVolumeGroupGainForId();
        [PreserveSig] int GetVolumeGroupGainForId();
        [PreserveSig] int GetActiveVolumeGroupForEndpointId();
        [PreserveSig] int GetVolumeGroupsForEndpoint();
        [PreserveSig] int GetCurrentVolumeContext();
        [PreserveSig] int SetVolumeGroupMuteForId();
        [PreserveSig] int GetVolumeGroupMuteForId();
        [PreserveSig] int SetRingerVibrateState();
        [PreserveSig] int GetRingerVibrateState();
        [PreserveSig] int SetPreferredChatApplication();
        [PreserveSig] int ResetPreferredChatApplication();
        [PreserveSig] int GetPreferredChatApplication();
        [PreserveSig] int GetCurrentChatApplications();
        [PreserveSig] int AddChatContextChanged();
        [PreserveSig] int RemoveChatContextChanged();
        [PreserveSig] uint SetPersistedDefaultAudioEndpoint(int processId, AudioDataFlow flow, AudioRole role, nint deviceId);
        [PreserveSig] uint GetPersistedDefaultAudioEndpoint(int processId, AudioDataFlow flow, AudioRole role, [MarshalAs(UnmanagedType.HString)] out string deviceId);
        [PreserveSig] uint ClearAllPersistedApplicationDefaultEndpoints();
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
