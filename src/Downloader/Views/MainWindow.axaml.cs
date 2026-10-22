using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Downloader.Services;
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
        var install = new InstallViewModel(product, Vm.MissingDependencies(product), Vm.DefaultInstallPath(product.Id), Vm.Settings.AskInstallLocation);
        if (!install.HasChoices)
        {
            Vm.Install(product, install.Components, install.DefaultInstallPath, withDependencies: true);
            return;
        }

        var dialog = new DialogView($"安装 {product.Name}", new InstallView { DataContext = install }, "安装");
        if (await ShowDialogAsync(dialog) == DialogResult.Primary)
            Vm.Install(product, install.Components, install.ResolvedInstallPath, install.InstallDependencies);
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

    public Task OpenFolderAsync(ProductViewModel product) => OpenFolderAsync("安装目录", product.InstallPath);

    /// <summary>在文件管理器中打开目录；目录不存在时提示。</summary>
    private async Task OpenFolderAsync(string title, string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                await ShowMessageAsync(title, $"{path} 还不存在，安装或下载后才会创建。");
                return;
            }
            Shell.Open(path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(title, $"无法打开 {path}：{ex.Message}");
        }
    }

    // ========== 工具栏 ==========

    // 无系统标题栏：在工具栏空白处按下左键即可拖动窗口
    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        await ShowChildWindowAsync(new SettingsWindow { DataContext = Vm.Settings });
        Vm.RefreshDefaultPaths();
    }

    private async void OnInstallFolderClick(object? sender, RoutedEventArgs e) =>
        await OpenFolderAsync("安装目录", Vm.Settings.InstallRootPath);

    private async void OnCacheFolderClick(object? sender, RoutedEventArgs e) =>
        await OpenFolderAsync("下载缓存目录", Vm.Settings.CacheFolderPath);

    private async void OnLogFolderClick(object? sender, RoutedEventArgs e) =>
        await OpenFolderAsync("日志目录", Log.Directory);

    private void OnWebsiteClick(object? sender, RoutedEventArgs e) => Shell.Open(CanApi.WebsiteUri.ToString());

    private async void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Account is { } account)
            await ShowMessageAsync("账号信息", $"姓名：{account.Name}\nCAN ID：{account.CanId}\n邮箱：{account.Email ?? "—"}\n等级：{account.RatingText}");
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        var body = new TextBlock { Text = "退出登录后回到登录窗口，正在进行的下载会暂停（已下载的部分保留）。", TextWrapping = TextWrapping.Wrap, MaxWidth = 360, LineHeight = 20 };
        if (await ShowDialogAsync(new DialogView("退出登录", body, "退出登录")) == DialogResult.Primary)
            await Vm.LogoutAsync();
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e) =>
        await ShowMessageAsync("关于", $"{AppInfo.Name} {AppInfo.Version}\n安装、更新 CAN 的各个客户端。软件列表与安装包来自 {CanApi.ApiHost}，API 新发布的软件会自动出现在软件库中。\n基于 Avalonia 构建。");
}
