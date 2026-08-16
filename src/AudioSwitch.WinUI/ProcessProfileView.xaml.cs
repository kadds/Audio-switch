using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AudioSwitch_WinUI;

public sealed partial class ProcessProfileView : UserControl
{
    private bool suppressChanges = true;
    private bool volumeProtectionEnabled = true;
    private IReadOnlyList<string> customProfileNames = Array.Empty<string>();
    private readonly ProcessAddressRule? addressRule;
    private readonly ProcessSwitchItem? parentModel;

    public ProcessSwitchItem Model { get; }
    public ProcessAddressRule? AddressRule => addressRule;
    public ProcessSwitchItem? ParentModel => parentModel;
    public AudioEndpointChoice? SelectedEndpoint => EndpointComboBox.SelectedItem as AudioEndpointChoice;

    public event EventHandler? SettingsChanged;
    public event EventHandler? TestActiveRequested;

    public ProcessProfileView(ProcessSwitchItem model, string? endpointPath)
        : this(model, endpointPath, null, null)
    {
    }

    public ProcessProfileView(
        ProcessSwitchItem model,
        string? endpointPath,
        ProcessAddressRule? addressRule,
        ProcessSwitchItem? parentModel)
    {
        Model = model;
        this.addressRule = addressRule;
        this.parentModel = parentModel;
        InitializeComponent();
        RefreshLocalization();

        ProcessTitleTextBlock.Text = addressRule == null
            ? model.DisplayName
            : $"{parentModel?.Name ?? model.Name} · {addressRule.DisplayName}";
        ((ComboBoxItem)MatchModeComboBox.Items[1]).IsEnabled = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        ((ComboBoxItem)MatchModeComboBox.Items[2]).IsEnabled = true;
        MatchModeComboBox.SelectedIndex = model.MatchMode switch
        {
            ProcessMatchMode.FullPath => 1,
            ProcessMatchMode.HttpState => 2,
            _ => 0
        };
        RemoteAddressTextBox.Text = model.RemoteAddress;
        RemoteTitleTextBox.Text = model.RemoteTitle;
        RemoteStatusTextBox.Text = model.RemoteStatusText;
        RemoteStateTextBox.Text = model.RemoteState;
        RefreshHttpStateMatchPanel();
        ForegroundOnlyCheckBox.IsChecked = model.ForegroundOnly;
        PriorityOverrideNumberBox.Value = model.PriorityOverride ?? double.NaN;
        GlobalVolumeEnabledCheckBox.IsChecked = model.GlobalVolumePercent.HasValue;
        GlobalVolumeSlider.Value = model.GlobalVolumePercent ?? 100;
        GlobalVolumeSlider.IsEnabled = model.GlobalVolumePercent.HasValue;
        RefreshGlobalVolumeText();
        ActiveSpatialAudioModeComboBox.ItemsSource = SpatialAudioModeCatalog.ProcessOptions;
        ActiveSpatialAudioModeComboBox.SelectedItem = SpatialAudioModeCatalog.FindById(model.ActiveSpatialAudioModeId);
        model.ActiveSpatialAudioModeId = (ActiveSpatialAudioModeComboBox.SelectedItem as SpatialAudioOption)?.Id ?? "keep";
        RefreshPresetChoices();
        suppressChanges = false;

        EndpointPath = endpointPath;
        if (addressRule != null) ConfigureAddressRuleEditor();
        RefreshCurrentMatch();
    }

    private void ConfigureAddressRuleEditor()
    {
        RuleMatchingTextBlock.Visibility = Visibility.Collapsed;
        RuleHintTextBlock.Visibility = Visibility.Collapsed;
        MatchModeComboBox.Visibility = Visibility.Collapsed;
        HttpStateMatchPanel.Visibility = Visibility.Collapsed;
        AddressRuleMatchPanel.Visibility = Visibility.Visible;
        ProcessPathTextBlock.Visibility = Visibility.Collapsed;
        AddressRuleNameTextBox.Text = addressRule?.Name ?? string.Empty;
        AddressRuleAddressTextBox.Text = addressRule?.Address ?? string.Empty;
    }

    public bool SetVolumeProtection(bool enabled)
    {
        bool changed = false;
        volumeProtectionEnabled = enabled;
        float maximum = VolumeSafety.MaximumPercent(enabled);
        suppressChanges = true;
        GlobalVolumeSlider.Maximum = maximum;
        if (Model.GlobalVolumePercent is float volume)
        {
            float safeVolume = VolumeSafety.Clamp(volume, enabled);
            changed = Math.Abs(safeVolume - volume) > 0.001f;
            Model.GlobalVolumePercent = safeVolume;
            GlobalVolumeSlider.Value = safeVolume;
        }
        else
        {
            GlobalVolumeSlider.Value = Math.Min(GlobalVolumeSlider.Value, maximum);
        }
        GlobalVolumeEnabledCheckBox.IsChecked = Model.GlobalVolumePercent.HasValue;
        GlobalVolumeSlider.IsEnabled = Model.GlobalVolumePercent.HasValue;
        suppressChanges = false;
        RefreshGlobalVolumeText();
        return changed;
    }

    public void RefreshLocalization()
    {
        ProcessDescriptionTextBlock.Text = Localization.Text("ProcessProfile_Description");
        ProcessPathTextBlock.Text = Localization.Text("ProcessProfile_PathUnavailable");
        RuleMatchingTextBlock.Text = Localization.Text("ProcessProfile_RuleMatching");
        RuleHintTextBlock.Text = Localization.Text("ProcessProfile_RuleHint");
        ((ComboBoxItem)MatchModeComboBox.Items[0]).Content = Localization.Content("ProcessProfile_ProcessName");
        ((ComboBoxItem)MatchModeComboBox.Items[1]).Content = Localization.Content("ProcessProfile_FullPath");
        ((ComboBoxItem)MatchModeComboBox.Items[2]).Content = Localization.Content("ProcessProfile_HttpState");
        HttpStateMatchHintTextBlock.Text = Localization.Text("ProcessProfile_HttpStateHint");
        AddressRuleMatchHintTextBlock.Text = Localization.Text("ProcessProfile_AddressRuleHint");
        AddressRuleNameTextBox.Header = Localization.Text("ProcessProfile_AddressRuleName");
        AddressRuleAddressTextBox.Header = Localization.Text("ProcessProfile_AddressRuleAddress");
        RemoteAddressTextBox.Header = Localization.Text("ProcessProfile_RemoteAddress");
        RemoteTitleTextBox.Header = Localization.Text("ProcessProfile_RemoteTitle");
        RemoteStatusTextBox.Header = Localization.Text("ProcessProfile_RemoteStatusText");
        RemoteStateTextBox.Header = Localization.Text("ProcessProfile_RemoteState");
        ForegroundOnlyCheckBox.Content = Localization.Content("ProcessProfile_ForegroundOnly");
        ForegroundHintTextBlock.Text = Localization.Text("ProcessProfile_ForegroundHint");
        PriorityOverrideLabelTextBlock.Text = Localization.Text("ProcessProfile_PriorityOverride");
        PriorityOverrideHintTextBlock.Text = Localization.Text("ProcessProfile_PriorityHint");
        OutputDeviceTitleTextBlock.Text = Localization.Text("ProcessProfile_OutputDevice");
        OutputHintTextBlock.Text = Localization.Text("ProcessProfile_OutputHint");
        GlobalVolumeLabelTextBlock.Text = Localization.Text("ProcessProfile_GlobalVolume");
        GlobalVolumeEnabledCheckBox.Content = Localization.Content("ProcessProfile_GlobalVolumeEnabled");
        GlobalVolumeHintTextBlock.Text = Localization.Text("ProcessProfile_GlobalVolumeHint");
        RefreshGlobalVolumeText();
        CurrentSpatialTextBlock.Text = Localization.Text("ProcessProfile_CurrentSpatial");
        SpatialLogicTextBlock.Text = Localization.Text("ProcessProfile_SpatialLogic");
        SpatialHintTextBlock.Text = Localization.Text("ProcessProfile_SpatialHint");
        SystemFormatTextBlock.Text = Localization.Text("ProcessProfile_SystemFormat");
        ActiveLabelTextBlock.Text = Localization.Text("ProcessProfile_ActiveLabel");
        PresetTransitionTextBlock.Text = Localization.Text("ProcessProfile_PresetTransition");
        PresetHintTextBlock.Text = Localization.Text("ProcessProfile_PresetHint");
        ActivePresetTextBlock.Text = Localization.Text("ProcessProfile_ActivePreset");
        TestActiveTextBlock.Text = Localization.Text("ProcessProfile_Test");
        ProfileSettingsTabItem.Header = Localization.Text("ProcessProfile_SettingsTab");
        ProcessPathTextBlock.Text = string.IsNullOrWhiteSpace(Model.ExecutablePath)
            ? Localization.Text("ProcessProfile_PathUnavailable")
            : Localization.FormatValue("ProcessProfile_Path", Model.ExecutablePath);
        RefreshAddressRuleEditorTitle();
        RefreshCurrentMatch();
    }

    private string? EndpointPath { get; set; }

    public void SetEndpoints(IEnumerable<AudioEndpointChoice> values)
    {
        suppressChanges = true;
        List<AudioEndpointChoice> options = new() { AudioEndpointChoice.CreateKeepCurrent() };
        options.AddRange(values);
        EndpointComboBox.ItemsSource = options;
        AudioEndpointChoice? selected = string.IsNullOrWhiteSpace(EndpointPath)
            ? options[0]
            : options.FirstOrDefault(item => string.Equals(item.EndpointPath, EndpointPath, StringComparison.OrdinalIgnoreCase));
        EndpointComboBox.SelectedItem = selected;
        suppressChanges = false;
        RefreshCurrentMatch();
    }

    public void SetCustomProfiles(IEnumerable<string> profiles)
    {
        customProfileNames = profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RefreshPresetChoices();
    }

    public void SetEndpoint(AudioEndpointChoice endpoint, string endpointFile)
    {
        suppressChanges = true;
        EndpointComboBox.SelectedItem = endpoint;
        suppressChanges = false;
        EndpointPath = endpoint.EndpointPath;
        Model.EndpointFile = endpointFile;
        RefreshCurrentMatch();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshCurrentMatch()
    {
        string spatialSummary = Localization.FormatValue(
            "Profile_SpatialSummary",
            SpatialAudioModeCatalog.FindById(Model.ActiveSpatialAudioModeId).Name);
        string profileSummary = Localization.FormatValue(
            "Profile_PresetSummary",
            PresetForDisplay(Model.ActiveSpatialAudioModeId, Model.ActiveProfile));
        AudioEndpointChoice? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            CurrentMatchTextBlock.Text = Localization.FormatValue("Profile_NoOutput", spatialSummary, profileSummary);
            ProfileStatusTextBlock.Text = Localization.FormatValue("Profile_StatusSummary", spatialSummary, profileSummary);
            return;
        }

        if (endpoint.IsKeepCurrent)
        {
            CurrentMatchTextBlock.Text = Localization.FormatValue("Profile_KeepOutput", spatialSummary, profileSummary);
            ProfileStatusTextBlock.Text = Localization.FormatValue("Profile_StatusSummary", spatialSummary, profileSummary);
            return;
        }

        if (!endpoint.ProbeCompleted)
        {
            CurrentMatchTextBlock.Text = $"{endpoint.DisplayName}\n{spatialSummary}\n{Localization.FormatValue("Profile_CurrentPreset", endpoint.Profile)}\n{Localization.Value("Profile_SpatialScanning")}";
            ProfileStatusTextBlock.Text = Localization.FormatValue("Profile_CheckingEndpoint", profileSummary);
        }
        else if (endpoint.IsMatched)
        {
            CurrentMatchTextBlock.Text = $"{endpoint.DisplayName}\n{spatialSummary}\n{Localization.FormatValue("Profile_CurrentPreset", endpoint.Profile)}\n{Localization.FormatValue("Profile_Matched", endpoint.MatchedProviders)}";
            ProfileStatusTextBlock.Text = Localization.FormatValue("Profile_MatchedStatus", profileSummary);
        }
        else
        {
            CurrentMatchTextBlock.Text = $"{endpoint.DisplayName}\n{spatialSummary}\n{Localization.FormatValue("Profile_CurrentPreset", endpoint.Profile)}\n{Localization.Value("Profile_NoMatch")}\n{Localization.FormatValue("Profile_Probe", endpoint.ProbeStatus)}";
            ProfileStatusTextBlock.Text = Localization.FormatValue("Profile_ProbeStatus", profileSummary, endpoint.ProbeStatus);
        }
    }

    private void TestActiveButton_Click(object sender, RoutedEventArgs e) => TestActiveRequested?.Invoke(this, EventArgs.Empty);

    private void EndpointComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedEndpoint is AudioEndpointChoice endpoint)
        {
            EndpointPath = endpoint.IsKeepCurrent ? null : endpoint.EndpointPath;
        }
        RefreshCurrentMatch();
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MatchModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchModeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse(tag, out ProcessMatchMode mode))
        {
            Model.MatchMode = mode;
        }
        RefreshHttpStateMatchPanel();
        RefreshCurrentMatch();
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshHttpStateMatchPanel()
    {
        bool enabled = addressRule == null && Model.MatchMode == ProcessMatchMode.HttpState;
        HttpStateMatchPanel.Visibility = enabled ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        ForegroundOnlyCheckBox.IsEnabled = !enabled;
    }

    private void RefreshAddressRuleEditorTitle()
    {
        ProcessTitleTextBlock.Text = addressRule == null
            ? Model.DisplayName
            : $"{parentModel?.Name ?? Model.Name} · {addressRule.DisplayName}";
    }

    private void AddressRuleField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (addressRule == null) return;
        addressRule.Name = AddressRuleNameTextBox.Text.Trim();
        addressRule.Address = AddressRuleAddressTextBox.Text.Trim();
        RefreshAddressRuleEditorTitle();
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoteStateField_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        Model.RemoteAddress = RemoteAddressTextBox.Text.Trim();
        Model.RemoteTitle = RemoteTitleTextBox.Text.Trim();
        Model.RemoteStatusText = RemoteStatusTextBox.Text.Trim();
        Model.RemoteState = RemoteStateTextBox.Text.Trim();
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ForegroundOnlyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        Model.ForegroundOnly = ForegroundOnlyCheckBox.IsChecked == true;
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PriorityOverrideNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            Model.PriorityOverride = null;
        }
        else
        {
            Model.PriorityOverride = Math.Clamp((int)Math.Round(args.NewValue), -10000, 10000);
        }

        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void GlobalVolumeEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = GlobalVolumeEnabledCheckBox.IsChecked == true;
        GlobalVolumeSlider.IsEnabled = enabled;
        Model.GlobalVolumePercent = enabled
            ? VolumeSafety.Clamp((float)GlobalVolumeSlider.Value, volumeProtectionEnabled)
            : null;
        RefreshGlobalVolumeText();
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void GlobalVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (GlobalVolumeEnabledCheckBox.IsChecked == true && !double.IsNaN(args.NewValue))
        {
            Model.GlobalVolumePercent = VolumeSafety.Clamp((float)args.NewValue, volumeProtectionEnabled);
        }

        RefreshGlobalVolumeText();
        if (!suppressChanges && GlobalVolumeEnabledCheckBox.IsChecked == true) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshGlobalVolumeText()
    {
        GlobalVolumeValueTextBlock.Text = Model.GlobalVolumePercent.HasValue
            ? $"{Model.GlobalVolumePercent.Value:0}%"
            : Localization.Value("ProcessProfile_KeepCurrentGlobalVolume");
    }

    private void ActiveSpatialAudioModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActiveSpatialAudioModeComboBox.SelectedItem is SpatialAudioOption option)
        {
            Model.ActiveSpatialAudioModeId = option.Id;
        }
        RefreshPresetChoices();
        RefreshCurrentMatch();
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshPresetChoices()
    {
        suppressChanges = true;
        SetPresetChoice(ActiveProfileComboBox, Model.ActiveSpatialAudioModeId, Model.ActiveProfile);
        suppressChanges = false;
    }

    private void SetPresetChoice(ComboBox comboBox, string modeId, string? requested)
    {
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId);
        comboBox.IsEnabled = provider != null;
        IReadOnlyList<string> profiles = provider is DolbyCapxProfileProvider
            ? provider.SupportedProfiles.Concat(customProfileNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : provider?.SupportedProfiles ?? Array.Empty<string>();
        comboBox.ItemsSource = profiles;

        if (provider == null)
        {
            comboBox.SelectedItem = null;
            Model.ActiveProfile = string.Empty;
            return;
        }

        string fallback = provider.DefaultActiveProfile;
        string selected = provider.SupportsProfile(requested ?? string.Empty) ? requested! : fallback;
        comboBox.SelectedItem = selected;
        Model.ActiveProfile = selected;
    }

    private static string PresetForDisplay(string modeId, string? profile)
    {
        return AudioProfileProviderRegistry.FindForSpatialAudioMode(modeId) == null
            ? Localization.Value("Profile_NotUsed")
            : string.IsNullOrWhiteSpace(profile) ? Localization.Value("Profile_Default") : profile;
    }

    private void ActiveProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        IAudioProfileProvider? provider = AudioProfileProviderRegistry.FindForSpatialAudioMode(Model.ActiveSpatialAudioModeId);
        Model.ActiveProfile = ActiveProfileComboBox.SelectedItem?.ToString() ?? provider?.DefaultActiveProfile ?? string.Empty;
        RefreshCurrentMatch();
        if (!suppressChanges) SettingsChanged?.Invoke(this, EventArgs.Empty);
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
                return endpointFile.Contains("MMDEVAPI#", StringComparison.OrdinalIgnoreCase) || endpointFile.StartsWith("{", StringComparison.Ordinal)
                    ? AudioEndpointChoice.NormalizeDeviceInterfacePath(endpointFile)
                    : null;
            }

            string value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : AudioEndpointChoice.NormalizeDeviceInterfacePath(value);
        }
        catch
        {
            return null;
        }
    }

}
