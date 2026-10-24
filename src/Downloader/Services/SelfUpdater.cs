using System.Diagnostics;

namespace Downloader.Services;

/// <summary>
/// 下载器替换自己，分两步，正在运行的程序文件始终不动：
/// <list type="number">
/// <item>旧版本把更新包里的新版本解压为 <c>&lt;原名&gt;.new</c>，以 <c>--apply-update &lt;进程号&gt;</c> 启动它，然后正常退出。</item>
/// <item>新版本（从 .new 启动，进入界面之前）等旧进程退出，把自己复制到原位置，启动它后退出。</item>
/// </list>
/// 最后启动的新版本删除留下的 .new。
/// 替换的对象：Windows 是 Downloader.exe，Linux 是 Downloader，macOS 是整个 CAN Downloader.app 包目录。
/// 不能在运行中把程序文件（或所在的 .app 包）改名后继续运行：单文件发布的程序按需从文件读取程序集，改名后再加载新的程序集会失败。
/// </summary>
public static class SelfUpdater
{
    private const string ApplyArg = "--apply-update";
    public const string NewSuffix = ".new";
    private const string OldSuffix = ".old";

    /// <summary>单文件程序名（Windows / Linux 的更新包里找这个文件）。</summary>
    private static string ExeName => OperatingSystem.IsWindows() ? "Downloader.exe" : "Downloader";

    /// <summary>.app 包里的入口（Info.plist 的 CFBundleExecutable），见 packaging/macos/make-app.sh。</summary>
    private const string BundleExecutable = "Downloader";

    /// <summary>不能自更新的原因；可以时为 null。只有发布出来的单文件程序（macOS 是 .app 包）能替换自己。</summary>
    public static string? CannotReplaceReason
    {
        get
        {
            if (Current() is not { } current)
                return "找不到下载器程序文件。";
            // 编译输出目录（dotnet run）里程序只是启动器，程序在旁边的 Downloader.dll
            if (File.Exists(Path.Combine(AppPaths.Program, "Downloader.dll")))
                return "开发版（编译输出目录）不能自动更新，请使用发布的版本。";
            if (current.Contains("/AppTranslocation/", StringComparison.Ordinal))
                return "请先把 CAN Downloader 拖进“应用程序”文件夹再打开，才能自动更新。";
            var folder = Path.GetDirectoryName(current)!;
            if (!AppPaths.IsWritable(folder))
                return OperatingSystem.IsLinux()
                    ? "下载器由系统包管理器安装（如 pacman），请用包管理器更新。"
                    : $"没有权限写入 {folder}，请手动下载新版本。";
            return null;
        }
    }

    /// <summary>第一步：把更新包（zip / tar.gz）里的新版本解压到旁边的 .new。</summary>
    public static void Stage(string packagePath)
    {
        var current = Current() ?? throw new InvalidOperationException("找不到下载器程序文件。");
        var target = current + NewSuffix;
        var staging = current + ".staging";
        DeletePath(staging);
        DeletePath(target);
        try
        {
            PackageInstaller.Extract(packagePath, staging);
            if (PlatformInfo.MacAppBundle is not null)
            {
                var bundle = Directory.EnumerateDirectories(staging, "*.app", SearchOption.AllDirectories).FirstOrDefault()
                             ?? throw new InvalidDataException("更新包里没有 .app 程序包。");
                Directory.Move(bundle, target);
                PlatformInfo.EnsureExecutable(LaunchPath(target));
            }
            else
            {
                var exe = Directory.EnumerateFiles(staging, ExeName, SearchOption.AllDirectories).FirstOrDefault()
                          ?? throw new InvalidDataException($"更新包里没有 {ExeName}。");
                File.Move(exe, target);
                PlatformInfo.EnsureExecutable(target);
            }
        }
        finally
        {
            DeletePath(staging);
        }
        Log.Info($"新版本已解压到 {target}");
    }

    /// <summary>第一步：启动 .new 接手替换。调用后当前进程应尽快退出。</summary>
    public static void LaunchApplier()
    {
        var current = Current() ?? throw new InvalidOperationException("找不到下载器程序文件。");
        var info = new ProcessStartInfo(LaunchPath(current + NewSuffix)) { UseShellExecute = false, WorkingDirectory = AppPaths.Program };
        info.ArgumentList.Add(ApplyArg);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// 第二步：命令行带 --apply-update 且本程序是 .new 时，等旧进程退出，复制到原位置并启动它。
    /// 返回 true 表示这次启动只为替换，调用方应直接退出、不显示界面。
    /// </summary>
    public static bool TryApplyFromArgs(string[] args)
    {
        var index = Array.IndexOf(args, ApplyArg);
        if (index < 0)
            return false;
        // 目标只能是自己去掉 .new 的同目录文件，不从命令行读取路径
        if (Current() is not { } self || !self.EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"忽略 {ApplyArg}：本程序不是待替换的新版本");
            return false;
        }
        var target = self[..^NewSuffix.Length];

        if (index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                if (!old.WaitForExit(TimeSpan.FromSeconds(30)))
                    Log.Warn($"旧进程 {pid} 30 秒内没有退出，仍尝试替换");
            }
            catch (ArgumentException)
            {
                // 已经退出
            }
        }

        var copied = false;
        for (var attempt = 0; attempt < 20 && !copied; attempt++)
        {
            try
            {
                if (Directory.Exists(self))
                    ReplaceBundle(self, target);
                else
                    File.Copy(self, target, overwrite: true);
                copied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"替换 {target} 失败（{ex.Message}），稍后重试");
                Thread.Sleep(500);
            }
        }
        if (copied)
            Log.Info($"已替换 {target}");
        else
            Log.Error($"无法替换 {target}，启动原来的版本");

        Process.Start(new ProcessStartInfo(LaunchPath(target)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! })?.Dispose();
        return true;
    }

    /// <summary>macOS：旧的 .app 先改名为 .old，复制新的过去；复制失败时还原。</summary>
    private static void ReplaceBundle(string source, string target)
    {
        var backup = target + OldSuffix;
        DeletePath(backup);
        if (Directory.Exists(target))
            Directory.Move(target, backup);
        try
        {
            CopyDirectory(source, target);
        }
        catch
        {
            DeletePath(target);
            if (Directory.Exists(backup))
                Directory.Move(backup, target);
            throw;
        }
        TryDelete(backup);
    }

    /// <summary>删除上次更新留下的 .new（以及 macOS 的 .old）。替换进程可能还没完全退出，删不掉就稍后重试。</summary>
    public static void CleanupOldVersion()
    {
        if (Current() is not { } current)
            return;
        var leftovers = new[] { current + NewSuffix, current + OldSuffix }
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
        if (leftovers.Count == 0)
            return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10 && leftovers.Count > 0; attempt++)
            {
                foreach (var path in leftovers.ToList())
                {
                    if (TryDelete(path))
                    {
                        Log.Info($"已删除 {path}");
                        leftovers.Remove(path);
                    }
                }
                if (leftovers.Count > 0)
                    await Task.Delay(1000);
            }
        });
    }

    /// <summary>替换的对象：macOS 的 .app 包目录，其他情况是程序文件。</summary>
    private static string? Current() => PlatformInfo.MacAppBundle ?? Environment.ProcessPath;

    /// <summary>启动用的程序文件：.app 包里是 Contents/MacOS 下的入口。</summary>
    private static string LaunchPath(string current) =>
        current.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || current.EndsWith(".app" + NewSuffix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(current, "Contents", "MacOS", BundleExecutable)
            : current;

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        // File.Copy 在 macOS / Linux 上保留权限（可执行位）
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            DeletePath(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
