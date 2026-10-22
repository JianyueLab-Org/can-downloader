using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Downloader.ViewModels;

/// <summary>
/// 主界面：软件库（由更新清单生成，按分类分组）、当前软件详情、下载队列。
/// 界面原型：下载、安装都是模拟的，由 <see cref="DispatcherTimer"/> 推进进度。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const double TickSeconds = 0.1;

    private readonly Random _random = new();
    private readonly DispatcherTimer _timer;
    private UpdateManifest? _manifest;
    private LibraryEntry? _selectedEntry;

    /// <param name="account">已登录的账号；为 null 时是离线模式。</param>
    public MainWindowViewModel(UpdateManifest? manifest, SettingsViewModel settings, UserAccount? account)
    {
        Settings = settings;
        Account = account;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(TickSeconds), DispatcherPriority.Background, (_, _) => Tick());

        if (account is null)
        {
            IsOffline = true;
            // 离线模式：使用上次缓存的清单（界面原型中就是同一个文件），不能下载
            if (manifest is null)
            {
                try { manifest = ManifestSource.Load(); } catch { /* 没有缓存时软件库为空 */ }
            }
            Notices.Add(new NoticeViewModel(NoticeKind.Warning, "离线模式：未登录，显示的是上次缓存的软件列表，不能下载或更新。",
                "登录", () => LoginRequested?.Invoke(this, EventArgs.Empty)));
        }

        if (manifest is not null)
            ApplyManifest(manifest, announce: false);

        SelectedEntry = Products.FirstOrDefault(p => p.HasUpdate) ?? Products.FirstOrDefault();
        LastCheckText = IsOffline ? "未登录" : $"已检查 {DateTime.Now:HH:mm}";
    }

    public SettingsViewModel Settings { get; }

    /// <summary>当前清单，退出登录时交给登录窗口。</summary>
    public UpdateManifest? Manifest => _manifest;

    public UserAccount? Account { get; }
    public string AccountText => Account is null ? "离线模式" : $"{Account.Name}（{Account.CanId}）";

    /// <summary>退出登录或离线模式下点“登录”：回到登录窗口。</summary>
    public event EventHandler? LoginRequested;

    /// <summary>退出登录：停止下载，回到登录窗口。</summary>
    public void Logout()
    {
        _timer.Stop();
        LoginRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>离线模式下检查更新、下载前需要先登录。</summary>
    [RelayCommand]
    private void RequestLogin() => LoginRequested?.Invoke(this, EventArgs.Empty);

    public string AppVersion => $"v{AppInfo.Version}";

    /// <summary>全部软件，按清单中的分类与顺序排列。</summary>
    public ObservableCollection<ProductViewModel> Products { get; } = [];

    /// <summary>左侧列表：分类标题 + 软件，按搜索文字过滤。</summary>
    public ObservableCollection<LibraryEntry> Entries { get; } = [];

    public ObservableCollection<NoticeViewModel> Notices { get; } = [];

    /// <summary>主界面“更新并重启”：关闭主窗口，重新进入启动检查并自动下载。</summary>
    public event EventHandler? RestartForUpdateRequested;

    /// <summary>需要主窗口显示的提示（界面原型中的打开软件等）。</summary>
    public event EventHandler<(string Title, string Message)>? MessageRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProducts), nameof(HasUpdates), nameof(IsOnline))]
    private bool _isOffline;

    public bool IsOnline => !IsOffline;

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
    /// <summary>离线时不能下载，不显示“全部更新”。</summary>
    public bool HasUpdates => UpdateCount > 0 && !IsOffline;
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
        ManifestInfo = $"清单发布于 {manifest.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {manifest.Server}";

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
                product = new ProductViewModel(item.Id)
                {
                    IsOffline = IsOffline,
                    InstalledVersion = LocalInstalls.FindVersion(item.Id),
                    InstallPath = Path.Combine(Settings.InstallRoot, item.Name)
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

        if (VersionUtil.IsNewer(manifest.Launcher.Version, AppInfo.Version))
        {
            Notices.Add(new NoticeViewModel(NoticeKind.Update,
                $"{AppInfo.Name} {manifest.Launcher.Version} 已发布（当前 {AppInfo.Version}）。",
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
            await Task.Delay(900);
            var manifest = await Task.Run(ManifestSource.Load);

            // 清除上一次检查留下的提示与“新”标记
            Notices.Clear();
            foreach (var product in Products)
                product.IsNew = false;

            ApplyManifest(manifest, announce: true);
            LastCheckText = $"已检查 {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            Notices.Add(new NoticeViewModel(NoticeKind.Warning, $"检查更新失败：{ex.Message}", "重试", () => _ = CheckUpdatesAsync()));
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanCheckUpdates() => !IsChecking && !IsOffline;

    /// <summary>演示：模拟服务器发布了新软件和新版本，然后检查更新。</summary>
    [RelayCommand]
    private Task SimulateReleaseAsync()
    {
        ManifestSource.IncludeDemoRelease = true;
        return CheckUpdatesAsync();
    }

    [RelayCommand]
    private void DismissNotice(NoticeViewModel notice) => Notices.Remove(notice);

    [RelayCommand]
    private void InvokeNotice(NoticeViewModel notice)
    {
        Notices.Remove(notice);
        notice.Invoke();
    }

    // ========== 下载队列 ==========

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

    public void Repair(ProductViewModel product) => Enqueue(product, ProductTask.Repair);

    private void Enqueue(ProductViewModel product, ProductTask task)
    {
        if (product.IsBusy || !product.CanDownload)
            return;

        product.CurrentTask = task;
        // 修复只重新下载校验失败的文件，演示中按 15% 计
        product.TotalBytes = task == ProductTask.Repair ? product.DownloadSize * 0.15 : product.DownloadSize;
        product.DownloadedBytes = 0;
        product.Speed = 0;
        product.Progress = 0;
        product.PhaseText = TaskTitle(product);
        product.TransferText = "等待其他下载完成";
        product.State = ProductState.Queued;

        RefreshSummary();
        _timer.Start();
    }

    public void TogglePause(ProductViewModel product)
    {
        switch (product.State)
        {
            case ProductState.Queued or ProductState.Downloading:
                product.State = ProductState.Paused;
                product.Speed = 0;
                product.TransferText = $"{Format.Size(product.DownloadedBytes)} / {Format.Size(product.TotalBytes)} · 已暂停";
                break;
            case ProductState.Paused:
                product.State = ProductState.Queued;
                product.TransferText = "等待其他下载完成";
                _timer.Start();
                break;
        }
        RefreshSummary();
    }

    public void Cancel(ProductViewModel product)
    {
        if (!product.IsTransferring)
            return;
        product.RefreshState();
        RemoveIfDelisted(product);
        RefreshSummary();
    }

    public async Task UninstallAsync(ProductViewModel product)
    {
        product.State = ProductState.Installing;
        product.PhaseText = "正在卸载…";
        await Task.Delay(1000);
        product.InstalledVersion = null;
        product.RefreshState();
        RemoveIfDelisted(product);
        RefreshSummary();
    }

    public void Launch(ProductViewModel product) =>
        MessageRequested?.Invoke(this, ("打开", $"界面原型：将启动 {Path.Combine(product.InstallPath, product.Executable ?? "")}。"));

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
        ProductTask.Repair => "校验并修复",
        _ => $"安装 {product.LatestVersion}"
    };

    /// <summary>推进模拟下载：按并发数启动排队的项目，按限速分配带宽。</summary>
    private void Tick()
    {
        var active = Products.Where(p => p.State == ProductState.Downloading).ToList();
        var maxConcurrent = Math.Max(1, (int)Settings.MaxConcurrentDownloads);
        foreach (var next in Products.Where(p => p.State == ProductState.Queued).ToList())
        {
            if (active.Count >= maxConcurrent)
                break;
            next.State = ProductState.Downloading;
            active.Add(next);
        }

        var limit = Settings.SpeedLimitBytes;
        foreach (var product in active)
        {
            var target = (6 + _random.NextDouble() * 8) * 1024 * 1024;
            product.Speed = product.Speed <= 0 ? target : product.Speed * 0.8 + target * 0.2;
            var speed = limit > 0 ? Math.Min(product.Speed, limit / active.Count) : product.Speed;

            product.DownloadedBytes = Math.Min(product.TotalBytes, product.DownloadedBytes + speed * TickSeconds);
            product.Progress = product.TotalBytes <= 0 ? 100 : product.DownloadedBytes / product.TotalBytes * 100;
            product.TransferText = $"{Format.Size(product.DownloadedBytes)} / {Format.Size(product.TotalBytes)} · {Format.Speed(speed)} · {Format.Remaining((product.TotalBytes - product.DownloadedBytes) / speed)}";

            if (product.DownloadedBytes >= product.TotalBytes)
                _ = FinishAsync(product);
        }

        if (!Products.Any(p => p.State is ProductState.Queued or ProductState.Downloading))
            _timer.Stop();
        RefreshSummary();
    }

    /// <summary>下载完成：校验、解压、安装。</summary>
    private async Task FinishAsync(ProductViewModel product)
    {
        product.State = ProductState.Installing;
        product.Progress = 100;
        product.TransferText = "";

        if (Settings.VerifyAfterDownload)
        {
            product.PhaseText = "正在校验文件…";
            await Task.Delay(700);
        }
        if (product.CurrentTask != ProductTask.Repair)
        {
            product.PhaseText = "正在解压…";
            await Task.Delay(700);
        }
        product.PhaseText = product.CurrentTask switch
        {
            ProductTask.Repair => "正在修复…",
            ProductTask.Update => "正在更新…",
            _ => "正在安装…"
        };
        await Task.Delay(700);

        product.InstalledVersion = product.LatestVersion;
        product.RefreshState();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(LibrarySummary));

        var downloading = Products.Where(p => p.State == ProductState.Downloading).ToList();
        var waiting = Products.Count(p => p.State is ProductState.Queued or ProductState.Paused);
        var installing = Products.Count(p => p.State == ProductState.Installing);
        HasTransfers = downloading.Count + waiting + installing > 0;

        var parts = new List<string>();
        if (downloading.Count > 0)
        {
            var limit = Settings.SpeedLimitBytes;
            var speed = downloading.Sum(p => limit > 0 ? Math.Min(p.Speed, limit / downloading.Count) : p.Speed);
            parts.Add($"↓ {Format.Speed(speed)}");
            parts.Add($"下载 {downloading.Count} 项");
        }
        if (waiting > 0)
            parts.Add($"等待 {waiting} 项");
        if (installing > 0)
            parts.Add($"安装 {installing} 项");
        TransferSummary = string.Join(" · ", parts);
    }
}

/// <summary>本机已安装的软件。界面原型：演示数据，实际应读取本机安装记录。</summary>
public static class LocalInstalls
{
    private static readonly Dictionary<string, string> Versions = new()
    {
        ["pilot-client"] = "1.3.2",
        ["controller-client"] = "2.0.1",
        ["msfs-plugin"] = "1.2.3",
        ["navdata"] = "2610",
        ["sector-files"] = "2026.10"
    };

    public static string? FindVersion(string id) => Versions.GetValueOrDefault(id);
}
