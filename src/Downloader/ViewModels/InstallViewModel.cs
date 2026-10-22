using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Downloader.Services;

namespace Downloader.ViewModels;

/// <summary>
/// 安装对话框：安装位置、组件（来自清单）、依赖项、磁盘空间。编辑组件副本，确认后写回。
/// 设置里关闭“安装时询问安装位置”时不显示位置，装到默认安装目录。
/// </summary>
public sealed partial class InstallViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ProductViewModel> _dependencies;

    public InstallViewModel(ProductViewModel product, IReadOnlyList<ProductViewModel> dependencies, string installPath, bool showLocation)
    {
        Product = product;
        _dependencies = dependencies;
        _installPath = installPath;
        DefaultInstallPath = installPath;
        ShowLocation = showLocation;
        Components = product.Components.Select(c => c.Clone()).ToList();
        foreach (var component in Components)
            component.PropertyChanged += OnComponentChanged;
    }

    public ProductViewModel Product { get; }

    public IReadOnlyList<ComponentViewModel> Components { get; }

    public bool ShowLocation { get; }
    public string DefaultInstallPath { get; }

    /// <summary>有需要用户选择的内容。没有时（不询问位置、没有可选组件和依赖）直接安装，不弹对话框。</summary>
    public bool HasChoices => ShowLocation || HasDependencies || Components.Any(c => c.IsOptional);

    /// <summary>确认后的安装目录：空白时用默认目录，相对路径按程序目录解析。</summary>
    public string ResolvedInstallPath => string.IsNullOrWhiteSpace(InstallPath) ? DefaultInstallPath : AppPaths.Resolve(InstallPath);

    /// <summary>“浏览…”选了一个目录：装到其中以软件名命名的子目录（已经是这个名字时直接用）。</summary>
    public void SetLocationFromFolder(string folder) =>
        InstallPath = string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), Product.Id, StringComparison.OrdinalIgnoreCase)
            ? folder
            : Path.Combine(folder, Product.Id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableText))]
    private string _installPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredText))]
    private bool _installDependencies = true;

    public bool HasDependencies => _dependencies.Count > 0;
    public string DependencyText => $"需要先安装：{string.Join("、", _dependencies.Select(d => d.Name))}";

    private long RequiredBytes =>
        Components.Where(c => c.IsSelected).Sum(c => c.Size)
        + (InstallDependencies ? _dependencies.Sum(d => d.DownloadSize) : 0);

    public string RequiredText => $"需要 {Format.Size(RequiredBytes)}";

    /// <summary>安装位置所在磁盘的剩余空间。</summary>
    public string AvailableText
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(ResolvedInstallPath);
                return string.IsNullOrEmpty(root) ? "" : $"可用 {Format.Size(new DriveInfo(root).AvailableFreeSpace)}";
            }
            catch
            {
                return "无法读取可用空间";
            }
        }
    }

    private void OnComponentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComponentViewModel.IsSelected))
            OnPropertyChanged(nameof(RequiredText));
    }
}
