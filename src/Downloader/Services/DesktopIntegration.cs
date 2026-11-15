using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using Downloader.ViewModels;

namespace Downloader.Services;

/// <summary>
/// 同一个数据目录只运行一个下载器：两个进程各自缓存并整份写回安装记录，会互相覆盖对方的记录。
/// 后启动的进程通知已在运行的那个显示窗口，然后退出。
/// 锁是 data 目录里的 instance.lock，以独占方式打开（Windows 是共享模式，macOS / Linux 是 flock），进程退出后自动释放。
/// </summary>
public static class SingleInstance
{
    private const string LockName = "instance.lock";
    private const string ActivateName = "activate.request";

    private static FileStream? _lock;
    private static FileSystemWatcher? _watcher;

    /// <summary>取得锁时返回 true；已有下载器在运行时返回 false。</summary>
    public static bool TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            _lock = new FileStream(Path.Combine(AppPaths.Data, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            // 数据目录不可写时之后也存不了任何东西，不因为锁拦住启动
            Log.Warn($"无法创建单实例锁：{ex.Message}");
            return true;
        }
    }

    /// <summary>请已在运行的下载器显示窗口。</summary>
    public static void SignalExisting()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Data, ActivateName), Environment.ProcessId.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"无法通知已在运行的下载器：{ex.Message}");
        }
    }

    /// <summary>其他进程请求显示窗口时调用 <paramref name="activate"/>（在文件监视线程上，调用方自行切回界面线程）。</summary>
    public static void ListenForActivation(Action activate)
    {
        try
        {
            _watcher = new FileSystemWatcher(AppPaths.Data, ActivateName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler handler = (_, _) => activate();
            _watcher.Created += handler;
            _watcher.Changed += handler;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log.Warn($"无法监视激活请求：{ex.Message}");
        }
    }
}

/// <summary>
/// 开机启动：Windows 写当前用户的 Run 注册表项，macOS 写 ~/Library/LaunchAgents 里的 plist，
/// Linux 写 $XDG_CONFIG_HOME/autostart（默认 ~/.config/autostart）里的 .desktop。只为当前用户设置，不需要管理员权限。
/// </summary>
public static class Autostart
{
    private const string WindowsValueName = "CAN Downloader";
    private const string MacLabel = "net.ceruleanavi.can-downloader";
    private const string LinuxFileName = "can-downloader.desktop";

    /// <summary>按设置开启或关闭。程序位置变了（便携目录搬走）时重新写入新位置。失败只记日志。</summary>
    public static void Apply(bool enabled)
    {
        var target = PlatformInfo.MacAppBundle ?? Environment.ProcessPath;
        if (target is null)
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                var value = $"\"{target}\"";
                if (!enabled)
                    key.DeleteValue(WindowsValueName, throwOnMissingValue: false);
                else if (key.GetValue(WindowsValueName) as string != value)
                    key.SetValue(WindowsValueName, value);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", MacLabel + ".plist");
                // .app 包用 open 启动，和在访达里双击一样
                string[] arguments = target.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? ["/usr/bin/open", target] : [target];
                var plist = new StringBuilder()
                    .AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""")
                    .AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""")
                    .AppendLine("""<plist version="1.0"><dict>""")
                    .AppendLine($"<key>Label</key><string>{MacLabel}</string>")
                    .AppendLine("<key>ProgramArguments</key><array>")
                    .AppendJoin("", arguments.Select(a => $"<string>{SecurityElement.Escape(a)}</string>\n"))
                    .AppendLine("</array>")
                    .AppendLine("<key>RunAtLoad</key><true/>")
                    .AppendLine("</dict></plist>")
                    .ToString();
                Write(path, enabled ? plist : null);
            }
            else
            {
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                var desktop = $"""
                    [Desktop Entry]
                    Type=Application
                    Name={AppInfo.Name}
                    Exec={DesktopExecQuote(target)}
                    Terminal=false
                    X-GNOME-Autostart-enabled=true

                    """;
                Write(Path.Combine(config, "autostart", LinuxFileName), enabled ? desktop.Replace("\r\n", "\n") : null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warn($"设置开机启动失败：{ex.Message}");
        }
    }

    /// <summary>写入（内容没变时不动）或删除（<paramref name="content"/> 为 null）。</summary>
    private static void Write(string path, string? content)
    {
        if (content is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>.desktop 的 Exec 参数：放在双引号里，转义 " ` $ \（规范要求），% 写成 %%。</summary>
    private static string DesktopExecQuote(string path)
    {
        var escaped = new StringBuilder();
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return "\"" + escaped.ToString().Replace("%", "%%") + "\"";
    }
}

/// <summary>
/// 系统通知（下载与安装完成）。Windows 用系统自带的 PowerShell 弹出 Toast 通知，macOS 用 osascript，Linux 用 notify-send。
/// 显示失败（例如 Linux 没装 notify-send）只记日志。文字通过环境变量或参数传入，不拼进命令。
/// </summary>
public static class SystemNotification
{
    // Windows PowerShell 自带、已注册的 AppUserModelID，未打包的程序借用它显示 Toast
    private const string WindowsPowerShellAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    private const string WindowsScript = """
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
        $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $text = $xml.GetElementsByTagName('text')
        $text.Item(0).AppendChild($xml.CreateTextNode($env:CAN_NOTIFY_TITLE)) > $null
        $text.Item(1).AppendChild($xml.CreateTextNode($env:CAN_NOTIFY_MESSAGE)) > $null
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:CAN_NOTIFY_APP).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
        """;

    public static void Show(string title, string message)
    {
        try
        {
            ProcessStartInfo info;
            if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-ExecutionPolicy");
                info.ArgumentList.Add("Bypass");
                info.ArgumentList.Add("-EncodedCommand");
                info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsScript)));
                info.Environment["CAN_NOTIFY_TITLE"] = title;
                info.Environment["CAN_NOTIFY_MESSAGE"] = message;
                info.Environment["CAN_NOTIFY_APP"] = WindowsPowerShellAppId;
            }
            else if (OperatingSystem.IsMacOS())
            {
                // 文字作为 AppleScript 的运行参数传入
                info = new ProcessStartInfo("/usr/bin/osascript");
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("on run argv");
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("display notification (item 2 of argv) with title (item 1 of argv)");
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("end run");
                info.ArgumentList.Add(title);
                info.ArgumentList.Add(message);
            }
            else
            {
                info = new ProcessStartInfo("notify-send");
                info.ArgumentList.Add("--app-name=" + AppInfo.Name);
                info.ArgumentList.Add("--");
                info.ArgumentList.Add(title);
                info.ArgumentList.Add(message);
            }
            info.UseShellExecute = false;
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"无法显示系统通知：{ex.Message}");
        }
    }
}
