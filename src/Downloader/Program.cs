using Avalonia;
using Avalonia.Media;
using Downloader.Services;
using Downloader.ViewModels;

namespace Downloader;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Log.Info($"启动 {AppInfo.Name} {AppInfo.Version}，{PlatformInfo.Name}");
        if (CanApi.BaseUri.Host != CanApi.ApiHost)
            Log.Warn($"使用环境变量 CAN_API_URL 指定的 API：{CanApi.BaseUri}");
        // 自更新的第二步：本次启动只为把新版本复制到原位置，不显示界面
        if (SelfUpdater.TryApplyFromArgs(args))
            return;
        // 同一个数据目录只运行一个下载器，否则两个进程会互相覆盖安装记录
        if (!SingleInstance.TryAcquire())
        {
            Log.Info("已有下载器在运行，通知它显示窗口后退出");
            SingleInstance.SignalExisting();
            return;
        }
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
            .With(new X11PlatformOptions { WmClass = "can-downloader", EnableSessionManagement = false })
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    "Microsoft YaHei UI", "PingFang SC", "Hiragino Sans GB", "Noto Sans CJK SC",
                    "Noto Sans SC", "Source Han Sans SC", "WenQuanYi Micro Hei", "Segoe UI Symbol",
                    "Apple Symbols", "DejaVu Sans", "Noto Sans Symbols 2", "Noto Sans Symbols"
                }.Select(name => new FontFallback { FontFamily = new FontFamily(name) }).ToArray()
            })
            .LogToTrace();
}
