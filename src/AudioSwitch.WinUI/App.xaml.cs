using H.NotifyIcon;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace AudioSwitch_WinUI;

public partial class App : Application
{
    private MainWindow? window;
    private MainPage? page;
    private TaskbarIcon? trayIcon;
    private MenuFlyoutItem? trayOpenItem;
    private MenuFlyoutItem? trayMonitorItem;
    private MenuFlyoutItem? trayExitItem;
    private AppInstance? appInstance;
    private bool allowClose;

    public nint MainWindowHandle => window == null ? 0 : WindowNative.GetWindowHandle(window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint hProcess);

    public App()
    {
        SwitchConfig startupConfig = SwitchConfig.Load(AppPaths.ConfigPath);
        Localization.Initialize(startupConfig.UiLanguage);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        appInstance = AppInstance.FindOrRegisterForKey("AudioSwitch");
        if (!appInstance.IsCurrent)
        {
            _ = RedirectActivationToMainInstanceAsync();
            return;
        }

        appInstance.Activated += AppInstance_Activated;
        if (window != null)
        {
            ShowWindow();
            return;
        }

        window = new MainWindow();
        page = window.ContentFrame.Content as MainPage;
        trayIcon = CreateTrayIcon();
        trayIcon.ForceCreate();
        window.AppWindow.Closing += AppWindow_Closing;
        window.Activate();
        try
        {
            window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch
        {
        }
        window.AppWindow.Resize(new SizeInt32(1440, 900));
        if (page?.StartHiddenRequested(args.Arguments) == true) HideWindow();
    }

    private async Task RedirectActivationToMainInstanceAsync()
    {
        try
        {
            await appInstance!.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
        }
        catch
        {
        }
        Environment.Exit(0);
    }

    private void AppInstance_Activated(object? sender, AppActivationArguments args)
    {
        window?.DispatcherQueue.TryEnqueue(ShowWindow);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        HideWindow();
    }

    private void TrayOpen_Click(object? sender, RoutedEventArgs e)
    {
        ShowWindow();
    }

    private void TrayMonitor_Click(object? sender, RoutedEventArgs e)
    {
        ShowWindow();
        page?.StartMonitoring();
    }

    private void TrayExit_Click(object? sender, RoutedEventArgs e)
    {
        allowClose = true;
        page?.Dispose();
        page = null;

        TaskbarIcon? icon = trayIcon;
        trayIcon = null;
        icon?.Dispose();

        MainWindow? closingWindow = window;
        window = null;
        if (closingWindow != null)
        {
            closingWindow.AppWindow.Closing -= AppWindow_Closing;
            closingWindow.Close();
        }

        // A hidden WinUI window and a tray message window can keep the
        // dispatcher alive after Close(). Tray Exit must terminate the app.
        Environment.Exit(0);
    }

    private void HideWindow()
    {
        if (window == null) return;
        // H.NotifyIcon enables Efficiency Mode when the tray icon is created.
        // Use its paired extension so the process is put back into the same
        // background state whenever the window is hidden.
        window.Hide(enableEfficiencyMode: true);
        TrimHiddenWindowWorkingSet();
    }

    private static void TrimHiddenWindowWorkingSet()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            using Process process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // Working-set trimming is an optional memory optimization.
        }
    }

    private void ShowWindow()
    {
        if (window == null) return;
        // ForceCreate enables Efficiency Mode. The extension disables it before
        // restoring the window, which is required for tray -> window activation.
        window.Show(disableEfficiencyMode: true);
        nint handle = WindowNative.GetWindowHandle(window);
        if (handle == 0) return;
        SetForegroundWindow(handle);
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var icon = new TaskbarIcon
        {
            ToolTipText = "Audio Switch",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.LeftOrRightClick
        };
        var menu = new MenuFlyout();
        trayOpenItem = new MenuFlyoutItem { Text = Localization.Text("Menu_OpenSettings") };
        trayOpenItem.Click += TrayOpen_Click;
        menu.Items.Add(trayOpenItem);
        trayMonitorItem = new MenuFlyoutItem { Text = Localization.Text("MainPage_StartMonitoring") };
        trayMonitorItem.Click += TrayMonitor_Click;
        menu.Items.Add(trayMonitorItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        trayExitItem = new MenuFlyoutItem { Text = Localization.Text("Menu_Exit") };
        trayExitItem.Click += TrayExit_Click;
        menu.Items.Add(trayExitItem);
        icon.ContextFlyout = menu;
        return icon;
    }

    public void RefreshTrayLocalization()
    {
        trayOpenItem!.Text = Localization.Text("Menu_OpenSettings");
        trayMonitorItem!.Text = Localization.Text("MainPage_StartMonitoring");
        trayExitItem!.Text = Localization.Text("Menu_Exit");
    }
}
