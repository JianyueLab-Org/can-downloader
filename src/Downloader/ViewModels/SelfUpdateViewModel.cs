using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Downloader.ViewModels;

public enum SelfUpdateStage
{
    /// <summary>连接服务器、获取清单、比较版本。</summary>
    Checking,
    /// <summary>已是最新版本，稍后自动进入主界面。</summary>
    UpToDate,
    /// <summary>有新版本，等待用户选择。</summary>
    Available,
    Downloading,
    /// <summary>安装更新并重启。</summary>
    Installing,
    /// <summary>无法获取更新清单。</summary>
    Failed
}

/// <summary>
/// 启动窗口：打开下载器时先检查下载器自身的更新，完成（或跳过）后进入登录窗口；无法连接时可离线模式进入主界面。
/// 界面原型：网络请求与下载都是模拟的，清单读取自 <see cref="ManifestSource"/>。
/// </summary>
public sealed partial class SelfUpdateViewModel : ViewModelBase
{
    private bool _simulateOffline;
    private readonly bool _autoUpdate;
    private CancellationTokenSource? _downloadCts;

    /// <param name="simulateOffline">演示连接失败（命令行 --offline）。</param>
    /// <param name="autoUpdate">从主界面“更新并重启”进入时，发现新版本后直接下载。</param>
    public SelfUpdateViewModel(bool simulateOffline = false, bool autoUpdate = false)
    {
        _simulateOffline = simulateOffline;
        _autoUpdate = autoUpdate;
    }

    /// <summary>检查流程结束。参数为获取到的清单，进入登录窗口；选择离线模式时为 null，直接进入主界面。</summary>
    public event EventHandler<UpdateManifest?>? Completed;

    public UpdateManifest? Manifest { get; private set; }

    public string AppName => AppInfo.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsAvailable), nameof(IsDownloading), nameof(IsInstalling),
        nameof(IsFailed), nameof(ShowStatus), nameof(ShowProgress))]
    private SelfUpdateStage _stage;

    public bool IsChecking => Stage is SelfUpdateStage.Checking or SelfUpdateStage.UpToDate;
    public bool IsAvailable => Stage == SelfUpdateStage.Available;
    public bool IsDownloading => Stage == SelfUpdateStage.Downloading;
    public bool IsInstalling => Stage == SelfUpdateStage.Installing;
    public bool IsFailed => Stage == SelfUpdateStage.Failed;
    public bool ShowStatus => Stage is not SelfUpdateStage.Available;
    public bool ShowProgress => Stage is SelfUpdateStage.Checking or SelfUpdateStage.Downloading or SelfUpdateStage.Installing;

    [ObservableProperty] private string _currentVersion = AppInfo.Version;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private double _progress;

    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private string _releaseInfo = "";
    [ObservableProperty] private bool _isMandatory;
    public List<string> Notes { get; private set; } = [];

    public async Task StartAsync()
    {
        Stage = SelfUpdateStage.Checking;
        IsIndeterminate = true;
        StatusText = "正在检查更新…";
        DetailText = "正在连接更新服务器";
        await Task.Delay(700);

        try
        {
            if (_simulateOffline)
                throw new TimeoutException("连接 update.can-network.example 超时。");

            DetailText = "正在获取更新清单";
            await Task.Delay(500);
            Manifest = await Task.Run(ManifestSource.Load);
        }
        catch (Exception ex)
        {
            Stage = SelfUpdateStage.Failed;
            StatusText = "无法检查更新";
            DetailText = ex is FileNotFoundException ? "找不到更新清单 manifest.json。" : ex.Message;
            return;
        }

        DetailText = $"正在比较版本（{Manifest.Server}）";
        await Task.Delay(350);

        var release = Manifest.Launcher;
        if (!VersionUtil.IsNewer(release.Version, AppInfo.Version))
        {
            Stage = SelfUpdateStage.UpToDate;
            IsIndeterminate = false;
            Progress = 100;
            StatusText = "已是最新版本";
            DetailText = $"{AppInfo.Name} {AppInfo.Version}";
            await Task.Delay(500);
            Completed?.Invoke(this, Manifest);
            return;
        }

        LatestVersion = release.Version;
        ReleaseInfo = $"{release.ReleaseDate} 发布 · {Format.Size(release.Size)}";
        IsMandatory = release.Mandatory;
        Notes = release.Notes;
        OnPropertyChanged(nameof(Notes));
        Stage = SelfUpdateStage.Available;

        if (_autoUpdate)
            await UpdateNowAsync();
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (Manifest is null)
            return;

        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        var total = (double)Manifest.Launcher.Size;
        var downloaded = 0d;

        Stage = SelfUpdateStage.Downloading;
        IsIndeterminate = false;
        Progress = 0;
        StatusText = $"正在下载 {AppInfo.Name} {LatestVersion}";

        try
        {
            var random = new Random();
            while (downloaded < total)
            {
                await Task.Delay(80, token);
                var speed = (5.5 + random.NextDouble() * 2.5) * 1024 * 1024;
                downloaded = Math.Min(total, downloaded + speed * 0.08);
                Progress = downloaded / total * 100;
                DetailText = $"{Format.Size(downloaded)} / {Format.Size(total)} · {Format.Speed(speed)} · {Format.Remaining((total - downloaded) / speed)}";
            }
        }
        catch (OperationCanceledException)
        {
            Stage = SelfUpdateStage.Available;
            return;
        }

        Stage = SelfUpdateStage.Installing;
        IsIndeterminate = true;
        StatusText = "正在安装更新…";
        DetailText = "校验文件签名";
        await Task.Delay(600);
        DetailText = "替换程序文件";
        await Task.Delay(600);
        StatusText = "正在重启…";
        DetailText = "";
        await Task.Delay(500);

        // 模拟重启后的新版本
        AppInfo.Version = LatestVersion;
        CurrentVersion = LatestVersion;
        Completed?.Invoke(this, Manifest);
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    /// <summary>暂不更新，进入登录窗口（必需更新时不可用）。</summary>
    [RelayCommand]
    private void Skip() => Completed?.Invoke(this, Manifest);

    /// <summary>重试。演示：命令行 --offline 模拟的失败只出现一次。</summary>
    [RelayCommand]
    private Task RetryAsync()
    {
        _simulateOffline = false;
        return StartAsync();
    }

    /// <summary>无法连接时离线模式进入：不登录，只显示上次缓存的软件列表。</summary>
    [RelayCommand]
    private void ContinueOffline() => Completed?.Invoke(this, null);
}
