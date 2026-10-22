using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Downloader.ViewModels;

/// <summary>安装对话框：安装位置、组件（来自清单）、依赖项、磁盘空间。编辑组件副本，确认后写回。</summary>
public sealed partial class InstallViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ProductViewModel> _dependencies;

    public InstallViewModel(ProductViewModel product, IReadOnlyList<ProductViewModel> dependencies)
    {
        Product = product;
        _dependencies = dependencies;
        _installPath = product.InstallPath;
        Components = product.Components.Select(c => c.Clone()).ToList();
        foreach (var component in Components)
            component.PropertyChanged += OnComponentChanged;
    }

    public ProductViewModel Product { get; }

    public IReadOnlyList<ComponentViewModel> Components { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableText))]
    private string _installPath;

    [ObservableProperty] private bool _createShortcut = true;

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
                var root = Path.GetPathRoot(InstallPath);
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
