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
