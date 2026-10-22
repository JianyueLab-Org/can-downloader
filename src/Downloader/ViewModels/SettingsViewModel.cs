using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Downloader.ViewModels;

/// <summary>下载器设置：常规、下载、更新、网络。界面原型，尚未持久化。</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    /// <summary>有未应用的修改时启用“应用”按钮。</summary>
    [ObservableProperty]
    private bool _isDirty;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(IsDirty) or nameof(SpeedLimitText) or nameof(IsCustomProxy)))
            IsDirty = true;
    }

    public void MarkSaved() => IsDirty = false;

    // —— 常规 ——
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _notifyWhenFinished = true;
    [ObservableProperty] private bool _launchAfterInstall;

    // —— 下载 ——
    [ObservableProperty] private string _installRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programFiles ? programFiles : "/opt", "CAN");

    [ObservableProperty] private string _cacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAN", "Downloads");

    [ObservableProperty] private decimal _maxConcurrentDownloads = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitText))]
    private bool _limitSpeed;

    /// <summary>限速，MB/s。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitText))]
    private double _speedLimit = 5;

    public string SpeedLimitText => LimitSpeed ? $"{SpeedLimit:0.#} MB/s" : "不限";

    /// <summary>限速（字节/秒），0 表示不限。</summary>
    public double SpeedLimitBytes => LimitSpeed ? SpeedLimit * 1024 * 1024 : 0;

    [ObservableProperty] private bool _verifyAfterDownload = true;
    [ObservableProperty] private bool _deleteCacheAfterInstall = true;

    // —— 更新 ——
    public IReadOnlyList<string> Channels { get; } = ["稳定版", "测试版", "开发版"];
    public IReadOnlyList<string> Servers { get; } = ["自动选择", "主服务器", "镜像（华东）", "镜像（华南）"];
    public IReadOnlyList<string> CheckIntervals { get; } = ["仅启动时", "每小时", "每 6 小时", "每天"];

    [ObservableProperty] private string? _channel = "稳定版";
    [ObservableProperty] private string? _server = "自动选择";
    [ObservableProperty] private string? _checkInterval = "每 6 小时";
    [ObservableProperty] private bool _autoUpdateInstalled;
    [ObservableProperty] private bool _showNewProducts = true;

    // —— 网络 ——
    public IReadOnlyList<string> ProxyModes { get; } = ["不使用代理", "使用系统代理", "自定义 HTTP 代理"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomProxy))]
    private string? _proxyMode = "使用系统代理";

    public bool IsCustomProxy => ProxyMode == "自定义 HTTP 代理";

    [ObservableProperty] private string _proxyHost = "127.0.0.1";
    [ObservableProperty] private decimal _proxyPort = 7890;
    [ObservableProperty] private decimal _timeoutSeconds = 15;
}
