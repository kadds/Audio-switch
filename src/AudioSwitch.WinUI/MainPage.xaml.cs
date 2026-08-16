using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
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
using Windows.Foundation;
using Windows.UI.Notifications;
using WinRT.Interop;

namespace AudioSwitch_WinUI;

public sealed partial class MainPage : Page, IDisposable
{
    private const long MaxLogFileBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "vivaldi",
        "opera",
        "opera_gx",
        "brave",
        "chromium",
        "firefox",
        "waterfox",
        "librewolf",
        "floorp",
        "arc",
        "zen",
        "yandex",
        "qqbrowser",
        "360chrome",
        "sogouexplorer",
        "maxthon",
        "coccoc",
        "iridium",
        "epic"
    };
    private readonly object logFileLock = new();
    private sealed record AudioRestoreCheckpoint(
        string? EndpointPath,
        string SpatialAudioModeId,
        float? GlobalVolumePercent);

    private sealed record ProcessRuleSnapshot(
        bool ForegroundOnly,
        int? PriorityOverride,
        string? EndpointPath,
        float? GlobalVolumePercent,
        string ActiveProfile,
        string ActiveSpatialAudioModeId,
        DolbyEqualizerSettings DolbyEqualizer);

    private sealed record HttpStateMatch(ProcessSwitchItem Rule, HttpStateMessage State, int Order);
    private sealed record AddressRuleMatch(ProcessSwitchItem Parent, ProcessAddressRule Rule, HttpStateMessage State, int Order);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private readonly ObservableCollection<ProcessChoice> processChoices = new();
    private readonly ObservableCollection<AudioEndpointChoice> endpoints = new();
    private readonly ObservableCollection<string> logEntries = new();
    private readonly Dictionary<string, ProcessSwitchItem> processConfigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ProcessSwitchItem Parent, ProcessAddressRule Rule)> addressRuleConfigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> temporarilyDisabledRuleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> endpointProbeTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer monitorTimer;
    private readonly DispatcherQueueTimer foregroundDebounceTimer;
    private readonly DispatcherQueueTimer audioRefreshTimer;
    private readonly DispatcherQueueTimer audioCurveTimer;
    private readonly DispatcherQueueTimer notificationHideTimer;
    private readonly MenuFlyout processContextFlyout;
    private readonly MenuFlyoutItem copyProcessRuleMenuItem;
    private readonly MenuFlyoutItem pasteProcessRuleMenuItem;
    private readonly MenuFlyoutItem toggleProcessRuleMenuItem;
    private readonly AudioCurveCapture audioCurveCapture = new();
    private readonly AudioApiQueue audioApiQueue = new();
    private readonly Dictionary<string, HttpStateMessage> latestHttpStates = new(StringComparer.OrdinalIgnoreCase);
    private SwitchConfig config;
    private LocalHttpStateServer? httpStateServer;
    private AudioSystemChangeMonitor? audioSystemChangeMonitor;
    private ForegroundWindowMonitor? foregroundWindowMonitor;
    private string pendingAudioChangeReason = "Audio system changed";
    private bool pageInitialized;
    private bool monitoring;
    private bool foregroundCallbackActive;
    private int? pendingForegroundProcessId;
    private bool suppressOutputDeviceSelection;
    private bool suppressGlobalVolumeSelection;
    private CancellationTokenSource? globalVolumeApplyCts;
    private bool suppressSpatialAudioSelection;
    private bool suppressGlobalProfileSelection;
    private bool suppressLanguageSelection;
    private bool suppressNotificationSelection;
    private bool suppressHttpListenerSelection;
    private bool audioStateInitialized;
    private string lastDefaultEndpointPath = string.Empty;
    private string lastDefaultSpatialAudioModeId = string.Empty;
    private ProcessRuleSnapshot? copiedProcessRule;
    private readonly ConcurrentDictionary<string, byte> runningOperations = new(StringComparer.OrdinalIgnoreCase);
    private int audioOperationInProgress;
    private int audioEvaluationPending;
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
        AddAddressRuleProcessButton.IsEnabled = false;
        UpdateProcessEmptyState();
        AllEndpointsListView.ItemsSource = endpoints;
        monitorTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        monitorTimer.Tick += MonitorTimer_Tick;
        foregroundDebounceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        foregroundDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
        foregroundDebounceTimer.Tick += ForegroundDebounceTimer_Tick;
        audioRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        audioRefreshTimer.Interval = TimeSpan.FromMilliseconds(250);
        audioRefreshTimer.Tick += AudioRefreshTimer_Tick;
        audioCurveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        audioCurveTimer.Interval = TimeSpan.FromMilliseconds(33);
        audioCurveTimer.Tick += AudioCurveTimer_Tick;
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
            exitRestoreCheckpoint = audioApiQueue.EnqueueAsync(() =>
            {
                AudioSystemStatus audio = AudioSystemState.Read(Array.Empty<AudioEndpointChoice>());
                float? volume = null;
                if (AudioVolumeController.TryGetEndpointVolume(null, out float currentVolume, out _)) volume = currentVolume;
                return Task.FromResult(new AudioRestoreCheckpoint(
                    audio.DefaultEndpointPath,
                    string.IsNullOrWhiteSpace(audio.DefaultSpatialAudioModeId) ? "off" : audio.DefaultSpatialAudioModeId,
                    volume));
            }).GetAwaiter().GetResult();
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
        AudioCurveTabTextBlock.Text = Localization.Text("MainPage_TabAudioCurve");
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
        SpatialDefaultProfileTitleTextBlock.Text = Localization.Text("MainPage_SpatialDefaultProfileTitle");
        SpatialDefaultProfileHintTextBlock.Text = Localization.Text("MainPage_SpatialDefaultProfileHint");
        AddCustomProfileButton.Content = Localization.Content("MainPage_AddCustomProfile");
        SpatialOptionsTextBlock.Text = Localization.Text("MainPage_SpatialOptions");
        AudioCurveTitleTextBlock.Text = Localization.Text("MainPage_AudioCurveTitle");
        AudioCurveHintTextBlock.Text = Localization.Text("MainPage_AudioCurveHint");
        AudioCurveOutputLabelTextBlock.Text = Localization.Text("MainPage_AudioCurveOutput");
        AudioCurveStatusLabelTextBlock.Text = Localization.Text("MainPage_AudioCurveStatus");
        AudioCurveRmsLabelTextBlock.Text = Localization.Text("MainPage_AudioCurveRms");
        AudioCurvePeakLabelTextBlock.Text = Localization.Text("MainPage_AudioCurvePeak");
        AudioCurveFrequencyTitleTextBlock.Text = Localization.Text("MainPage_AudioCurveFrequencyTitle");
        AudioCurveLowLabelTextBlock.Text = Localization.Text("MainPage_AudioCurveLow");
        AudioCurveMidLabelTextBlock.Text = Localization.Text("MainPage_AudioCurveMid");
        AudioCurveHighLabelTextBlock.Text = Localization.Text("MainPage_AudioCurveHigh");
        AudioCurveFooterTextBlock.Text = Localization.Text("MainPage_AudioCurveFooter");
        AudioCurveEmptyTextBlock.Text = Localization.Text("MainPage_AudioCurveStartHint");
        UpdateAudioCurveToggleText(audioCurveCapture.IsRunning);
        UpdateAudioCurveUi(audioCurveCapture.Snapshot());
        RefreshGlobalSpatialProfileChoices();
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
        HttpListenerTitleTextBlock.Text = Localization.Text("MainPage_HttpListenerTitle");
        HttpListenerEnabledCheckBox.Content = Localization.Content("MainPage_HttpListenerEnabled");
        HttpListenerBindLabelTextBlock.Text = Localization.Text("MainPage_HttpListenerBindLabel");
        HttpListenerPortLabelTextBlock.Text = Localization.Text("MainPage_HttpListenerPortLabel");
        HttpListenerPasswordLabelTextBlock.Text = Localization.Text("MainPage_HttpListenerPasswordLabel");
        ChromeExtensionTitleTextBlock.Text = Localization.Text("MainPage_ChromeExtensionTitle");
        ChromeExtensionHintTextBlock.Text = Localization.Text("MainPage_ChromeExtensionHint");
        OpenChromeExtensionTextBlock.Text = Localization.Text("MainPage_OpenChromeExtension");
        TrayHintTextBlock.Text = Localization.Text("MainPage_TrayHint");
        LogExportTitleTextBlock.Text = Localization.Text("MainPage_LogExportTitle");
        LogExportHintTextBlock.Text = $"{Localization.Text("MainPage_LogExportHint")}\n{AppPaths.LogPath}";
        OpenLogTextBlock.Text = Localization.Text("MainPage_OpenLog");
        ExportLogTextBlock.Text = Localization.Text("MainPage_ExportLog");
        ClearLogTextBlock.Text = Localization.Text("MainPage_ClearLog");
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
        ToolTipService.SetToolTip(AudioCurveTabButton, Localization.Get("MainPage_TabAudioCurve.ToolTip"));
        ToolTipService.SetToolTip(AddProcessButton, Localization.Get("MainPage_AddProcess.ToolTip"));
        ToolTipService.SetToolTip(AddAddressRuleProcessButton, Localization.Get("MainPage_AddAddressRule.ToolTip"));
        ToolTipService.SetToolTip(RemoveProcessButton, Localization.Get("MainPage_RemoveProcess.ToolTip"));
        ToolTipService.SetToolTip(SelectProcessButton, Localization.Get("MainPage_SelectProcess.ToolTip"));
        ToolTipService.SetToolTip(OpenSelectButton, Localization.Get("MainPage_OpenExe.ToolTip"));
        ToolTipService.SetToolTip(OpenChromeExtensionButton, Localization.Get("MainPage_OpenChromeExtension.ToolTip"));
        ToolTipService.SetToolTip(OpenLogButton, Localization.Get("MainPage_OpenLog.ToolTip"));
        ToolTipService.SetToolTip(ExportLogButton, Localization.Get("MainPage_ExportLog.ToolTip"));
        ToolTipService.SetToolTip(ClearLogButton, Localization.Get("MainPage_ClearLog.ToolTip"));
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
        RefreshGlobalSpatialProfileChoices();
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

            item.AddressRules ??= new List<ProcessAddressRule>();
            foreach (ProcessAddressRule addressRule in item.AddressRules)
            {
                if (string.IsNullOrWhiteSpace(addressRule.RuleId))
                {
                    addressRule.RuleId = Guid.NewGuid().ToString("N");
                    processMetadataChanged = true;
                }

                addressRule.Action ??= new ProcessSwitchItem();
                if (!string.Equals(addressRule.Action.Name, item.Name, StringComparison.Ordinal))
                {
                    addressRule.Action.Name = item.Name;
                    processMetadataChanged = true;
                }
                if (!string.IsNullOrWhiteSpace(addressRule.Address)) addressRule.Address = addressRule.Address.Trim();
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
            foreach (ProcessAddressRule addressRule in item.AddressRules)
            {
                addressRuleConfigs[addressRule.RuleId] = (item, addressRule);
                processChoices.Add(ProcessChoice.FromAddressRule(item, addressRule));
            }
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

        try
        {
            foregroundWindowMonitor = new ForegroundWindowMonitor(
                DispatcherQueue,
                ForegroundWindowChanged,
                Log);
        }
        catch (Exception ex)
        {
            Log("Foreground callback initialization failed: " + ex.Message);
        }

        StartWithWindowsCheckBox.IsChecked = config.StartWithWindows;
        StartSilentCheckBox.IsChecked = config.StartSilent;
        StartMonitorCheckBox.IsChecked = config.StartMonitor;
        VolumeProtectionCheckBox.IsChecked = config.VolumeProtectionEnabled;
        MonitorIntervalNumberBox.Value = Math.Clamp(config.IntervalSeconds, 1, 60);
        suppressHttpListenerSelection = true;
        HttpListenerEnabledCheckBox.IsChecked = config.HttpListenerEnabled;
        HttpListenerBindComboBox.SelectedItem = HttpListenerBindComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), config.HttpListenerBindAddress, StringComparison.OrdinalIgnoreCase))
            ?? HttpListenerBindComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        HttpListenerPortNumberBox.Value = Math.Clamp(config.HttpListenerPort, 1, 65535);
        HttpListenerPasswordBox.Password = config.HttpListenerPassword;
        suppressHttpListenerSelection = false;

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
        StartHttpListener();
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
            List<AudioEndpointChoice> loaded = await audioApiQueue.EnqueueAsync(() => Task.FromResult(AudioEndpointChoice.ReadAll()));
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
        AudioSystemStatus audio = await audioApiQueue.EnqueueAsync(() => Task.FromResult(AudioSystemState.Read(endpoints.ToList())));
        string endpointPath = audio.DefaultEndpointPath ?? string.Empty;
        string spatialModeId = string.IsNullOrWhiteSpace(audio.DefaultSpatialAudioModeId)
            ? "off"
            : audio.DefaultSpatialAudioModeId;
        bool outputChanged = audioStateInitialized && !string.Equals(lastDefaultEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase);
        bool spatialChanged = audioStateInitialized && !string.Equals(lastDefaultSpatialAudioModeId, spatialModeId, StringComparison.OrdinalIgnoreCase);
        lastDefaultEndpointPath = endpointPath;
        lastDefaultSpatialAudioModeId = spatialModeId;
        audioStateInitialized = true;
        if (outputChanged)
        {
            foreach (SpatialAudioOption option in SpatialAudioModeCatalog.Options)
            {
                option.IsEnabled = true;
            }
        }
        if (outputChanged) audioCurveCapture.SetEndpoint(endpointPath);

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
        (bool Read, float Percent, string Error) volumeReadback = await audioApiQueue.EnqueueAsync(() =>
        {
            float percent = 0;
            string error = string.Empty;
            bool read = !string.IsNullOrWhiteSpace(endpointPath) && AudioVolumeController.TryGetEndpointVolume(null, out percent, out error);
            return Task.FromResult((read, percent, error));
        });
        float endpointVolumePercent = volumeReadback.Percent;
        string volumeError = volumeReadback.Error;
        bool volumeRead = volumeReadback.Read;
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
                string protectionResult = await audioApiQueue.EnqueueAsync(() => Task.FromResult(
                    AudioVolumeController.SetEndpointVolume(null, safeVolume, apply: true)));
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
        RefreshGlobalSpatialProfileChoices();

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

    private void AudioCurveToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (audioCurveCapture.IsRunning)
        {
            StopAudioCurve();
            return;
        }

        audioCurveCapture.Start(string.IsNullOrWhiteSpace(lastDefaultEndpointPath) ? null : lastDefaultEndpointPath);
        audioCurveTimer.Start();
        UpdateAudioCurveToggleText(true);
        UpdateAudioCurveUi(audioCurveCapture.Snapshot());
    }

    private void StopAudioCurve()
    {
        audioCurveTimer.Stop();
        audioCurveCapture.Stop();
        UpdateAudioCurveToggleText(false);
        UpdateAudioCurveUi(audioCurveCapture.Snapshot());
    }

    private void AudioCurveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        AudioCurveSnapshot snapshot = audioCurveCapture.Snapshot();
        UpdateAudioCurveUi(snapshot);
        if (!audioCurveCapture.IsRunning && snapshot.State == AudioCurveCaptureState.Error)
        {
            audioCurveTimer.Stop();
            UpdateAudioCurveToggleText(false);
        }
    }

    private void AudioCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AudioCurveSnapshot snapshot = audioCurveCapture.Snapshot();
        DrawAudioCurve(snapshot.Samples);
        DrawFrequencyCurves(snapshot.LowFrequencyLevels, snapshot.MidFrequencyLevels, snapshot.HighFrequencyLevels);
    }

    private void AudioFrequencyCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AudioCurveSnapshot snapshot = audioCurveCapture.Snapshot();
        DrawFrequencyCurves(snapshot.LowFrequencyLevels, snapshot.MidFrequencyLevels, snapshot.HighFrequencyLevels);
    }

    private void UpdateAudioCurveUi(AudioCurveSnapshot snapshot)
    {
        string endpointName = endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.EndpointPath, snapshot.EndpointPath, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? (string.IsNullOrWhiteSpace(snapshot.EndpointPath)
                ? Localization.Value("Status_Unavailable")
                : Localization.Text("MainPage_AudioCurveCurrentOutput"));
        AudioCurveEndpointTextBlock.Text = endpointName;
        AudioCurveStatusTextBlock.Text = snapshot.State switch
        {
            AudioCurveCaptureState.Starting => Localization.Text("MainPage_AudioCurveStarting"),
            AudioCurveCaptureState.Listening => Localization.Text("MainPage_AudioCurveListening"),
            AudioCurveCaptureState.NoSignal => Localization.Text("MainPage_AudioCurveNoSignal"),
            AudioCurveCaptureState.Error => string.IsNullOrWhiteSpace(snapshot.ErrorMessage)
                ? Localization.Text("MainPage_AudioCurveError")
                : $"{Localization.Text("MainPage_AudioCurveError")}: {snapshot.ErrorMessage}",
            _ => Localization.Text("MainPage_AudioCurveStopped")
        };
        AudioCurveRmsTextBlock.Text = snapshot.State is AudioCurveCaptureState.Listening or AudioCurveCaptureState.NoSignal
            ? $"{snapshot.RmsPercent:0.0}%"
            : "--%";
        AudioCurvePeakTextBlock.Text = snapshot.State is AudioCurveCaptureState.Listening or AudioCurveCaptureState.NoSignal
            ? $"{snapshot.PeakPercent:0.0}%"
            : "--%";
        AudioCurveEmptyTextBlock.Visibility = snapshot.State is AudioCurveCaptureState.Listening or AudioCurveCaptureState.NoSignal
            ? Visibility.Collapsed
            : Visibility.Visible;
        DrawAudioCurve(snapshot.Samples);
        DrawFrequencyCurves(snapshot.LowFrequencyLevels, snapshot.MidFrequencyLevels, snapshot.HighFrequencyLevels);
    }

    private void UpdateAudioCurveToggleText(bool running)
    {
        AudioCurveToggleIcon.Symbol = running ? Symbol.Pause : Symbol.Play;
        AudioCurveToggleTextBlock.Text = Localization.Text(running ? "MainPage_AudioCurveStop" : "MainPage_AudioCurveStart");
    }

    private void DrawAudioCurve(IReadOnlyList<float> samples)
    {
        double width = AudioCurveCanvas.ActualWidth;
        double height = AudioCurveCanvas.ActualHeight;
        if (width <= 4 || height <= 4) return;

        AudioCurveCenterLine.X1 = 0;
        AudioCurveCenterLine.X2 = width;
        AudioCurveCenterLine.Y1 = height / 2;
        AudioCurveCenterLine.Y2 = height / 2;
        AudioCurvePolyline.Points.Clear();
        if (samples.Count == 0) return;

        for (int index = 0; index < samples.Count; index++)
        {
            double x = samples.Count == 1 ? 0 : width * index / (samples.Count - 1);
            double y = height / 2 - Math.Clamp(samples[index], -1f, 1f) * height * 0.44;
            AudioCurvePolyline.Points.Add(new Point(x, y));
        }
    }

    private void DrawFrequencyCurves(
        IReadOnlyList<float> lowLevels,
        IReadOnlyList<float> midLevels,
        IReadOnlyList<float> highLevels)
    {
        double width = AudioFrequencyCanvas.ActualWidth;
        double height = AudioFrequencyCanvas.ActualHeight;
        if (width <= 4 || height <= 4) return;

        AudioFrequencyCenterLine.X1 = 0;
        AudioFrequencyCenterLine.X2 = width;
        AudioFrequencyCenterLine.Y1 = height - 2;
        AudioFrequencyCenterLine.Y2 = height - 2;
        AudioCurveLowPolyline.Points.Clear();
        AudioCurveMidPolyline.Points.Clear();
        AudioCurveHighPolyline.Points.Clear();
        AddFrequencyPoints(AudioCurveLowPolyline, lowLevels, width, height);
        AddFrequencyPoints(AudioCurveMidPolyline, midLevels, width, height);
        AddFrequencyPoints(AudioCurveHighPolyline, highLevels, width, height);
    }

    private static void AddFrequencyPoints(Polyline polyline, IReadOnlyList<float> levels, double width, double height)
    {
        if (levels.Count == 0) return;
        for (int index = 0; index < levels.Count; index++)
        {
            double x = levels.Count == 1 ? 0 : width * index / (levels.Count - 1);
            double level = Math.Clamp(levels[index], 0f, 1f);
            double y = height - 2 - level * (height - 8);
            polyline.Points.Add(new Point(x, y));
        }
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
        string result = await audioApiQueue.EnqueueAsync(() => Task.FromResult(
            ProcessAudioRouter.SetSystemDefaultOutputDevice(endpoint.EndpointPath, apply: true)));
        Log(result);
        if (config.GlobalVolumePercent is float globalVolume)
        {
            Log(await audioApiQueue.EnqueueAsync(() => Task.FromResult(AudioVolumeController.SetEndpointVolume(
                null,
                VolumeSafety.Clamp(globalVolume, config.VolumeProtectionEnabled),
                apply: true))));
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
            string result = await audioApiQueue.EnqueueAsync(
                () => Task.FromResult(AudioVolumeController.SetEndpointVolume(null, percent, apply: true)),
                applyCts.Token);
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

        AudioSystemStatus audio = await audioApiQueue.EnqueueAsync(() => Task.FromResult(AudioSystemState.Read(endpoints.ToList())));
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

        string result = await audioApiQueue.EnqueueAsync(() => AudioSystemState.SetDefaultSpatialAudioModeAsync(
            audio.DefaultEndpointPath,
            option.Id,
            apply: true));
        Log(result);

        bool confirmed = AudioSystemState.IsSpatialAudioResultConfirmed(result, option.Id);
        if (confirmed)
        {
            option.IsEnabled = true;
            UpdateGlobalSpatialAudio(option.Id);
        }
        else if (AudioSystemState.IsSpatialAudioPermissionDenied(result))
        {
            option.IsEnabled = false;
            Log($"Spatial audio option disabled for the current endpoint: {option.Name}.");
        }

        await RefreshAudioStateAsync();

        if (!confirmed && !string.Equals(lastDefaultSpatialAudioModeId, option.Id, StringComparison.OrdinalIgnoreCase))
        {
            RestoreGlobalSpatialAudioFromActual(lastDefaultSpatialAudioModeId);
        }
    }

    private void RestoreGlobalSpatialAudioFromActual(string modeId)
    {
        if (string.IsNullOrWhiteSpace(modeId) || string.Equals(modeId, "keep", StringComparison.OrdinalIgnoreCase)) return;

        // A failed user-initiated switch must not leave the desired global profile
        // pointing at the provider that Windows rejected for this endpoint.
        config.GlobalSpatialAudioModeId = modeId;
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId);
        if (provider == null || !provider.SupportsProfile(config.GlobalActiveProfile))
        {
            config.GlobalActiveProfile = provider?.DefaultActiveProfile ?? string.Empty;
        }

        SaveConfig();
        RefreshGlobalSpatialProfileChoices();
        activeGlobalSettingsSignature = string.Empty;
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

        bool changed = !string.Equals(config.GlobalSpatialAudioModeId, modeId, StringComparison.OrdinalIgnoreCase);
        config.GlobalSpatialAudioModeId = modeId;
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId);
        if (provider == null)
        {
            changed |= !string.IsNullOrWhiteSpace(config.GlobalActiveProfile);
            config.GlobalActiveProfile = string.Empty;
        }
        else if (!provider.SupportsProfile(config.GlobalActiveProfile))
        {
            config.GlobalActiveProfile = provider.DefaultActiveProfile;
            changed = true;
        }
        SaveConfig();
        RefreshGlobalSpatialProfileChoices();
        if (changed)
        {
            activeGlobalSettingsSignature = string.Empty;
            if (monitoring && audioStateInitialized) MonitorTimer_Tick(monitorTimer, new object());
        }
    }

    private void RefreshGlobalSpatialProfileChoices()
    {
        string modeId = !string.IsNullOrWhiteSpace(config.GlobalSpatialAudioModeId)
            ? config.GlobalSpatialAudioModeId
            : (!string.IsNullOrWhiteSpace(lastDefaultSpatialAudioModeId) ? lastDefaultSpatialAudioModeId : "off");
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId);

        suppressGlobalProfileSelection = true;
        IReadOnlyList<string> globalProfiles = GetSupportedProfiles(provider);
        SpatialDefaultProfileComboBox.ItemsSource = globalProfiles;
        SpatialDefaultProfileComboBox.IsEnabled = provider != null && globalProfiles.Count > 0;
        AddCustomProfileButton.IsEnabled = provider is DolbyCapxProfileProvider;
        SpatialDefaultProfileComboBox.SelectedItem = provider != null && provider.SupportsProfile(config.GlobalActiveProfile)
            ? config.GlobalActiveProfile
            : provider?.DefaultActiveProfile;
        suppressGlobalProfileSelection = false;
    }

    private IReadOnlyList<string> GetCustomProfileNames() =>
        (config.CustomEqualizerProfiles ?? new List<DolbyEqualizerProfile>())
            .Where(profile => profile != null && DolbyEqualizerCatalog.IsApplicationProfile(profile.Profile))
            .Select(profile => profile.Profile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private DolbyEqualizerSettings GetCustomEqualizerSettings()
    {
        config.CustomEqualizerProfiles ??= new List<DolbyEqualizerProfile>();
        return new DolbyEqualizerSettings { Profiles = config.CustomEqualizerProfiles };
    }

    private IReadOnlyList<string> GetSupportedProfiles(IAudioProfileProvider? provider)
    {
        if (provider == null) return Array.Empty<string>();
        if (provider is not DolbyCapxProfileProvider) return provider.SupportedProfiles;

        return provider.SupportedProfiles
            .Concat(GetCustomProfileNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async void AddCustomProfileButton_Click(object sender, RoutedEventArgs e)
    {
        string modeId = !string.IsNullOrWhiteSpace(config.GlobalSpatialAudioModeId)
            ? config.GlobalSpatialAudioModeId
            : lastDefaultSpatialAudioModeId;
        if (AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId) is not DolbyCapxProfileProvider) return;

        config.CustomEqualizerProfiles ??= new List<DolbyEqualizerProfile>();
        string? selectedProfile = SpatialDefaultProfileComboBox.SelectedItem as string;
        DolbyEqualizerProfile? editingProfile = config.CustomEqualizerProfiles.FirstOrDefault(profile =>
            profile != null && string.Equals(profile.Profile, selectedProfile, StringComparison.OrdinalIgnoreCase));
        string initialName = editingProfile == null
            ? $"EQ {config.CustomEqualizerProfiles.Count + 1}"
            : editingProfile.Profile.StartsWith(DolbyEqualizerCatalog.ApplicationProfilePrefix, StringComparison.OrdinalIgnoreCase)
                ? editingProfile.Profile[DolbyEqualizerCatalog.ApplicationProfilePrefix.Length..].Trim()
                : editingProfile.Profile;

        var nameBox = new TextBox
        {
            Header = Localization.Text("MainPage_CustomProfileName"),
            Text = initialName,
            PlaceholderText = Localization.Text("MainPage_CustomProfileNamePlaceholder")
        };
        var enabledCheckBox = new CheckBox
        {
            Content = Localization.Content("MainPage_CustomProfileEnabled"),
            IsChecked = editingProfile?.Enabled ?? true,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var surroundVirtualizerCheckBox = new CheckBox
        {
            Content = Localization.Content("MainPage_CustomProfileSurroundVirtualizer"),
            IsChecked = editingProfile?.SurroundVirtualizerEnabled ?? false,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var validationText = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var bandsPanel = new StackPanel { Spacing = 7, Margin = new Thickness(0, 10, 0, 0) };
        List<Slider> sliders = new();
        List<float> initialGains = DolbyEqualizerCatalog.NormalizeGains(editingProfile?.BandGains);
        for (int index = 0; index < DolbyEqualizerCatalog.BandCount; index++)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var frequency = new TextBlock
            {
                Text = FormatEqualizerFrequency(DolbyEqualizerCatalog.CenterFrequencies[index]),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(frequency, Localization.Get($"MainPage_CustomProfileBand{index}.ToolTip"));
            Grid.SetColumn(frequency, 0);
            var slider = new Slider
            {
                Minimum = DolbyEqualizerCatalog.MinimumGain,
                Maximum = DolbyEqualizerCatalog.MaximumGain,
                StepFrequency = 0.5,
                Value = initialGains[index],
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetColumn(slider, 1);
            var value = new TextBlock
            {
                Text = FormatEqualizerGain(initialGains[index]),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.78
            };
            Grid.SetColumn(value, 2);
            var bandLabel = new TextBlock
            {
                Text = Localization.Get($"MainPage_CustomProfileBandLabel{index}.Text"),
                FontSize = 12,
                Opacity = 0.68,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(bandLabel, 3);
            slider.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue)) value.Text = FormatEqualizerGain((float)args.NewValue);
            };
            row.Children.Add(frequency);
            row.Children.Add(slider);
            row.Children.Add(value);
            row.Children.Add(bandLabel);
            bandsPanel.Children.Add(row);
            sliders.Add(slider);
        }

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            Text = Localization.Text("MainPage_CustomProfileHint"),
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = Localization.Get("MainPage_CustomProfileBandHint.Text"),
            Opacity = 0.58,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(nameBox);
        body.Children.Add(enabledCheckBox);
        body.Children.Add(surroundVirtualizerCheckBox);
        body.Children.Add(bandsPanel);
        body.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            Title = Localization.Text(editingProfile == null
                ? "MainPage_CustomProfileDialogTitle"
                : "MainPage_CustomProfileEditTitle"),
            PrimaryButtonText = Localization.Text("MainPage_CustomProfileSave"),
            SecondaryButtonText = editingProfile == null
                ? null
                : Localization.Text("MainPage_CustomProfileDelete"),
            CloseButtonText = Localization.Text("MainPage_CustomProfileCancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                Content = body,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            XamlRoot = XamlRoot
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                validationText.Text = Localization.Text("MainPage_CustomProfileNameRequired");
                args.Cancel = true;
                return;
            }

            string profileId = DolbyEqualizerCatalog.CreateApplicationProfile(name);
            bool duplicate = config.CustomEqualizerProfiles.Any(profile =>
                profile != null && !ReferenceEquals(profile, editingProfile) &&
                string.Equals(profile.Profile, profileId, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                validationText.Text = Localization.Text("MainPage_CustomProfileNameDuplicate");
                args.Cancel = true;
            }
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && editingProfile != null)
        {
            DeleteCustomProfile(editingProfile);
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        string profileName = DolbyEqualizerCatalog.CreateApplicationProfile(nameBox.Text.Trim());
        DolbyEqualizerProfile savedProfile = editingProfile ?? new DolbyEqualizerProfile();
        savedProfile.Profile = profileName;
        savedProfile.Enabled = enabledCheckBox.IsChecked == true;
        savedProfile.SurroundVirtualizerEnabled = surroundVirtualizerCheckBox.IsChecked == true;
        savedProfile.BandGains = sliders.Select(slider => (float)slider.Value).ToList();
        if (editingProfile == null) config.CustomEqualizerProfiles.Add(savedProfile);
        SaveConfig();
        RefreshGlobalSpatialProfileChoices();
        if (ProfileEditorHost.Content is ProcessProfileView view) view.SetCustomProfiles(GetCustomProfileNames());
    }

    private void DeleteCustomProfile(DolbyEqualizerProfile profile)
    {
        config.CustomEqualizerProfiles ??= new List<DolbyEqualizerProfile>();
        config.CustomEqualizerProfiles.Remove(profile);
        string deletedProfile = profile.Profile;

        if (string.Equals(config.GlobalActiveProfile, deletedProfile, StringComparison.OrdinalIgnoreCase))
        {
            config.GlobalActiveProfile = DefaultProfileForMode(config.GlobalSpatialAudioModeId);
        }

        foreach (ProcessSwitchItem process in config.Processes)
        {
            ClearDeletedProfileReference(process, deletedProfile);
            foreach (ProcessAddressRule addressRule in process.AddressRules ?? new List<ProcessAddressRule>())
            {
                if (addressRule.Action != null) ClearDeletedProfileReference(addressRule.Action, deletedProfile);
            }
        }

        SaveConfig();
        activeGlobalSettingsSignature = string.Empty;
        RefreshGlobalSpatialProfileChoices();
        if (ProfileEditorHost.Content is ProcessProfileView view)
        {
            view.SetCustomProfiles(GetCustomProfileNames());
        }
        if (monitoring && audioStateInitialized) MonitorTimer_Tick(monitorTimer, new object());
    }

    private static void ClearDeletedProfileReference(ProcessSwitchItem item, string deletedProfile)
    {
        if (!string.Equals(item.ActiveProfile, deletedProfile, StringComparison.OrdinalIgnoreCase)) return;
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(item.ActiveSpatialAudioModeId);
        item.ActiveProfile = provider?.DefaultActiveProfile ?? string.Empty;
    }

    private static string DefaultProfileForMode(string? modeId) =>
        AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId)?.DefaultActiveProfile ?? string.Empty;

    private static string FormatEqualizerFrequency(int frequency) => frequency >= 1000
        ? $"{frequency / 1000d:0.#} kHz"
        : $"{frequency} Hz";

    private static string FormatEqualizerGain(float gain) => $"{gain:0.0} dB";

    private void SpatialDefaultProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressGlobalProfileSelection || SpatialDefaultProfileComboBox.SelectedItem is not string profile) return;

        string modeId = !string.IsNullOrWhiteSpace(config.GlobalSpatialAudioModeId)
            ? config.GlobalSpatialAudioModeId
            : lastDefaultSpatialAudioModeId;
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId);
        if (provider == null || !provider.SupportsProfile(profile) || string.Equals(config.GlobalActiveProfile, profile, StringComparison.OrdinalIgnoreCase)) return;

        config.GlobalActiveProfile = profile;
        SaveConfig();
        activeGlobalSettingsSignature = string.Empty;

        // The monitor intentionally ignores AudioSwitch while its own window is
        // foreground. A profile selected from this ComboBox must therefore be
        // applied directly instead of waiting for the foreground monitor.
        if (RunGlobalProfileAsync(profile, modeId, "global-spatial"))
        {
            activeGlobalSettingsSignature = GlobalSettingsSignature();
        }
    }

    private void RefreshProfileEditorEndpoints()
    {
        if (ProfileEditorHost.Content is ProcessProfileView view)
        {
            view.SetEndpoints(endpoints);
            view.SetCustomProfiles(GetCustomProfileNames());
            if (view.SelectedEndpoint is { IsKeepCurrent: false } endpoint) ProbeEndpointForView(view, endpoint);
        }
    }

    private void ProcessListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessListView.SelectedItem is not ProcessChoice choice)
        {
            RemoveProcessButton.IsEnabled = false;
            AddAddressRuleProcessButton.IsEnabled = false;
            return;
        }

        RemoveProcessButton.IsEnabled = true;
        if (choice.IsAddressRule && addressRuleConfigs.TryGetValue(choice.RuleId, out (ProcessSwitchItem Parent, ProcessAddressRule Rule) addressContext))
        {
            AddAddressRuleProcessButton.IsEnabled = addressContext.Parent.MatchMode != ProcessMatchMode.HttpState;
            ShowAddressRuleProfile(choice, addressContext.Parent, addressContext.Rule);
        }
        else if (processConfigs.TryGetValue(choice.RuleId, out ProcessSwitchItem? model))
        {
            AddAddressRuleProcessButton.IsEnabled = model.MatchMode != ProcessMatchMode.HttpState;
            ShowProcessProfile(choice, model);
        }
        else
        {
            RemoveProcessButton.IsEnabled = false;
            AddAddressRuleProcessButton.IsEnabled = false;
        }
        UpdateProcessContextMenuState();
    }

    private void ShowProcessProfile(ProcessChoice choice, ProcessSwitchItem model)
    {
        var view = new ProcessProfileView(model, ReadEndpointPath(model.EndpointFile));
        bool volumeChanged = view.SetVolumeProtection(config.VolumeProtectionEnabled);
        view.SetEndpoints(endpoints);
        view.SetCustomProfiles(GetCustomProfileNames());
        view.SettingsChanged += ProfileView_SettingsChanged;
        view.TestActiveRequested += ProfileView_TestActiveRequested;
        ProfileEditorHost.Content = view;
        if (volumeChanged) SaveConfig();
        if (view.SelectedEndpoint is { IsKeepCurrent: false } endpoint) ProbeEndpointForView(view, endpoint);
    }

    private void ShowAddressRuleProfile(ProcessChoice choice, ProcessSwitchItem parent, ProcessAddressRule addressRule)
    {
        addressRule.Action ??= new ProcessSwitchItem { Name = parent.Name };
        addressRule.Action.Name = parent.Name;
        var view = new ProcessProfileView(
            addressRule.Action,
            ReadEndpointPath(addressRule.Action.EndpointFile),
            addressRule,
            parent);
        bool volumeChanged = view.SetVolumeProtection(config.VolumeProtectionEnabled);
        view.SetEndpoints(endpoints);
        view.SetCustomProfiles(GetCustomProfileNames());
        view.SettingsChanged += ProfileView_SettingsChanged;
        view.TestActiveRequested += ProfileView_TestActiveRequested;
        ProfileEditorHost.Content = view;
        RefreshAddressRuleChoice(addressRule);
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
            ReadEndpointPath(model.EndpointFile),
            model.GlobalVolumePercent,
            model.ActiveProfile,
            model.ActiveSpatialAudioModeId,
            model.DolbyEqualizer?.Clone() ?? new DolbyEqualizerSettings());
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
        target.GlobalVolumePercent = VolumeSafety.Clamp(copiedProcessRule.GlobalVolumePercent, config.VolumeProtectionEnabled);
        target.ActiveProfile = copiedProcessRule.ActiveProfile;
        target.ActiveSpatialAudioModeId = copiedProcessRule.ActiveSpatialAudioModeId;
        target.DolbyEqualizer = copiedProcessRule.DolbyEqualizer.Clone();
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
        AudioCurvePage.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = tab == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (tab != 3) StopAudioCurve();
        UpdateTabSelection(tab);
        AnimateTabPage(tab switch
        {
            1 => OutputDevicesPage,
            2 => SpatialAudioPage,
            3 => AudioCurvePage,
            4 => GeneralPage,
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
        Button[] buttons = { ProcessRulesTabButton, OutputDevicesTabButton, SpatialAudioTabButton, AudioCurveTabButton, GeneralTabButton };
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

    private async void AddAddressRuleProcessButton_Click(object sender, RoutedEventArgs e)
    {
        ProcessSwitchItem? parent = null;
        ProcessChoice? parentChoice = null;
        if (ProcessListView.SelectedItem is ProcessChoice selected)
        {
            if (selected.IsAddressRule && addressRuleConfigs.TryGetValue(selected.RuleId, out (ProcessSwitchItem Parent, ProcessAddressRule Rule) addressContext))
            {
                parent = addressContext.Parent;
            }
            else if (processConfigs.TryGetValue(selected.RuleId, out ProcessSwitchItem? selectedParent))
            {
                parent = selectedParent;
            }
        }

        if (parent == null || parent.MatchMode == ProcessMatchMode.HttpState) return;
        parentChoice = processChoices.FirstOrDefault(choice => string.Equals(choice.RuleId, parent.RuleId, StringComparison.OrdinalIgnoreCase));
        if (parentChoice == null) return;

        if (!IsSupportedBrowserProcess(parent.Name))
        {
            await ShowBrowserIntegrationDialogAsync(chromeSelected: false);
            return;
        }

        if (!config.BrowserIntegrationPromptDismissed)
        {
            ContentDialogResult result = await ShowBrowserIntegrationDialogAsync(chromeSelected: true);
            if (result != ContentDialogResult.Primary) return;
        }

        var addressRule = new ProcessAddressRule
        {
            Name = Localization.Value("ProcessProfile_NewAddressRule"),
            Action = new ProcessSwitchItem
            {
                Name = parent.Name,
                MatchMode = ProcessMatchMode.ProcessName,
                ForegroundOnly = false,
                EndpointFile = parent.EndpointFile,
                ActiveProfile = parent.ActiveProfile,
                ActiveSpatialAudioModeId = parent.ActiveSpatialAudioModeId,
                GlobalVolumePercent = parent.GlobalVolumePercent,
                DolbyEqualizer = parent.DolbyEqualizer?.Clone() ?? new DolbyEqualizerSettings()
            }
        };
        parent.AddressRules.Add(addressRule);
        addressRuleConfigs[addressRule.RuleId] = (parent, addressRule);

        int parentIndex = processChoices.IndexOf(parentChoice);
        int insertIndex = parentIndex + 1;
        while (insertIndex < processChoices.Count &&
               processChoices[insertIndex].IsAddressRule &&
               string.Equals(processChoices[insertIndex].ParentRuleId, parent.RuleId, StringComparison.OrdinalIgnoreCase))
        {
            insertIndex++;
        }

        ProcessChoice childChoice = ProcessChoice.FromAddressRule(parent, addressRule);
        processChoices.Insert(insertIndex, childChoice);
        UpdateProcessEmptyState();
        ProcessListView.SelectedItem = childChoice;
        SaveConfig();
        UpdateMonitoringSchedule();
    }

    private static bool IsSupportedBrowserProcess(string? processName)
    {
        string normalized = Path.GetFileNameWithoutExtension((processName ?? string.Empty).Trim());
        return BrowserProcessNames.Contains(normalized);
    }

    private async Task<ContentDialogResult> ShowBrowserIntegrationDialogAsync(bool chromeSelected)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Localization.Text(chromeSelected
                ? "BrowserIntegrationDialog.Description"
                : "BrowserIntegrationDialog.ChromeRequiredDescription"),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = Localization.Text("BrowserIntegrationDialog.PluginHint"),
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });

        CheckBox? dontShowAgain = null;
        if (chromeSelected)
        {
            dontShowAgain = new CheckBox
            {
                Content = Localization.Text("BrowserIntegrationDialog.DontShowAgain"),
                Margin = new Thickness(0, 4, 0, 0)
            };
            content.Children.Add(dontShowAgain);
        }

        var dialog = new ContentDialog
        {
            Title = Localization.Text(chromeSelected
                ? "BrowserIntegrationDialog.Title"
                : "BrowserIntegrationDialog.ChromeRequiredTitle"),
            PrimaryButtonText = Localization.Text(chromeSelected
                ? "BrowserIntegrationDialog.Continue"
                : "BrowserIntegrationDialog.Acknowledge"),
            SecondaryButtonText = Localization.Text("BrowserIntegrationDialog.OpenPlugin"),
            CloseButtonText = Localization.Text("BrowserIntegrationDialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (chromeSelected && dontShowAgain?.IsChecked == true)
        {
            config.BrowserIntegrationPromptDismissed = true;
            AppPaths.EnsureDataDirectories();
            config.Save(AppPaths.ConfigPath);
        }

        if (result == ContentDialogResult.Secondary)
        {
            OpenChromeExtensionDirectory();
        }

        return result;
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
        UpdateMonitoringSchedule();
    }

    private void RemoveProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessListView.SelectedItem is not ProcessChoice choice) return;

        if (choice.IsAddressRule && addressRuleConfigs.TryGetValue(choice.RuleId, out (ProcessSwitchItem Parent, ProcessAddressRule Rule) addressContext))
        {
            addressContext.Parent.AddressRules.Remove(addressContext.Rule);
            addressRuleConfigs.Remove(choice.RuleId);
            temporarilyDisabledRuleIds.Remove(choice.RuleId);
            processChoices.Remove(choice);
            UpdateProcessEmptyState();
            ProcessChoice? parentChoice = processChoices.FirstOrDefault(item => string.Equals(item.RuleId, addressContext.Parent.RuleId, StringComparison.OrdinalIgnoreCase));
            if (parentChoice != null) ProcessListView.SelectedItem = parentChoice;
            SaveConfig();
            UpdateMonitoringSchedule();
            return;
        }

        if (!processConfigs.TryGetValue(choice.RuleId, out ProcessSwitchItem? model)) return;
        processConfigs.Remove(choice.RuleId);
        temporarilyDisabledRuleIds.Remove(choice.RuleId);
        foreach (ProcessAddressRule addressRule in model.AddressRules)
        {
            addressRuleConfigs.Remove(addressRule.RuleId);
            temporarilyDisabledRuleIds.Remove(addressRule.RuleId);
        }
        config.Processes.Remove(model);
        foreach (ProcessChoice processChoice in processChoices
                     .Where(item => string.Equals(item.RuleId, model.RuleId, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(item.ParentRuleId, model.RuleId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            processChoices.Remove(processChoice);
        }
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
        UpdateMonitoringSchedule();
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
            int browserOrder = IsSupportedBrowserProcess(a.Name).CompareTo(IsSupportedBrowserProcess(b.Name));
            if (browserOrder != 0) return browserOrder;
            int windowOrder = b.HasWindow.CompareTo(a.HasWindow);
            return windowOrder != 0 ? windowOrder : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });
        return result;
    }

    private void ProfileView_SettingsChanged(object? sender, EventArgs e)
    {
        if (sender is not ProcessProfileView view) return;

        if (view.AddressRule is ProcessAddressRule addressRule && view.ParentModel is ProcessSwitchItem parentModel)
        {
            if (view.SelectedEndpoint is { IsKeepCurrent: true })
            {
                view.Model.EndpointFile = string.Empty;
            }
            else if (view.SelectedEndpoint is AudioEndpointChoice addressEndpoint)
            {
                string? currentPath = ReadEndpointPath(view.Model.EndpointFile);
                if (!string.Equals(currentPath, addressEndpoint.EndpointPath, StringComparison.OrdinalIgnoreCase))
                {
                    string endpointFile = EndpointFileForAddressRule(parentModel.Name, addressRule.RuleId);
                    File.WriteAllText(endpointFile, addressEndpoint.EndpointPath, Encoding.UTF8);
                    view.Model.EndpointFile = endpointFile;
                }
                ProbeEndpointForView(view, addressEndpoint);
            }

            RefreshAddressRuleChoice(addressRule);
            SaveConfig();
            UpdateMonitoringSchedule();
            return;
        }

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
        UpdateMonitoringSchedule();
        // Rule edits are persisted here. The monitor applies the rule only
        // after it wins the normal match-priority selection; editing an
        // inactive rule must not change the system default output immediately.
    }

    private void RefreshAddressRuleChoice(ProcessAddressRule addressRule)
    {
        if (addressRuleConfigs.TryGetValue(addressRule.RuleId, out _) &&
            processChoices.FirstOrDefault(choice => string.Equals(choice.RuleId, addressRule.RuleId, StringComparison.OrdinalIgnoreCase)) is ProcessChoice choice)
        {
            choice.UpdateAddressRule(addressRule);
        }
    }

    private void ProfileView_TestActiveRequested(object? sender, EventArgs e)
    {
        if (sender is ProcessProfileView view)
        {
            RunProfileAsync(
                view.Model,
                view.Model.ActiveProfile,
                view.Model.ActiveSpatialAudioModeId,
                $"{view.Model.RuleId}:test");
        }
    }

    private Task EnsureEndpointProbeAsync(AudioEndpointChoice endpoint)
    {
        if (endpoint.ProbeCompleted) return Task.CompletedTask;
        lock (endpointProbeTasks)
        {
            if (!endpointProbeTasks.TryGetValue(endpoint.EndpointId, out Task? task))
            {
                task = audioApiQueue.EnqueueAsync(() => AudioProfileProviderRegistry.ProbeAllAsync(endpoint));
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
        pendingForegroundProcessId = null;
        foregroundDebounceTimer.Stop();
        Interlocked.Exchange(ref audioEvaluationPending, 0);
        activeGlobalSettingsSignature = string.Empty;
        foregroundCallbackActive = foregroundWindowMonitor?.Start() == true;
        UpdateMonitoringSchedule();
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
        foregroundDebounceTimer.Stop();
        pendingForegroundProcessId = null;
        foregroundWindowMonitor?.Stop();
        foregroundCallbackActive = false;
        Interlocked.Exchange(ref audioEvaluationPending, 0);
        activeGlobalSettingsSignature = string.Empty;
        MonitorButtonText.Text = Localization.Text("MainPage_StartMonitoring");
        MonitorButtonIcon.Symbol = Symbol.Play;
        StatusText.Text = Localization.Value("Status_MonitoringStopped");
        Log("Monitoring stopped.");
    }

    private void UpdateMonitoringSchedule()
    {
        if (!monitoring)
        {
            monitorTimer.Stop();
            return;
        }

        bool hasBackgroundRules = config.Processes.Any(item => !item.ForegroundOnly);
        if (!foregroundCallbackActive || hasBackgroundRules)
        {
            // Foreground changes are event-driven. Keep only a low-frequency
            // reconciliation pass for background rules; use the configured
            // interval as the full fallback when hook registration failed.
            int seconds = foregroundCallbackActive
                ? Math.Max(10, config.IntervalSeconds)
                : Math.Max(1, config.IntervalSeconds);
            monitorTimer.Interval = TimeSpan.FromSeconds(seconds);
            monitorTimer.Start();
        }
        else
        {
            monitorTimer.Stop();
        }
    }

    private void ForegroundWindowChanged(int? processId)
    {
        if (!monitoring) return;

        pendingForegroundProcessId = processId;
        foregroundDebounceTimer.Stop();
        foregroundDebounceTimer.Start();
        LogMonitorDecision(
            $"foreground-callback-queued|{processId?.ToString() ?? "<none>"}",
            $"Foreground callback queued: pid={processId?.ToString() ?? "<none>"}; waiting for a stable window.");
    }

    private void ForegroundDebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        foregroundDebounceTimer.Stop();
        if (!monitoring) return;

        int? processId = pendingForegroundProcessId;
        pendingForegroundProcessId = null;
        LogMonitorDecision(
            $"foreground-stable|{processId?.ToString() ?? "<none>"}",
            $"Foreground callback stabilized: pid={processId?.ToString() ?? "<none>"}.");
        MonitorTimer_Tick(monitorTimer, new object(), processId);
    }

    private Task<HttpStateDispatchResult> HandleHttpStateAsync(HttpStateMessage state)
    {
        var completion = new TaskCompletionSource<HttpStateDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                completion.TrySetResult(DispatchHttpState(state));
            }
            catch (Exception ex)
            {
                completion.TrySetResult(new HttpStateDispatchResult(500, false, false, ex.Message));
            }
        }))
        {
            completion.TrySetResult(new HttpStateDispatchResult(503, false, false, "AudioSwitch UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private HttpStateDispatchResult DispatchHttpState(HttpStateMessage state)
    {
        latestHttpStates[state.ProcessName] = state;
        // Dispatch the event that just arrived. The cached states are for the
        // monitor fallback only; they must not let an unrelated older browser
        // state trigger this request.
        AddressRuleMatch? addressMatch = FindBestAddressRuleMatch(state);
        HttpStateMatch? match = FindBestHttpStateMatch(state);
        if (addressMatch != null &&
            (match == null || AddressRulePriority(addressMatch) >= RulePriority(match.Rule)))
        {
            string addressSignature = AddressRuleSignature(addressMatch);
            if (string.Equals(activeGlobalSettingsSignature, addressSignature, StringComparison.Ordinal))
            {
                return new HttpStateDispatchResult(200, true, false, "The matching address sub-rule is already active.", addressMatch.Rule.RuleId, addressMatch.Rule.DisplayName);
            }

            bool addressQueued = RunAddressProfileAsync(addressMatch, AddressRuleOperationKey(addressMatch));
            if (!addressQueued)
            {
                return new HttpStateDispatchResult(409, true, false, "The audio queue is busy; the address state was recorded and will be retried by monitoring.", addressMatch.Rule.RuleId, addressMatch.Rule.DisplayName);
            }

            activeGlobalSettingsSignature = addressSignature;
            Log($"HTTP address sub-rule matched '{addressMatch.Rule.DisplayName}': process={state.ProcessName}; address={state.Address}");
            return new HttpStateDispatchResult(200, true, true, "The matching address audio action was queued.", addressMatch.Rule.RuleId, addressMatch.Rule.DisplayName);
        }

        if (match == null)
        {
            Log($"HTTP state received without a matching rule: process={state.ProcessName}; address={state.Address}; title={state.Title}; status={state.StatusText}; state={state.State}");
            return new HttpStateDispatchResult(404, false, false, "No HTTP state rule matched the message.");
        }

        string signature = HttpStateSignature(match);
        if (string.Equals(activeGlobalSettingsSignature, signature, StringComparison.Ordinal))
        {
            return new HttpStateDispatchResult(200, true, false, "The matching HTTP state is already active.", match.Rule.RuleId, match.Rule.Name);
        }

        bool queued = RunProfileAsync(
            match.Rule,
            match.Rule.ActiveProfile,
            match.Rule.ActiveSpatialAudioModeId,
            HttpStateOperationKey(match));
        if (!queued)
        {
            return new HttpStateDispatchResult(409, true, false, "The audio queue is busy; the HTTP state was recorded and will be retried by monitoring.", match.Rule.RuleId, match.Rule.Name);
        }

        activeGlobalSettingsSignature = signature;
        Log($"HTTP state matched rule '{match.Rule.Name}': process={state.ProcessName}; address={state.Address}; title={state.Title}; status={state.StatusText}; state={state.State}");
        return new HttpStateDispatchResult(200, true, true, "The matching audio action was queued.", match.Rule.RuleId, match.Rule.Name);
    }

    private AddressRuleMatch? FindBestAddressRuleMatch(HttpStateMessage? onlyState = null)
    {
        var matches = new List<AddressRuleMatch>();
        int order = 0;
        foreach (ProcessSwitchItem parent in config.Processes)
        {
            if (temporarilyDisabledRuleIds.Contains(parent.RuleId) ||
                parent.MatchMode == ProcessMatchMode.HttpState ||
                parent.AddressRules.Count == 0)
            {
                order++;
                continue;
            }

            IEnumerable<HttpStateMessage> states = onlyState == null
                ? latestHttpStates.Values
                : new[] { onlyState };
            foreach (ProcessAddressRule addressRule in parent.AddressRules)
            {
                foreach (HttpStateMessage state in states)
                {
                    if (AddressRuleMatches(parent, addressRule, state))
                    {
                        matches.Add(new AddressRuleMatch(parent, addressRule, state, order));
                    }
                }
            }
            order++;
        }

        return matches
            .OrderByDescending(AddressRulePriority)
            .ThenBy(match => match.Order)
            .FirstOrDefault();
    }

    private static bool AddressRuleMatches(ProcessSwitchItem parent, ProcessAddressRule addressRule, HttpStateMessage state) =>
        !string.IsNullOrWhiteSpace(addressRule.Address) &&
        ProcessNamesEqual(parent.Name, state.ProcessName) &&
        state.Address.Contains(addressRule.Address.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int AddressRulePriority(AddressRuleMatch match)
    {
        if (match.Rule.Action.PriorityOverride.HasValue) return match.Rule.Action.PriorityOverride.Value;
        return 250 + Math.Min(match.Rule.Address.Trim().Length, 100);
    }

    private string AddressRuleSignature(AddressRuleMatch match)
    {
        ProcessSwitchItem action = match.Rule.Action;
        string value = string.Join("\u001f",
            match.Parent.RuleId,
            match.Rule.RuleId,
            match.State.ProcessName,
            match.State.Address,
            match.State.Title,
            match.State.StatusText,
            match.State.State,
            action.ActiveSpatialAudioModeId,
            action.ActiveProfile,
            ReadEndpointPath(action.EndpointFile) ?? string.Empty,
            action.GlobalVolumePercent?.ToString() ?? "<parent>",
            match.Parent.GlobalVolumePercent?.ToString() ?? "<default>",
            config.GlobalVolumePercent?.ToString() ?? "<none>");
        return "address|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string AddressRuleOperationKey(AddressRuleMatch match) =>
        "address:" + match.Rule.RuleId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", match.State.ProcessName, match.State.Address)))).Substring(0, 16);

    private HttpStateMatch? FindBestHttpStateMatch(HttpStateMessage? onlyState = null)
    {
        var matches = new List<HttpStateMatch>();
        int order = 0;
        foreach (ProcessSwitchItem rule in config.Processes)
        {
            if (rule.MatchMode != ProcessMatchMode.HttpState) { order++; continue; }
            IEnumerable<HttpStateMessage> states = onlyState == null
                ? latestHttpStates.Values
                : new[] { onlyState };
            foreach (HttpStateMessage state in states)
            {
                if (HttpStateMatches(rule, state)) matches.Add(new HttpStateMatch(rule, state, order));
            }
            order++;
        }

        return matches
            .OrderByDescending(match => RulePriority(match.Rule))
            .ThenBy(match => match.Order)
            .FirstOrDefault();
    }

    private static bool HttpStateMatches(ProcessSwitchItem rule, HttpStateMessage state) =>
        ProcessNamesEqual(rule.Name, state.ProcessName) &&
        RemoteFieldMatches(rule.RemoteAddress, state.Address) &&
        RemoteFieldMatches(rule.RemoteTitle, state.Title) &&
        RemoteFieldMatches(rule.RemoteStatusText, state.StatusText) &&
        RemoteFieldMatches(rule.RemoteState, state.State);

    private static bool ProcessNamesEqual(string left, string right)
    {
        static string Normalize(string value)
        {
            string result = value.Trim();
            return result.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool RemoteFieldMatches(string expected, string actual) =>
        string.IsNullOrWhiteSpace(expected) ||
        actual.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string HttpStateSignature(HttpStateMatch match)
    {
        string value = string.Join("\u001f", match.Rule.RuleId, match.State.ProcessName, match.State.Address, match.State.Title, match.State.StatusText, match.State.State, match.Rule.ActiveSpatialAudioModeId, match.Rule.ActiveProfile, ReadEndpointPath(match.Rule.EndpointFile) ?? string.Empty);
        return "http|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string HttpStateOperationKey(HttpStateMatch match) =>
        "http:" + match.Rule.RuleId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", match.State.ProcessName, match.State.Address, match.State.Title, match.State.StatusText, match.State.State)))).Substring(0, 16);

    private void MonitorTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        MonitorTimer_Tick(sender, args, null);
    }

    private void MonitorTimer_Tick(DispatcherQueueTimer sender, object args, int? foregroundProcessIdOverride)
    {
        if (!audioStateInitialized) return;

        if (Volatile.Read(ref audioOperationInProgress) != 0 || audioApiQueue.IsBusy)
        {
            Interlocked.Exchange(ref audioEvaluationPending, 1);
            LogMonitorDecision(
                "monitor-deferred-audio-operation",
                "Monitor evaluation deferred while an audio operation is in progress.");
            return;
        }

        int? foregroundProcessId = foregroundProcessIdOverride ?? GetForegroundProcessId();

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

        List<ProcessSwitchItem> rules = config.Processes.ToList();
        var candidates = new Dictionary<int, List<(ProcessSwitchItem Rule, int Order)>>();
        var ruleMatches = new List<string>();
        int order = 0;
        foreach (ProcessSwitchItem item in rules)
        {
            if (temporarilyDisabledRuleIds.Contains(item.RuleId)) continue;
            List<int> processIds = GetMatchingProcessIds(item, foregroundProcessId);
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

        ProcessSwitchItem? activeRule = effectiveRules
            .Select(pair => (Rule: pair.Value, ProcessId: pair.Key))
            .OrderByDescending(match => RulePriority(match.Rule))
            .ThenByDescending(match => match.ProcessId == foregroundProcessId)
            .Select(match => match.Rule)
            .FirstOrDefault();

        AddressRuleMatch? activeAddressRule = FindBestAddressRuleMatch();
        HttpStateMatch? activeHttpState = FindBestHttpStateMatch();
        if (activeAddressRule != null &&
            (activeRule == null || AddressRulePriority(activeAddressRule) >= RulePriority(activeRule)) &&
            (activeHttpState == null || AddressRulePriority(activeAddressRule) >= RulePriority(activeHttpState.Rule)))
        {
            string addressSignature = AddressRuleSignature(activeAddressRule);
            if (!string.Equals(activeGlobalSettingsSignature, addressSignature, StringComparison.Ordinal))
            {
                if (RunAddressProfileAsync(activeAddressRule, AddressRuleOperationKey(activeAddressRule)))
                {
                    activeGlobalSettingsSignature = addressSignature;
                }
            }
            return;
        }

        if (activeHttpState != null &&
            (activeRule == null || RulePriority(activeHttpState.Rule) >= RulePriority(activeRule)))
        {
            activeRule = activeHttpState.Rule;
        }

        if (activeRule != null)
        {
            bool useHttpState = activeHttpState != null && ReferenceEquals(activeHttpState.Rule, activeRule);
            string ruleSignature = useHttpState
                ? HttpStateSignature(activeHttpState!)
                : $"rule|{activeRule.RuleId}|{activeRule.ActiveSpatialAudioModeId}|{activeRule.ActiveProfile}|{ReadEndpointPath(activeRule.EndpointFile)}|{activeRule.GlobalVolumePercent?.ToString() ?? "<default>"}|{config.GlobalVolumePercent?.ToString() ?? "<none>"}";
            if (!string.Equals(activeGlobalSettingsSignature, ruleSignature, StringComparison.Ordinal))
            {
                string operationKey = useHttpState
                    ? HttpStateOperationKey(activeHttpState!)
                    : $"{activeRule.RuleId}:spatial";
                if (RunProfileAsync(activeRule, activeRule.ActiveProfile, activeRule.ActiveSpatialAudioModeId, operationKey))
                {
                    activeGlobalSettingsSignature = ruleSignature;
                }
            }
            return;
        }

        string globalSignature = GlobalSettingsSignature();
        if (!string.Equals(activeGlobalSettingsSignature, globalSignature, StringComparison.Ordinal))
        {
            string globalMode = string.IsNullOrWhiteSpace(config.GlobalSpatialAudioModeId) ? "off" : config.GlobalSpatialAudioModeId;
            string globalProfile = config.GlobalActiveProfile ?? string.Empty;
            if (RunGlobalProfileAsync(globalProfile, globalMode, "global-spatial"))
            {
                activeGlobalSettingsSignature = globalSignature;
            }
        }
    }

    private string GlobalSettingsSignature()
    {
        string globalMode = string.IsNullOrWhiteSpace(config.GlobalSpatialAudioModeId) ? "off" : config.GlobalSpatialAudioModeId;
        string globalProfile = config.GlobalActiveProfile ?? string.Empty;
        string globalEndpointPath = GetGlobalEndpointPath();
        return $"global|{globalMode}|{globalProfile}|{globalEndpointPath}|{config.GlobalVolumePercent?.ToString() ?? "<none>"}";
    }

    private static int RulePriority(ProcessSwitchItem item)
    {
        if (item.PriorityOverride.HasValue) return item.PriorityOverride.Value;

        if (item.MatchMode == ProcessMatchMode.HttpState) return 200;

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

    private static List<int> GetMatchingProcessIds(ProcessSwitchItem item, int? foregroundProcessId)
    {
        var result = new List<int>();
        if (item.MatchMode == ProcessMatchMode.HttpState) return result;
        try
        {
            if (item.MatchMode == ProcessMatchMode.ProcessName)
            {
                foreach (Process process in Process.GetProcessesByName(item.Name))
                {
                    using (process) result.Add(process.Id);
                }
                return ApplyForegroundFilter(item, result, foregroundProcessId);
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
        return ApplyForegroundFilter(item, result, foregroundProcessId);
    }

    private static List<int> ApplyForegroundFilter(ProcessSwitchItem item, List<int> processIds, int? foregroundProcessId)
    {
        if (!item.ForegroundOnly || processIds.Count == 0) return processIds;

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

    private bool RunProfileAsync(
        ProcessSwitchItem item,
        string profile,
        string spatialAudioModeId,
        string? operationKeyOverride = null)
    {
        return RunAudioOperationAsync(
            item.Name,
            ReadEndpointPath(item.EndpointFile),
            profile,
            spatialAudioModeId,
            apply: true,
            equalizer: GetCustomEqualizerSettings(),
            endpointVolumePercent: VolumeSafety.Clamp(
                item.GlobalVolumePercent ?? config.GlobalVolumePercent,
                config.VolumeProtectionEnabled),
            operationKey: operationKeyOverride ?? item.RuleId);
    }

    private bool RunAddressProfileAsync(AddressRuleMatch match, string operationKey)
    {
        ProcessSwitchItem action = match.Rule.Action;
        float? requestedVolume = action.GlobalVolumePercent ?? match.Parent.GlobalVolumePercent ?? config.GlobalVolumePercent;
        return RunAudioOperationAsync(
            $"{match.Parent.Name} / {match.Rule.DisplayName}",
            ReadEndpointPath(action.EndpointFile),
            action.ActiveProfile,
            action.ActiveSpatialAudioModeId,
            apply: true,
            equalizer: GetCustomEqualizerSettings(),
            endpointVolumePercent: VolumeSafety.Clamp(requestedVolume, config.VolumeProtectionEnabled),
            operationKey: operationKey);
    }

    private bool RunGlobalProfileAsync(string profile, string spatialAudioModeId, string operationKey)
    {
        return RunAudioOperationAsync(
            Localization.Value("MainPage_GlobalDefaults"),
            GetGlobalEndpointPath(),
            profile,
            spatialAudioModeId,
            apply: true,
            equalizer: GetCustomEqualizerSettings(),
            endpointVolumePercent: VolumeSafety.Clamp(config.GlobalVolumePercent, config.VolumeProtectionEnabled),
            operationKey: operationKey);
    }

    private bool RunAudioOperationAsync(
        string operationName,
        string? configuredEndpointPath,
        string profile,
        string spatialAudioModeId,
        bool apply,
        DolbyEqualizerSettings? equalizer,
        float? endpointVolumePercent,
        string operationKey)
    {
        if (!runningOperations.TryAdd(operationKey, 0))
        {
            Log($"[{operationName}] another operation is already running.");
            return false;
        }

        SpatialAudioOption spatialMode = SpatialAudioModeCatalog.FindById(spatialAudioModeId);
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(spatialMode.Id);
        if (provider != null && !provider.SupportsProfile(profile))
        {
            runningOperations.TryRemove(operationKey, out _);
            Log($"[{operationName}] the selected spatial format does not support preset '{profile}'.");
            return false;
        }

        if (Interlocked.CompareExchange(ref audioOperationInProgress, 1, 0) != 0)
        {
            runningOperations.TryRemove(operationKey, out _);
            Interlocked.Exchange(ref audioEvaluationPending, 1);
            Log($"[{operationName}] audio operation deferred because another switch is still running.");
            return false;
        }

        // A process rule selects the system default output device. It does not
        // create an independent per-process audio route.
        bool shouldSetGlobalOutput = !string.IsNullOrWhiteSpace(configuredEndpointPath);
        configuredEndpointPath ??= string.Empty;
        List<AudioEndpointChoice> endpointSnapshot = endpoints.ToList();
        _ = audioApiQueue.EnqueueAsync(async () =>
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

                if (shouldSetGlobalOutput && !string.IsNullOrWhiteSpace(endpointPath))
                {
                    if (string.Equals(lastDefaultEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"[{operationName}] default output unchanged; switch API skipped.");
                    }
                    else
                    {
                        string outputResult = ProcessAudioRouter.SetSystemDefaultOutputDevice(endpointPath, apply);
                        Log($"[{operationName}] {outputResult}");
                        if (outputResult.StartsWith("C# default output switched", StringComparison.OrdinalIgnoreCase))
                        {
                            lastDefaultEndpointPath = endpointPath;
                        }
                    }
                }
                if (endpointVolumePercent.HasValue)
                {
                    Log($"[{operationName}] global volume target=default; {AudioVolumeController.SetEndpointVolume(
                        null,
                        endpointVolumePercent.Value,
                        apply)}");
                }
                bool refreshDolbyAudioGraph = apply && provider is DolbyCapxProfileProvider && spatialMode.Id != "keep";
                bool profileAppliedBeforeSpatialRefresh = false;
                if (refreshDolbyAudioGraph)
                {
                    Log($"[{operationName}] applying {spatialMode.Name} preset {profile} before audio graph refresh (APPLY)...");
                    Log((await provider!.InvokeSetterAsync(endpointPath, profile, apply, equalizer)).Trim());
                    profileAppliedBeforeSpatialRefresh = true;
                }

                if (spatialMode.Id != "keep")
                {
                    Log($"[{operationName}] requesting spatial format {spatialMode.Name} {(apply ? "(APPLY)" : "(DRY-RUN)")}...");
                    string spatialResult = await AudioSystemState.SetDefaultSpatialAudioModeAsync(
                        endpointPath,
                        spatialMode.Id,
                        apply,
                        forceRefresh: refreshDolbyAudioGraph);
                    Log(spatialResult.Trim());
                    if (apply && !AudioSystemState.IsSpatialAudioResultConfirmed(spatialResult, spatialMode.Id))
                    {
                        Log($"[{operationName}] spatial format was not confirmed; skipped {spatialMode.Name} preset.");
                        return;
                    }
                }

                if (provider != null && !profileAppliedBeforeSpatialRefresh)
                {
                    Log($"[{operationName}] applying {spatialMode.Name} preset {profile} {(apply ? "(APPLY)" : "(DRY-RUN)")}...");
                    Log((await provider.InvokeSetterAsync(endpointPath, profile, apply, equalizer)).Trim());
                }
            }
            catch (Exception ex)
            {
                Log($"[{operationName}] profile operation failed: {ex.Message}");
            }
            finally
            {
                runningOperations.TryRemove(operationKey, out _);
                Interlocked.Exchange(ref audioOperationInProgress, 0);
                if (Interlocked.Exchange(ref audioEvaluationPending, 0) != 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!monitoring) return;
                        foregroundDebounceTimer.Stop();
                        pendingForegroundProcessId = null;
                        Log("Audio operation completed; re-evaluating the current foreground rule.");
                        MonitorTimer_Tick(monitorTimer, new object());
                    });
                }
            }
        });
        return true;
    }

    private async void GeneralOption_Click(object sender, RoutedEventArgs e)
    {
        config.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        config.StartSilent = StartSilentCheckBox.IsChecked == true;
        config.StartMonitor = StartMonitorCheckBox.IsChecked == true;
        config.HttpListenerEnabled = HttpListenerEnabledCheckBox.IsChecked == true;
        if (ReferenceEquals(sender, VolumeProtectionCheckBox))
        {
            config.VolumeProtectionEnabled = VolumeProtectionCheckBox.IsChecked == true;
        }
        SaveConfig();

        if (ReferenceEquals(sender, HttpListenerEnabledCheckBox))
        {
            StartHttpListener();
        }

        if (ReferenceEquals(sender, VolumeProtectionCheckBox))
        {
            await ApplyVolumeProtectionAsync();
        }
    }

    private void HttpListenerBindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressHttpListenerSelection || HttpListenerBindComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string bindAddress) return;
        config.HttpListenerBindAddress = bindAddress;
        SaveConfig();
        StartHttpListener();
    }

    private void HttpListenerPortNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (suppressHttpListenerSelection || double.IsNaN(args.NewValue)) return;

        int port = Math.Clamp((int)Math.Round(args.NewValue), 1, 65535);
        if (Math.Abs(args.NewValue - port) > double.Epsilon)
        {
            sender.Value = port;
            return;
        }

        config.HttpListenerPort = port;
        SaveConfig();
        if (config.HttpListenerEnabled) StartHttpListener();
    }

    private void HttpListenerPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (suppressHttpListenerSelection) return;
        config.HttpListenerPassword = HttpListenerPasswordBox.Password;
        SaveConfig();
        if (config.HttpListenerEnabled) StartHttpListener();
    }

    private void StartHttpListener()
    {
        StopHttpListener();
        if (!config.HttpListenerEnabled) return;

        try
        {
            string bindAddress = string.Equals(config.HttpListenerBindAddress, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
                ? "0.0.0.0"
                : "127.0.0.1";
            int port = Math.Clamp(config.HttpListenerPort, 1, 65535);
            httpStateServer = new LocalHttpStateServer(bindAddress, port, config.HttpListenerPassword, HandleHttpStateAsync);
            httpStateServer.Start();
            string passwordState = string.IsNullOrEmpty(config.HttpListenerPassword) ? "without password" : "with password";
            Log($"HTTP state listener started: http://{bindAddress}:{port}/api/state ({passwordState}).");
            if (bindAddress == "0.0.0.0" && string.IsNullOrEmpty(config.HttpListenerPassword))
            {
                Log("WARNING: HTTP state listener is reachable on all network interfaces without a password.");
            }
        }
        catch (Exception ex)
        {
            httpStateServer = null;
            Log($"HTTP state listener failed to start: {ex.Message}");
        }
    }

    private void StopHttpListener()
    {
        LocalHttpStateServer? server = httpStateServer;
        httpStateServer = null;
        if (server == null) return;
        server.Dispose();
        Log("HTTP state listener stopped.");
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

        if (globalVolumeClamped)
        {
            config.GlobalVolumePercent = safeGlobalVolume;
            SaveConfig();
            string result = await audioApiQueue.EnqueueAsync(() => Task.FromResult(
                AudioVolumeController.SetEndpointVolume(null, safeGlobalVolume, apply: true)));
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
        SaveConfig();
        UpdateMonitoringSchedule();
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

    private static string EndpointFileForAddressRule(string processName, string ruleId)
    {
        AppPaths.EnsureDataDirectories();
        string key = processName + "\u001f" + ruleId;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).Substring(0, 16).ToLowerInvariant();
        return Path.Combine(AppPaths.WorkDirectory, $"endpoint-address-{hash}.txt");
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
            string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AudioSwitch.WinUI.exe");
            run.SetValue("AudioSwitch", $"\"{executable}\" --silent");
        }
        else
        {
            run.DeleteValue("AudioSwitch", false);
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDataDirectories();
            string content;
            lock (logFileLock)
            {
                content = File.Exists(AppPaths.LogPath)
                    ? File.ReadAllText(AppPaths.LogPath, Encoding.UTF8)
                    : logEntries.Count == 0
                        ? Localization.Text("MainPage_NoLog")
                        : string.Join(Environment.NewLine, logEntries);
            }

            string viewDirectory = Path.Combine(Path.GetTempPath(), "AudioSwitch");
            Directory.CreateDirectory(viewDirectory);
            string viewPath = Path.Combine(viewDirectory, "audio-switch-current.log");
            File.WriteAllText(viewPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            string notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (!File.Exists(notepadPath)) throw new FileNotFoundException("Windows Notepad was not found.", notepadPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = notepadPath,
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = true,
                Arguments = $"\"{viewPath}\""
            });
            Log($"Opened log snapshot in Notepad: {viewPath}");
        }
        catch (Exception ex)
        {
            Log("Opening log in Notepad failed: " + ex.Message);
        }
    }

    private void OpenChromeExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        OpenChromeExtensionDirectory();
    }

    private bool OpenChromeExtensionDirectory()
    {
        try
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "chrome-extension"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "chrome-extension"))
            };
            string directory = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
            if (!Directory.Exists(directory))
            {
                Log($"Chrome extension directory was not found: {directory}");
                return false;
            }

            string explorerPath = Path.Combine(Environment.SystemDirectory, "explorer.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = explorerPath,
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                ArgumentList = { directory }
            });
            return true;
        }
        catch (Exception ex)
        {
            Log("Opening Chrome extension directory failed: " + ex.Message);
            return false;
        }
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

    private async void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Localization.Text("Dialog_ClearLog.Title"),
            Content = new TextBlock
            {
                Text = Localization.Text("Dialog_ClearLog.Description"),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = Localization.Text("Dialog_ClearLog.Primary"),
            CloseButtonText = Localization.Text("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        bool failed = false;
        lock (logFileLock)
        {
            logEntries.Clear();
            foreach (string path in new[] { AppPaths.LogPath, AppPaths.RotatedLogPath })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                    failed = true;
                }
            }
        }

        StatusText.Text = Localization.Value(failed ? "Status_LogClearFailed" : "Status_LogCleared");
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
            audioApiQueue.EnqueueAsync(async () =>
            {
                AudioSystemStatus current = AudioSystemState.Read(Array.Empty<AudioEndpointChoice>());
                if (!string.IsNullOrWhiteSpace(checkpoint.EndpointPath) &&
                    !string.Equals(current.DefaultEndpointPath, checkpoint.EndpointPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Exit restore output -> {ProcessAudioRouter.SetSystemDefaultOutputDevice(checkpoint.EndpointPath, apply: true)}");
                }

                if (!string.IsNullOrWhiteSpace(checkpoint.EndpointPath) && !string.IsNullOrWhiteSpace(checkpoint.SpatialAudioModeId))
                {
                    string spatialResult = await AudioSystemState.SetDefaultSpatialAudioModeAsync(
                        checkpoint.EndpointPath,
                        checkpoint.SpatialAudioModeId,
                        apply: true);
                    Log($"Exit restore spatial audio -> {spatialResult}");
                }

                if (checkpoint.GlobalVolumePercent is float volume)
                {
                    float safeVolume = VolumeSafety.Clamp(volume, config.VolumeProtectionEnabled);
                    Log($"Exit restore volume -> {AudioVolumeController.SetEndpointVolume(null, safeVolume, apply: true)}");
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"Exit audio restore failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref exitRestoreStarted, 1) != 0) return;
        StopAudioCurve();
        audioRefreshTimer.Stop();
        audioCurveTimer.Stop();
        notificationHideTimer.Stop();
        monitorTimer.Stop();
        foregroundDebounceTimer.Stop();
        pendingForegroundProcessId = null;
        globalVolumeApplyCts?.Cancel();
        globalVolumeApplyCts?.Dispose();
        globalVolumeApplyCts = null;
        StopHttpListener();
        foregroundWindowMonitor?.Dispose();
        foregroundWindowMonitor = null;
        audioSystemChangeMonitor?.Dispose();
        audioSystemChangeMonitor = null;
        audioCurveCapture.Dispose();
        RestoreExitRestoreCheckpoint();
        audioApiQueue.Dispose();
    }
}
