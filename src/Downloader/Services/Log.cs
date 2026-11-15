using System.Text;
using System.Text.RegularExpressions;

namespace Downloader.Services;

/// <summary>
/// 日志：写到程序目录下的 logs，每天一个文件（logs/2026-10-23.log），文件名按日期排序。
/// 不记录密码、验证码和会话内容。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static DateOnly _lastCleanup;

    private static string Redact(string message)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) message = message.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        message = Regex.Replace(message, @"https?://[^\s\""<>]+", match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/[已省略路径]"
                : "[已省略地址]");
        message = Regex.Replace(message, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[已隐藏邮箱]");
        message = Regex.Replace(message, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[已隐藏 IP]");
        message = Regex.Replace(message, @"\b(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:]{0,4}\b", "[已隐藏 IP]");
        return message;
    }

    public static string Directory => AppPaths.Logs;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId,3}] {Redact(message)}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
                else System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var today = DateOnly.FromDateTime(now);
                if (_lastCleanup != today)
                {
                    foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.log"))
                        if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var date)
                            && date < today.AddDays(-30)) File.Delete(file);
                    _lastCleanup = today;
                }
                var path = Path.Combine(Directory, $"{now:yyyy-MM-dd}.log");
                if (!OperatingSystem.IsWindows())
                {
                    using var file = new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.Append, Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    });
                    using var writer = new StreamWriter(file, Encoding.UTF8);
                    writer.Write(line);
                    return;
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不了日志不影响使用
            }
        }
    }
}
