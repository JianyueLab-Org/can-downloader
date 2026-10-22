using System.Diagnostics;
using System.IO.Compression;

namespace Downloader.Services;

/// <summary>
/// 下载器替换自己，分两步，正在运行的 exe 文件始终不动：
/// <list type="number">
/// <item>旧版本把更新包里的 Downloader.exe 解压为 <c>Downloader.exe.new</c>，以 <c>--apply-update &lt;进程号&gt;</c> 启动它，然后正常退出。</item>
/// <item>新版本（从 .new 启动，进入界面之前）等旧进程退出，把自己复制到 Downloader.exe，启动它后退出。</item>
/// </list>
/// 最后启动的 Downloader.exe 删除留下的 .new。
/// 不能在运行中把 exe 改名后继续运行：单文件发布的程序按需从 exe 读取程序集，改名后再加载新的程序集会失败。
/// </summary>
public static class SelfUpdater
{
    private const string ExeName = "Downloader.exe";
    private const string ApplyArg = "--apply-update";
    private const string NewSuffix = ".new";

    /// <summary>不能自更新的原因；可以时为 null。只有发布出来的单文件 exe 能替换自己。</summary>
    public static string? CannotReplaceReason
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return "当前系统不支持自动更新，请手动下载新版本。";
            if (Environment.ProcessPath is not { } exe)
                return "找不到下载器程序文件。";
            // 编译输出目录（dotnet run）里 exe 只是启动器，程序在旁边的 Downloader.dll
            if (File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Downloader.dll")))
                return "开发版（编译输出目录）不能自动更新，请使用发布的版本。";
            return null;
        }
    }

    /// <summary>第一步：把新版本解压到 exe 旁边的 .new。</summary>
    public static void Stage(string zipPath)
    {
        var target = NewPath();
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"更新包里没有 {ExeName}。");
        entry.ExtractToFile(target, overwrite: true);
        Log.Info($"新版本已解压到 {target}");
    }

    /// <summary>第一步：启动 .new 接手替换。调用后当前进程应尽快退出。</summary>
    public static void LaunchApplier()
    {
        var info = new ProcessStartInfo(NewPath()) { UseShellExecute = false, WorkingDirectory = AppPaths.Base };
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
        if (Environment.ProcessPath is not { } self || !self.EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase))
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
                File.Copy(self, target, overwrite: true);
                copied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
        if (copied)
            Log.Info($"已替换 {target}");
        else
            Log.Error($"无法替换 {target}，启动原来的版本");

        Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = AppPaths.Base })?.Dispose();
        return true;
    }

    /// <summary>删除上次更新留下的 .new。替换进程可能还没完全退出，删不掉就稍后重试。</summary>
    public static void CleanupOldVersion()
    {
        var leftover = NewPath();
        if (!File.Exists(leftover))
            return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Delete(leftover);
                    Log.Info($"已删除 {leftover}");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    private static string NewPath() =>
        (Environment.ProcessPath ?? Path.Combine(AppPaths.Base, ExeName)) + NewSuffix;
}
