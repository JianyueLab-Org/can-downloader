using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Downloader.ViewModels;

/// <summary>已登录的 CAN 账号。</summary>
public sealed record UserAccount(string CanId, string Name, string Rating);

/// <summary>
/// 登录窗口：检查完下载器更新后显示，登录后才能进入主界面下载；也可以选择离线模式（只能查看已安装的软件）。
/// 启动时选择了“稍后”的话，这里仍可更新下载器。界面原型：任意数字 CAN ID 与非空密码都能登录。
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    /// <summary>记住的账号（演示数据）。</summary>
    private static string _rememberedId = "1234567";

    public LoginViewModel(UpdateManifest? manifest)
    {
        Manifest = manifest;
        if (manifest is not null && VersionUtil.IsNewer(manifest.Launcher.Version, AppInfo.Version))
            LauncherUpdateText = $"{AppInfo.Name} {manifest.Launcher.Version} 已发布（当前 {AppInfo.Version}）";
        _canId = RememberAccount ? _rememberedId : "";
    }

    /// <summary>登录成功。</summary>
    public event EventHandler<UserAccount>? LoggedIn;

    /// <summary>离线模式进入主界面。</summary>
    public event EventHandler? OfflineRequested;

    /// <summary>登录前更新下载器：回到启动窗口下载并重启。</summary>
    public event EventHandler? UpdateLauncherRequested;

    public UpdateManifest? Manifest { get; private set; }

    public string AppName => AppInfo.Name;
    public string CurrentVersion => AppInfo.Version;

    public string? LauncherUpdateText { get; }
    public bool HasLauncherUpdate => LauncherUpdateText is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _canId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _password = "";

    [ObservableProperty] private bool _rememberAccount = true;
    [ObservableProperty] private bool _autoLogin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginButtonText), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;
    public string LoginButtonText => IsBusy ? "正在登录…" : "登录";

    private bool CanLogin() => !IsBusy && CanId.Trim().Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorText = "";
        var id = CanId.Trim();
        if (!id.All(char.IsDigit))
        {
            ErrorText = "CAN ID 应为数字。";
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Delay(900);
            // 登录后获取软件清单（启动检查失败、从离线模式回来登录时还没有清单）
            Manifest ??= await Task.Run(ManifestSource.Load);
        }
        catch (Exception ex)
        {
            ErrorText = ex is FileNotFoundException ? "无法获取软件清单，请稍后重试或使用离线模式。" : ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _rememberedId = RememberAccount ? id : "";
        LoggedIn?.Invoke(this, new UserAccount(id, "张三", "S3"));
    }

    [RelayCommand]
    private void ContinueOffline() => OfflineRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void UpdateLauncher() => UpdateLauncherRequested?.Invoke(this, EventArgs.Empty);
}
