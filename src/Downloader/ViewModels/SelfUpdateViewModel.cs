using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Downloader.Services;

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
/// 启动窗口：打开下载器时连接 CAN API 获取软件清单并检查下载器自身的更新（API <c>apps</c> 里的 can-downloader），
/// 完成（或跳过）后进入登录窗口；无法连接时可离线模式进入主界面。
/// 更新：下载新版本、核对 SHA-256，由 <see cref="SelfUpdater"/> 交给新版本替换程序文件并重启。
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

    /// <summary>新版本已启动并会替换程序文件，当前进程应退出。</summary>
    public event EventHandler? RestartRequested;

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

    /// <summary>上次“立即更新”失败的原因，显示在更新说明下方。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateError))]
    private string _updateErrorText = "";

    public bool HasUpdateError => UpdateErrorText.Length > 0;

    public async Task StartAsync()
    {
        Stage = SelfUpdateStage.Checking;
        IsIndeterminate = true;
        StatusText = "正在检查更新…";
        DetailText = $"正在连接 {CanApi.ApiHost}";

        try
        {
            if (_simulateOffline)
            {
                await Task.Delay(700);
                throw new ApiException("timeout", $"连接 {CanApi.ApiHost} 超时（--offline 演示）。");
            }

            Manifest = await ManifestSource.FetchAsync();
        }
        catch (ApiException ex)
        {
            Stage = SelfUpdateStage.Failed;
            StatusText = "无法检查更新";
            DetailText = ex.Message;
            return;
        }

        if (Manifest.Launcher is not { } release || !VersionUtil.IsNewer(release.Version, AppInfo.Version))
        {
            Stage = SelfUpdateStage.UpToDate;
            IsIndeterminate = false;
            Progress = 100;
            StatusText = "已是最新版本";
            DetailText = $"{AppInfo.Name} {AppInfo.Version}";
            await Task.Delay(300);
            Completed?.Invoke(this, Manifest);
            return;
        }

        LatestVersion = release.Version;
        ReleaseInfo = $"{release.ReleaseDate} 发布 · {Format.Size(release.Size)}{(release.Prerelease ? " · 预发布" : "")}";
        IsMandatory = release.Mandatory;
        Notes = release.Notes;
        OnPropertyChanged(nameof(Notes));
        Stage = SelfUpdateStage.Available;

        if (_autoUpdate)
            await UpdateNowAsync();
    }

    /// <summary>下载新版本、核对 SHA-256、交给新版本替换并重启。失败时回到“发现新版本”并显示原因。</summary>
    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (Manifest?.Launcher is not { } release)
            return;
        UpdateErrorText = "";
        if (SelfUpdater.CannotReplaceReason is { } reason)
        {
            UpdateErrorText = reason;
            return;
        }
        if (!Uri.TryCreate(CanApi.BaseUri, release.DownloadUrl, out var url))
        {
            UpdateErrorText = "发布信息里没有下载地址。";
            return;
        }

        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        Stage = SelfUpdateStage.Downloading;
        IsIndeterminate = false;
        Progress = 0;
        StatusText = $"正在下载 {AppInfo.Name} {release.Version}";
        DetailText = $"正在连接 {url.Host}…";
        var zip = Path.Combine(AppPaths.Downloads, $"CAN-Downloader-{release.Version}.zip");
        Log.Info($"开始更新下载器 {AppInfo.Version} → {release.Version}");

        try
        {
            var progress = new DownloadProgress();
            var download = Task.Run(() => PackageInstaller.DownloadAsync(url, zip, release.Size, progress, () => 0, token), token);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long last = -1;
            var speed = 0d;
            while (!download.IsCompleted)
            {
                await Task.WhenAny(download, Task.Delay(250, CancellationToken.None));
                var (done, total) = (progress.Downloaded, progress.Total);
                if (total <= 0)
                    continue;
                if (last >= 0)
                {
                    var instant = (done - last) / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                    speed = speed <= 0 ? instant : speed * 0.7 + instant * 0.3;
                }
                clock.Restart();
                last = done;
                Progress = done * 100.0 / total;
                var remaining = speed > 1 ? Format.Remaining((total - done) / speed) : "正在计算剩余时间";
                DetailText = $"{Format.Size(done)} / {Format.Size(total)} · {Format.Speed(speed)} · {remaining}";
            }
            await download;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log.Info("取消更新下载器");
            Stage = SelfUpdateStage.Available;
            return;
        }
        catch (Exception ex)
        {
            Fail("下载失败", ex);
            return;
        }

        Stage = SelfUpdateStage.Installing;
        IsIndeterminate = true;
        StatusText = "正在安装更新…";
        try
        {
            DetailText = "校验文件";
            await Task.Run(() =>
            {
                if (release.Sha256 is { } sha256)
                    PackageInstaller.VerifySha256(zip, sha256);
                PackageInstaller.Verify(zip);
            });
            DetailText = "准备新版本";
            await Task.Run(() => SelfUpdater.Stage(zip));
        }
        catch (Exception ex)
        {
            if (ex is InvalidDataException)
                TryDelete(zip);
            Fail("安装更新失败", ex);
            return;
        }

        TryDelete(zip);
        try
        {
            SelfUpdater.LaunchApplier();
        }
        catch (Exception ex)
        {
            Fail("无法启动新版本", ex);
            return;
        }
        StatusText = "正在重启…";
        DetailText = "";
        Log.Info($"交给新版本 {release.Version} 完成替换，退出");
        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(string title, Exception ex)
    {
        Log.Error($"更新下载器：{title}", ex);
        Stage = SelfUpdateStage.Available;
        UpdateErrorText = $"{title}：{ex.Message}";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 缓存删不掉不影响 */ }
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
