using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Downloader.Services;
using Downloader.ViewModels;
using Downloader.Views;

namespace Downloader;

/// <summary>
/// 启动流程：启动窗口（连接 CAN API、检查下载器更新）→ 登录窗口（OAuth，登录前仍可更新下载器）→ 主界面。
/// 勾选了“自动登录”且保存的授权仍有效时跳过登录窗口。主界面退出登录时回到登录窗口；“更新并重启”回到启动窗口。
/// 每个窗口只能离开一次：离开前的窗口（已不是当前窗口）晚到的事件一律忽略，不会同时打开两个界面。
/// </summary>
public partial class App : Application
{
    private readonly SettingsViewModel _settings = SettingsViewModel.Load();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            SelfUpdater.CleanupOldVersion();
            CanApi.Shared.Configure(_settings);
            // 开机启动：程序搬了位置也指向现在的位置；设置保存时再按新设置更新
            Autostart.Apply(_settings.LaunchAtStartup);
            _settings.Saved += (_, _) =>
            {
                Autostart.Apply(_settings.LaunchAtStartup);
                if (_tray is not null)
                    _tray.IsVisible = _settings.MinimizeToTray;
            };
            CreateTrayIcon();
            // 再次打开下载器时（另一个进程）显示已在运行的窗口
            SingleInstance.ListenForActivation(() => Dispatcher.UIThread.Post(ShowCurrentWindow));
            // 命令行 --offline：演示无法连接更新服务器
            var offline = desktop.Args?.Contains("--offline") == true;
            ShowSplash(offline, autoUpdate: false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 系统托盘图标：设置了“关闭窗口时最小化到系统托盘”时，点主窗口的 ✕ 只隐藏窗口，下载继续；
    /// 点托盘图标恢复窗口，托盘菜单可以退出。
    /// </summary>
    private void CreateTrayIcon()
    {
        try
        {
            var show = new NativeMenuItem("显示 " + AppInfo.Name);
            show.Click += (_, _) => ShowCurrentWindow();
            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) => _desktop?.Shutdown();
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Downloader/Assets/app.ico"))),
                ToolTipText = AppInfo.Name,
                Menu = new NativeMenu { Items = { show, new NativeMenuItemSeparator(), exit } },
                IsVisible = _settings.MinimizeToTray
            };
            _tray.Clicked += (_, _) => ShowCurrentWindow();
            TrayIcon.SetIcons(this, [_tray]);
        }
        catch (Exception ex)
        {
            // 系统不支持托盘时（例如没有托盘的 Linux 桌面）照常运行，关闭窗口就是退出
            Log.Warn($"无法创建托盘图标：{ex.Message}");
            _tray = null;
        }
    }

    /// <summary>主窗口点 ✕ 时是否只隐藏到托盘。</summary>
    public bool CanHideToTray => _tray is not null && _settings.MinimizeToTray;

    /// <summary>显示并激活当前窗口（从托盘恢复、再次打开下载器时）。</summary>
    private void ShowCurrentWindow()
    {
        if (_desktop?.MainWindow is not { } window)
            return;
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>新窗口先显示再关闭旧窗口，避免中途没有窗口导致程序退出。</summary>
    private void SwitchTo(Window next, Window? previous)
    {
        _desktop!.MainWindow = next;
        next.Show();
        // 从浏览器授权回来时把窗口带到前台
        next.Activate();
        previous?.Close();
    }

    /// <summary><paramref name="window"/> 仍是当前窗口时才可以离开它。</summary>
    private bool IsCurrent(Window window) => ReferenceEquals(_desktop?.MainWindow, window);

    private void ShowSplash(bool offline, bool autoUpdate, Window? previous = null)
    {
        var vm = new SelfUpdateViewModel(_settings, offline, autoUpdate);
        var splash = new SplashWindow { DataContext = vm };
        vm.RestartRequested += (_, _) => _desktop!.Shutdown();
        var leaving = false;
        vm.Completed += async (_, manifest) =>
        {
            if (leaving || !IsCurrent(splash))
                return;
            leaving = true;
            vm.StatusText = "正在登录…";
            if (await LoginViewModel.TryResumeAsync() is { } account)
                ShowMain(manifest, account, splash);
            else
                ShowLogin(manifest, splash);
        };

        SwitchTo(splash, previous);
        _ = vm.StartAsync();
    }

    private void ShowLogin(UpdateManifest? manifest, Window previous)
    {
        var vm = new LoginViewModel(manifest);
        var login = new LoginWindow { DataContext = vm };
        vm.LoggedIn += (_, account) =>
        {
            if (IsCurrent(login))
                ShowMain(vm.Manifest, account, login);
        };
        vm.UpdateLauncherRequested += (_, _) =>
        {
            if (!IsCurrent(login))
                return;
            // 不能一边等浏览器授权一边去更新：放弃这次登录
            vm.CancelLoginCommand.Execute(null);
            ShowSplash(offline: false, autoUpdate: true, previous: login);
        };

        SwitchTo(login, previous);
    }

    private void ShowMain(UpdateManifest? manifest, UserAccount account, Window previous)
    {
        var vm = new MainWindowViewModel(manifest, _settings, account);
        var main = new MainWindow { DataContext = vm };
        vm.RestartForUpdateRequested += async (_, _) =>
        {
            if (!IsCurrent(main))
                return;
            // 先停下这个界面的下载并等安装结束，旧界面不能在后台继续安装
            await vm.StopAsync();
            ShowSplash(offline: false, autoUpdate: true, previous: main);
        };
        vm.LoginRequested += (_, _) =>
        {
            if (IsCurrent(main))
                ShowLogin(vm.Manifest, main);
        };

        SwitchTo(main, previous);
    }
}
