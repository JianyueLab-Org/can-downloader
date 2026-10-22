using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Downloader.Services;

namespace Downloader.ViewModels;

/// <summary>
/// 下载器设置：常规、下载、更新、网络。保存在程序目录下的 data/settings.json；
/// 程序目录里的路径保存为相对路径，整个目录搬走后仍然有效。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private const string FileName = "settings.json";

    /// <summary>需要保存的设置：本类所有可写的公共属性（IsDirty 除外）。</summary>
    private static readonly PropertyInfo[] Persisted = typeof(SettingsViewModel)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.SetMethod!.IsPublic && p.Name != nameof(IsDirty))
        .ToArray();

    public static SettingsViewModel Load()
    {
        var settings = new SettingsViewModel();
        if (LocalData.Load<Dictionary<string, JsonElement>>(FileName) is { } saved)
        {
            foreach (var property in Persisted)
            {
                if (!saved.TryGetValue(property.Name, out var value))
                    continue;
                try { property.SetValue(settings, value.Deserialize(property.PropertyType)); }
                catch (Exception ex) when (ex is JsonException or ArgumentException) { /* 格式不对的项保持默认 */ }
            }
            // 旧的目录不存在也没关系，用到时会创建；空值恢复默认
            if (string.IsNullOrWhiteSpace(settings.InstallRoot)) settings.InstallRoot = AppPaths.Apps;
            if (string.IsNullOrWhiteSpace(settings.CacheFolder)) settings.CacheFolder = AppPaths.Downloads;
            settings.InstallRoot = AppPaths.Resolve(settings.InstallRoot);
            settings.CacheFolder = AppPaths.Resolve(settings.CacheFolder);
        }
        settings.MarkSaved();
        return settings;
    }

    public void Save()
    {
        var values = Snapshot();
        values[nameof(InstallRoot)] = RelativeToBase(InstallRoot);
        values[nameof(CacheFolder)] = RelativeToBase(CacheFolder);
        LocalData.Save(FileName, values);
        MarkSaved();
        Log.Info($"设置已保存：安装目录 {InstallRoot}，缓存 {CacheFolder}，询问安装位置 {AskInstallLocation}，下载源 {DownloadSource}，代理 {ProxyMode}");
    }

    /// <summary>当前设置的副本，“取消”时用 <see cref="Restore"/> 还原。</summary>
    public Dictionary<string, object?> Snapshot() => Persisted.ToDictionary(p => p.Name, p => p.GetValue(this));

    public void Restore(Dictionary<string, object?> snapshot)
    {
        foreach (var property in Persisted)
        {
            if (snapshot.TryGetValue(property.Name, out var value))
                property.SetValue(this, value);
        }
        MarkSaved();
    }

    private static string RelativeToBase(string path)
    {
        var full = AppPaths.Resolve(path);
        var relative = Path.GetRelativePath(AppPaths.Base, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
    }

    /// <summary>有未应用的修改时启用“应用”按钮。</summary>
    [ObservableProperty]
    private bool _isDirty;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(IsDirty) or nameof(SpeedLimitText) or nameof(IsCustomProxy) or nameof(UseGitHub)))
            IsDirty = true;
    }

    public void MarkSaved() => IsDirty = false;

    // —— 常规 ——
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _notifyWhenFinished = true;
    [ObservableProperty] private bool _launchAfterInstall;

    // —— 下载 ——
    /// <summary>默认安装目录：程序目录下的 apps，每个软件装在其中以软件名命名的子目录。</summary>
    [ObservableProperty] private string _installRoot = AppPaths.Apps;

    /// <summary>安装时在对话框里询问安装位置；关闭后直接装到默认安装目录，不显示位置。</summary>
    [ObservableProperty] private bool _askInstallLocation = true;

    [ObservableProperty] private string _cacheFolder = AppPaths.Downloads;

    /// <summary>实际使用的目录（展开环境变量，相对路径按程序目录解析）。</summary>
    public string InstallRootPath => AppPaths.Resolve(InstallRoot);
    public string CacheFolderPath => AppPaths.Resolve(CacheFolder);

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
    public const string SourceSite = "本站（api.ceruleanavi.net）";
    public const string SourceGitHub = "GitHub";

    /// <summary>下载源：本站中转（默认，国内连接 GitHub 容易卡住），或直接从 GitHub Releases 下载。</summary>
    public IReadOnlyList<string> DownloadSources { get; } = [SourceSite, SourceGitHub];
    public IReadOnlyList<string> CheckIntervals { get; } = ["仅启动时", "每小时", "每 6 小时", "每天"];

    [ObservableProperty] private string? _channel = "稳定版";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseGitHub))]
    private string? _downloadSource = SourceSite;

    public bool UseGitHub => DownloadSource == SourceGitHub;
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
