using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Downloader.ViewModels;

/// <summary>左侧软件库的一行：分类标题或软件。</summary>
public abstract class LibraryEntry : ViewModelBase
{
    /// <summary>分类标题不可选中。</summary>
    public abstract bool IsSelectable { get; }
}

public sealed class CategoryHeaderEntry(string name, int count) : LibraryEntry
{
    public string Name { get; } = name;
    public string CountText { get; } = count.ToString();
    public override bool IsSelectable => false;
}

public enum ProductState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Queued,
    Downloading,
    Paused,
    /// <summary>校验、解压、安装或卸载中，不能暂停或取消。</summary>
    Installing
}

public enum ProductTask
{
    Install,
    Update,
    Repair
}

/// <summary>
/// 软件库中的一个软件。清单信息（名称、最新版本、组件、更新日志）由 <see cref="Apply"/> 从更新清单写入，
/// 本机信息（已安装版本、安装位置）与下载进度保存在这里。
/// </summary>
public sealed partial class ProductViewModel : LibraryEntry
{
    public ProductViewModel(string id) => Id = id;

    public override bool IsSelectable => true;

    public string Id { get; }

    // —— 清单信息 ——
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _categoryName = "";
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private IBrush _tileForeground = Brushes.Gray;
    [ObservableProperty] private IBrush _tileBackground = Brushes.Transparent;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _releaseDate = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(IsPrimaryEnabled))]
    private string? _executable;
    [ObservableProperty] private string _requiresText = "";

    /// <summary>下载地址：本站中转与 GitHub 原始地址，按设置里的下载源选择。</summary>
    public string? DownloadUrl { get; private set; }
    public string? OriginUrl { get; private set; }
    /// <summary>安装包的 SHA-256，有的话下载后核对。</summary>
    public string? Sha256 { get; private set; }

    [ObservableProperty] private bool _isPrerelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotesUrl))]
    private string? _notesUrl;

    public bool HasNotesUrl => NotesUrl is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(StatusText), nameof(VersionText))]
    private string _latestVersion = "";

    public string CategoryId { get; private set; } = "";
    public IReadOnlyList<string> Requires { get; private set; } = [];
    public ObservableCollection<ComponentViewModel> Components { get; } = [];
    public ObservableCollection<ChangelogViewModel> Changelog { get; } = [];

    /// <summary>选中组件的下载大小。</summary>
    public long DownloadSize => Components.Where(c => c.IsSelected).Sum(c => c.Size);
    public string DownloadSizeText => Format.Size(DownloadSize);

    /// <summary>本次检查更新时新出现在清单里的软件，显示“新”标记。</summary>
    [ObservableProperty] private bool _isNew;

    /// <summary>已从清单中移除，但本机仍安装着。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(HasUpdate), nameof(StatusText))]
    private bool _isDelisted;

    // —— 本机信息 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(HasUpdate), nameof(StatusText), nameof(VersionText), nameof(InstalledVersionText))]
    private string? _installedVersion;

    [ObservableProperty] private string _installPath = "";

    public bool IsInstalled => InstalledVersion is not null;
    public bool HasUpdate => IsInstalled && !IsDelisted && VersionUtil.IsNewer(LatestVersion, InstalledVersion);
    public string InstalledVersionText => InstalledVersion ?? "未安装";

    /// <summary>离线模式下不能下载。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isOffline;

    public bool CanDownload => !IsOffline && !IsDelisted && DownloadUrl is not null;

    // —— 状态 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsTransferring), nameof(IsPaused), nameof(IsInstallingState),
        nameof(StatusText), nameof(IsInstalledState), nameof(IsUpdateState), nameof(PrimaryText), nameof(IsPrimaryAccent), nameof(IsPrimaryEnabled),
        nameof(HasMenu), nameof(CanCancel))]
    private ProductState _state;

    public ProductTask CurrentTask { get; set; }

    public bool IsBusy => State is ProductState.Queued or ProductState.Downloading or ProductState.Paused or ProductState.Installing;
    public bool IsTransferring => State is ProductState.Queued or ProductState.Downloading or ProductState.Paused;
    public bool IsPaused => State == ProductState.Paused;
    public bool IsInstallingState => State == ProductState.Installing;
    public bool CanCancel => IsTransferring;

    /// <summary>已安装、空闲时才显示 ⋯（打开目录、修复、卸载）。</summary>
    public bool HasMenu => IsInstalled && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progress;

    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _transferText = "";

    public double DownloadedBytes { get; set; }
    public double TotalBytes { get; set; }
    public double Speed { get; set; }

    /// <summary>软件库列表中名称下方的一行状态。</summary>
    public string StatusText => State switch
    {
        ProductState.Queued => "等待下载",
        ProductState.Downloading => $"下载中 {Progress:0}%",
        ProductState.Paused => $"已暂停 {Progress:0}%",
        ProductState.Installing => PhaseText,
        ProductState.UpdateAvailable => $"{InstalledVersion} → {LatestVersion}",
        ProductState.Installed when IsDelisted => $"{InstalledVersion} · 已下架",
        ProductState.Installed => InstalledVersion!,
        _ => "未安装"
    };

    /// <summary>状态文字颜色：已安装为成功色，可更新为警示色，进行中为强调色。</summary>
    public bool IsInstalledState => State == ProductState.Installed;
    public bool IsUpdateState => State == ProductState.UpdateAvailable;

    /// <summary>详情页标题旁的版本说明。</summary>
    public string VersionText => HasUpdate ? $"{InstalledVersion} → {LatestVersion}" : InstalledVersion ?? LatestVersion;

    /// <summary>详情页主按钮：安装 / 更新 / 打开 / 暂停 / 继续。</summary>
    public string PrimaryText => State switch
    {
        ProductState.NotInstalled => "安装",
        ProductState.UpdateAvailable => "更新",
        ProductState.Queued or ProductState.Downloading => "暂停",
        ProductState.Paused => "继续",
        ProductState.Installing => "请稍候",
        _ => Executable is null ? "已安装" : "打开"
    };

    public bool IsPrimaryAccent => State is ProductState.NotInstalled or ProductState.UpdateAvailable;

    public bool IsPrimaryEnabled => State switch
    {
        ProductState.NotInstalled or ProductState.UpdateAvailable => CanDownload,
        ProductState.Installing => false,
        ProductState.Installed => Executable is not null,
        _ => true
    };

    partial void OnIsOfflineChanged(bool value) => OnPropertyChanged(nameof(IsPrimaryEnabled));

    partial void OnIsDelistedChanged(bool value) => OnPropertyChanged(nameof(IsPrimaryEnabled));

    partial void OnPhaseTextChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnInstalledVersionChanged(string? value) => RefreshChangelogMarks();

    /// <summary>空闲时根据已安装版本与最新版本重新计算状态。</summary>
    public void RefreshState()
    {
        State = !IsInstalled ? ProductState.NotInstalled
            : HasUpdate ? ProductState.UpdateAvailable
            : ProductState.Installed;
        Progress = 0;
        PhaseText = "";
        TransferText = "";
    }

    /// <summary>写入清单信息。组件勾选状态：已安装的保持原样，新组件按清单默认值。</summary>
    public void Apply(ManifestProduct product, string categoryName, IReadOnlyDictionary<string, string> productNames)
    {
        Name = product.Name;
        CategoryId = product.Category;
        CategoryName = categoryName;
        Icon = string.IsNullOrWhiteSpace(product.Icon) ? product.Name[..1] : product.Icon;
        Summary = product.Summary;
        Description = product.Description;
        LatestVersion = product.Version;
        ReleaseDate = product.ReleaseDate;
        Executable = product.Executable;
        DownloadUrl = product.DownloadUrl;
        OriginUrl = product.OriginUrl;
        Sha256 = product.Sha256;
        IsPrerelease = product.Prerelease;
        NotesUrl = product.NotesUrl;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(IsPrimaryEnabled));
        Requires = product.Requires;
        RequiresText = product.Requires.Count == 0
            ? "无"
            : string.Join("、", product.Requires.Select(id => productNames.GetValueOrDefault(id, id)));
        IsDelisted = false;

        var color = Color.TryParse(product.Color, out var c) ? c : Color.Parse("#8B9098");
        TileForeground = new SolidColorBrush(color);
        TileBackground = new SolidColorBrush(Color.FromArgb(0x29, color.R, color.G, color.B));

        var selected = Components.ToDictionary(x => x.Id, x => x.IsSelected);
        Components.Clear();
        foreach (var component in product.Components)
        {
            Components.Add(new ComponentViewModel(component)
            {
                IsSelected = component.Required || selected.GetValueOrDefault(component.Id, component.Default)
            });
        }

        Changelog.Clear();
        foreach (var entry in product.Changelog)
            Changelog.Add(new ChangelogViewModel(entry));
        RefreshChangelogMarks();

        OnPropertyChanged(nameof(DownloadSize));
        OnPropertyChanged(nameof(DownloadSizeText));
        if (!IsBusy)
            RefreshState();
    }

    public void NotifyComponentsChanged()
    {
        OnPropertyChanged(nameof(DownloadSize));
        OnPropertyChanged(nameof(DownloadSizeText));
    }

    private void RefreshChangelogMarks()
    {
        foreach (var entry in Changelog)
        {
            entry.IsCurrent = InstalledVersion is not null && VersionUtil.Compare(entry.Version, InstalledVersion) == 0;
            entry.IsNew = InstalledVersion is not null && VersionUtil.IsNewer(entry.Version, InstalledVersion);
        }
    }
}

public sealed partial class ComponentViewModel(ManifestComponent component) : ViewModelBase
{
    public string Id { get; } = component.Id;
    public string Name { get; } = component.Name;
    public long Size { get; } = component.Size;
    public string SizeText { get; } = Format.Size(component.Size);
    public bool IsRequired { get; } = component.Required;
    public bool IsOptional => !IsRequired;

    [ObservableProperty] private bool _isSelected;

    public ComponentViewModel Clone() => new(new ManifestComponent { Id = Id, Name = Name, Size = Size, Required = IsRequired })
    {
        IsSelected = IsSelected
    };
}

public sealed partial class ChangelogViewModel(ManifestChangelog entry) : ViewModelBase
{
    public string Version { get; } = entry.Version;
    public string Date { get; } = entry.Date;
    public IReadOnlyList<string> Changes { get; } = entry.Changes;

    /// <summary>本机已安装的版本。</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>比已安装版本新。</summary>
    [ObservableProperty] private bool _isNew;
}

public enum NoticeKind
{
    Info,
    Update,
    Warning
}

/// <summary>工具栏下方的提示条：清单新增软件、有可用更新、下载器新版本、离线模式等。</summary>
public sealed class NoticeViewModel(NoticeKind kind, string text, string? actionText = null, Action? action = null)
{
    public NoticeKind Kind { get; } = kind;
    public string Text { get; } = text;
    public string? ActionText { get; } = actionText;
    public bool HasAction => ActionText is not null;
    public bool IsUpdate => Kind == NoticeKind.Update;
    public bool IsWarning => Kind == NoticeKind.Warning;

    public void Invoke() => action?.Invoke();
}
