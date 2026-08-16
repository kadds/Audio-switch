using System.Runtime.InteropServices;

namespace AudioSwitch_WinUI;

/// <summary>
/// Windows Core Audio master-volume control for an endpoint.
/// </summary>
public static class AudioVolumeController
{
    private const int RenderDataFlow = 0;
    private const int ConsoleRole = 0;
    private const int MultimediaRole = 1;
    private const uint ClsctxAll = 23;
    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EmptyEventContext = Guid.Empty;

    public static bool TryGetEndpointVolume(string? endpointPath, out float percent)
    {
        return TryGetEndpointVolume(endpointPath, out percent, out _);
    }

    public static bool TryGetEndpointVolume(string? endpointPath, out float percent, out string error)
    {
        percent = 0;
        error = string.Empty;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpointVolume = null;
        nint interfacePointer = 0;
        try
        {
            device = OpenEndpoint(endpointPath, out error);
            if (device == null) return false;

            Guid iid = EndpointVolumeIid;
            int hr = device.Activate(ref iid, ClsctxAll, 0, out interfacePointer);
            if (hr < 0 || interfacePointer == 0)
            {
                error = $"IAudioEndpointVolume activation failed: {FormatHresult(hr)}";
                return false;
            }

            endpointVolume = (IAudioEndpointVolume)Marshal.GetTypedObjectForIUnknown(interfacePointer, typeof(IAudioEndpointVolume));
            hr = endpointVolume.GetMasterVolumeLevelScalar(out float scalar);
            if (hr < 0)
            {
                error = $"GetMasterVolumeLevelScalar failed: {FormatHresult(hr)}";
                return false;
            }

            percent = Math.Clamp(scalar, 0f, 1f) * 100f;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ReleaseCom(endpointVolume);
            ReleaseCom(device);
            if (interfacePointer != 0) Marshal.Release(interfacePointer);
        }
    }

    public static string SetEndpointVolume(string? endpointPath, float percent, bool apply)
    {
        percent = Math.Clamp(percent, 0f, 100f);
        if (!apply) return $"C# dry-run: endpoint volume -> {percent:0}%";

        IMMDevice? device = null;
        IAudioEndpointVolume? endpointVolume = null;
        nint interfacePointer = 0;
        try
        {
            device = OpenEndpoint(endpointPath, out string endpointError);
            if (device == null) return $"Endpoint volume switch failed: {endpointError}";

            Guid iid = EndpointVolumeIid;
            int hr = device.Activate(ref iid, ClsctxAll, 0, out interfacePointer);
            if (hr < 0 || interfacePointer == 0) return $"Endpoint volume switch failed: {FormatHresult(hr)}";
            endpointVolume = (IAudioEndpointVolume)Marshal.GetTypedObjectForIUnknown(interfacePointer, typeof(IAudioEndpointVolume));
            Guid eventContext = EmptyEventContext;
            hr = endpointVolume.SetMasterVolumeLevelScalar(percent / 100f, ref eventContext);
            if (hr < 0) return $"Endpoint volume switch failed: {FormatHresult(hr)}";

            int readbackHr = endpointVolume.GetMasterVolumeLevelScalar(out float readbackScalar);
            return readbackHr >= 0
                ? $"C# endpoint volume set -> {percent:0}% (readback {Math.Clamp(readbackScalar, 0f, 1f) * 100f:0}%)"
                : $"C# endpoint volume set -> {percent:0}% (readback failed: {FormatHresult(readbackHr)})";
        }
        catch (Exception ex)
        {
            return $"Endpoint volume switch failed: {ex.Message}";
        }
        finally
        {
            ReleaseCom(endpointVolume);
            ReleaseCom(device);
            if (interfacePointer != 0) Marshal.Release(interfacePointer);
        }
    }

    private static IMMDevice? OpenEndpoint(string? endpointPath, out string error)
    {
        error = string.Empty;
        IMMDeviceEnumerator? enumerator = null;
        object? rawEnumerator = null;
        IMMDevice? device = null;
        try
        {
            rawEnumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid, throwOnError: true)!);
            enumerator = (IMMDeviceEnumerator)rawEnumerator!;
            int hr;
            if (string.IsNullOrWhiteSpace(endpointPath))
            {
                hr = enumerator.GetDefaultAudioEndpoint(RenderDataFlow, ConsoleRole, out device);
            }
            else
            {
                hr = enumerator.GetDevice(endpointPath, out device);
                if (hr < 0)
                {
                    string normalizedPath = NormalizeEndpointPath(endpointPath);
                    if (!string.Equals(normalizedPath, endpointPath, StringComparison.OrdinalIgnoreCase))
                    {
                        ReleaseCom(device);
                        device = null;
                        hr = enumerator.GetDevice(normalizedPath, out device);
                    }
                }
            }

            if (hr < 0 || device == null)
            {
                error = $"endpoint could not be opened ({FormatHresult(hr)}; id={endpointPath ?? "default"})";
                ReleaseCom(device);
                return null;
            }

            return device;
        }
        catch (Exception ex)
        {
            error = $"endpoint could not be opened ({ex.Message}; id={endpointPath ?? "default"})";
            ReleaseCom(device);
            return null;
        }
        finally
        {
            if (enumerator != null) ReleaseCom(enumerator);
            else ReleaseCom(rawEnumerator);
        }
    }

    private static string NormalizeEndpointPath(string endpointPath)
    {
        return AudioEndpointChoice.NormalizeCoreAudioEndpointId(endpointPath);
    }

    private static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static string FormatHresult(int hr) => $"0x{hr:X8}";

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
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
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
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint supportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

}
