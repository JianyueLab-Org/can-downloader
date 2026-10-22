using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Downloader.ViewModels;
using Downloader.Views;

namespace Downloader;

/// <summary>
/// 启动流程：启动窗口（检查下载器更新）→ 登录窗口（登录前仍可更新下载器）→ 主界面。
/// 启动检查失败或在登录窗口选择离线模式时，不登录直接进入主界面（不能下载）。
/// 主界面退出登录、离线模式点“登录”时回到登录窗口；“更新并重启”回到启动窗口。
/// </summary>
public partial class App : Application
{
    private readonly SettingsViewModel _settings = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            // 命令行 --offline：演示无法连接更新服务器
            var offline = desktop.Args?.Contains("--offline") == true;
            ShowSplash(offline, autoUpdate: false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>新窗口先显示再关闭旧窗口，避免中途没有窗口导致程序退出。</summary>
    private void SwitchTo(Window next, Window? previous)
    {
        _desktop!.MainWindow = next;
        next.Show();
        previous?.Close();
    }

    private void ShowSplash(bool offline, bool autoUpdate, Window? previous = null)
    {
        var vm = new SelfUpdateViewModel(offline, autoUpdate);
        var splash = new SplashWindow { DataContext = vm };
        vm.Completed += (_, manifest) =>
        {
            if (manifest is null)
                ShowMain(null, null, splash);
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
        vm.LoggedIn += (_, account) => ShowMain(vm.Manifest, account, login);
        vm.OfflineRequested += (_, _) => ShowMain(vm.Manifest, null, login);
        vm.UpdateLauncherRequested += (_, _) => ShowSplash(offline: false, autoUpdate: true, previous: login);

        SwitchTo(login, previous);
    }

    private void ShowMain(UpdateManifest? manifest, UserAccount? account, Window previous)
    {
        var vm = new MainWindowViewModel(manifest, _settings, account);
        var main = new MainWindow { DataContext = vm };
        vm.RestartForUpdateRequested += (_, _) => ShowSplash(offline: false, autoUpdate: true, previous: main);
        vm.LoginRequested += (_, _) => ShowLogin(vm.Manifest, main);

        SwitchTo(main, previous);
    }
}
