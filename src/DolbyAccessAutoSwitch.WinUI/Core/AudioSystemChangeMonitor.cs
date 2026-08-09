using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media.Audio;

namespace DolbyAccessAutoSwitch_WinUI;

/// <summary>
/// Bridges native Core Audio endpoint notifications and the WinRT spatial audio
/// configuration notification into one small C# event source.
/// </summary>
public sealed class AudioSystemChangeMonitor : IDisposable
{
    private readonly Action<string> changed;
    private readonly Action<string>? diagnostic;
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private IMMDeviceEnumerator? enumerator;
    private AudioEndpointNotificationClient? notificationClient;
    private SpatialAudioDeviceConfiguration? spatialConfiguration;
    private TypedEventHandler<SpatialAudioDeviceConfiguration, object>? spatialConfigurationChanged;
    private string? spatialEndpointId;
    private bool started;
    private bool disposed;

    public AudioSystemChangeMonitor(Action<string> changed, Action<string>? diagnostic = null)
    {
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
        this.diagnostic = diagnostic;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (started) return;

        object? rawEnumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid, throwOnError: true)!);
        IMMDeviceEnumerator nextEnumerator = (IMMDeviceEnumerator)rawEnumerator!;
        AudioEndpointNotificationClient nextClient = new(Notify);
        try
        {
            int hr = nextEnumerator.RegisterEndpointNotificationCallback(nextClient);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            enumerator = nextEnumerator;
            notificationClient = nextClient;
            started = true;
            Diagnostic("IMMNotificationClient registration succeeded.");
            UpdateSpatialAudioSubscription();
        }
        catch
        {
            ReleaseComObject(nextEnumerator);
            throw;
        }
    }

    /// <summary>
    /// Selects the endpoint whose spatial-audio configuration should be observed.
    /// The page calls this after each state read because the default output can change.
    /// </summary>
    public void SetSpatialAudioEndpoint(string? endpointId)
    {
        ThrowIfDisposed();
        bool sameEndpoint = string.Equals(spatialEndpointId, endpointId, StringComparison.OrdinalIgnoreCase) &&
            (!started || spatialConfiguration != null);
        spatialEndpointId = endpointId;
        if (started && !sameEndpoint) UpdateSpatialAudioSubscription();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        started = false;

        RemoveSpatialAudioSubscription();

        if (enumerator != null && notificationClient != null)
        {
            try { enumerator.UnregisterEndpointNotificationCallback(notificationClient); }
            catch { }
        }

        ReleaseComObject(enumerator);
        enumerator = null;
        notificationClient = null;
        Diagnostic("Audio notification listeners disposed.");
        GC.SuppressFinalize(this);
    }

    private void UpdateSpatialAudioSubscription()
    {
        RemoveSpatialAudioSubscription();
        if (string.IsNullOrWhiteSpace(spatialEndpointId)) return;

        try
        {
            SpatialAudioDeviceConfiguration configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(
                AudioEndpointChoice.NormalizeDeviceInterfacePath(spatialEndpointId));
            TypedEventHandler<SpatialAudioDeviceConfiguration, object> handler = (_, _) => Notify("Spatial audio configuration changed");
            configuration.ConfigurationChanged += handler;
            spatialConfiguration = configuration;
            spatialConfigurationChanged = handler;
            Diagnostic($"SpatialAudioDeviceConfiguration subscribed: device={spatialEndpointId}");
        }
        catch (Exception ex)
        {
            Diagnostic($"SpatialAudioDeviceConfiguration subscription failed: device={spatialEndpointId}; {ex.Message}");
            Notify($"Spatial audio listener unavailable: {ex.Message}");
        }
    }

    private void RemoveSpatialAudioSubscription()
    {
        if (spatialConfiguration != null && spatialConfigurationChanged != null)
        {
            try { spatialConfiguration.ConfigurationChanged -= spatialConfigurationChanged; }
            catch { }
        }

        spatialConfiguration = null;
        spatialConfigurationChanged = null;
    }

    private void Notify(string reason)
    {
        if (disposed) return;
        try { changed(reason); }
        catch { }
    }

    private void Diagnostic(string message)
    {
        try { diagnostic?.Invoke(message); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComVisible(true)]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioEndpointNotificationClient : IMMNotificationClient
    {
        private readonly Action<string> notify;

        public AudioEndpointNotificationClient(Action<string> notify)
        {
            this.notify = notify;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            notify($"IMMNotificationClient.OnDeviceStateChanged: device={deviceId}; state=0x{newState:X}");
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            notify($"IMMNotificationClient.OnDeviceAdded: device={deviceId}");
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            notify($"IMMNotificationClient.OnDeviceRemoved: device={deviceId}");
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string deviceId)
        {
            if (flow == EDataFlow.Render)
            {
                notify($"IMMNotificationClient.OnDefaultDeviceChanged: flow={flow}; role={role}; device={deviceId}");
            }
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            notify($"IMMNotificationClient.OnPropertyValueChanged: device={deviceId}; property={key.FormatId}/{key.PropertyId}");
            return 0;
        }
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
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out nint device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out nint device);
        [PreserveSig] int RegisterEndpointNotificationCallback([In][MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback([In][MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
    }
}
