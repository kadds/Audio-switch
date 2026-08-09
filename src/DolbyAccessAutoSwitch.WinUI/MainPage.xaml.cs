using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WinRT.Interop;

namespace DolbyAccessAutoSwitch_WinUI;

public sealed partial class MainPage : Page, IDisposable
{
    private const long MaxLogFileBytes = 4 * 1024 * 1024;
    private readonly object logFileLock = new();
    private sealed record AudioRestoreCheckpoint(
        string? EndpointPath,
        string SpatialAudioModeId,
        float? VolumePercent);

    private sealed record ProcessRuleSnapshot(
        bool ForegroundOnly,
        int? PriorityOverride,
        float? VolumePercent,
        string? EndpointPath,
        string ActiveProfile,
        string ActiveSpatialAudioModeId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private readonly ObservableCollection<ProcessChoice> processChoices = new();
    private readonly ObservableCollection<AudioEndpointChoice> endpoints = new();
    private readonly ObservableCollection<string> logEntries = new();
    private readonly Dictionary<string, ProcessSwitchItem> processConfigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> temporarilyDisabledRuleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> appliedProcessEndpointPaths = new();
    private readonly Dictionary<string, Task> endpointProbeTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessSwitchItem> activeProcessRules = new();
    private readonly DispatcherQueueTimer monitorTimer;
    private readonly DispatcherQueueTimer audioRefreshTimer;
    private readonly DispatcherQueueTimer notificationHideTimer;
    private readonly MenuFlyout processContextFlyout;
    private readonly MenuFlyoutItem copyProcessRuleMenuItem;
    private readonly MenuFlyoutItem pasteProcessRuleMenuItem;
    private readonly MenuFlyoutItem toggleProcessRuleMenuItem;
    private SwitchConfig config;
    private AudioSystemChangeMonitor? audioSystemChangeMonitor;
    private string pendingAudioChangeReason = "Audio system changed";
    private bool pageInitialized;
    private bool monitoring;
    private bool suppressOutputDeviceSelection;
    private bool suppressGlobalVolumeSelection;
    private CancellationTokenSource? globalVolumeApplyCts;
    private bool suppressSpatialAudioSelection;
    private bool suppressLanguageSelection;
    private bool suppressNotificationSelection;
    private bool audioStateInitialized;
    private string lastDefaultEndpointPath = string.Empty;
    private string lastDefaultSpatialAudioModeId = string.Empty;
    private ProcessRuleSnapshot? copiedProcessRule;
    private readonly ConcurrentDictionary<string, byte> runningOperations = new(StringComparer.OrdinalIgnoreCase);
    private string activeGlobalSettingsSignature = string.Empty;
    private string lastMonitorDecisionSignature = string.Empty;
    private AudioRestoreCheckpoint? exitRestoreCheckpoint;
    private int exitRestoreStarted;

    public MainPage()
    {
        InitializeComponent();
        processContextFlyout = new MenuFlyout();
        copyProcessRuleMenuItem = new MenuFlyoutItem();
        copyProcessRuleMenuItem.Click += CopyProcessRuleMenuItem_Click;
        pasteProcessRuleMenuItem = new MenuFlyoutItem();
        pasteProcessRuleMenuItem.Click += PasteProcessRuleMenuItem_Click;
        toggleProcessRuleMenuItem = new MenuFlyoutItem();
        toggleProcessRuleMenuItem.Click += ToggleProcessRuleMenuItem_Click;
        processContextFlyout.Items.Add(copyProcessRuleMenuItem);
        processContextFlyout.Items.Add(pasteProcessRuleMenuItem);
        processContextFlyout.Items.Add(new MenuFlyoutSeparator());
        processContextFlyout.Items.Add(toggleProcessRuleMenuItem);
        AppPaths.EnsureDataDirectories();
        config = SwitchConfig.Load(AppPaths.ConfigPath);
        ApplyLocalization();
        ApplyLanguageSelection();
        CaptureExitRestoreCheckpoint();

        ProcessListView.ItemsSource = processChoices;
        RemoveProcessButton.IsEnabled = false;
        UpdateProcessEmptyState();
        AllEndpointsListView.ItemsSource = endpoints;
        monitorTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        monitorTimer.Tick += MonitorTimer_Tick;
        audioRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        audioRefreshTimer.Interval = TimeSpan.FromMilliseconds(250);
        audioRefreshTimer.Tick += AudioRefreshTimer_Tick;
        notificationHideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        notificationHideTimer.Interval = TimeSpan.FromSeconds(3.5);
        notificationHideTimer.Tick += NotificationHideTimer_Tick;
        ApplyNotificationSelection();
        Loaded += MainPage_Loaded;
    }

    private void CaptureExitRestoreCheckpoint()
    {
        if (exitRestoreCheckpoint != null) return;

        try
        {
            AudioSystemStatus audio = AudioSystemState.Read(Array.Empty<AudioEndpointChoice>());
            float? volume = null;
            if (AudioVolumeController.TryGetEndpointVolume(null, out float currentVolume, out _)) volume = currentVolume;
            exitRestoreCheckpoint = new AudioRestoreCheckpoint(
                audio.DefaultEndpointPath,
                string.IsNullOrWhiteSpace(audio.DefaultSpatialAudioModeId) ? "off" : audio.DefaultSpatialAudioModeId,
                volume);
        }
        catch
        {
            exitRestoreCheckpoint = new AudioRestoreCheckpoint(null, string.Empty, null);
        }
    }

    private void ApplyLocalization()
    {
        MonitorButtonText.Text = Localization.Text("MainPage_StartMonitoring");
        RulesTabTextBlock.Text = Localization.Text("MainPage_TabRules");
        OutputTabTextBlock.Text = Localization.Text("MainPage_TabOutput");
        SpatialTabTextBlock.Text = Localization.Text("MainPage_TabSpatial");
        GeneralTabTextBlock.Text = Localization.Text("MainPage_TabGeneral");
        NotificationsTabTextBlock.Text = Localization.Text("MainPage_TabNotifications");
        ProcessTitleTextBlock.Text = Localization.Text("MainPage_ProcessTitle");
        ProcessDescriptionTextBlock.Text = Localization.Text("MainPage_ProcessDescription");
        ProcessEmptyTitleTextBlock.Text = Localization.Text("MainPage_ProcessEmptyTitle");
        ProcessEmptyHintTextBlock.Text = Localization.Text("MainPage_ProcessEmptyHint");
        OutputCurrentLabelTextBlock.Text = Localization.Text("MainPage_CurrentOutputLabel");
        OutputVolumeLabelTextBlock.Text = Localization.Text("MainPage_OutputVolume");
        OutputVolumeHintTextBlock.Text = Localization.Text("MainPage_OutputVolumeHint");
        CurrentOutputDeviceText.Text = Localization.Text("MainPage_ReadingOutput");
        OutputTitleTextBlock.Text = Localization.Text("MainPage_OutputTitle");
        OutputDevicesStatusText.Text = Localization.Text("MainPage_OutputStatus");
        SpatialTitleTextBlock.Text = Localization.Text("MainPage_SpatialTitle");
        SpatialCurrentLabelTextBlock.Text = Localization.Text("MainPage_CurrentOutputLabel");
        SpatialAudioCurrentOutputText.Text = Localization.Text("MainPage_ReadingOutput");
        SpatialOptionsTextBlock.Text = Localization.Text("MainPage_SpatialOptions");
        GeneralTitleTextBlock.Text = Localization.Text("MainPage_GeneralTitle");
        LanguageLabelTextBlock.Text = Localization.Text("MainPage_LanguageLabel");
        ((ComboBoxItem)LanguageComboBox.Items[0]).Content = Localization.Content("Language_System");
        ((ComboBoxItem)LanguageComboBox.Items[1]).Content = Localization.Content("Language_Chinese");
        ((ComboBoxItem)LanguageComboBox.Items[2]).Content = Localization.Content("Language_English");
        LanguageHintTextBlock.Text = Localization.Text("MainPage_LanguageHint");
        StartWithWindowsCheckBox.Content = Localization.Content("MainPage_StartWithWindows");
        StartSilentCheckBox.Content = Localization.Content("MainPage_StartSilent");
        StartMonitorCheckBox.Content = Localization.Content("MainPage_StartMonitor");
        VolumeProtectionCheckBox.Content = Localization.Content("MainPage_VolumeProtection");
        VolumeProtectionHintTextBlock.Text = Localization.Text("MainPage_VolumeProtectionHint");
        IntervalLabelTextBlock.Text = Localization.Text("MainPage_IntervalLabel");
        IntervalHintTextBlock.Text = Localization.Text("MainPage_IntervalHint");
        TrayHintTextBlock.Text = Localization.Text("MainPage_TrayHint");
        LogExportTitleTextBlock.Text = Localization.Text("MainPage_LogExportTitle");
        LogExportHintTextBlock.Text = $"{Localization.Text("MainPage_LogExportHint")}\n{AppPaths.LogPath}";
        ExportLogTextBlock.Text = Localization.Text("MainPage_ExportLog");
        copyProcessRuleMenuItem.Text = Localization.Text("MainPage_CopyRule");
        pasteProcessRuleMenuItem.Text = Localization.Text("MainPage_PasteRule");
        NotificationsTitleTextBlock.Text = Localization.Text("MainPage_NotificationsTitle");
        NotificationsHintTextBlock.Text = Localization.Text("MainPage_NotificationsHint");
        OutputNotificationTitleTextBlock.Text = Localization.Text("MainPage_OutputNotificationTitle");
        OutputNotificationLabelTextBlock.Text = Localization.Text("MainPage_OutputNotificationLabel");
        SpatialNotificationTitleTextBlock.Text = Localization.Text("MainPage_SpatialNotificationTitle");
        SpatialNotificationLabelTextBlock.Text = Localization.Text("MainPage_SpatialNotificationLabel");
        NotificationModeHintTextBlock.Text = Localization.Text("MainPage_NotificationModeHint");
        SetNotificationModeText(OutputNotificationModeComboBox);
        SetNotificationModeText(SpatialNotificationModeComboBox);
        ToolTipService.SetToolTip(ProcessRulesTabButton, Localization.Get("MainPage_TabRules.ToolTip"));
        ToolTipService.SetToolTip(OutputDevicesTabButton, Localization.Get("MainPage_TabOutput.ToolTip"));
        ToolTipService.SetToolTip(SpatialAudioTabButton, Localization.Get("MainPage_TabSpatial.ToolTip"));
        ToolTipService.SetToolTip(GeneralTabButton, Localization.Get("MainPage_TabGeneral.ToolTip"));
        ToolTipService.SetToolTip(NotificationsTabButton, Localization.Get("MainPage_TabNotifications.ToolTip"));
        ToolTipService.SetToolTip(AddProcessButton, Localization.Get("MainPage_AddProcess.ToolTip"));
        ToolTipService.SetToolTip(RemoveProcessButton, Localization.Get("MainPage_RemoveProcess.ToolTip"));
        ToolTipService.SetToolTip(SelectProcessButton, Localization.Get("MainPage_SelectProcess.ToolTip"));
        ToolTipService.SetToolTip(OpenSelectButton, Localization.Get("MainPage_OpenExe.ToolTip"));
        ToolTipService.SetToolTip(ExportLogButton, Localization.Get("MainPage_ExportLog.ToolTip"));
        copyProcessRuleMenuItem.Text = Localization.Text("MainPage_CopyRule");
        pasteProcessRuleMenuItem.Text = Localization.Text("MainPage_PasteRule");
        UpdateProcessContextMenuState();
        UpdateProcessContextMenuState();
        ToolTipService.SetToolTip(DismissInAppNotificationButton, Localization.Get("Notification_Dismiss.ToolTip"));
    }

    private static void SetNotificationModeText(ComboBox comboBox)
    {
        ((ComboBoxItem)comboBox.Items[0]).Content = Localization.Content("Notification_None");
        ((ComboBoxItem)comboBox.Items[1]).Content = Localization.Content("Notification_System");
        ((ComboBoxItem)comboBox.Items[2]).Content = Localization.Content("Notification_InApp");
    }

    private void ApplyNotificationSelection()
    {
        suppressNotificationSelection = true;
        SetNotificationModeSelection(OutputNotificationModeComboBox, config.OutputNotificationMode);
        SetNotificationModeSelection(SpatialNotificationModeComboBox, config.SpatialNotificationMode);
        suppressNotificationSelection = false;
    }

    private static void SetNotificationModeSelection(ComboBox comboBox, string? mode)
    {
        for (int index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void ApplyLanguageSelection()
    {
        suppressLanguageSelection = true;
        LanguageComboBox.SelectedIndex = config.UiLanguage switch
        {
            "zh-CN" => 1,
            "en-US" => 2,
            _ => 0
        };
        suppressLanguageSelection = false;
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressLanguageSelection ||
            LanguageComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string languageSetting ||
            string.Equals(config.UiLanguage, languageSetting, StringComparison.OrdinalIgnoreCase)) return;

        config.UiLanguage = languageSetting;
        SaveConfig();
        Localization.Initialize(config.UiLanguage);
        ApplyLocalization();
        ApplyLanguageSelection();
        ApplyNotificationSelection();
        (Application.Current as App)?.RefreshTrayLocalization();

        ProcessChoice? selectedProcess = ProcessListView.SelectedItem as ProcessChoice;
        ProcessListView.ItemsSource = null;
        ProcessListView.ItemsSource = processChoices;
        ProcessListView.SelectedItem = selectedProcess;
        if (ProfileEditorHost.Content is ProcessProfileView view) view.RefreshLocalization();
        SpatialAudioOptionsListView.ItemsSource = null;
        SpatialAudioOptionsListView.ItemsSource = SpatialAudioModeCatalog.Options;
        if (pageInitialized) _ = RefreshAudioStateAsync();
    }

    private void NotificationMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressNotificationSelection || sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;

        if (ReferenceEquals(comboBox, OutputNotificationModeComboBox)) config.OutputNotificationMode = mode;
        else if (ReferenceEquals(comboBox, SpatialNotificationModeComboBox)) config.SpatialNotificationMode = mode;
        else return;

        SaveConfig();
    }

    public bool StartHiddenRequested(string launchArguments)
    {
        return config.StartSilent || launchArguments.Contains("--silent", StringComparison.OrdinalIgnoreCase);
    }

    public void StartConfiguredMonitor()
    {
        if (config.StartMonitor) StartMonitoring();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (pageInitialized) return;
        pageInitialized = true;
        StatusText.Text = Localization.Value("Status_Ready");

        // The process list contains only user-created rules; global defaults live in the Output and Spatial tabs.
        bool processMetadataChanged = config.Processes.RemoveAll(item => string.Equals(item.Name, "__default__", StringComparison.OrdinalIgnoreCase)) > 0;
        foreach (ProcessSwitchItem item in config.Processes.ToList())
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                config.Processes.Remove(item);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.RuleId))
            {
                item.RuleId = Guid.NewGuid().ToString("N");
                processMetadataChanged = true;
            }

            if (processConfigs.ContainsKey(item.RuleId))
            {
                config.Processes.Remove(item);
                continue;
            }

            processConfigs[item.RuleId] = item;
            ProcessChoice? runningChoice = item.MatchMode == ProcessMatchMode.FullPath && string.IsNullOrWhiteSpace(item.ExecutablePath)
                ? null
                : FindProcessChoice(item.Name, item.MatchMode == ProcessMatchMode.FullPath ? item.ExecutablePath : null);
            if (runningChoice != null)
            {
                if (!string.Equals(item.ExecutablePath, runningChoice.FilePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.Description, runningChoice.Description, StringComparison.Ordinal) ||
                    !string.Equals(item.IconPath, runningChoice.IconPath, StringComparison.OrdinalIgnoreCase)) processMetadataChanged = true;
                item.ExecutablePath = runningChoice.FilePath;
                item.Description = runningChoice.Description;
                item.IconPath = runningChoice.IconPath;
            }
            ProcessChoice processChoice = runningChoice ?? ProcessChoice.FromStored(item);
            processChoice.RuleId = item.RuleId;
            processChoices.Add(processChoice);
        }

        if (processMetadataChanged) SaveConfig();
        UpdateProcessEmptyState();

        try
        {
            audioSystemChangeMonitor = new AudioSystemChangeMonitor(AudioSystemChanged, Log);
        }
        catch (Exception ex)
        {
            Log("Audio listener initialization failed: " + ex.Message);
        }

        StartWithWindowsCheckBox.IsChecked = config.StartWithWindows;
        StartSilentCheckBox.IsChecked = config.StartSilent;
        StartMonitorCheckBox.IsChecked = config.StartMonitor;
        VolumeProtectionCheckBox.IsChecked = config.VolumeProtectionEnabled;
        MonitorIntervalNumberBox.Value = Math.Clamp(config.IntervalSeconds, 1, 60);

        ProfileEditorHost.Content = new TextBlock
        {
            Text = Localization.Text("MainPage_ProfileEmpty"),
            Opacity = 0.68,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        await LoadEndpointsAsync();
        try
        {
            audioSystemChangeMonitor?.Start();
            Log("Audio device and spatial audio listeners enabled.");
        }
        catch (Exception ex)
        {
            Log("Audio listener registration failed: " + ex.Message);
        }
        if (processChoices.Count > 0) ProcessListView.SelectedIndex = 0;
        Log("WinUI 3 interface ready.");
        StartConfiguredMonitor();
    }

    private async Task LoadEndpointsAsync()
    {
        await RefreshEndpointsAsync(null);
    }

    private async Task RefreshEndpointsAsync(string? changeReason)
    {
        try
        {
            StatusText.Text = changeReason == null
                ? Localization.Value("Status_LoadingOutputs")
                : Localization.FormatValue("Status_RefreshingAudio", changeReason);
            double? endpointScrollOffset = ReadVerticalScrollOffset(AllEndpointsListView);
            List<AudioEndpointChoice> loaded = await Task.Run(AudioEndpointChoice.ReadAll);
            endpoints.Clear();
            foreach (AudioEndpointChoice endpoint in loaded) endpoints.Add(endpoint);
            lock (endpointProbeTasks) endpointProbeTasks.Clear();
            Log($"Audio endpoint scan returned {loaded.Count} active render endpoints.");
            RefreshProfileEditorEndpoints();
            await RefreshAudioStateAsync();
            RestoreVerticalScrollOffset(AllEndpointsListView, endpointScrollOffset);
            StatusText.Text = Localization.FormatValue("Status_OutputsFound", endpoints.Count);
            if (changeReason != null) Log($"{changeReason}; output and spatial audio lists refreshed.");
        }
        catch (Exception ex)
        {
            StatusText.Text = Localization.Value("Status_OutputScanFailed");
            Log("Output device scan failed: " + ex.Message);
        }
    }

    private async Task RefreshAudioStateAsync()
    {
        AudioSystemStatus audio = await Task.Run(() => AudioSystemState.Read(endpoints.ToList()));
        string endpointPath = audio.DefaultEndpointPath ?? string.Empty;
        string spatialModeId = string.IsNullOrWhiteSpace(audio.DefaultSpatialAudioModeId)
            ? "off"
            : audio.DefaultSpatialAudioModeId;
        bool outputChanged = audioStateInitialized && !string.Equals(lastDefaultEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase);
        bool spatialChanged = audioStateInitialized && !string.Equals(lastDefaultSpatialAudioModeId, spatialModeId, StringComparison.OrdinalIgnoreCase);
        lastDefaultEndpointPath = endpointPath;
        lastDefaultSpatialAudioModeId = spatialModeId;
        audioStateInitialized = true;

        bool globalSettingsChanged = false;
        string globalEndpointFile = EndpointFileForGlobalDefaults();
        if (!string.IsNullOrWhiteSpace(endpointPath) &&
            (!File.Exists(globalEndpointFile) || !string.Equals(config.GlobalEndpointFile, globalEndpointFile, StringComparison.OrdinalIgnoreCase)))
        {
            File.WriteAllText(globalEndpointFile, endpointPath, Encoding.UTF8);
            config.GlobalEndpointFile = globalEndpointFile;
            globalSettingsChanged = true;
        }

        if (string.IsNullOrWhiteSpace(config.GlobalSpatialAudioModeId))
        {
            config.GlobalSpatialAudioModeId = spatialModeId;
            IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(spatialModeId);
            config.GlobalActiveProfile = provider?.DefaultActiveProfile ?? string.Empty;
            globalSettingsChanged = true;
        }

        string currentOutput = audio.DefaultEndpoint?.DisplayName ?? Localization.Value("Status_Unavailable");
        if (audio.DefaultEndpoint != null && !string.IsNullOrWhiteSpace(audio.DefaultEndpoint.Details))
        {
            currentOutput += $"\n{audio.DefaultEndpoint.Details}";
        }

        CurrentOutputDeviceText.Text = currentOutput;
        SpatialAudioCurrentOutputText.Text = currentOutput;
        float endpointVolumePercent = 0;
        string volumeError = string.Empty;
        bool volumeRead = !string.IsNullOrWhiteSpace(endpointPath) && AudioVolumeController.TryGetEndpointVolume(null, out endpointVolumePercent, out volumeError);
        suppressGlobalVolumeSelection = true;
        float volumeMaximum = VolumeSafety.MaximumPercent(config.VolumeProtectionEnabled);
        GlobalVolumeSlider.Maximum = volumeMaximum;
        // A read failure must not make the control unusable. Core Audio can still
        // accept a write for some virtual/driver-managed endpoints; use the last
        // saved value until a fresh read becomes available.
        GlobalVolumeSlider.IsEnabled = !string.IsNullOrWhiteSpace(endpointPath);
        if (volumeRead)
        {
            float safeVolume = VolumeSafety.Clamp(endpointVolumePercent, config.VolumeProtectionEnabled);
            if (safeVolume < endpointVolumePercent)
            {
                string protectionResult = AudioVolumeController.SetEndpointVolume(null, safeVolume, apply: true);
                Log($"Volume protection capped {endpointVolumePercent:0}% -> {safeVolume:0}%: {protectionResult}");
                if (protectionResult.StartsWith("C# endpoint volume set", StringComparison.OrdinalIgnoreCase))
                {
                    endpointVolumePercent = safeVolume;
                }
            }

            GlobalVolumeSlider.Value = endpointVolumePercent;
            OutputVolumeValueTextBlock.Text = $"{endpointVolumePercent:0}%";
            Log($"Endpoint volume read -> {endpointVolumePercent:0}%; endpoint={endpointPath}.");
            float? safeConfiguredVolume = VolumeSafety.Clamp(config.GlobalVolumePercent, config.VolumeProtectionEnabled);
            if (!config.GlobalVolumePercent.HasValue || config.GlobalVolumePercent.Value != safeConfiguredVolume)
            {
                config.GlobalVolumePercent = VolumeSafety.Clamp(endpointVolumePercent, config.VolumeProtectionEnabled);
                globalSettingsChanged = true;
            }
        }
        else
        {
            if (config.GlobalVolumePercent is float savedVolume)
            {
                float safeSavedVolume = VolumeSafety.Clamp(savedVolume, config.VolumeProtectionEnabled);
                GlobalVolumeSlider.Value = safeSavedVolume;
                OutputVolumeValueTextBlock.Text = $"{safeSavedVolume:0}%";
                if (safeSavedVolume != savedVolume)
                {
                    config.GlobalVolumePercent = safeSavedVolume;
                    globalSettingsChanged = true;
                }
            }
            else
            {
                OutputVolumeValueTextBlock.Text = "--%";
            }
            Log($"Endpoint volume read failed: {volumeError}; endpoint={endpointPath ?? "Unavailable"}.");
        }
        suppressGlobalVolumeSelection = false;
        if (globalSettingsChanged) SaveConfig();
        string activeSpatialMode = SpatialAudioModeCatalog.FindById(audio.ActiveSpatialAudioModeId).Name;
        string defaultSpatialMode = SpatialAudioModeCatalog.FindById(audio.DefaultSpatialAudioModeId).Name;
        SpatialAudioStatusText.Text = Localization.FormatValue(
            "Status_SpatialSupport",
            audio.SpatialAudioAvailable ? Localization.Value("Status_Yes") : Localization.Value("Status_No"),
            activeSpatialMode,
            defaultSpatialMode,
            audio.MaxDynamicObjects);
        audioSystemChangeMonitor?.SetSpatialAudioEndpoint(audio.DefaultEndpointPath);
        Log($"Audio state read: default={audio.DefaultEndpoint?.DisplayName ?? "Unavailable"}; endpoint={audio.DefaultEndpointPath ?? "Unavailable"}; spatial default={defaultSpatialMode}; active={activeSpatialMode}; supported={audio.SpatialAudioAvailable}.");
        suppressOutputDeviceSelection = true;
        AllEndpointsListView.SelectedItem = audio.DefaultEndpoint;
        suppressOutputDeviceSelection = false;

        suppressSpatialAudioSelection = true;
        SpatialAudioOptionsListView.ItemsSource = audio.Options;
        // Select the configured default. ActiveSpatialAudioFormat can remain on the
        // format of an already-running stream, which made Off look like it failed.
        SpatialAudioOptionsListView.SelectedItem = audio.Options.FirstOrDefault(option => string.Equals(option.Id, audio.DefaultSpatialAudioModeId, StringComparison.OrdinalIgnoreCase));
        suppressSpatialAudioSelection = false;

        if (outputChanged)
        {
            NotifyAudioChange("output", audio.DefaultEndpoint?.DisplayName ?? Localization.Value("Status_Unavailable"));
        }

        if (spatialChanged)
        {
            NotifyAudioChange("spatial", SpatialAudioModeCatalog.FindById(spatialModeId).Name);
        }
    }

    private void NotifyAudioChange(string changeType, string value)
    {
        bool isOutput = string.Equals(changeType, "output", StringComparison.OrdinalIgnoreCase);
        string mode = isOutput ? config.OutputNotificationMode : config.SpatialNotificationMode;
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase)) return;

        string title = isOutput
            ? Localization.Value("Notification_OutputChangedTitle")
            : Localization.Value("Notification_SpatialChangedTitle");
        string body = value;

        if (string.Equals(mode, "system", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryShowSystemNotification(title, body)) ShowInAppNotification(title, body);
            return;
        }

        if (string.Equals(mode, "in-app", StringComparison.OrdinalIgnoreCase)) ShowInAppNotification(title, body);
    }

    private bool TryShowSystemNotification(string title, string body)
    {
        try
        {
            var xml = new XmlDocument();
            string safeTitle = SecurityElement.Escape(title) ?? string.Empty;
            string safeBody = SecurityElement.Escape(body) ?? string.Empty;
            xml.LoadXml($"<toast><visual><binding template=\"ToastGeneric\"><text>{safeTitle}</text><text>{safeBody}</text></binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception ex)
        {
            Log("System notification failed; using in-app notification: " + ex.Message);
            return false;
        }
    }

    private void ShowInAppNotification(string title, string body)
    {
        InAppNotificationTitleTextBlock.Text = title;
        InAppNotificationBodyTextBlock.Text = body;
        InAppNotificationBanner.Visibility = Visibility.Visible;
        notificationHideTimer.Stop();
        notificationHideTimer.Start();
    }

    private void NotificationHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        notificationHideTimer.Stop();
        InAppNotificationBanner.Visibility = Visibility.Collapsed;
    }

    private void DismissInAppNotification_Click(object sender, RoutedEventArgs e)
    {
        notificationHideTimer.Stop();
        InAppNotificationBanner.Visibility = Visibility.Collapsed;
    }

    private void AudioSystemChanged(string reason)
    {
        if (!pageInitialized) return;
        Log($"Audio listener callback received: {reason}; refresh scheduled in 250 ms.");
        DispatcherQueue.TryEnqueue(() =>
        {
            pendingAudioChangeReason = reason;
            audioRefreshTimer.Stop();
            audioRefreshTimer.Start();
        });
    }

    private async void AudioRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        audioRefreshTimer.Stop();
        string reason = pendingAudioChangeReason;
        pendingAudioChangeReason = "Audio system changed";
        Log($"Audio refresh started: {reason}.");
        await RefreshEndpointsAsync(reason);
    }

    private async void AllEndpointsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressOutputDeviceSelection || AllEndpointsListView.SelectedItem is not AudioEndpointChoice endpoint) return;
        if (string.Equals(endpoint.EndpointPath, lastDefaultEndpointPath, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Default output unchanged; skipped switch API for {endpoint.DisplayName}.");
            return;
        }

        UpdateGlobalOutput(endpoint.EndpointPath);
        string result = await Task.Run(() => ProcessAudioRouter.SetSystemDefaultOutputDevice(endpoint.EndpointPath, apply: true));
        Log(result);
        if (config.GlobalVolumePercent is float globalVolume)
        {
            Log(await Task.Run(() => AudioVolumeController.SetEndpointVolume(
                null,
                VolumeSafety.Clamp(globalVolume, config.VolumeProtectionEnabled),
                apply: true)));
        }
        await RefreshAudioStateAsync();
    }

    private async void GlobalVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (suppressGlobalVolumeSelection || double.IsNaN(e.NewValue) || string.IsNullOrWhiteSpace(lastDefaultEndpointPath)) return;

        float percent = VolumeSafety.Clamp((float)Math.Clamp(e.NewValue, 0, 100), config.VolumeProtectionEnabled);
        OutputVolumeValueTextBlock.Text = $"{percent:0}%";
        globalVolumeApplyCts?.Cancel();
        globalVolumeApplyCts?.Dispose();
        var applyCts = new CancellationTokenSource();
        globalVolumeApplyCts = applyCts;
        try
        {
            // Slider drag raises many ValueChanged events. Debounce them so an
            // older async write cannot finish after the final thumb position.
            await Task.Delay(80, applyCts.Token);
            applyCts.Token.ThrowIfCancellationRequested();
            string result = AudioVolumeController.SetEndpointVolume(null, percent, apply: true);
            if (applyCts.IsCancellationRequested) return;
            Log(result);
            OutputVolumeHintTextBlock.Text = result;
            if (result.StartsWith("C# endpoint volume set", StringComparison.OrdinalIgnoreCase))
            {
                config.GlobalVolumePercent = percent;
                SaveConfig();
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) return;
            Log($"Endpoint volume switch failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(globalVolumeApplyCts, applyCts))
            {
                globalVolumeApplyCts = null;
                applyCts.Dispose();
            }
        }
    }

    private async void SpatialAudioOptionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSpatialAudioSelection || SpatialAudioOptionsListView.SelectedItem is not SpatialAudioOption option) return;

        AudioSystemStatus audio = await Task.Run(() => AudioSystemState.Read(endpoints.ToList()));
        if (string.IsNullOrWhiteSpace(audio.DefaultEndpointPath))
        {
            Log(Localization.Get("Log_NoDefaultOutputDevice"));
            return;
        }
        if (string.Equals(option.Id, audio.DefaultSpatialAudioModeId, StringComparison.OrdinalIgnoreCase))
        {
            UpdateGlobalSpatialAudio(option.Id);
            Log($"Spatial audio unchanged; skipped switch API for {option.Name}.");
            return;
        }

        string result = await AudioSystemState.SetDefaultSpatialAudioModeAsync(audio.DefaultEndpointPath, option.Id, apply: true);
        UpdateGlobalSpatialAudio(option.Id);
        Log(result);
        await RefreshAudioStateAsync();
    }

    private void UpdateGlobalOutput(string endpointPath)
    {
        if (string.IsNullOrWhiteSpace(endpointPath)) return;

        string endpointFile = EndpointFileForGlobalDefaults();
        File.WriteAllText(endpointFile, endpointPath, Encoding.UTF8);
        config.GlobalEndpointFile = endpointFile;
        SaveConfig();
    }

    private void UpdateGlobalSpatialAudio(string modeId)
    {
        if (string.Equals(modeId, "keep", StringComparison.OrdinalIgnoreCase)) return;

        config.GlobalSpatialAudioModeId = modeId;
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId);
        config.GlobalActiveProfile = provider?.DefaultActiveProfile ?? string.Empty;
        SaveConfig();
    }

    private void RefreshProfileEditorEndpoints()
    {
        if (ProfileEditorHost.Content is ProcessProfileView view)
        {
            view.SetEndpoints(endpoints);
            if (view.SelectedEndpoint is { IsKeepCurrent: false } endpoint) ProbeEndpointForView(view, endpoint);
        }
    }

    private void ProcessListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessListView.SelectedItem is not ProcessChoice choice || !processConfigs.TryGetValue(choice.RuleId, out ProcessSwitchItem? model))
        {
            RemoveProcessButton.IsEnabled = false;
            return;
        }

        RemoveProcessButton.IsEnabled = true;
        ShowProcessProfile(choice, model);
    }

    private void ShowProcessProfile(ProcessChoice choice, ProcessSwitchItem model)
    {
        var view = new ProcessProfileView(model, ReadEndpointPath(model.EndpointFile));
        bool volumeChanged = view.SetVolumeProtection(config.VolumeProtectionEnabled);
        view.SetEndpoints(endpoints);
        view.SettingsChanged += ProfileView_SettingsChanged;
        view.TestActiveRequested += ProfileView_TestActiveRequested;
        ProfileEditorHost.Content = view;
        if (volumeChanged) SaveConfig();
        if (view.SelectedEndpoint is { IsKeepCurrent: false } endpoint) ProbeEndpointForView(view, endpoint);
    }

    private void ProcessListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not ListViewItem container ||
            container.Content is not ProcessChoice choice ||
            !processConfigs.ContainsKey(choice.RuleId)) return;

        ProcessListView.SelectedItem = choice;
        UpdateProcessContextMenuState();
        processContextFlyout.ShowAt(ProcessListView, e.GetPosition(ProcessListView));
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void UpdateProcessContextMenuState()
    {
        copyProcessRuleMenuItem.IsEnabled = ProcessListView.SelectedItem is ProcessChoice choice && processConfigs.ContainsKey(choice.RuleId);
        pasteProcessRuleMenuItem.IsEnabled = copiedProcessRule != null && copyProcessRuleMenuItem.IsEnabled;
        toggleProcessRuleMenuItem.IsEnabled = copyProcessRuleMenuItem.IsEnabled;
        toggleProcessRuleMenuItem.Text = ProcessListView.SelectedItem is ProcessChoice selected && temporarilyDisabledRuleIds.Contains(selected.RuleId)
            ? Localization.Value("MainPage_EnableRule")
            : Localization.Value("MainPage_DisableRule");
    }

    private void CopyProcessRuleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessListView.SelectedItem is not ProcessChoice choice || !processConfigs.TryGetValue(choice.RuleId, out ProcessSwitchItem? model)) return;

        copiedProcessRule = new ProcessRuleSnapshot(
            model.ForegroundOnly,
            model.PriorityOverride,
            model.VolumePercent,
            ReadEndpointPath(model.EndpointFile),
            model.ActiveProfile,
            model.ActiveSpatialAudioModeId);
        UpdateProcessContextMenuState();
        Log($"Copied rule from {model.DisplayName}.");
    }

    private void PasteProcessRuleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (copiedProcessRule == null ||
            ProcessListView.SelectedItem is not ProcessChoice choice ||
            !processConfigs.TryGetValue(choice.RuleId, out ProcessSwitchItem? target)) return;

        target.ForegroundOnly = copiedProcessRule.ForegroundOnly;
        target.PriorityOverride = copiedProcessRule.PriorityOverride;
        target.VolumePercent = VolumeSafety.Clamp(copiedProcessRule.VolumePercent, config.VolumeProtectionEnabled);
        target.ActiveProfile = copiedProcessRule.ActiveProfile;
        target.ActiveSpatialAudioModeId = copiedProcessRule.ActiveSpatialAudioModeId;
        if (string.IsNullOrWhiteSpace(copiedProcessRule.EndpointPath))
        {
            target.EndpointFile = string.Empty;
        }
        else
        {
            string endpointFile = EndpointFileForProcess(target.Name);
            File.WriteAllText(endpointFile, copiedProcessRule.EndpointPath, Encoding.UTF8);
            target.EndpointFile = endpointFile;
        }

        SaveConfig();
        ShowProcessProfile(choice, target);
        Log($"Pasted rule to {target.DisplayName}.");
    }

    private void ToggleProcessRuleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessListView.SelectedItem is not ProcessChoice choice ||
            !processConfigs.TryGetValue(choice.RuleId, out ProcessSwitchItem? model)) return;

        if (!temporarilyDisabledRuleIds.Add(model.RuleId)) temporarilyDisabledRuleIds.Remove(model.RuleId);
        choice.SetRuleDisabled(temporarilyDisabledRuleIds.Contains(model.RuleId));
        activeProcessRules.Clear();
        activeGlobalSettingsSignature = string.Empty;
        UpdateProcessContextMenuState();
        Log(temporarilyDisabledRuleIds.Contains(model.RuleId)
            ? $"Temporarily disabled rule: {model.DisplayName}."
            : $"Temporarily enabled rule: {model.DisplayName}.");
        if (monitoring) MonitorTimer_Tick(monitorTimer, new object());
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out int tab)) return;
        ProcessRulesPage.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutputDevicesPage.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        SpatialAudioPage.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPage.Visibility = tab == 4 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabSelection(tab);
        AnimateTabPage(tab switch
        {
            1 => OutputDevicesPage,
            2 => SpatialAudioPage,
            3 => GeneralPage,
            4 => NotificationsPage,
            _ => ProcessRulesPage
        });
    }

    private static void AnimateTabPage(Grid page)
    {
        if (page.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            page.RenderTransform = transform;
        }

        page.Opacity = 0;
        transform.X = 10;

        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, page);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var slide = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");

        storyboard.Children.Add(opacity);
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    private void UpdateTabSelection(int selectedTab)
    {
        Button[] buttons = { ProcessRulesTabButton, OutputDevicesTabButton, SpatialAudioTabButton, GeneralTabButton, NotificationsTabButton };
        for (int index = 0; index < buttons.Length; index++)
        {
            buttons[index].Style = (Style)Application.Current.Resources[index == selectedTab ? "ActiveTabButtonStyle" : "TabButtonStyle"];
        }
    }

    private void AddProcess_Click(object sender, RoutedEventArgs e)
    {
        ShowAddProcessDialogAsync();
    }

    private async void ShowAddProcessDialogAsync()
    {
        var processTextBox = new TextBox
        {
            PlaceholderText = Localization.Text("Dialog_ProcessPlaceholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360
        };
        var dialog = new ContentDialog
        {
            Title = Localization.Get("Dialog_AddProcess.Title"),
            PrimaryButtonText = Localization.Get("Dialog_AddProcess.Primary"),
            CloseButtonText = Localization.Text("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = processTextBox,
            XamlRoot = XamlRoot
        };
        dialog.Opened += (_, _) => processTextBox.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AddProcess(processTextBox.Text);
        }
    }

    private async void OpenSelectButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

        nint windowHandle = (Application.Current as App)?.MainWindowHandle ?? 0;
        if (windowHandle == 0)
        {
            Log("Open/select skipped: main window handle is unavailable.");
            return;
        }

        InitializeWithWindow.Initialize(picker, windowHandle);
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null) return;

        string processName = Path.GetFileNameWithoutExtension(file.Path);
        ProcessChoice choice = ProcessChoice.FromStored(new ProcessSwitchItem
        {
            Name = processName,
            ExecutablePath = file.Path
        });
        AddProcess(processName, choice);
    }

    private void AddProcess(string? name, ProcessChoice? detectedChoice = null)
    {
        string value = (name ?? string.Empty).Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (string.IsNullOrWhiteSpace(value)) return;

        ProcessChoice choice = detectedChoice ?? FindProcessChoice(value) ?? new ProcessChoice { Name = value };
        var model = new ProcessSwitchItem
        {
            RuleId = Guid.NewGuid().ToString("N"),
            Name = value,
            ExecutablePath = choice.FilePath,
            MatchMode = string.IsNullOrWhiteSpace(choice.FilePath) ? ProcessMatchMode.ProcessName : ProcessMatchMode.FullPath,
            Description = choice.Description,
            IconPath = choice.IconPath
        };
        choice.RuleId = model.RuleId;
        processConfigs.Add(model.RuleId, model);
        config.Processes.Add(model);
        processChoices.Add(choice);
        UpdateProcessEmptyState();
        ProcessListView.SelectedIndex = processChoices.Count - 1;
        SaveConfig();
    }

    private void RemoveProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessListView.SelectedItem is not ProcessChoice choice || !processConfigs.TryGetValue(choice.RuleId, out ProcessSwitchItem? model)) return;
        processConfigs.Remove(choice.RuleId);
        temporarilyDisabledRuleIds.Remove(choice.RuleId);
        config.Processes.Remove(model);
        processChoices.Remove(choice);
        UpdateProcessEmptyState();
        ProfileEditorHost.Content = new TextBlock
        {
            Text = processChoices.Count == 0 ? Localization.Text("MainPage_ProfileEmpty") : Localization.Text("MainPage_ProfileSelect"),
            Opacity = 0.68,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        if (processChoices.Count > 0) ProcessListView.SelectedIndex = 0;
        SaveConfig();
    }

    private void UpdateProcessEmptyState()
    {
        ProcessEmptyState.Visibility = processChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SelectProcessButton_Click(object sender, RoutedEventArgs e)
    {
        ProcessChoice? selected = await ShowProcessPickerAsync();
        if (selected != null) AddProcess(selected.Name, selected);
    }

    private async Task<ProcessChoice?> ShowProcessPickerAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Localization.Get("Dialog_SelectProcess.Title"),
            PrimaryButtonText = Localization.Get("Dialog_AddProcess.Primary"),
            CloseButtonText = Localization.Text("Dialog_Cancel"),
            Width = 640,
            MinWidth = 560,
            MaxWidth = 720,
            XamlRoot = XamlRoot
        };
        var root = new Grid
        {
            MinHeight = 360,
            MaxHeight = 500,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowSpacing = 10
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var filterBar = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var filter = new TextBox
        {
            PlaceholderText = Localization.Text("Dialog_ProcessFilter"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            MaxWidth = 440
        };
        var windowOnlyCheckBox = new CheckBox
        {
            Content = Localization.Content("Dialog_ProcessWindowOnly"),
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 300,
            Padding = new Thickness(0, 0, 0, 8)
        };
        list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Visible);
        list.SetValue(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Enabled);
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='4' ColumnSpacing='10'><Grid.ColumnDefinitions><ColumnDefinition Width='40'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><Image Grid.Column='0' Width='32' Height='32' Source='{Binding IconSource}'/><StackPanel Grid.Column='1' Spacing='1'><Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis'/><Border Grid.Column='1' Background='{ThemeResource SubtleFillColorSecondaryBrush}' CornerRadius='4' Padding='6,1'><TextBlock Text='{Binding WindowStatus}' FontSize='11' Opacity='0.75'/></Border></Grid><TextBlock Text='{Binding Description}' Opacity='0.65' FontSize='12' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding WindowTitle}' Opacity='0.55' FontSize='11' TextTrimming='CharacterEllipsis'/></StackPanel></Grid></DataTemplate>");
        var choices = new List<ProcessChoice>();
        Grid.SetRow(list, 1);
        filterBar.Children.Add(filter);
        filterBar.Children.Add(windowOnlyCheckBox);
        Grid.SetRow(filterBar, 0);
        root.Children.Add(filterBar);
        root.Children.Add(list);
        dialog.Content = root;

        void Refresh()
        {
            string text = filter.Text.Trim();
            bool showWindowedOnly = windowOnlyCheckBox.IsChecked == true;
            List<ProcessChoice> filtered = choices.Where(choice =>
                (!showWindowedOnly || choice.HasWindow) &&
                (string.IsNullOrWhiteSpace(text) ||
                 choice.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                 choice.Description.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                 choice.WindowTitle.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
            list.ItemsSource = filtered;
        }

        filter.TextChanged += (_, _) => Refresh();
        windowOnlyCheckBox.Checked += (_, _) => Refresh();
        windowOnlyCheckBox.Unchecked += (_, _) => Refresh();
        _ = Task.Run(LoadRunningProcesses).ContinueWith(task => DispatcherQueue.TryEnqueue(() =>
        {
            if (task.IsFaulted) return;
            choices.AddRange(task.Result);
            Refresh();
        }));

        ProcessChoice? selected = null;
        list.SelectionChanged += (_, _) => selected = list.SelectedItem as ProcessChoice;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (selected == null) args.Cancel = true;
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? selected : null;
    }

    private static List<ProcessChoice> LoadRunningProcesses()
    {
        var byName = new Dictionary<string, ProcessChoice>(StringComparer.OrdinalIgnoreCase);
        int currentId = Environment.ProcessId;
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentId || string.Equals(process.ProcessName, "Idle", StringComparison.OrdinalIgnoreCase)) continue;
                ProcessChoice? choice = ProcessChoice.FromProcess(process);
                if (choice != null && (!byName.TryGetValue(choice.Name, out ProcessChoice? existing) ||
                    (!existing.HasWindow && choice.HasWindow) ||
                    (string.IsNullOrWhiteSpace(existing.IconPath) && !string.IsNullOrWhiteSpace(choice.IconPath)))) byName[choice.Name] = choice;
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        var result = byName.Values.ToList();
        result.Sort((a, b) =>
        {
            int windowOrder = b.HasWindow.CompareTo(a.HasWindow);
            return windowOrder != 0 ? windowOrder : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });
        return result;
    }

    private void ProfileView_SettingsChanged(object? sender, EventArgs e)
    {
        if (sender is not ProcessProfileView view) return;
        if (view.SelectedEndpoint is { IsKeepCurrent: true })
        {
            view.Model.EndpointFile = string.Empty;
        }
        else if (view.SelectedEndpoint is AudioEndpointChoice endpoint)
        {
            string? currentPath = ReadEndpointPath(view.Model.EndpointFile);
            if (!string.Equals(currentPath, endpoint.EndpointPath, StringComparison.OrdinalIgnoreCase))
            {
                string endpointFile = EndpointFileForProcess(view.Model.Name);
                File.WriteAllText(endpointFile, endpoint.EndpointPath, Encoding.UTF8);
                view.Model.EndpointFile = endpointFile;
            }
            ProbeEndpointForView(view, endpoint);
        }
        SaveConfig();
        RouteOutputForRunningProcesses(view.Model);
    }

    private void ProfileView_TestActiveRequested(object? sender, EventArgs e)
    {
        if (sender is ProcessProfileView view)
        {
            RunProfileAsync(view.Model, view.Model.ActiveProfile, view.Model.ActiveSpatialAudioModeId, GetMatchingProcessIds(view.Model));
        }
    }

    private Task EnsureEndpointProbeAsync(AudioEndpointChoice endpoint)
    {
        if (endpoint.ProbeCompleted) return Task.CompletedTask;
        lock (endpointProbeTasks)
        {
            if (!endpointProbeTasks.TryGetValue(endpoint.EndpointId, out Task? task))
            {
                task = Task.Run(() => AudioProfileProviderRegistry.ProbeAll(endpoint));
                endpointProbeTasks[endpoint.EndpointId] = task;
            }
            return task;
        }
    }

    private async void ProbeEndpointForView(ProcessProfileView view, AudioEndpointChoice endpoint)
    {
        try
        {
            await EnsureEndpointProbeAsync(endpoint);
        }
        catch
        {
        }
        DispatcherQueue.TryEnqueue(view.RefreshCurrentMatch);
    }

    private void MonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (monitoring) StopMonitoring(); else StartMonitoring();
    }

    public void StartMonitoring()
    {
        if (monitoring) return;
        monitoring = true;
        activeProcessRules.Clear();
        activeGlobalSettingsSignature = string.Empty;
        monitorTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, config.IntervalSeconds));
        monitorTimer.Start();
        MonitorButtonText.Text = Localization.Text("MainPage_StopMonitoring");
        MonitorButtonIcon.Symbol = Symbol.Stop;
        StatusText.Text = Localization.Value("Status_Monitoring");
        Log("Monitoring started.");
        if (audioStateInitialized) MonitorTimer_Tick(monitorTimer, new object());
    }

    private void StopMonitoring()
    {
        monitoring = false;
        monitorTimer.Stop();
        activeProcessRules.Clear();
        activeGlobalSettingsSignature = string.Empty;
        MonitorButtonText.Text = Localization.Text("MainPage_StartMonitoring");
        MonitorButtonIcon.Symbol = Symbol.Play;
        StatusText.Text = Localization.Value("Status_MonitoringStopped");
        Log("Monitoring stopped.");
    }

    private void MonitorTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!audioStateInitialized) return;

        int? foregroundProcessId = GetForegroundProcessId();

        // Editing a rule brings Audio Switch itself to the foreground. Treat that
        // as a neutral UI state so a foreground-only game rule is not torn down
        // and reapplied every time the user changes the selected rule.
        if (foregroundProcessId == Environment.ProcessId)
        {
            LogMonitorDecision(
                $"ui|{foregroundProcessId}",
                "Monitor skipped because Audio Switch is the foreground process.");
            return;
        }

        foreach (int processId in appliedProcessEndpointPaths.Keys.Where(processId => !IsProcessRunning(processId)).ToList())
        {
            appliedProcessEndpointPaths.Remove(processId);
        }

        List<ProcessSwitchItem> rules = config.Processes.ToList();
        var candidates = new Dictionary<int, List<(ProcessSwitchItem Rule, int Order)>>();
        var ruleMatches = new List<string>();
        int order = 0;
        foreach (ProcessSwitchItem item in rules)
        {
            if (temporarilyDisabledRuleIds.Contains(item.RuleId)) continue;
            List<int> processIds = GetMatchingProcessIds(item);
            ruleMatches.Add($"{item.Name}={FormatProcessIds(processIds)}");
            foreach (int processId in processIds)
            {
                if (!candidates.TryGetValue(processId, out List<(ProcessSwitchItem Rule, int Order)>? matches))
                {
                    matches = new List<(ProcessSwitchItem Rule, int Order)>();
                    candidates[processId] = matches;
                }
                matches.Add((item, order));
            }
            order++;
        }

        var effectiveRules = new Dictionary<int, ProcessSwitchItem>();
        foreach ((int processId, List<(ProcessSwitchItem Rule, int Order)> matches) in candidates)
        {
            (ProcessSwitchItem Rule, int Order) best = matches
                .OrderByDescending(match => RulePriority(match.Rule))
                .ThenBy(match => match.Order)
                .First();
            effectiveRules[processId] = best.Rule;
        }

        string effectiveMatches = effectiveRules.Count == 0
            ? "<none>"
            : string.Join(", ", effectiveRules
                .OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Value.Name}[{pair.Key},p{RulePriority(pair.Value)}]"));
        string monitorDecision =
            $"foregroundPid={foregroundProcessId?.ToString() ?? "<none>"}; " +
            $"rules={string.Join("; ", ruleMatches)}; effective={effectiveMatches}; " +
            $"global={config.GlobalSpatialAudioModeId}/{config.GlobalActiveProfile}";
        LogMonitorDecision(monitorDecision, $"Monitor scan: {monitorDecision}");

        HashSet<int> processIdsToUpdate = activeProcessRules.Keys.Concat(effectiveRules.Keys).ToHashSet();
        foreach (int processId in processIdsToUpdate)
        {
            activeProcessRules.TryGetValue(processId, out ProcessSwitchItem? previousRule);
            effectiveRules.TryGetValue(processId, out ProcessSwitchItem? nextRule);
            if (previousRule != null && nextRule != null && string.Equals(previousRule.RuleId, nextRule.RuleId, StringComparison.OrdinalIgnoreCase)) continue;

            if (nextRule != null)
            {
                RunProfileAsync(nextRule, string.Empty, "keep", new[] { processId }, $"{nextRule.RuleId}:output:{processId}");
            }
            else if (previousRule != null)
            {
                IReadOnlyList<int> restoreTarget = IsProcessRunning(processId) ? new[] { processId } : Array.Empty<int>();
                RunGlobalProfileAsync(string.Empty, "keep", restoreTarget, $"global-output:{processId}");
            }
        }

        activeProcessRules.Clear();
        foreach ((int processId, ProcessSwitchItem rule) in effectiveRules) activeProcessRules[processId] = rule;

        ProcessSwitchItem? activeRule = effectiveRules
            .Select(pair => (Rule: pair.Value, ProcessId: pair.Key))
            .OrderByDescending(match => RulePriority(match.Rule))
            .ThenByDescending(match => match.ProcessId == foregroundProcessId)
            .Select(match => match.Rule)
            .FirstOrDefault();

        if (activeRule != null)
        {
            string ruleSignature = $"rule|{activeRule.RuleId}|{activeRule.ActiveSpatialAudioModeId}|{activeRule.ActiveProfile}|{ReadEndpointPath(activeRule.EndpointFile)}";
            if (!string.Equals(activeGlobalSettingsSignature, ruleSignature, StringComparison.Ordinal))
            {
                activeGlobalSettingsSignature = ruleSignature;
                RunProfileAsync(activeRule, activeRule.ActiveProfile, activeRule.ActiveSpatialAudioModeId, Array.Empty<int>(), $"{activeRule.RuleId}:spatial");
            }
            return;
        }

        string globalMode = string.IsNullOrWhiteSpace(config.GlobalSpatialAudioModeId) ? "off" : config.GlobalSpatialAudioModeId;
        string globalProfile = config.GlobalActiveProfile ?? string.Empty;
        string globalEndpointPath = GetGlobalEndpointPath();
        string globalSignature = $"global|{globalMode}|{globalProfile}|{globalEndpointPath}";
        if (!string.Equals(activeGlobalSettingsSignature, globalSignature, StringComparison.Ordinal))
        {
            activeGlobalSettingsSignature = globalSignature;
            RunGlobalProfileAsync(globalProfile, globalMode, Array.Empty<int>(), "global-spatial");
        }
    }

    private static int RulePriority(ProcessSwitchItem item)
    {
        if (item.PriorityOverride.HasValue) return item.PriorityOverride.Value;

        int priority = item.ForegroundOnly ? 100 : 0;
        if (item.MatchMode == ProcessMatchMode.FullPath) priority += 10;
        return priority;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static List<int> GetMatchingProcessIds(ProcessSwitchItem item)
    {
        var result = new List<int>();
        try
        {
            if (item.MatchMode == ProcessMatchMode.ProcessName)
            {
                foreach (Process process in Process.GetProcessesByName(item.Name))
                {
                    using (process) result.Add(process.Id);
                }
                return ApplyForegroundFilter(item, result);
            }

            if (string.IsNullOrWhiteSpace(item.ExecutablePath)) return result;

            string expectedPath = NormalizePath(item.ExecutablePath);
            string processName = Path.GetFileNameWithoutExtension(expectedPath);
            if (string.IsNullOrWhiteSpace(processName)) return result;
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string actualPath = ProcessChoice.GetExecutablePath(process);
                    if (!string.IsNullOrWhiteSpace(actualPath) && PathsEqual(actualPath, expectedPath)) result.Add(process.Id);
                }
            }
        }
        catch
        {
        }
        return ApplyForegroundFilter(item, result);
    }

    private static List<int> ApplyForegroundFilter(ProcessSwitchItem item, List<int> processIds)
    {
        if (!item.ForegroundOnly || processIds.Count == 0) return processIds;

        int? foregroundProcessId = GetForegroundProcessId();
        return foregroundProcessId.HasValue && processIds.Contains(foregroundProcessId.Value)
            ? new List<int> { foregroundProcessId.Value }
            : new List<int>();
    }

    private static int? GetForegroundProcessId()
    {
        nint foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0 || GetWindowThreadProcessId(foregroundWindow, out uint processId) == 0 || processId == 0) return null;
        return processId <= int.MaxValue ? (int)processId : null;
    }

    private void RunProfileAsync(ProcessSwitchItem item, string profile, string spatialAudioModeId, IReadOnlyList<int>? processIds = null, string? operationKeyOverride = null)
    {
        IReadOnlyList<int> targetProcessIds = processIds ?? Array.Empty<int>();
        RunAudioOperationAsync(
            item.Name,
            ReadEndpointPath(item.EndpointFile),
            profile,
            spatialAudioModeId,
            apply: true,
            VolumeSafety.Clamp(item.VolumePercent, config.VolumeProtectionEnabled),
            targetProcessIds,
            operationKeyOverride ?? item.RuleId);
    }

    private void RunGlobalProfileAsync(string profile, string spatialAudioModeId, IReadOnlyList<int> processIds, string operationKey)
    {
        RunAudioOperationAsync(
            Localization.Value("MainPage_GlobalDefaults"),
            GetGlobalEndpointPath(),
            profile,
            spatialAudioModeId,
            apply: true,
            processVolumePercent: processIds.Count > 0
                ? VolumeSafety.Clamp(config.GlobalVolumePercent, config.VolumeProtectionEnabled)
                : null,
            targetProcessIds: processIds,
            operationKey: operationKey);
    }

    private void RunAudioOperationAsync(
        string operationName,
        string? configuredEndpointPath,
        string profile,
        string spatialAudioModeId,
        bool apply,
        float? processVolumePercent,
        IReadOnlyList<int> targetProcessIds,
        string operationKey)
    {
        if (!runningOperations.TryAdd(operationKey, 0))
        {
            Log($"[{operationName}] another operation is already running.");
            return;
        }

        SpatialAudioOption spatialMode = SpatialAudioModeCatalog.FindById(spatialAudioModeId);
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(spatialMode.Id);
        if (provider != null && !provider.SupportsProfile(profile))
        {
            runningOperations.TryRemove(operationKey, out _);
            Log($"[{operationName}] the selected spatial format does not support preset '{profile}'.");
            return;
        }

        configuredEndpointPath ??= string.Empty;
        List<AudioEndpointChoice> endpointSnapshot = endpoints.ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                string endpointPath = configuredEndpointPath;
                if (string.IsNullOrWhiteSpace(endpointPath) && spatialMode.Id != "keep")
                {
                    endpointPath = AudioSystemState.Read(endpointSnapshot).DefaultEndpointPath ?? string.Empty;
                }

                if (spatialMode.Id != "keep" && string.IsNullOrWhiteSpace(endpointPath))
                {
                    Log($"[{operationName}] no current output device is available for spatial audio.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(endpointPath))
                {
                    foreach (int processId in targetProcessIds)
                    {
                        Log(SetProcessOutputIfChanged(processId, endpointPath, apply));
                    }
                }
                if (processVolumePercent.HasValue)
                {
                    foreach (int processId in targetProcessIds)
                    {
                        Log(AudioVolumeController.SetProcessVolume(processId, endpointPath, processVolumePercent.Value, apply));
                    }
                }
                if (spatialMode.Id != "keep")
                {
                    Log($"[{operationName}] requesting spatial format {spatialMode.Name} {(apply ? "(APPLY)" : "(DRY-RUN)")}...");
                    Log((await AudioSystemState.SetDefaultSpatialAudioModeAsync(endpointPath, spatialMode.Id, apply)).Trim());
                }

                if (provider != null)
                {
                    Log($"[{operationName}] applying {spatialMode.Name} preset {profile} {(apply ? "(APPLY)" : "(DRY-RUN)")}...");
                    Log(provider.InvokeSetter(endpointPath, profile, apply).Trim());
                }
            }
            catch (Exception ex)
            {
                Log($"[{operationName}] profile operation failed: {ex.Message}");
            }
            finally
            {
                runningOperations.TryRemove(operationKey, out _);
            }
        });
    }

    private void RouteOutputForRunningProcesses(ProcessSwitchItem item)
    {
        List<int> processIds = GetMatchingProcessIds(item);
        if (processIds.Count == 0) return;
        string endpointPath = ReadEndpointPath(item.EndpointFile) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(endpointPath) && !item.VolumePercent.HasValue) return;

        string operationKey = $"{item.RuleId}:manual";
        if (!runningOperations.TryAdd(operationKey, 0))
        {
            Log($"[{item.Name}] another operation for this process is already running.");
            return;
        }

        const bool apply = true;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (int processId in processIds)
                {
                    if (!string.IsNullOrWhiteSpace(endpointPath))
                    {
                        Log(SetProcessOutputIfChanged(processId, endpointPath, apply));
                    }
                    if (item.VolumePercent.HasValue)
                    {
                        Log(AudioVolumeController.SetProcessVolume(
                            processId,
                            endpointPath,
                            VolumeSafety.Clamp(item.VolumePercent.Value, config.VolumeProtectionEnabled),
                            apply));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[{item.Name}] output or volume operation failed: {ex.Message}");
            }
            finally
            {
                runningOperations.TryRemove(operationKey, out _);
            }
        });
    }

    private string SetProcessOutputIfChanged(int processId, string endpointPath, bool apply)
    {
        if (appliedProcessEndpointPaths.TryGetValue(processId, out string? previousEndpointPath) &&
            string.Equals(previousEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase))
        {
            return $"C# process {processId} output unchanged -> switch API skipped";
        }

        string result = ProcessAudioRouter.SetOutputDevice(processId, endpointPath, apply);
        if (result.StartsWith("C# process ", StringComparison.OrdinalIgnoreCase) &&
            result.Contains(" output routed", StringComparison.OrdinalIgnoreCase))
        {
            appliedProcessEndpointPaths[processId] = endpointPath;
        }

        return result;
    }

    private async void GeneralOption_Click(object sender, RoutedEventArgs e)
    {
        config.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        config.StartSilent = StartSilentCheckBox.IsChecked == true;
        config.StartMonitor = StartMonitorCheckBox.IsChecked == true;
        if (ReferenceEquals(sender, VolumeProtectionCheckBox))
        {
            config.VolumeProtectionEnabled = VolumeProtectionCheckBox.IsChecked == true;
        }
        SaveConfig();

        if (ReferenceEquals(sender, VolumeProtectionCheckBox))
        {
            await ApplyVolumeProtectionAsync();
        }
    }

    private async Task ApplyVolumeProtectionAsync()
    {
        float maximum = VolumeSafety.MaximumPercent(config.VolumeProtectionEnabled);
        bool globalVolumeClamped = false;
        float requestedGlobalVolume = (float)Math.Clamp(GlobalVolumeSlider.Value, 0, 100);
        float safeGlobalVolume = VolumeSafety.Clamp(requestedGlobalVolume, config.VolumeProtectionEnabled);

        suppressGlobalVolumeSelection = true;
        GlobalVolumeSlider.Maximum = maximum;
        if (Math.Abs(GlobalVolumeSlider.Value - safeGlobalVolume) > 0.001)
        {
            GlobalVolumeSlider.Value = safeGlobalVolume;
            globalVolumeClamped = true;
        }
        suppressGlobalVolumeSelection = false;

        if (ProfileEditorHost.Content is ProcessProfileView view && view.SetVolumeProtection(config.VolumeProtectionEnabled))
        {
            SaveConfig();
        }

        if (globalVolumeClamped)
        {
            config.GlobalVolumePercent = safeGlobalVolume;
            SaveConfig();
            string result = AudioVolumeController.SetEndpointVolume(null, safeGlobalVolume, apply: true);
            Log($"Volume protection applied -> {safeGlobalVolume:0}%: {result}");
            OutputVolumeValueTextBlock.Text = $"{safeGlobalVolume:0}%";
        }

        await Task.CompletedTask;
    }

    private void MonitorIntervalNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(sender.Value)) return;

        int intervalSeconds = Math.Clamp((int)Math.Round(sender.Value), 1, 60);
        if (Math.Abs(sender.Value - intervalSeconds) > double.Epsilon)
        {
            sender.Value = intervalSeconds;
            return;
        }

        config.IntervalSeconds = intervalSeconds;
        if (monitoring) monitorTimer.Interval = TimeSpan.FromSeconds(intervalSeconds);
        SaveConfig();
    }

    private ProcessChoice? FindProcessChoice(string name, string? requiredPath = null)
    {
        try
        {
            string? normalizedPath = string.IsNullOrWhiteSpace(requiredPath) ? null : NormalizePath(requiredPath);
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    ProcessChoice? choice = ProcessChoice.FromProcess(process);
                    if (choice != null && (normalizedPath == null || PathsEqual(choice.FilePath, normalizedPath))) return choice;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim(); }
    }

    private static bool PathsEqual(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static double? ReadVerticalScrollOffset(DependencyObject root)
    {
        ScrollViewer? scrollViewer = FindScrollViewer(root);
        return scrollViewer?.VerticalOffset;
    }

    private void RestoreVerticalScrollOffset(DependencyObject root, double? offset)
    {
        if (!offset.HasValue || offset.Value <= 0) return;
        DispatcherQueue.TryEnqueue(() => FindScrollViewer(root)?.ChangeView(null, (float)offset.Value, null));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            ScrollViewer? nested = FindScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static string? ReadEndpointPath(string? endpointFile)
    {
        if (string.IsNullOrWhiteSpace(endpointFile)) return null;
        string path = endpointFile;
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppPaths.RepositoryRoot, path);
        try
        {
            if (!File.Exists(path))
            {
                return endpointFile.Contains("MMDEVAPI#", StringComparison.OrdinalIgnoreCase) ||
                    endpointFile.StartsWith("{", StringComparison.Ordinal)
                    ? AudioEndpointChoice.NormalizeDeviceInterfacePath(endpointFile)
                    : null;
            }

            string value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : AudioEndpointChoice.NormalizeDeviceInterfacePath(value);
        }
        catch { return null; }
    }

    private string GetGlobalEndpointPath() => ReadEndpointPath(config.GlobalEndpointFile) ?? lastDefaultEndpointPath;

    private static string EndpointFileForProcess(string processName)
    {
        AppPaths.EnsureDataDirectories();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(processName))).Substring(0, 16).ToLowerInvariant();
        return Path.Combine(AppPaths.WorkDirectory, $"endpoint-{hash}.txt");
    }

    private static string EndpointFileForGlobalDefaults() => EndpointFileForProcess("global-defaults");

    private void SaveConfig()
    {
        config.Save(AppPaths.ConfigPath);
        UpdateStartupRegistration();
    }

    private void UpdateStartupRegistration()
    {
        using RegistryKey? run = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        if (run == null) return;
        if (config.StartWithWindows)
        {
            string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DolbyAccessAutoSwitch.WinUI.exe");
            run.SetValue("DolbyAccessAutoSwitch", $"\"{executable}\" --silent");
        }
        else run.DeleteValue("DolbyAccessAutoSwitch", false);
    }

    private async void ExportLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"audio-switch-log-{DateTime.Now:yyyyMMdd-HHmmss}"
            };
            picker.FileTypeChoices.Add(Localization.Text("MainPage_LogFileType"), new List<string> { ".txt" });

            nint windowHandle = (Application.Current as App)?.MainWindowHandle ?? 0;
            if (windowHandle == 0)
            {
                Log("Log export skipped: main window handle is unavailable.");
                return;
            }

            InitializeWithWindow.Initialize(picker, windowHandle);
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null) return;

            string content;
            lock (logFileLock)
            {
                content = File.Exists(AppPaths.LogPath)
                    ? File.ReadAllText(AppPaths.LogPath, Encoding.UTF8)
                    : logEntries.Count == 0
                        ? Localization.Text("MainPage_NoLog")
                        : string.Join(Environment.NewLine, logEntries);
            }
            await Windows.Storage.FileIO.WriteTextAsync(file, content);
            Log($"Log exported: {file.Path}");
        }
        catch (Exception ex)
        {
            Log("Log export failed: " + ex.Message);
        }
    }

    private static string FormatProcessIds(IReadOnlyList<int> processIds) =>
        processIds.Count == 0 ? "<none>" : string.Join(",", processIds);

    private void LogMonitorDecision(string signature, string message)
    {
        if (string.Equals(lastMonitorDecisionSignature, signature, StringComparison.Ordinal)) return;
        lastMonitorDecisionSignature = signature;
        Log(message);
    }

    private void Log(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        AppendLogFile(entry);
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = message;
            logEntries.Add(entry);
            while (logEntries.Count > 1000) logEntries.RemoveAt(0);
        });
    }

    private void AppendLogFile(string entry)
    {
        try
        {
            lock (logFileLock)
            {
                AppPaths.EnsureDataDirectories();
                if (File.Exists(AppPaths.LogPath) && new FileInfo(AppPaths.LogPath).Length >= MaxLogFileBytes)
                {
                    File.Move(AppPaths.LogPath, AppPaths.RotatedLogPath, overwrite: true);
                }

                using var writer = new StreamWriter(
                    AppPaths.LogPath,
                    append: true,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine(entry);
            }
        }
        catch
        {
            // Logging must never interrupt audio switching or the UI thread.
        }
    }

    private void RestoreExitRestoreCheckpoint()
    {
        AudioRestoreCheckpoint? checkpoint = exitRestoreCheckpoint;
        if (checkpoint == null) return;

        try
        {
            AudioSystemStatus current = AudioSystemState.Read(Array.Empty<AudioEndpointChoice>());
            if (!string.IsNullOrWhiteSpace(checkpoint.EndpointPath) &&
                !string.Equals(current.DefaultEndpointPath, checkpoint.EndpointPath, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Exit restore output -> {ProcessAudioRouter.SetSystemDefaultOutputDevice(checkpoint.EndpointPath, apply: true)}");
            }

            if (!string.IsNullOrWhiteSpace(checkpoint.EndpointPath) && !string.IsNullOrWhiteSpace(checkpoint.SpatialAudioModeId))
            {
                string spatialResult = Task.Run(() => AudioSystemState.SetDefaultSpatialAudioModeAsync(
                    checkpoint.EndpointPath,
                    checkpoint.SpatialAudioModeId,
                    apply: true)).GetAwaiter().GetResult();
                Log($"Exit restore spatial audio -> {spatialResult}");
            }

            if (checkpoint.VolumePercent is float volume)
            {
                float safeVolume = VolumeSafety.Clamp(volume, config.VolumeProtectionEnabled);
                Log($"Exit restore volume -> {AudioVolumeController.SetEndpointVolume(null, safeVolume, apply: true)}");
            }
        }
        catch (Exception ex)
        {
            Log($"Exit audio restore failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref exitRestoreStarted, 1) != 0) return;
        audioRefreshTimer.Stop();
        notificationHideTimer.Stop();
        monitorTimer.Stop();
        globalVolumeApplyCts?.Cancel();
        globalVolumeApplyCts?.Dispose();
        globalVolumeApplyCts = null;
        audioSystemChangeMonitor?.Dispose();
        audioSystemChangeMonitor = null;
        RestoreExitRestoreCheckpoint();
    }
}
