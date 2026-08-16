using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Windows.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace AudioSwitch_WinUI;

public partial class App : Application
{
    private MainWindow? window;
    private MainPage? page;
    private TaskbarIcon? trayIcon;
    private PopupMenuItem? trayOpenItem;
    private PopupMenuItem? trayMonitorItem;
    private PopupMenuItem? trayExitItem;
    private PopupMenu? trayMenu;
    private AppInstance? appInstance;
    private bool allowClose;

    public nint MainWindowHandle => window == null ? 0 : WindowNative.GetWindowHandle(window);

    private sealed class ActionCommand : ICommand
    {
        private readonly Action execute;

        public ActionCommand(Action execute) => this.execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }

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
        // Keep the WinUI dispatcher at normal QoS while the tray icon is
        // alive. Efficiency mode is intended for a permanently hidden app;
        // this app restores its main window from the tray.
        trayIcon.ForceCreate(enablesEfficiencyMode: false);
        window.AppWindow.Closing += AppWindow_Closing;
        window.Activate();
        try
        {
            string? appIconPath = FindAppIconPath();
            if (appIconPath != null) window.AppWindow.SetIcon(appIconPath);
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
        sender.Hide();
    }

    private void TrayOpen_Click(object? sender, EventArgs e)
    {
        ShowWindow();
    }

    private void TrayMonitor_Click(object? sender, EventArgs e)
    {
        ShowWindow();
        page?.StartMonitoring();
    }

    private void TrayExit_Click(object? sender, EventArgs e)
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
        window?.AppWindow.Hide();
    }

    private void ShowWindow()
    {
        if (window == null) return;
        window.AppWindow.Show(activateWindow: true);
        window.Activate();
    }

    private TaskbarIcon CreateTrayIcon()
    {
        TaskbarIcon icon = window!.TrayIcon;
        icon.ToolTipText = "Audio Switch";
        icon.Id = Guid.Parse("a2bb0e31-59b1-4f5c-8d32-7e7b7e4d4e21");
        icon.MenuActivation = PopupActivationMode.RightClick;
        icon.LeftClickCommand = new ActionCommand(ShowWindow);
        string? iconPath = FindAppIconPath();
        if (iconPath != null)
        {
            icon.Icon = new System.Drawing.Icon(iconPath);
        }
        trayMenu = new PopupMenu();
        trayOpenItem = new PopupMenuItem(Localization.Text("Menu_OpenSettings"), TrayOpen_Click);
        trayMenu.Items.Add(trayOpenItem);
        trayMonitorItem = new PopupMenuItem(Localization.Text("MainPage_StartMonitoring"), TrayMonitor_Click);
        trayMenu.Items.Add(trayMonitorItem);
        trayMenu.Items.Add(new PopupMenuSeparator());
        trayExitItem = new PopupMenuItem(Localization.Text("Menu_Exit"), TrayExit_Click);
        trayMenu.Items.Add(trayExitItem);
        icon.ContextMenuMode = ContextMenuMode.PopupMenu;
        if (icon.TrayIcon is TrayIconWithContextMenu trayIconWithMenu)
        {
            trayIconWithMenu.ContextMenu = trayMenu;
        }
        return icon;
    }

    private static string? FindAppIconPath()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            Path.Combine(AppContext.BaseDirectory, "AppX", "Assets", "AppIcon.ico")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public void RefreshTrayLocalization()
    {
        trayOpenItem!.Text = Localization.Text("Menu_OpenSettings");
        trayMonitorItem!.Text = Localization.Text("MainPage_StartMonitoring");
        trayExitItem!.Text = Localization.Text("Menu_Exit");
    }
}
