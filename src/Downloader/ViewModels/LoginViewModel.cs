using Avalonia.Threading;
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
/// 登录窗口：检查完下载器更新后显示，登录后才能进入主界面下载；也可以选择离线模式（只能查看缓存的软件列表）。
/// 支持密码登录（CAN ID 或邮箱 + 密码）和邮箱验证码登录，对应 can-api 的 /api/v1/auth/signin 与 /api/v1/auth/code/*。
/// 启动时选择了“稍后”的话，这里仍可更新下载器。
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private const int CodeCooldownSeconds = 60;

    private readonly AccountSettings _settings = AccountStore.Load();
    private readonly DispatcherTimer _cooldownTimer;
    private string? _requestId;

    public LoginViewModel(UpdateManifest? manifest)
    {
        Manifest = manifest;
        if (manifest?.Launcher is { } launcher && VersionUtil.IsNewer(launcher.Version, AppInfo.Version))
            LauncherUpdateText = $"{AppInfo.Name} {launcher.Version} 已发布（当前 {AppInfo.Version}）";

        _rememberAccount = _settings.RememberAccount;
        _autoLogin = _settings.AutoLogin;
        _isCodeMode = _settings.UseCodeLogin;
        _canId = _settings.RememberAccount ? _settings.RememberedId : "";

        _cooldownTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            CodeCooldown = Math.Max(0, CodeCooldown - 1);
            if (CodeCooldown == 0)
                _cooldownTimer!.Stop();
        });
    }

    /// <summary>
    /// 启动时的自动登录：勾选了“自动登录”且保存过会话时，向服务器确认会话仍然有效。
    /// 返回 null 表示需要显示登录窗口（没有保存的会话、会话已失效或无法连接）。
    /// </summary>
    public static async Task<UserAccount?> TryResumeAsync()
    {
        var settings = AccountStore.Load();
        if (!settings.AutoLogin || AccountStore.LoadSession(settings) is not { } session)
            return null;

        CanApi.Shared.RestoreSession(session);
        try
        {
            if (await CanApi.Shared.GetSessionAsync() is { } user)
            {
                Log.Info($"自动登录：{user.Username}");
                return UserAccount.From(user);
            }
        }
        catch (ApiException)
        {
            // 无法连接时回到登录窗口，由用户决定重试或离线
            CanApi.Shared.ClearSession();
            return null;
        }

        // 会话已过期或被吊销（例如修改了密码）
        Log.Info("自动登录：保存的会话已失效");
        CanApi.Shared.ClearSession();
        AccountStore.ForgetSession();
        return null;
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

    /// <summary>CAN ID 或邮箱。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(SendCodeCommand))]
    private string _canId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _password = "";

    [ObservableProperty] private bool _rememberAccount;
    [ObservableProperty] private bool _autoLogin;

    // —— 验证码登录 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordMode), nameof(SwitchModeText))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isCodeMode;

    public bool IsPasswordMode => !IsCodeMode;
    public string SwitchModeText => IsCodeMode ? "改用密码登录" : "改用邮箱验证码登录";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _code = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendCodeText))]
    [NotifyCanExecuteChangedFor(nameof(SendCodeCommand))]
    private int _codeCooldown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendCodeText))]
    [NotifyCanExecuteChangedFor(nameof(SendCodeCommand))]
    private bool _isSendingCode;

    public string SendCodeText => IsSendingCode ? "正在发送…"
        : CodeCooldown > 0 ? $"{CodeCooldown} 秒后重发"
        : _requestId is null ? "发送验证码" : "重新发送";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeHint))]
    private string _codeHintText = "";

    public bool HasCodeHint => CodeHintText.Length > 0;

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

    [RelayCommand]
    private void SwitchMode()
    {
        IsCodeMode = !IsCodeMode;
        ErrorText = "";
    }

    private bool CanSendCode() => !IsSendingCode && CodeCooldown == 0 && CanId.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSendCode))]
    private async Task SendCodeAsync()
    {
        ErrorText = "";
        IsSendingCode = true;
        try
        {
            _requestId = await CanApi.Shared.RequestCodeAsync(CanId.Trim());
            Log.Info("已请求邮箱验证码");
            // 账号不存在时服务器同样返回成功，不会透露账号是否存在
            CodeHintText = "如果该账号存在，验证码已发送到绑定的邮箱。";
            CodeCooldown = CodeCooldownSeconds;
            _cooldownTimer.Start();
        }
        catch (ApiException ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsSendingCode = false;
        }
        LoginCommand.NotifyCanExecuteChanged();
    }

    private bool CanLogin() => !IsBusy && CanId.Trim().Length > 0
                                       && (IsCodeMode ? _requestId is not null && Code.Trim().Length > 0 : Password.Length > 0);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorText = "";
        var id = CanId.Trim();
        IsBusy = true;
        try
        {
            // 勾选“自动登录”时请服务器签发长期会话
            var user = IsCodeMode
                ? await CanApi.Shared.VerifyCodeAsync(_requestId!, Code.Trim(), AutoLogin)
                : await CanApi.Shared.SignInAsync(id, Password, AutoLogin);

            // 登录后获取软件清单（启动检查失败、从离线模式回来登录时还没有清单）；失败时先用缓存，进入主界面后可重试
            if (Manifest is null)
            {
                try { Manifest = await ManifestSource.FetchAsync(); }
                catch (ApiException) { Manifest = ManifestSource.LoadCached(); }
            }

            SaveSettings(id);
            Log.Info($"登录成功：{user.Username}（{(IsCodeMode ? "验证码" : "密码")}，自动登录 {AutoLogin}）");
            LoggedIn?.Invoke(this, UserAccount.From(user));
        }
        catch (ApiException ex)
        {
            Log.Warn($"登录失败：{ex.Code}");
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveSettings(string id)
    {
        _settings.RememberAccount = RememberAccount || AutoLogin;
        _settings.RememberedId = _settings.RememberAccount ? id : "";
        _settings.AutoLogin = AutoLogin;
        _settings.UseCodeLogin = IsCodeMode;
        AccountStore.SetSession(_settings, AutoLogin ? CanApi.Shared.CurrentSession : null);
        AccountStore.Save(_settings);
    }

    [RelayCommand]
    private void ContinueOffline()
    {
        Log.Info("进入离线模式");
        OfflineRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void UpdateLauncher() => UpdateLauncherRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSignUp() => Shell.Open(CanApi.SignUpUrl);

    [RelayCommand]
    private void OpenResetPassword() => Shell.Open(CanApi.ResetPasswordUrl);
}
