using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Downloader.ViewModels;

namespace Downloader.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
            vm.MessageRequested += async (_, m) => await ShowMessageAsync(m.Title, m.Message);
    }

    /// <summary>在窗口内的遮罩层上显示模态对话框，返回用户点击的按钮。</summary>
    public async Task<DialogResult> ShowDialogAsync(DialogView dialog)
    {
        DialogLayer.Children.Add(dialog);
        DialogLayer.IsVisible = true;
        dialog.Focus();
        try
        {
            return await dialog.Result;
        }
        finally
        {
            DialogLayer.Children.Remove(dialog);
            DialogLayer.IsVisible = DialogLayer.Children.Count > 0;
        }
    }

    /// <summary>显示只有“确定”按钮的提示。</summary>
    public Task ShowMessageAsync(string title, string message) =>
        ShowDialogAsync(new DialogView(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, LineHeight = 20 }, null, closeText: "确定"));

    /// <summary>模态子窗口打开期间，主窗口蒙上遮罩，明确区分当前操作的窗口。</summary>
    private async Task ShowChildWindowAsync(Window window)
    {
        DialogLayer.IsVisible = true;
        try
        {
            await window.ShowDialog(this);
        }
        finally
        {
            DialogLayer.IsVisible = DialogLayer.Children.Count > 0;
        }
    }

    // ========== 软件操作（由 ProductView 调用） ==========

    /// <summary>主按钮：安装（先选组件与位置）/ 更新 / 打开 / 暂停 / 继续。</summary>
    public async Task RunPrimaryActionAsync(ProductViewModel product)
    {
        switch (product.State)
        {
            case ProductState.NotInstalled:
                await InstallAsync(product);
                break;
            case ProductState.UpdateAvailable:
                Vm.Update(product);
                break;
            case ProductState.Queued or ProductState.Downloading or ProductState.Paused:
                Vm.TogglePause(product);
                break;
            case ProductState.Installed:
                Vm.Launch(product);
                break;
        }
    }

    private async Task InstallAsync(ProductViewModel product)
    {
        var install = new InstallViewModel(product, Vm.MissingDependencies(product));
        var dialog = new DialogView($"安装 {product.Name}", new InstallView { DataContext = install }, "安装");
        if (await ShowDialogAsync(dialog) == DialogResult.Primary)
            Vm.Install(product, install.Components, install.InstallPath, install.InstallDependencies);
    }

    public async Task UninstallAsync(ProductViewModel product)
    {
        var body = new TextBlock
        {
            Text = $"将从 {product.InstallPath} 删除 {product.Name} {product.InstalledVersion}。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            LineHeight = 20
        };
        var dialog = new DialogView("卸载", body, "卸载");
        if (await ShowDialogAsync(dialog) == DialogResult.Primary)
            await Vm.UninstallAsync(product);
    }

    public void Cancel(ProductViewModel product) => Vm.Cancel(product);

    public void Repair(ProductViewModel product) => Vm.Repair(product);

    public Task OpenFolderAsync(ProductViewModel product) =>
        ShowMessageAsync("打开安装目录", $"界面原型：将在文件管理器中打开 {product.InstallPath}。");

    // ========== 工具栏 ==========

    // 无系统标题栏：在工具栏空白处按下左键即可拖动窗口
    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        await ShowChildWindowAsync(new SettingsWindow { DataContext = Vm.Settings });

    private async void OnInstallFolderClick(object? sender, RoutedEventArgs e) =>
        await ShowMessageAsync("安装目录", $"界面原型：将在文件管理器中打开 {Vm.Settings.InstallRoot}。");

    private async void OnCacheFolderClick(object? sender, RoutedEventArgs e) =>
        await ShowMessageAsync("下载缓存目录", $"界面原型：将在文件管理器中打开 {Vm.Settings.CacheFolder}。");

    private async void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Account is { } account)
            await ShowMessageAsync("账号信息", $"姓名：{account.Name}\nCAN ID：{account.CanId}\n网络等级：{account.Rating}");
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        var body = new TextBlock { Text = "退出登录后回到登录窗口，正在进行的下载会被取消。", TextWrapping = TextWrapping.Wrap, MaxWidth = 360, LineHeight = 20 };
        if (await ShowDialogAsync(new DialogView("退出登录", body, "退出登录")) == DialogResult.Primary)
            Vm.Logout();
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e) =>
        await ShowMessageAsync("关于", $"{AppInfo.Name} {AppInfo.Version}（界面原型）\n基于 Avalonia 构建。软件列表由更新清单生成，清单新增的软件会自动出现在软件库中。");
}
