using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Downloader.Services;

namespace Downloader.ViewModels;

/// <summary>已登录的 CAN 账号。</summary>
public sealed record UserAccount(string CanId, string Name, string? Email, int Rating)
{
    public string RatingText => Ratings.Describe(Rating);

    public static UserAccount From(ApiUser user) => new(user.Username, user.Name, user.Email, user.Rating);
}

/// <summary>
/// 登录窗口：检查完下载器更新后显示，登录后才能进入主界面。
/// 用 CAN 账号 OAuth 登录（<see cref="OAuthLogin"/>）：在浏览器里打开 CAN 网站登录并授权，下载器不接触密码。
/// 勾选“自动登录”时把 refresh 令牌加密保存在本机，下次启动直接登录。启动时选择了“稍后”的话，这里仍可更新下载器。
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    /// <summary>等待浏览器授权的最长时间。</summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    private readonly AccountSettings _settings = AccountStore.Load();
    private CancellationTokenSource? _signIn;

    public LoginViewModel(UpdateManifest? manifest)
    {
        Manifest = manifest;
        if (manifest?.Launcher is { } launcher && VersionUtil.IsNewer(launcher.Version, AppInfo.Version))
            LauncherUpdateText = $"{AppInfo.Name} {launcher.Version} 已发布（当前 {AppInfo.Version}）";
        _autoLogin = _settings.AutoLogin;
    }

    /// <summary>
    /// 启动时的自动登录：用保存的 refresh 令牌换新令牌并读取账号信息。
    /// 返回 null 表示需要显示登录窗口（没有保存的授权、授权已失效或无法连接）。
    /// </summary>
    public static async Task<UserAccount?> TryResumeAsync()
    {
        var settings = AccountStore.Load();
        if (!settings.AutoLogin || AccountStore.LoadRefreshToken(settings) is not { } refreshToken)
            return null;

        try
        {
            var tokens = await OAuthLogin.RefreshAsync(refreshToken);
            // refresh 令牌每次都会换新，旧的已作废，必须保存新的
            AccountStore.SaveRefreshToken(settings, tokens.RefreshToken);
            var user = await OAuthLogin.GetUserAsync(tokens.AccessToken);
            Log.Info($"自动登录：{user.Username}");
            return UserAccount.From(user);
        }
        catch (ApiException ex)
        {
            // 授权被撤销或过期时丢掉保存的令牌；网络问题时保留，下次再试
            if (ex.Code is "invalid_grant" or "invalid_token")
                AccountStore.SaveRefreshToken(settings, null);
            Log.Warn($"自动登录失败：{ex.Code}");
            return null;
        }
    }

    /// <summary>登录成功。</summary>
    public event EventHandler<UserAccount>? LoggedIn;

    /// <summary>登录前更新下载器：回到启动窗口下载并重启。</summary>
    public event EventHandler? UpdateLauncherRequested;

    public UpdateManifest? Manifest { get; private set; }

    public string AppName => AppInfo.Name;
    public string CurrentVersion => AppInfo.Version;

    public string? LauncherUpdateText { get; }
    public bool HasLauncherUpdate => LauncherUpdateText is not null;

    [ObservableProperty] private bool _autoLogin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    [ObservableProperty] private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task LoginAsync()
    {
        ErrorText = "";
        IsBusy = true;
        _signIn = new CancellationTokenSource(SignInTimeout);
        var token = _signIn.Token;
        try
        {
            StatusText = "已在浏览器中打开 CAN 网站，请在网页上登录并授权…";
            var tokens = await OAuthLogin.SignInAsync(token);
            StatusText = "正在读取账号信息…";
            var user = await OAuthLogin.GetUserAsync(tokens.AccessToken, token);

            // 登录后获取软件清单（启动时获取失败的话）；仍然失败时进入主界面后可重试
            if (Manifest is null)
            {
                try { Manifest = await ManifestSource.FetchAsync(token); }
                catch (ApiException) { /* 主界面显示“检查更新” */ }
            }

            _settings.AutoLogin = AutoLogin;
            AccountStore.SaveRefreshToken(_settings, AutoLogin ? tokens.RefreshToken : null);
            Log.Info($"登录成功：{user.Username}（自动登录 {AutoLogin}）");
            LoggedIn?.Invoke(this, UserAccount.From(user));
        }
        catch (OperationCanceledException)
        {
            ErrorText = _signIn.IsCancellationRequested && !_cancelledByUser ? "等待授权超时，请重新登录。" : "已取消登录。";
        }
        catch (ApiException ex)
        {
            Log.Warn($"登录失败：{ex.Code}");
            ErrorText = ex.Message;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 打不开浏览器
            Log.Error("登录：无法打开浏览器", ex);
            ErrorText = $"无法打开浏览器：{ex.Message}";
        }
        finally
        {
            _cancelledByUser = false;
            _signIn.Dispose();
            _signIn = null;
            StatusText = "";
            IsBusy = false;
        }
    }

    private bool _cancelledByUser;

    [RelayCommand]
    private void CancelLogin()
    {
        _cancelledByUser = true;
        _signIn?.Cancel();
    }

    [RelayCommand]
    private void UpdateLauncher() => UpdateLauncherRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSignUp() => Shell.Open(CanApi.SignUpUrl);
}
