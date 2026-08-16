using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Serialization;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Audio;
using Windows.Media.Devices;

namespace AudioSwitch_WinUI;

/// <summary>
/// Serializes audio-control writes on one background consumer. WinRT calls are
/// asynchronous, but the queue still guarantees that a later switch cannot
/// overtake an earlier endpoint/profile update.
/// </summary>
public sealed class AudioApiQueue : IDisposable
{
    private interface IQueuedOperation
    {
        Task ExecuteAsync();
    }

    private sealed class QueuedOperation<T> : IQueuedOperation
    {
        private readonly Func<Task<T>> callback;
        private readonly CancellationToken cancellationToken;
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QueuedOperation(Func<Task<T>> callback, CancellationToken cancellationToken)
        {
            this.callback = callback;
            this.cancellationToken = cancellationToken;
        }
        public Task<T> Result => completion.Task;

        public async Task ExecuteAsync()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await callback().ConfigureAwait(false));
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
    }

    private readonly Channel<IQueuedOperation> channel = Channel.CreateUnbounded<IQueuedOperation>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private int pendingCount;
    private int disposed;

    public AudioApiQueue()
    {
        worker = Task.Run(ProcessAsync);
    }

    public bool IsBusy => Volatile.Read(ref pendingCount) > 0;

    public Task EnqueueAsync(Func<Task> callback, CancellationToken cancellationToken = default) => EnqueueAsync(async () =>
    {
        await callback().ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task<T> EnqueueAsync<T>(Func<Task<T>> callback, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0) return Task.FromException<T>(new ObjectDisposedException(nameof(AudioApiQueue)));

        var operation = new QueuedOperation<T>(callback, cancellationToken);
        Interlocked.Increment(ref pendingCount);
        if (!channel.Writer.TryWrite(operation))
        {
            Interlocked.Decrement(ref pendingCount);
            return Task.FromException<T>(new InvalidOperationException("Audio API queue is closed."));
        }

        return operation.Result;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (IQueuedOperation operation in channel.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await operation.ExecuteAsync().ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref pendingCount);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        channel.Writer.TryComplete();
        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch
        {
        }
        shutdown.Cancel();
        shutdown.Dispose();
    }
}

[Serializable]
public sealed class ProcessSwitchItem
{
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public ProcessMatchMode MatchMode { get; set; } = ProcessMatchMode.FullPath;
    public string RemoteAddress { get; set; } = string.Empty;
    public string RemoteTitle { get; set; } = string.Empty;
    public string RemoteStatusText { get; set; } = string.Empty;
    public string RemoteState { get; set; } = string.Empty;
    public List<ProcessAddressRule> AddressRules { get; set; } = new();
    public bool ForegroundOnly { get; set; } = true;
    public int? PriorityOverride { get; set; }
    public string Description { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string EndpointFile { get; set; } = string.Empty;
    public float? GlobalVolumePercent { get; set; }
    public string ActiveProfile { get; set; } = "Game";
    public string ActiveSpatialAudioModeId { get; set; } = "keep";
    [XmlIgnore]
    public string DisplayName => Name;
    [XmlIgnore]
    public string DisplayDescription => Description;
}

[Serializable]
public sealed class ProcessAddressRule
{
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public ProcessSwitchItem Action { get; set; } = new();

    [XmlIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Localization.Value("ProcessProfile_AddressRuleDefaultName")
        : Name;

    [XmlIgnore]
    public string DisplayAddress => string.IsNullOrWhiteSpace(Address) ? "URL is not configured" : Address;
}

public enum ProcessMatchMode
{
    ProcessName,
    FullPath,
    HttpState
}

[Serializable]
public sealed class SwitchConfig
{
    public List<ProcessSwitchItem> Processes { get; set; } = new();
    public string GlobalEndpointFile { get; set; } = string.Empty;
    public string GlobalSpatialAudioModeId { get; set; } = string.Empty;
    public string GlobalActiveProfile { get; set; } = string.Empty;
    public float? GlobalVolumePercent { get; set; }
    public bool VolumeProtectionEnabled { get; set; } = true;
    public string UiLanguage { get; set; } = "System";
    public int IntervalSeconds { get; set; } = 2;
    public bool StartWithWindows { get; set; }
    public bool StartSilent { get; set; }
    public bool StartMonitor { get; set; }
    public string OutputNotificationMode { get; set; } = "none";
    public string SpatialNotificationMode { get; set; } = "none";
    public bool HttpListenerEnabled { get; set; }
    public string HttpListenerBindAddress { get; set; } = "127.0.0.1";
    public int HttpListenerPort { get; set; } = 8765;
    public string HttpListenerPassword { get; set; } = string.Empty;
    public bool BrowserIntegrationPromptDismissed { get; set; }

    public static SwitchConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return new XmlSerializer(typeof(SwitchConfig)).Deserialize(stream) as SwitchConfig ?? new SwitchConfig();
            }
        }
        catch
        {
        }
        return new SwitchConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        new XmlSerializer(typeof(SwitchConfig)).Serialize(stream, this);
    }
}

public static class VolumeSafety
{
    public const float ProtectedMaximumPercent = 80f;

    public static float MaximumPercent(bool protectionEnabled) =>
        protectionEnabled ? ProtectedMaximumPercent : 100f;

    public static float Clamp(float percent, bool protectionEnabled) =>
        Math.Clamp(percent, 0f, MaximumPercent(protectionEnabled));

    public static float? Clamp(float? percent, bool protectionEnabled) =>
        percent.HasValue ? Clamp(percent.Value, protectionEnabled) : null;
}

public static class AppPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string DataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSwitch");
    public static string WorkDirectory { get; } = Path.Combine(DataRoot, "work");
    public static string IconDirectory { get; } = Path.Combine(DataRoot, "icons");
    public static string ConfigPath { get; } = Path.Combine(DataRoot, "audio-switch-config.xml");
    public static string LogPath { get; } = Path.Combine(DataRoot, "audio-switch.log");
    public static string RotatedLogPath { get; } = LogPath + ".1";

    public static void EnsureDataDirectories()
    {
        Directory.CreateDirectory(WorkDirectory);
        Directory.CreateDirectory(IconDirectory);
    }

    private static string FindRepositoryRoot()
    {
        string? current = Environment.GetEnvironmentVariable("AUDIOSWITCH_PROJECT_ROOT");
        var candidates = new List<string?> { current, AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            DirectoryInfo? directory = new DirectoryInfo(candidate);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "CapxSetProfile.cs")) && File.Exists(Path.Combine(directory.FullName, "src", "AudioSwitch.WinUI", "AudioSwitch.WinUI.csproj"))) return directory.FullName;
                directory = directory.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }
}

public sealed class ProcessChoice : INotifyPropertyChanged
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint processHandle, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    public string RuleId { get; set; } = string.Empty;
    public string ParentRuleId { get; init; } = string.Empty;
    public bool IsAddressRule { get; init; }
    public string Name { get; set; } = string.Empty;
    public string RuleAddress { get; set; } = string.Empty;
    public int Id { get; init; }
    public string Description { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string IconPath { get; init; } = string.Empty;
    public bool HasWindow { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
    public bool IsRuleDisabled { get; private set; }
    public double DisplayOpacity => IsRuleDisabled ? 0.46 : 1.0;
    public string WindowStatus => HasWindow
        ? Localization.Value("Dialog_ProcessWindowed")
        : Localization.Value("Dialog_ProcessBackground");
    public string DisplayName => Name;
    public string DisplayMarker => IsAddressRule ? "↳" : string.Empty;
    public string DisplayDescription => IsAddressRule
        ? (string.IsNullOrWhiteSpace(RuleAddress) ? Localization.Value("ProcessProfile_AddressRuleUrlMissing") : RuleAddress)
        : Description;
    public BitmapImage? IconSource => string.IsNullOrWhiteSpace(IconPath) ? null : new BitmapImage(new Uri(IconPath, UriKind.Absolute));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetRuleDisabled(bool disabled)
    {
        if (IsRuleDisabled == disabled) return;
        IsRuleDisabled = disabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRuleDisabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayOpacity)));
    }

    public void UpdateAddressRule(ProcessAddressRule rule)
    {
        if (!IsAddressRule) return;
        Name = rule.DisplayName;
        RuleAddress = rule.Address;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayDescription)));
    }

    public override string ToString() => $"[{Id}] {Name}{(string.IsNullOrWhiteSpace(Description) ? string.Empty : " - " + Description)}";

    public static ProcessChoice? FromProcess(Process process)
    {
        string path = GetExecutablePath(process);
        string description = string.Empty;
        string iconPath = string.Empty;
        bool hasWindow = false;
        string windowTitle = string.Empty;
        try
        {
            hasWindow = process.MainWindowHandle != 0;
            windowTitle = process.MainWindowTitle ?? string.Empty;
        }
        catch
        {
        }
        try
        {
            if (File.Exists(path))
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                description = string.IsNullOrWhiteSpace(version.FileDescription) ? version.ProductName ?? string.Empty : version.FileDescription;
                iconPath = SaveIcon(path);
            }
        }
        catch
        {
        }
        return new ProcessChoice
        {
            Name = process.ProcessName,
            Id = process.Id,
            FilePath = path,
            Description = description,
            IconPath = iconPath,
            HasWindow = hasWindow,
            WindowTitle = windowTitle
        };
    }

    internal static string GetExecutablePath(Process process)
    {
        try
        {
            string path = process.MainModule?.FileName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch
        {
        }

        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == 0) return string.Empty;
        try
        {
            var buffer = new StringBuilder(1024);
            int length = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static ProcessChoice FromStored(ProcessSwitchItem item)
    {
        string path = item.ExecutablePath;
        string description = item.Description;
        string iconPath = item.IconPath;
        if (File.Exists(path))
        {
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                if (string.IsNullOrWhiteSpace(description)) description = string.IsNullOrWhiteSpace(version.FileDescription) ? version.ProductName ?? string.Empty : version.FileDescription;
                if (string.IsNullOrWhiteSpace(iconPath)) iconPath = SaveIcon(path);
            }
            catch
            {
            }
        }
        if (!File.Exists(iconPath)) iconPath = string.Empty;
        return new ProcessChoice
        {
            Name = item.Name,
            RuleId = item.RuleId,
            Description = description,
            FilePath = path,
            IconPath = iconPath,
        };
    }

    public static ProcessChoice FromAddressRule(ProcessSwitchItem parent, ProcessAddressRule rule) => new()
    {
        Name = rule.DisplayName,
        RuleId = rule.RuleId,
        ParentRuleId = parent.RuleId,
        IsAddressRule = true,
        RuleAddress = rule.Address
    };

    private static string SaveIcon(string executablePath)
    {
        try
        {
            AppPaths.EnsureDataDirectories();
            string target = Path.Combine(AppPaths.IconDirectory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(executablePath))).Substring(0, 16) + ".png");
            if (File.Exists(target)) return target;
            using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
            using Bitmap? bitmap = icon?.ToBitmap();
            if (bitmap == null) return string.Empty;
            bitmap.Save(target, ImageFormat.Png);
            return target;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed class AudioProfileMatch
{
    public string ProviderId { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string CurrentProfile { get; init; } = string.Empty;
}

public sealed class AudioEndpointChoice
{
    public bool IsKeepCurrent { get; init; }
    public string EndpointId { get; init; } = string.Empty;
    public string EndpointPath { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public string DeviceDescription { get; init; } = string.Empty;
    public string Driver { get; init; } = string.Empty;
    public string DriverProvider { get; init; } = string.Empty;
    public string Profile { get; set; } = "unknown";
    public bool HasProfile { get; init; }
    public bool ProbeCompleted { get; set; }
    public string ProbeStatus { get; set; } = "Not scanned";
    public ObservableCollection<AudioProfileMatch> Matches { get; } = new();
    public bool IsMatched => Matches.Count > 0;
    public string MatchedProviders => string.Join(", ", Matches.Select(match => match.ProviderName));
    public string DisplayName => IsKeepCurrent
        ? Localization.Value("ProcessProfile_KeepCurrentOutput")
        : string.IsNullOrWhiteSpace(DeviceDescription) ||
        string.Equals(FriendlyName, DeviceDescription, StringComparison.CurrentCultureIgnoreCase)
        ? FriendlyName
        : $"{FriendlyName} ({DeviceDescription})";
    public string Details => string.Join("  ·  ", new[]
    {
        string.IsNullOrWhiteSpace(DriverProvider) ? null : $"provider: {DriverProvider}",
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(DeviceDescription) || string.Equals(FriendlyName, DeviceDescription, StringComparison.CurrentCultureIgnoreCase)
            ? FriendlyName
            : $"{FriendlyName}  |  {DeviceDescription}";
    }

    public static AudioEndpointChoice CreateKeepCurrent() => new() { IsKeepCurrent = true };

    // Keep the Windows device-interface path for WinRT APIs such as
    // SpatialAudioDeviceConfiguration. Core Audio COM calls normalize it to the
    // endpoint ID returned by IMMDevice.GetId() before calling GetDevice.
    public static string BuildEndpointPath(string endpointId) =>
        $@"\\?\SWD#MMDEVAPI#{{0.0.0.00000000}}.{endpointId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}";

    public static string NormalizeCoreAudioEndpointId(string endpointPath)
    {
        if (string.IsNullOrWhiteSpace(endpointPath)) return string.Empty;

        const string marker = "MMDEVAPI#";
        int markerIndex = endpointPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            string coreAudioId = endpointPath[(markerIndex + marker.Length)..];
            int interfaceIndex = coreAudioId.IndexOf("#{", StringComparison.Ordinal);
            return interfaceIndex > 0 ? coreAudioId[..interfaceIndex] : coreAudioId;
        }

        if (endpointPath.StartsWith("{", StringComparison.Ordinal) &&
            endpointPath.EndsWith("}", StringComparison.Ordinal) &&
            !endpointPath.StartsWith("{0.0.0.00000000}.", StringComparison.OrdinalIgnoreCase))
        {
            return $@"{{0.0.0.00000000}}.{endpointPath}";
        }

        return endpointPath;
    }

    public static string NormalizeDeviceInterfacePath(string endpointPath)
    {
        if (string.IsNullOrWhiteSpace(endpointPath)) return string.Empty;
        if (endpointPath.Contains("MMDEVAPI#", StringComparison.OrdinalIgnoreCase)) return endpointPath;
        string coreAudioId = NormalizeCoreAudioEndpointId(endpointPath);
        return string.IsNullOrWhiteSpace(coreAudioId)
            ? string.Empty
            : $@"\\?\SWD#MMDEVAPI#{coreAudioId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}";
    }

    public static List<AudioEndpointChoice> ReadAll()
    {
        const string renderRoot = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render";
        const string profileSet = "{45da5c30-2837-4ac4-b1e2-50acc3865974}";
        const string profileValue = "{e36464a1-2f4b-440b-a776-8b32b26a7f01},1";
        var result = new List<AudioEndpointChoice>();
        using RegistryKey? render = Registry.LocalMachine.OpenSubKey(renderRoot);
        if (render == null) return result;
        foreach (string endpointId in render.GetSubKeyNames())
        {
            using RegistryKey? endpoint = render.OpenSubKey(endpointId);
            using RegistryKey? properties = endpoint?.OpenSubKey("Properties");
            using RegistryKey? profile = endpoint?.OpenSubKey($"FxProperties\\{profileSet}\\User");
            if (endpoint == null) continue;
            int deviceState = Convert.ToInt32(endpoint.GetValue("DeviceState", 0));
            if ((deviceState & 1) == 0) continue;
            byte[]? raw = profile?.GetValue(profileValue) as byte[];
            result.Add(new AudioEndpointChoice
            {
                EndpointId = endpointId,
                EndpointPath = BuildEndpointPath(endpointId),
                FriendlyName = Convert.ToString(properties?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2")) ?? endpointId,
                DeviceDescription = Convert.ToString(properties?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6")) ?? string.Empty,
                Driver = Convert.ToString(properties?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6")) ?? "Unknown driver",
                DriverProvider = Convert.ToString(properties?.GetValue("{a8b865dd-2e3d-4094-ad97-e593a70c75d6},6")) ?? string.Empty,
                Profile = DecodeProfile(raw),
                HasProfile = raw is { Length: >= 5 }
            });
        }
        result.Sort((a, b) =>
        {
            int compare = StringComparer.CurrentCultureIgnoreCase.Compare(a.FriendlyName, b.FriendlyName);
            if (compare != 0) return compare;

            compare = StringComparer.CurrentCultureIgnoreCase.Compare(a.DeviceDescription, b.DeviceDescription);
            if (compare != 0) return compare;

            // Registry subkey enumeration order is not a stable UI order. The
            // endpoint id is the final deterministic tie-breaker for devices
            // that expose the same friendly name and description.
            return StringComparer.OrdinalIgnoreCase.Compare(a.EndpointId, b.EndpointId);
        });
        return result;
    }

    internal static string DecodeProfile(byte[]? raw)
    {
        if (raw == null || raw.Length < 5) return "unknown";
        int offset = raw.Length - 5;
        if (raw[offset] != 1 || raw[offset + 2] != 0 || raw[offset + 3] != 0 || raw[offset + 4] != 0) return "unknown";
        string[] profiles = { "Dynamic", "Game", "Movie", "Music", "Voice", "Custom1", "Custom2", "Custom3" };
        return raw[offset + 1] < profiles.Length ? profiles[raw[offset + 1]] : $"Unknown({raw[offset + 1]})";
    }
}

public sealed class SpatialAudioOption
{
    public string Id { get; init; } = string.Empty;
    public string NameKey { get; init; } = string.Empty;
    public string Name => Localization.Get(NameKey);
    public string AudioProfileProviderId { get; init; } = string.Empty;
    public string AvailabilityKey { get; init; } = string.Empty;
    public string Availability => Localization.Get(AvailabilityKey);
    public string FormatSubtype { get; init; } = string.Empty;
}

public static class SpatialAudioModeCatalog
{
    public static IReadOnlyList<SpatialAudioOption> Options { get; } = new[]
    {
        new SpatialAudioOption { Id = "off", NameKey = "Spatial_Off.Name", AvailabilityKey = "Spatial_Off.Availability", FormatSubtype = string.Empty },
        new SpatialAudioOption { Id = "windows-sonic", NameKey = "Spatial_WindowsSonic.Name", AvailabilityKey = "Spatial_WindowsSonic.Availability", FormatSubtype = SpatialAudioFormatSubtype.WindowsSonic },
        new SpatialAudioOption { Id = "dolby-atmos-headphones", NameKey = "Spatial_Dolby.Name", AudioProfileProviderId = "dolby-capx", AvailabilityKey = "Spatial_Dolby.Availability", FormatSubtype = SpatialAudioFormatSubtype.DolbyAtmosForHeadphones },
        new SpatialAudioOption { Id = "dts-headphone-x", NameKey = "Spatial_Dts.Name", AudioProfileProviderId = "dts-sad", AvailabilityKey = "Spatial_Dts.Availability", FormatSubtype = SpatialAudioFormatSubtype.DTSHeadphoneX }
    };

    public static IReadOnlyList<SpatialAudioOption> ProcessOptions { get; } = new[]
    {
        new SpatialAudioOption { Id = "keep", NameKey = "Spatial_Keep.Name", AvailabilityKey = "Spatial_Keep.Availability" }
    }.Concat(Options).ToList();

    public static SpatialAudioOption FindById(string? id) =>
        ProcessOptions.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) ?? ProcessOptions[0];

    public static SpatialAudioOption FindByFormat(string? format) =>
        string.IsNullOrWhiteSpace(format)
            ? FindById("off")
            : Options.FirstOrDefault(option => string.Equals(option.FormatSubtype, format, StringComparison.OrdinalIgnoreCase)) ?? FindById("off");
}

public sealed class AudioSystemStatus
{
    public string? DefaultEndpointPath { get; init; }
    public AudioEndpointChoice? DefaultEndpoint { get; init; }
    public bool SpatialAudioAvailable { get; init; }
    public uint MaxDynamicObjects { get; init; }
    public string ActiveSpatialAudioModeId { get; init; } = "off";
    public string DefaultSpatialAudioModeId { get; init; } = "off";
    public string SpatialAudioStatus => SpatialAudioAvailable
        ? $"Available · {MaxDynamicObjects} dynamic objects"
        : "Off or unavailable for this output device";
    public IReadOnlyList<SpatialAudioOption> Options { get; init; } = Array.Empty<SpatialAudioOption>();
}

public static class AudioSystemState
{
    private const uint ClsCtxInprocServer = 0x1;
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid SpatialAudioClientIid = new("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F");

    public static AudioSystemStatus Read(IReadOnlyList<AudioEndpointChoice> endpoints)
    {
        string? defaultPath = null;
        AudioEndpointChoice? defaultEndpoint = null;
        try
        {
            defaultPath = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (!string.IsNullOrWhiteSpace(defaultPath)) defaultPath = AudioEndpointChoice.NormalizeDeviceInterfacePath(defaultPath);
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(defaultPath))
        {
            string? coreAudioEndpointId = ReadDefaultEndpointPathFromCoreAudio();
            if (!string.IsNullOrWhiteSpace(coreAudioEndpointId)) defaultPath = AudioEndpointChoice.NormalizeDeviceInterfacePath(coreAudioEndpointId);
        }
        defaultEndpoint = endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.EndpointPath, defaultPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(defaultPath) && defaultPath.Contains(endpoint.EndpointId, StringComparison.OrdinalIgnoreCase)));

        bool spatialAvailable = false;
        uint maxDynamicObjects = 0;
        string activeSpatialFormat = string.Empty;
        string defaultSpatialFormat = string.Empty;
        if (!string.IsNullOrWhiteSpace(defaultPath))
        {
            TryReadSpatialAudio(AudioEndpointChoice.NormalizeCoreAudioEndpointId(defaultPath), out spatialAvailable, out maxDynamicObjects);
            try
            {
                SpatialAudioDeviceConfiguration configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(defaultPath);
                spatialAvailable = configuration.IsSpatialAudioSupported;
                activeSpatialFormat = configuration.ActiveSpatialAudioFormat ?? string.Empty;
                defaultSpatialFormat = configuration.DefaultSpatialAudioFormat ?? string.Empty;
            }
            catch
            {
            }
        }

        return new AudioSystemStatus
        {
            DefaultEndpointPath = defaultPath,
            DefaultEndpoint = defaultEndpoint,
            SpatialAudioAvailable = spatialAvailable,
            MaxDynamicObjects = maxDynamicObjects,
            ActiveSpatialAudioModeId = SpatialAudioModeCatalog.FindByFormat(activeSpatialFormat).Id,
            DefaultSpatialAudioModeId = SpatialAudioModeCatalog.FindByFormat(defaultSpatialFormat).Id,
            Options = SpatialAudioModeCatalog.Options
        };
    }

    public static async Task<string> SetDefaultSpatialAudioModeAsync(
        string endpointPath,
        string modeId,
        bool apply,
        bool forceRefresh = false)
    {
        SpatialAudioOption option = SpatialAudioModeCatalog.FindById(modeId);
        if (option.Id == "keep") return "Spatial audio format unchanged";
        if (!apply) return $"C# dry-run: spatial format -> {option.Name}";

        try
        {
            SpatialAudioDeviceConfiguration configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(
                AudioEndpointChoice.NormalizeDeviceInterfacePath(endpointPath));
            string currentModeId = SpatialAudioModeCatalog.FindByFormat(configuration.DefaultSpatialAudioFormat ?? string.Empty).Id;
            bool sameMode = string.Equals(currentModeId, option.Id, StringComparison.OrdinalIgnoreCase);
            if (sameMode && !forceRefresh)
            {
                return $"C# spatial format unchanged: {option.Name}; switch API skipped";
            }

            bool refreshed = false;
            if (sameMode && forceRefresh && option.Id != "off")
            {
                // Dolby can accept a new AppService profile while the current
                // Spatial Audio stream keeps the old DSP graph. Recreate that
                // graph by briefly clearing and restoring the same format.
                SetDefaultSpatialAudioFormatResult offResult = await SetSpatialAudioOffAsync(configuration);
                string offDefaultFormat = configuration.DefaultSpatialAudioFormat ?? string.Empty;
                string offActiveFormat = configuration.ActiveSpatialAudioFormat ?? string.Empty;
                for (int attempt = 0; attempt < 3 && !SpatialFormatReadbackMatches(offDefaultFormat, "off"); attempt++)
                {
                    await Task.Delay(100);
                    offDefaultFormat = configuration.DefaultSpatialAudioFormat ?? string.Empty;
                    offActiveFormat = configuration.ActiveSpatialAudioFormat ?? string.Empty;
                }

                if (!SpatialFormatReadbackMatches(offDefaultFormat, "off"))
                {
                    string offDefault = SpatialAudioModeCatalog.FindByFormat(offDefaultFormat).Name;
                    string offActive = SpatialAudioModeCatalog.FindByFormat(offActiveFormat).Name;
                    return $"C# spatial format refresh failed before reactivation: {offResult.Status}; default={offDefault}; active={offActive}";
                }

                refreshed = true;
                await Task.Delay(150);
            }

            // Off has no public SpatialAudioFormatSubtype. Windows stores it as
            // GUID_NULL, so use the documented GUID-shaped value first. Keep the
            // other representations as fallbacks for older Windows builds.
            SetDefaultSpatialAudioFormatResult result = option.Id == "off"
                ? await SetSpatialAudioOffAsync(configuration)
                : await configuration.SetDefaultSpatialAudioFormatAsync(option.FormatSubtype);

            // Windows updates the endpoint configuration asynchronously. Read it
            // back after the call so Off is not reported as failed merely because
            // the UI refreshed before the endpoint had published the new value.
            string defaultFormat = configuration.DefaultSpatialAudioFormat ?? string.Empty;
            string activeFormat = configuration.ActiveSpatialAudioFormat ?? string.Empty;
            for (int attempt = 0; attempt < 3 && !SpatialFormatReadbackMatches(defaultFormat, option.Id); attempt++)
            {
                await Task.Delay(100);
                defaultFormat = configuration.DefaultSpatialAudioFormat ?? string.Empty;
                activeFormat = configuration.ActiveSpatialAudioFormat ?? string.Empty;
            }

            string readbackDefault = SpatialAudioModeCatalog.FindByFormat(defaultFormat).Name;
            string readbackActive = SpatialAudioModeCatalog.FindByFormat(activeFormat).Name;
            if (result.Status == SetDefaultSpatialAudioFormatStatus.AccessDenied)
            {
                return option.Id == "off"
                    ? "C# spatial format Off unavailable: Windows denied clearing the provider-owned spatial format (AccessDenied); the public WinRT API does not expose an Off subtype for this endpoint."
                    : $"C# spatial format {option.Name} unavailable: the provider denied this app (AccessDenied); default={readbackDefault}; active={readbackActive}";
            }

            if (!SpatialFormatReadbackMatches(defaultFormat, option.Id))
            {
                return $"C# spatial format {option.Name} not confirmed after {result.Status}; default={readbackDefault}; active={readbackActive}";
            }

            return refreshed
                ? $"C# spatial format {option.Name} refreshed: off -> {result.Status}; default={readbackDefault}; active={readbackActive}"
                : $"C# spatial format {option.Name}: {result.Status}; default={readbackDefault}; active={readbackActive}";
        }
        catch (Exception ex)
        {
            return $"C# spatial format {option.Name} failed: {ex.Message}";
        }
    }

    private static bool SpatialFormatReadbackMatches(string? format, string modeId)
    {
        if (string.Equals(modeId, "off", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(format) ||
                (Guid.TryParse(format, out Guid parsed) && parsed == Guid.Empty);
        }

        return string.Equals(
            SpatialAudioModeCatalog.FindByFormat(format).Id,
            modeId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SetDefaultSpatialAudioFormatResult> SetSpatialAudioOffAsync(
        SpatialAudioDeviceConfiguration configuration)
    {
        SetDefaultSpatialAudioFormatResult? lastResult = null;
        foreach (string clearValue in new[]
        {
            Guid.Empty.ToString("D"),
            Guid.Empty.ToString("B"),
            string.Empty
        })
        {
            SetDefaultSpatialAudioFormatResult result =
                await configuration.SetDefaultSpatialAudioFormatAsync(clearValue);
            lastResult = result;

            string readback = configuration.DefaultSpatialAudioFormat ?? string.Empty;
            if (result.Status == SetDefaultSpatialAudioFormatStatus.Succeeded &&
                SpatialFormatReadbackMatches(readback, "off"))
            {
                return result;
            }
        }

        return lastResult!;
    }

    private static string? ReadDefaultEndpointPathFromCoreAudio()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        nint id = 0;
        object? rawEnumerator = null;
        try
        {
            rawEnumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid, throwOnError: true)!);
            enumerator = (IMMDeviceEnumerator)rawEnumerator!;
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
            if (hr < 0 || device == null) return null;
            hr = device.GetId(out id);
            if (hr < 0 || id == 0) return null;
            return Marshal.PtrToStringUni(id);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (id != 0) Marshal.FreeCoTaskMem(id);
            ReleaseComObject(device);
            if (enumerator != null) ReleaseComObject(enumerator);
            else ReleaseComObject(rawEnumerator);
        }
    }

    private static bool TryReadSpatialAudio(string endpointPath, out bool available, out uint maxDynamicObjects)
    {
        available = false;
        maxDynamicObjects = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        ISpatialAudioClient? client = null;
        object? rawEnumerator = null;
        try
        {
            rawEnumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid, throwOnError: true)!);
            enumerator = (IMMDeviceEnumerator)rawEnumerator!;
            int getDeviceHr = enumerator.GetDevice(endpointPath, out device);
            if (getDeviceHr < 0 || device == null) return false;

            Guid spatialAudioClientIid = SpatialAudioClientIid;
            int activateHr = device.Activate(ref spatialAudioClientIid, ClsCtxInprocServer, 0, out client);
            if (activateHr < 0 || client == null) return false;

            int objectCountHr = client.GetMaxDynamicObjectCount(out maxDynamicObjects);
            available = objectCountHr >= 0 && maxDynamicObjects > 0;
            return objectCountHr >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(client);
            ReleaseComObject(device);
            if (enumerator != null) ReleaseComObject(enumerator);
            else ReleaseComObject(rawEnumerator);
        }
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
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(nint client);
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsContext, nint activationParams, out ISpatialAudioClient client);
        int OpenPropertyStore(uint access, out nint propertyStore);
        int GetId(out nint id);
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpatialAudioClient
    {
        int GetStaticObjectPosition(uint type, out float x, out float y, out float z);
        int GetNativeStaticObjectTypeMask(out uint mask);
        int GetMaxDynamicObjectCount(out uint value);
        int GetSupportedAudioObjectFormatEnumerator(out nint enumerator);
        int GetMaxFrameCount(nint objectFormat, out uint frameCountPerBuffer);
        int IsAudioObjectFormatSupported(nint objectFormat);
        int IsSpatialAudioStreamAvailable(ref Guid streamUuid, nint auxiliaryInfo);
        int ActivateSpatialAudioStream(nint activationParams, ref Guid iid, out nint stream);
    }
}

public interface IAudioProfileProvider
{
    string Id { get; }
    string DisplayName { get; }
    string DefaultActiveProfile { get; }
    IReadOnlyList<string> SupportedProfiles { get; }
    bool SupportsProfile(string profile);
    Task<bool> TryProbeAsync(AudioEndpointChoice endpoint);
    Task<string> InvokeSetterAsync(string endpointPath, string profile, bool apply);
}

public static class AudioProfileProviderRegistry
{
    public static IReadOnlyList<IAudioProfileProvider> Providers { get; } = new[]
    {
        (IAudioProfileProvider)new DolbyCapxProfileProvider(),
        new DtsSoundUnboundProfileProvider()
    };

    public static IAudioProfileProvider Find(string id) => Providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Providers[0];

    public static IAudioProfileProvider? FindForSpatialAudioMode(string? modeId)
    {
        SpatialAudioOption option = SpatialAudioModeCatalog.FindById(modeId);
        return string.IsNullOrWhiteSpace(option.AudioProfileProviderId)
            ? null
            : Providers.FirstOrDefault(provider => string.Equals(provider.Id, option.AudioProfileProviderId, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task ProbeAllAsync(AudioEndpointChoice endpoint)
    {
        endpoint.Matches.Clear();
        endpoint.ProbeStatus = "Checking providers...";
        foreach (IAudioProfileProvider provider in Providers)
        {
            try
            {
                if (await provider.TryProbeAsync(endpoint).ConfigureAwait(false))
                {
                    endpoint.Matches.Add(new AudioProfileMatch { ProviderId = provider.Id, ProviderName = provider.DisplayName, CurrentProfile = endpoint.Profile });
                }
            }
            catch (Exception ex)
            {
                endpoint.ProbeStatus = $"{provider.DisplayName}: {ex.Message}";
            }
        }
        if (endpoint.Matches.Count == 0 && endpoint.ProbeStatus == "Checking providers...") endpoint.ProbeStatus = "No provider matched";
        if (endpoint.Matches.Count > 0) endpoint.ProbeStatus = $"Matched: {endpoint.MatchedProviders}";
        endpoint.ProbeCompleted = true;
    }
}

public sealed class DolbyCapxProfileProvider : IAudioProfileProvider
{
    private static readonly string[] Profiles = { "Dynamic", "Game", "Movie", "Music", "Voice", "Custom1", "Custom2", "Custom3" };
    private const string DolbyAccessPackageFamilyName = "DolbyLaboratories.DolbyAccess_rz1tebttyb220";
    private const string DolbyAccessAppServiceName = "com.DolbyLaboratories.DolbyAccess.";
    private const string DolbyAtmosForHeadphonesCodec = "{8F3BBD02-6BBE-4B60-9F8B-406837CE466F}";

    public string Id => "dolby-capx";
    public string DisplayName => "Dolby Access / AppService";
    public string DefaultActiveProfile => "Game";
    public IReadOnlyList<string> SupportedProfiles => Profiles;

    public bool SupportsProfile(string profile) => Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> TryProbeAsync(AudioEndpointChoice endpoint)
    {
        try
        {
            string? profile = await ReadRuntimeProfileAsync(endpoint.EndpointPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(profile) || !SupportsProfile(profile)) return false;

            endpoint.Profile = profile;
            endpoint.ProbeStatus = $"Dolby Access AppService / {profile}";
            return true;
        }
        catch
        {
            // Dolby Access can be installed without exposing this endpoint to
            // the AppService. That is a normal non-match.
            return false;
        }
    }

    public async Task<string> InvokeSetterAsync(string endpointPath, string profile, bool apply)
    {
        if (!SupportsProfile(profile)) throw new ArgumentException($"Unsupported spatial audio preset: {profile}", nameof(profile));
        if (!apply) return $"dry-run: Dolby Access AppService profile -> {profile}";

        string actual = await ApplyRuntimeProfileAsync(endpointPath, profile).ConfigureAwait(false);
        return $"Dolby Access AppService profile {profile} applied; runtime readback={actual}";
    }

    private static async Task<string> ApplyRuntimeProfileAsync(string endpointPath, string profile)
    {
        using AppServiceConnection connection = await OpenDolbyAccessAppServiceAsync();
        string profileParameters = BuildProfileParameters(profile);

        await SendDolbyRequestAsync(connection, CreateRequest("SetProfile", endpointPath, profileParameters));
        await SendDolbyRequestAsync(connection, CreateRequest("SyncProfile", endpointPath, profileParameters));
        AppServiceResponse response = await SendDolbyRequestAsync(connection, CreateRequest("GetProfile", endpointPath));
        string actual = ReadProfileType(response);
        if (!string.Equals(actual, profile, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Dolby Access AppService returned profile '{actual}', expected '{profile}'.");
        }

        return actual;
    }

    private static async Task<string?> ReadRuntimeProfileAsync(string endpointPath)
    {
        using AppServiceConnection connection = await OpenDolbyAccessAppServiceAsync();
        AppServiceResponse response = await SendDolbyRequestAsync(connection, CreateRequest("GetProfile", endpointPath));
        return ReadProfileType(response);
    }

    private static async Task<AppServiceConnection> OpenDolbyAccessAppServiceAsync()
    {
        var connection = new AppServiceConnection
        {
            PackageFamilyName = DolbyAccessPackageFamilyName,
            AppServiceName = DolbyAccessAppServiceName
        };

        AppServiceConnectionStatus status = await connection.OpenAsync();
        if (status != AppServiceConnectionStatus.Success)
        {
            connection.Dispose();
            throw new InvalidOperationException($"Dolby Access AppService could not be opened: {status}");
        }

        return connection;
    }

    private static async Task<AppServiceResponse> SendDolbyRequestAsync(AppServiceConnection connection, ValueSet request)
    {
        AppServiceResponse response = await connection.SendMessageAsync(request);
        if (response.Status != AppServiceResponseStatus.Success)
        {
            throw new InvalidOperationException($"Dolby Access AppService request failed: {response.Status}");
        }

        if (response.Message.TryGetValue("ERROR", out object? error))
        {
            throw new InvalidOperationException($"Dolby Access AppService rejected the request: {error}");
        }

        return response;
    }

    private static ValueSet CreateRequest(string command, string endpointPath, string? profileParameters = null)
    {
        var request = new ValueSet
        {
            ["Command"] = command,
            ["DeviceID"] = endpointPath,
            ["MediaCodecName"] = DolbyAtmosForHeadphonesCodec
        };
        if (profileParameters != null) request["ProfileParameters"] = profileParameters;
        return request;
    }

    private static string ReadProfileType(AppServiceResponse response)
    {
        if (!response.Message.TryGetValue("ProfileParameters", out object? value) || value is not string json)
        {
            throw new InvalidOperationException("Dolby Access AppService did not return ProfileParameters.");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("Type", out JsonElement type)
            ? type.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string BuildProfileParameters(string profile)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["IntelligentEqualizerType"] = "Detailed",
            ["CustomEqualizerSettings"] = null,
            ["IsPerformanceMode"] = null,
            ["IsSurroundVirtualizerEnabled"] = null,
            ["IsDialogueEnhancerEnabled"] = null,
            ["IsVolumeLevelerEnabled"] = null,
            ["GamingSubProfile"] = null,
            ["Type"] = profile
        };
        return JsonSerializer.Serialize(parameters);
    }

}

public sealed class DtsSoundUnboundProfileProvider : IAudioProfileProvider
{
    private const string DtsPackageFamilyName = "DTSInc.DTSSoundUnbound_t5j2fzbtdg37r";
    private const string DtsAppId = "App";
    private const uint DesktopPackageActivationOptions = 4 | 16 | 32;
    private static readonly IReadOnlyDictionary<string, string> ProfileBlobs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // These labels are read from the installed Sound Unbound resource
            // catalog: generic Headphone:X plus the partner SAD catalog.
            ["Balanced"] = "02-SPAC-HqHeightAndHgNf_SD1_Hp_Normal_v4_RC2.SPAC.crypt",
            ["Spacious"] = "04-SPAC-HqHeightAndHgNf_SD2_Hp_Normal_v4_RC2.SPAC.crypt",
            ["Gaming: Balanced"] = "2403-GamingBalanced.SPAC.crypt",
            ["Gaming: Neutral"] = "2403-GamingNeutral.SPAC.crypt",
            ["Gaming: Spacious"] = "2403-GamingSpacious.SPAC.crypt",
            ["Gaming: Spacious 2"] = "2403-GamingSpacious2.SPAC.crypt",
            ["Movies: Balanced"] = "2403-MoviesBalanced.SPAC.crypt",
            ["Movies: Spacious"] = "2403-MoviesSpacious.SPAC.crypt"
        };

    public string Id => "dts-sad";
    public string DisplayName => "DTS Sound Unbound / Headphone:X";
    public string DefaultActiveProfile => "Balanced";
    public IReadOnlyList<string> SupportedProfiles => ProfileBlobs.Keys.ToArray();

    public bool SupportsProfile(string profile) =>
        ProfileBlobs.ContainsKey(profile);

    public async Task<bool> TryProbeAsync(AudioEndpointChoice endpoint)
    {
        if (!File.Exists(HelperPath)) return false;

        try
        {
            DtsHelperResult result = await Task.Run(() => InvokeHelper(endpoint.EndpointPath, "-", apply: false)).ConfigureAwait(false);
            if (!result.Supported) return false;
            endpoint.Profile = result.CurrentProfile ?? string.Empty;
            endpoint.ProbeStatus = string.IsNullOrWhiteSpace(result.CurrentProfile)
                ? "C# DTS Sound Unbound / Headphone:X"
                : $"C# DTS Sound Unbound / Headphone:X / {result.CurrentProfile}";
            return true;
        }
        catch
        {
            // DTS can be installed without exposing a generic HPX runtime entry
            // for the current endpoint. That is a normal non-match.
            return false;
        }
    }

    public async Task<string> InvokeSetterAsync(string endpointPath, string profile, bool apply)
    {
        if (!ProfileBlobs.TryGetValue(profile, out string? blobName))
        {
            throw new ArgumentException($"Unsupported DTS spatial audio preset: {profile}", nameof(profile));
        }

        if (!apply)
        {
            return $"C# dry-run: DTS Sound Unbound Headphone:X -> {profile} ({blobName})";
        }

        DtsHelperResult result = await Task.Run(() => InvokeHelper(endpointPath, profile, apply: true)).ConfigureAwait(false);
        if (!result.Supported)
        {
            return "DTS Sound Unbound does not expose generic Headphone:X runtime state for this endpoint; profile was not changed";
        }

        if (!result.Applied)
        {
            return $"DTS Sound Unbound profile {profile} was not confirmed by runtime readback" +
                   (string.IsNullOrWhiteSpace(result.Reason) ? string.Empty : $": {result.Reason}");
        }

        return $"DTS Sound Unbound Headphone:X profile {profile} applied; runtime readback={result.Blob ?? blobName}";
    }

    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "tools", "bin", "DtsSetProfile.exe");

    private static DtsHelperResult InvokeHelper(string endpointPath, string profile, bool apply)
    {
        string token = Guid.NewGuid().ToString("N");
        string outputFile = Path.Combine(Path.GetTempPath(), $"AudioSwitch-Dts-{token}.json");
        try
        {
            string arguments = string.Join(" ",
                QuoteCommandLineArgument(endpointPath),
                QuoteCommandLineArgument(profile),
                QuoteCommandLineArgument(outputFile),
                apply ? QuoteCommandLineArgument("--apply") : string.Empty);

            _ = DesktopPackageActivator.Start(
                DtsPackageFamilyName + "!" + DtsAppId,
                HelperPath,
                arguments);

            WaitForHelper(outputFile);
            if (!File.Exists(outputFile)) throw new IOException("The C# DTS helper did not produce a result.");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputFile));
            JsonElement root = document.RootElement;
            bool hasErrorType = root.TryGetProperty("errorType", out JsonElement errorType);
            bool hasError = root.TryGetProperty("error", out _);
            if (hasErrorType || hasError)
            {
                string message = root.TryGetProperty("message", out JsonElement errorMessage)
                    ? errorMessage.GetString() ?? "unknown error"
                    : root.ToString();
                string type = errorType.ValueKind == JsonValueKind.String ? errorType.GetString() ?? "error" : "error";
                throw new InvalidOperationException($"DTS helper failed ({type}): {message}");
            }

            return new DtsHelperResult(
                root.TryGetProperty("supported", out JsonElement supported) && supported.GetBoolean(),
                root.TryGetProperty("applied", out JsonElement applied) && applied.GetBoolean(),
                ReadOptionalString(root, "currentProfile"),
                ReadOptionalString(root, "currentBlob"),
                ReadOptionalString(root, "profile"),
                ReadOptionalString(root, "blob"),
                ReadOptionalString(root, "reason"));
        }
        finally
        {
            try { if (File.Exists(outputFile)) File.Delete(outputFile); } catch { }
        }
    }

    private static void WaitForHelper(string outputFile)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 0) return;
            Thread.Sleep(100);
        }

        throw new TimeoutException("The C# DTS helper timed out.");
    }

    private static string QuoteCommandLineArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record DtsHelperResult(
        bool Supported,
        bool Applied,
        string? CurrentProfile,
        string? CurrentBlob,
        string? Profile,
        string? Blob,
        string? Reason);

    private static class DesktopPackageActivator
    {
        [ComImport]
        [Guid("168EB462-775F-42AE-9111-D714B2306C2E")]
        private sealed class ActivatorClass
        {
        }

        [ComImport]
        [Guid("F158268A-D5A5-45CE-99CF-00D6C3F3FC0A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopAppXActivator
        {
            void Activate(
                [MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string executable,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments,
                out uint processId);

            void ActivateWithOptions(
                [MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string executable,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments,
                uint options,
                uint parentProcessId,
                out uint processId);
        }

        public static uint Start(string applicationUserModelId, string executable, string arguments)
        {
            object activatorObject = new ActivatorClass();
            IDesktopAppXActivator activator = (IDesktopAppXActivator)activatorObject;
            activator.ActivateWithOptions(
                applicationUserModelId,
                executable,
                arguments,
                DesktopPackageActivationOptions,
                0,
                out uint processId);
            return processId;
        }
    }
}
