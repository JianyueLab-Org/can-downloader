using Avalonia;
using Downloader.Services;
using Downloader.ViewModels;

namespace Downloader;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Log.Info($"启动 {AppInfo.Name} {AppInfo.Version}，程序目录 {AppPaths.Base}，参数 [{string.Join(' ', args)}]");
        if (CanApi.BaseUri.Host != CanApi.ApiHost)
            Log.Warn($"使用环境变量 CAN_API_URL 指定的 API：{CanApi.BaseUri}");
        // 自更新的第二步：本次启动只为把新版本复制到原位置，不显示界面
        if (SelfUpdater.TryApplyFromArgs(args))
            return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("未处理的异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("未观察到的任务异常", e.Exception);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.Info("退出");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
