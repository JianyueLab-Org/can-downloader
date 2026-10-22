using System.Text;

namespace Downloader.Services;

/// <summary>
/// 日志：写到程序目录下的 logs，每天一个文件（logs/2026-10-23.log），文件名按日期排序。
/// 不记录密码、验证码和会话内容。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Directory => AppPaths.Logs;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId,3}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, $"{now:yyyy-MM-dd}.log"), line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不了日志不影响使用
            }
        }
    }
}
