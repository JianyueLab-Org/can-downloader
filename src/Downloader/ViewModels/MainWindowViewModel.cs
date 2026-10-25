using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Downloader.Services;

namespace Downloader.ViewModels;

/// <summary>
/// 主界面：软件库（由更新清单生成，按分类分组）、当前软件详情、下载队列。
/// 下载经 <see cref="CanApi"/> 进行（本站中转或 GitHub），安装是把 zip 解压到安装目录并记录到 <see cref="LocalInstalls"/>；
/// <see cref="DispatcherTimer"/> 定时把下载线程的进度刷新到界面。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const double TickSeconds = 0.25;

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ProductViewModel, Transfer> _transfers = [];
    private UpdateManifest? _manifest;
    private LibraryEntry? _selectedEntry;

    /// <param name="manifest">软件清单；为 null（获取失败）时软件库为空，可点“检查更新”重试。</param>
    /// <param name="account">已登录的账号。</param>
    public MainWindowViewModel(UpdateManifest? manifest, SettingsViewModel settings, UserAccount account)
    {
        Settings = settings;
        Account = account;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(TickSeconds), DispatcherPriority.Background, (_, _) => Tick());

        if (manifest is not null)
            ApplyManifest(manifest, announce: false);

        SelectedEntry = Products.FirstOrDefault(p => p.HasUpdate) ?? Products.FirstOrDefault();
        LastCheckText = manifest is null ? "" : $"已检查 {DateTime.Now:HH:mm}";
    }

    public SettingsViewModel Settings { get; }

    /// <summary>当前清单，退出登录时交给登录窗口。</summary>
    public UpdateManifest? Manifest => _manifest;

    public UserAccount Account { get; }
    public string AccountText => $"{(Account.Name.Length > 0 ? Account.Name : "CAN 用户")}（{Account.CanId}）";

    /// <summary>退出登录：回到登录窗口。</summary>
    public event EventHandler? LoginRequested;

    /// <summary>退出登录：停止下载（已下载的部分保留，下次可续传），吊销 OAuth 授权，回到登录窗口。</summary>
    public async Task LogoutAsync()
    {
        _timer.Stop();
        foreach (var transfer in _transfers.Values)
            transfer.Cts?.Cancel();
        await OAuthLogin.SignOutAsync();
        var account = AccountStore.Load();
        AccountStore.SaveRefreshToken(account, null);
        Log.Info("退出登录");
        LoginRequested?.Invoke(this, EventArgs.Empty);
    }

    public string AppVersion => $"v{AppInfo.Version}";

    /// <summary>全部软件，按清单中的分类与顺序排列。</summary>
    public ObservableCollection<ProductViewModel> Products { get; } = [];

    /// <summary>左侧列表：分类标题 + 软件，按搜索文字过滤。</summary>
    public ObservableCollection<LibraryEntry> Entries { get; } = [];

    public ObservableCollection<NoticeViewModel> Notices { get; } = [];

    /// <summary>主界面“更新并重启”：关闭主窗口，重新进入启动检查并自动下载。</summary>
    public event EventHandler? RestartForUpdateRequested;

    /// <summary>需要主窗口显示的提示（例如找不到要打开的程序）。</summary>
    public event EventHandler<(string Title, string Message)>? MessageRequested;

    [ObservableProperty] private string _searchText = "";

    partial void OnSearchTextChanged(string value) => RebuildEntries();

    public LibraryEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            // 分类标题不可选中：保留原来的选择
            if (value is { IsSelectable: false })
            {
                Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedEntry)));
                return;
            }
            if (SetProperty(ref _selectedEntry, value))
                OnPropertyChanged(nameof(SelectedProduct));
        }
    }

    public ProductViewModel? SelectedProduct => _selectedEntry as ProductViewModel;

    public bool HasProducts => Products.Count > 0;
    public bool HasEntries => Entries.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand))]
    private bool _isChecking;

    public string CheckButtonText => IsChecking ? "正在检查…" : "检查更新";

    [ObservableProperty] private string _lastCheckText = "";
    [ObservableProperty] private string _manifestInfo = "";

    // —— 汇总 ——
    public int UpdateCount => Products.Count(p => p.State == ProductState.UpdateAvailable);
    public bool HasUpdates => UpdateCount > 0;
    public string UpdateAllText => $"全部更新（{UpdateCount}）";
    public string LibrarySummary => $"已安装 {Products.Count(p => p.IsInstalled)} · 共 {Products.Count} 个";

    [ObservableProperty] private string _transferSummary = "";
    [ObservableProperty] private bool _hasTransfers;

    // ========== 清单 ==========

    /// <summary>
    /// 按清单生成软件库：已有的软件更新清单信息，新出现的自动添加，清单中移除且未安装的删除，
    /// 已安装的标记为“已下架”。<paramref name="announce"/> 为真时用提示条报告变化。
    /// </summary>
    private void ApplyManifest(UpdateManifest manifest, bool announce)
    {
        _manifest = manifest;
        ManifestInfo = $"发布于 {manifest.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {manifest.Server}";

        var categoryNames = manifest.Categories.ToDictionary(c => c.Id, c => c.Name);
        var productNames = manifest.Products.ToDictionary(p => p.Id, p => p.Name);
        var existing = Products.ToDictionary(p => p.Id);
        var added = new List<ProductViewModel>();
        var updated = new List<ProductViewModel>();

        // 按分类顺序排列；未知分类归入“其他”，放在最后
        var categoryOrder = manifest.Categories.Select(c => c.Id).ToList();
        var ordered = manifest.Products
            .Select((p, index) => (p, index))
            .OrderBy(x => categoryOrder.IndexOf(x.p.Category) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.p);

        var result = new List<ProductViewModel>();
        foreach (var item in ordered)
        {
            if (existing.Remove(item.Id, out var product))
            {
                var hadUpdate = product.HasUpdate;
                var oldVersion = product.LatestVersion;
                product.Apply(item, categoryNames.GetValueOrDefault(item.Category, "其他"), productNames);
                if (product.HasUpdate && (!hadUpdate || VersionUtil.IsNewer(product.LatestVersion, oldVersion)))
                    updated.Add(product);
            }
            else
            {
                var record = LocalInstalls.Find(item.Id);
                product = new ProductViewModel(item.Id)
                {
                    InstalledVersion = record?.Version,
                    InstallPath = record?.Path ?? DefaultInstallPath(item.Id)
                };
                product.Apply(item, categoryNames.GetValueOrDefault(item.Category, "其他"), productNames);
                product.IsNew = announce;
                added.Add(product);
            }
            result.Add(product);
        }

        // 清单里已经没有的软件：已安装或进行中的保留并标记，否则移除
        foreach (var product in existing.Values)
        {
            if (product.IsInstalled || product.IsBusy)
            {
                product.IsDelisted = true;
                product.RefreshState();
                result.Add(product);
            }
        }

        Products.Clear();
        foreach (var product in result)
            Products.Add(product);
        RebuildEntries();
        RefreshSummary();

        if (!announce)
            return;

        if (manifest.Launcher is { } launcher && VersionUtil.IsNewer(launcher.Version, AppInfo.Version))
        {
            Notices.Add(new NoticeViewModel(NoticeKind.Update,
                $"{AppInfo.Name} {launcher.Version} 已发布（当前 {AppInfo.Version}）。",
                "更新并重启", () => RestartForUpdateRequested?.Invoke(this, EventArgs.Empty)));
        }

        if (added.Count > 0 && Settings.ShowNewProducts)
        {
            Notices.Add(new NoticeViewModel(NoticeKind.Info,
                $"软件库新增 {added.Count} 个项目：{string.Join("、", added.Select(p => p.Name))}",
                "查看", () => SelectedEntry = added[0]));
        }

        if (updated.Count > 0)
        {
            if (Settings.AutoUpdateInstalled)
            {
                foreach (var product in updated)
                    Enqueue(product, ProductTask.Update);
                Notices.Add(new NoticeViewModel(NoticeKind.Info,
                    $"已开始自动更新：{string.Join("、", updated.Select(p => $"{p.Name} {p.LatestVersion}"))}"));
            }
            else
            {
                Notices.Add(new NoticeViewModel(NoticeKind.Update,
                    $"{updated.Count} 个已安装的软件有新版本：{string.Join("、", updated.Select(p => $"{p.Name} {p.LatestVersion}"))}",
                    "全部更新", UpdateAll));
            }
        }
    }

    private void RebuildEntries()
    {
        var selected = _selectedEntry;
        var filter = SearchText.Trim();

        Entries.Clear();
        foreach (var group in Products.GroupBy(p => p.CategoryName))
        {
            var items = group.Where(p => filter.Length == 0
                                         || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                         || p.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                         || p.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0)
                continue;
            Entries.Add(new CategoryHeaderEntry(group.Key, items.Count));
            foreach (var item in items)
                Entries.Add(item);
        }

        SelectedEntry = selected is ProductViewModel p && Products.Contains(p)
            ? p
            : Entries.OfType<ProductViewModel>().FirstOrDefault();
        // 列表清空时 ListBox 会把选择写回 null，这里再通知一次以恢复高亮
        OnPropertyChanged(nameof(SelectedEntry));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasProducts));
    }

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesAsync()
    {
        IsChecking = true;
        try
        {
            CanApi.Shared.Configure(Settings);
            var manifest = await ManifestSource.FetchAsync();

            // 清除上一次检查留下的提示与“新”标记
            Notices.Clear();
            foreach (var product in Products)
                product.IsNew = false;

            ApplyManifest(manifest, announce: true);
            LastCheckText = $"已检查 {DateTime.Now:HH:mm}";
        }
        catch (ApiException ex)
        {
            Notices.Add(new NoticeViewModel(NoticeKind.Warning, $"检查更新失败：{ex.Message}", "重试", () => _ = CheckUpdatesAsync()));
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanCheckUpdates() => !IsChecking;

    [RelayCommand]
    private void DismissNotice(NoticeViewModel notice) => Notices.Remove(notice);

    [RelayCommand]
    private void InvokeNotice(NoticeViewModel notice)
    {
        Notices.Remove(notice);
        notice.Invoke();
    }

    // ========== 下载队列 ==========

    /// <summary>入队时固定发布身份；暂停、排队及清单刷新不改变本次下载，重试会创建新任务。</summary>
    private sealed class Transfer(ProductTask task, string productId, string version, Uri? url, string zipPath,
        long expectedSize, string? sha256)
    {
        public ProductTask Task { get; } = task;
        public string ProductId { get; } = productId;
        public string Version { get; } = version;
        public Uri? Url { get; } = url;
        public string ZipPath { get; } = zipPath;
        public long ExpectedSize { get; } = expectedSize;
        public string? Sha256 { get; } = sha256;
        public DownloadProgress Progress { get; } = new();
        public CancellationTokenSource? Cts { get; set; }
        /// <summary>上一次下载的任务。继续下载前要等它释放 .part 文件。</summary>
        public Task? Run { get; set; }
        /// <summary>上次刷新界面时的已下载字节数，-1 表示刚开始。</summary>
        public long LastBytes { get; set; }
    }

    public string DefaultInstallPath(string id) => Path.Combine(Settings.InstallRootPath, id);

    /// <summary>设置里改了默认安装目录：未安装的软件跟着改。</summary>
    public void RefreshDefaultPaths()
    {
        foreach (var product in Products.Where(p => !p.IsInstalled && !p.IsBusy))
            product.InstallPath = DefaultInstallPath(product.Id);
    }

    [RelayCommand]
    private void UpdateAll()
    {
        foreach (var product in Products.Where(p => p.State == ProductState.UpdateAvailable).ToList())
            Enqueue(product, ProductTask.Update);
    }

    /// <summary>安装：组件与安装位置已在安装对话框中选好。</summary>
    public void Install(ProductViewModel product, IReadOnlyList<ComponentViewModel> components, string installPath, bool withDependencies)
    {
        foreach (var component in product.Components)
            component.IsSelected = components.FirstOrDefault(c => c.Id == component.Id)?.IsSelected ?? component.IsSelected;
        product.NotifyComponentsChanged();
        product.InstallPath = installPath;

        if (withDependencies)
        {
            foreach (var dependency in MissingDependencies(product))
                Enqueue(dependency, ProductTask.Install);
        }
        Enqueue(product, ProductTask.Install);
    }

    public IReadOnlyList<ProductViewModel> MissingDependencies(ProductViewModel product) =>
        product.Requires
            .Select(id => Products.FirstOrDefault(p => p.Id == id))
            .OfType<ProductViewModel>()
            .Where(p => !p.IsInstalled && !p.IsBusy)
            .ToList();

    public void Update(ProductViewModel product) => Enqueue(product, ProductTask.Update);

    /// <summary>修复：重新下载最新的安装包并覆盖安装。</summary>
    public void Repair(ProductViewModel product) => Enqueue(product, ProductTask.Repair);

    private void Enqueue(ProductViewModel product, ProductTask task)
    {
        if (product.IsBusy || !product.CanDownload)
            return;

        // 与清单写入同在界面线程；在任何异步操作或状态通知前捕获本次发布。
        var url = Settings.UseGitHub && product.OriginUrl is { } origin ? origin : product.DownloadUrl;
        Uri.TryCreate(CanApi.BaseUri, url, out var uri);
        var transfer = new Transfer(task, product.Id, product.LatestVersion, uri,
            PackageInstaller.CachePath(Settings.CacheFolderPath, product.Id, product.LatestVersion),
            product.DownloadSize, product.Sha256);

        product.CurrentTask = task;
        product.TotalBytes = transfer.ExpectedSize;
        product.DownloadedBytes = 0;
        product.Speed = 0;
        product.Progress = 0;
        product.PhaseText = TaskTitle(product);
        product.TransferText = "等待其他下载完成";
        product.State = ProductState.Queued;
        _transfers[product] = transfer;

        Pump();
    }

    /// <summary>按设置的同时下载数启动排队中的项目。</summary>
    private void Pump()
    {
        CanApi.Shared.Configure(Settings);
        var active = Products.Count(p => p.State == ProductState.Downloading);
        var maxConcurrent = Math.Max(1, (int)Settings.MaxConcurrentDownloads);
        foreach (var next in Products.Where(p => p.State == ProductState.Queued).ToList())
        {
            if (active >= maxConcurrent)
                break;
            if (!_transfers.TryGetValue(next, out var transfer))
                continue;
            Start(next, transfer);
            active++;
        }

        if (Products.Any(p => p.State is ProductState.Queued or ProductState.Downloading))
            _timer.Start();
        RefreshSummary();
    }

    private void Start(ProductViewModel product, Transfer transfer)
    {
        if (transfer.Url is not { } uri)
        {
            Fail(product, transfer, "下载失败", new InvalidDataException("没有可用的下载地址。"));
            return;
        }

        product.State = ProductState.Downloading;
        product.Speed = 0;
        product.TransferText = $"正在连接 {uri.Host}…";
        var cts = new CancellationTokenSource();
        transfer.Cts = cts;
        transfer.LastBytes = -1;
        Log.Info($"开始下载 {transfer.ProductId} {transfer.Version}（{uri.Host}）→ {transfer.ZipPath}");
        transfer.Run = RunAsync(product, transfer, transfer.Run, cts.Token);
    }

    /// <summary>下载，然后校验、安装。暂停、取消时下载被取消，状态已由调用方设置好。</summary>
    private async Task RunAsync(ProductViewModel product, Transfer transfer, Task? previous, CancellationToken ct)
    {
        if (previous is not null)
        {
            try { await previous; }
            catch { /* 上一次的结果已经处理过 */ }
        }

        try
        {
            await Task.Run(() => PackageInstaller.DownloadAsync(transfer.Url!, transfer.ZipPath, transfer.ExpectedSize, transfer.Progress,
                SpeedLimitPerDownload, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail(product, transfer, "下载失败", ex);
            Pump();
            return;
        }

        if (ct.IsCancellationRequested || !_transfers.ContainsKey(product))
            return;
        Log.Info($"下载完成 {transfer.ProductId} {transfer.Version}（{transfer.Progress.Total} 字节）");
        product.Speed = 0;
        product.State = ProductState.Installing;
        Pump();
        await InstallPackageAsync(product, transfer);
    }

    private volatile int _activeDownloads;

    /// <summary>限速平均分给正在下载的项目（在下载线程上调用）。</summary>
    private double SpeedLimitPerDownload()
    {
        var limit = Settings.SpeedLimitBytes;
        return limit <= 0 ? 0 : limit / Math.Max(1, _activeDownloads);
    }

    /// <summary>下载完成：校验、暂存并以可回滚操作提交文件和安装记录。</summary>
    private async Task InstallPackageAsync(ProductViewModel product, Transfer transfer)
    {
        product.State = ProductState.Installing;
        product.Progress = 100;
        product.TransferText = "";
        RefreshSummary();

        var zip = transfer.ZipPath;
        var installDir = product.InstallPath;
        try
        {
            // 发布方提供了 SHA-256 时总是核对；“下载完成后校验文件”另外完整读取一遍压缩包
            var sha256 = transfer.Sha256;
            if (sha256 is not null || Settings.VerifyAfterDownload)
            {
                product.PhaseText = "正在校验文件…";
                try
                {
                    await Task.Run(() =>
                    {
                        if (sha256 is not null)
                            PackageInstaller.VerifySha256(zip, sha256);
                        if (Settings.VerifyAfterDownload)
                            PackageInstaller.Verify(zip);
                    });
                }
                catch (InvalidDataException)
                {
                    TryDelete(zip);
                    throw new InvalidDataException("安装包已损坏，已删除，请重新下载。");
                }
            }

            product.PhaseText = transfer.Task switch
            {
                ProductTask.Repair => "正在修复…",
                ProductTask.Update => "正在更新…",
                _ => "正在安装…"
            };
            var record = await Task.Run(() => PackageInstaller.Install(zip, transfer.ProductId, transfer.Version, installDir));
            Log.Info($"已安装 {product.Id} {record.Version} → {record.Path}（{record.Files.Count} 个文件）");
            product.InstalledVersion = record.Version;
            product.InstallPath = record.Path;
            if (Settings.DeleteCacheAfterInstall)
                TryDelete(zip);
        }
        catch (Exception ex)
        {
            if (LocalInstalls.Find(product.Id) is { } previous)
            {
                product.InstalledVersion = previous.Version;
                product.InstallPath = previous.Path;
            }
            Fail(product, transfer, transfer.Task == ProductTask.Install ? "安装失败" : "更新失败", ex);
            return;
        }

        _transfers.Remove(product);
        product.RefreshState();
        RefreshSummary();

        if (Settings.LaunchAfterInstall && transfer.Task == ProductTask.Install && product.Executable is not null)
            Launch(product);
    }

    /// <summary>下载或安装失败：恢复状态，用提示条报告原因并提供重试。</summary>
    private void Fail(ProductViewModel product, Transfer transfer, string title, Exception ex)
    {
        Log.Error($"{product.Id} {title}", ex);
        _transfers.Remove(product);
        product.RefreshState();
        RefreshSummary();
        var task = transfer.Task;
        Notices.Add(new NoticeViewModel(NoticeKind.Warning, $"{product.Name} {title}：{Describe(ex, product)}",
            "重试", () => Enqueue(product, task)));
    }

    private static string Describe(Exception ex, ProductViewModel product) => ex switch
    {
        ApiException or InvalidDataException => ex.Message,
        UnauthorizedAccessException => $"没有写入权限（{ex.Message}）。可以在设置中更换安装目录或下载缓存目录。",
        IOException => $"文件被占用或无法写入，如果 {product.Name} 正在运行请先关闭。（{ex.Message}）",
        _ => ex.Message
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 删不掉缓存不影响安装 */ }
    }

    public void TogglePause(ProductViewModel product)
    {
        if (!_transfers.TryGetValue(product, out var transfer))
            return;
        switch (product.State)
        {
            case ProductState.Queued or ProductState.Downloading:
                product.State = ProductState.Paused;
                transfer.Cts?.Cancel();
                Log.Info($"暂停下载 {product.Id}");
                product.Speed = 0;
                product.TransferText = $"{Format.Size(product.DownloadedBytes)} / {Format.Size(product.TotalBytes)} · 已暂停";
                break;
            case ProductState.Paused:
                product.State = ProductState.Queued;
                product.TransferText = "等待其他下载完成";
                break;
        }
        Pump();
    }

    /// <summary>取消下载：删除已下载的部分。</summary>
    public void Cancel(ProductViewModel product)
    {
        if (!product.IsTransferring || !_transfers.Remove(product, out var transfer))
            return;
        transfer.Cts?.Cancel();
        Log.Info($"取消下载 {product.Id}");
        product.RefreshState();
        RemoveIfDelisted(product);
        _ = DeletePartialAsync(transfer);
        Pump();
    }

    private static async Task DeletePartialAsync(Transfer transfer)
    {
        try { if (transfer.Run is not null) await transfer.Run; }
        catch { /* 已取消 */ }
        TryDelete(PackageInstaller.PartialPath(transfer.ZipPath));
    }

    public async Task UninstallAsync(ProductViewModel product)
    {
        var record = LocalInstalls.Find(product.Id);
        product.State = ProductState.Installing;
        product.PhaseText = "正在卸载…";
        try
        {
            if (record is not null)
                await Task.Run(() => PackageInstaller.Uninstall(record));
            Log.Info($"已卸载 {product.Id}（{record?.Path}）");
            LocalInstalls.Remove(product.Id);
            product.InstalledVersion = null;
            product.InstallPath = DefaultInstallPath(product.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{product.Id} 卸载失败", ex);
            Notices.Add(new NoticeViewModel(NoticeKind.Warning,
                $"{product.Name} 卸载失败：{Describe(ex, product)}", "重试", () => _ = UninstallAsync(product)));
        }
        product.RefreshState();
        RemoveIfDelisted(product);
        RefreshSummary();
    }

    public void Launch(ProductViewModel product)
    {
        var exe = Path.Combine(product.InstallPath, product.Executable ?? "");
        if (!File.Exists(exe))
        {
            MessageRequested?.Invoke(this, ("打开", $"找不到 {exe}。可以在 ⋯ 菜单中“校验并修复”。"));
            return;
        }
        try
        {
            Log.Info($"启动 {exe}");
            Shell.Open(exe, product.InstallPath);
        }
        catch (Exception ex)
        {
            Log.Error($"无法启动 {exe}", ex);
            MessageRequested?.Invoke(this, ("打开", $"无法启动 {product.Name}：{ex.Message}"));
        }
    }

    private void RemoveIfDelisted(ProductViewModel product)
    {
        if (!product.IsDelisted || product.IsInstalled)
            return;
        Products.Remove(product);
        RebuildEntries();
    }

    private static string TaskTitle(ProductViewModel product) => product.CurrentTask switch
    {
        ProductTask.Update => $"更新 {product.InstalledVersion} → {product.LatestVersion}",
        ProductTask.Repair => $"重新下载并修复 {product.LatestVersion}",
        _ => $"安装 {product.LatestVersion}"
    };

    /// <summary>把下载线程的进度刷新到界面，计算速度与剩余时间。</summary>
    private void Tick()
    {
        foreach (var product in Products.Where(p => p.State == ProductState.Downloading))
        {
            if (!_transfers.TryGetValue(product, out var transfer))
                continue;
            var downloaded = transfer.Progress.Downloaded;
            var total = transfer.Progress.Total;
            if (total <= 0)
                continue;   // 还在连接
            if (transfer.LastBytes < 0)
            {
                // 刚开始：续传时已有的部分不计入速度
                transfer.LastBytes = downloaded;
                continue;
            }

            var instant = Math.Max(0, downloaded - transfer.LastBytes) / TickSeconds;
            transfer.LastBytes = downloaded;
            product.Speed = product.Speed <= 0 ? instant : product.Speed * 0.7 + instant * 0.3;
            product.DownloadedBytes = downloaded;
            product.TotalBytes = total;
            product.Progress = (double)downloaded / total * 100;
            var remaining = product.Speed > 1 ? Format.Remaining((total - downloaded) / product.Speed) : "正在计算剩余时间";
            product.TransferText = $"{Format.Size(downloaded)} / {Format.Size(total)} · {Format.Speed(product.Speed)} · {remaining}";
        }

        if (!Products.Any(p => p.State is ProductState.Queued or ProductState.Downloading))
            _timer.Stop();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(LibrarySummary));

        var downloading = Products.Where(p => p.State == ProductState.Downloading).ToList();
        _activeDownloads = downloading.Count;
        var waiting = Products.Count(p => p.State is ProductState.Queued or ProductState.Paused);
        var installing = Products.Count(p => p.State == ProductState.Installing);
        HasTransfers = downloading.Count + waiting + installing > 0;

        var parts = new List<string>();
        if (downloading.Count > 0)
        {
            parts.Add($"↓ {Format.Speed(downloading.Sum(p => p.Speed))}");
            parts.Add($"下载 {downloading.Count} 项");
        }
        if (waiting > 0)
            parts.Add($"等待 {waiting} 项");
        if (installing > 0)
            parts.Add($"安装 {installing} 项");
        TransferSummary = string.Join(" · ", parts);
    }
}
