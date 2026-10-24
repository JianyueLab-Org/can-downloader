using System.Runtime.InteropServices;

namespace Downloader.Services;

/// <summary>
/// 当前系统与发布包的对应关系。发布文件名里的平台标识与 can-pilot 一致：
/// <c>win-x64</c>、<c>osx-arm64</c>、<c>linux-x64</c>、<c>linux-arm64</c>；macOS 客户端也兼容 <c>osx-universal</c> 通用包。
/// </summary>
public static class PlatformInfo
{
    /// <summary>按实际可执行文件路径检查，包含 .app 内和安装目录内的辅助进程。</summary>
    public static void ThrowIfRunning(string installDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                { continue; }
                if (executable is not null && Path.GetFullPath(executable).StartsWith(root, comparison))
                    throw new IOException("软件正在运行，请先关闭软件后再更新或修复。");
            }
        }
    }
    public static string Name { get; } =
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>本机能运行的安装包，按优先顺序（macOS 优先 ARM64，其次兼容通用包；Windows ARM 也能运行 x64 版）。</summary>
    public static IReadOnlyList<string> PackageKeys { get; } = DetectPackageKeys();

    /// <summary>macOS 上从 .app 包里运行时为包目录（…/CAN Downloader.app），否则为 null。</summary>
    public static string? MacAppBundle { get; } = DetectMacAppBundle();

    private static string[] DetectPackageKeys()
    {
        var arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return arm64 ? ["win-arm64", "win-x64"] : ["win-x64"];
        if (OperatingSystem.IsMacOS())
            return arm64 ? ["osx-arm64", "osx-universal"] : [];
        return arm64 ? ["linux-arm64"] : ["linux-x64"];
    }

    /// <summary>安装包的扩展名：Linux 用 tar.gz（保留可执行权限），其他系统用 zip。</summary>
    public static string PackageExtension(string key) =>
        key.StartsWith("linux-", StringComparison.Ordinal) ? ".tar.gz" : ".zip";

    /// <summary>程序文件名。软件库里写的是 Windows 的 xxx.exe，其他系统去掉 .exe。</summary>
    public static string ExecutableName(string name) =>
        !OperatingSystem.IsWindows() && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// 已安装软件的启动目标：macOS 上优先是安装目录里的 .app 包，其他情况是程序文件。找不到时返回 null。
    /// </summary>
    public static string? FindLaunchTarget(string installDir, string executable)
    {
        if (OperatingSystem.IsMacOS() && Directory.Exists(installDir)
            && Directory.EnumerateDirectories(installDir, "*.app").Order(StringComparer.Ordinal).FirstOrDefault() is { } bundle)
            return bundle;
        var path = Path.Combine(installDir, executable);
        return File.Exists(path) ? path : null;
    }

    /// <summary>macOS / Linux：给程序文件加上可执行权限（压缩包里没记录权限时解压出来的文件不能运行）。</summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserExecute) == 0)
            File.SetUnixFileMode(path, mode | UnixFileMode.UserRead | execute);
    }

    /// <summary>程序在 …/X.app/Contents/MacOS 里（自更新时是 …/X.app.new/Contents/MacOS）。</summary>
    private static string? DetectMacAppBundle()
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var macOS = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var contents = Path.GetDirectoryName(macOS);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return Path.GetFileName(macOS) == "MacOS" && Path.GetFileName(contents) == "Contents" && bundle is not null
               && (bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                   || bundle.EndsWith(".app" + SelfUpdater.NewSuffix, StringComparison.OrdinalIgnoreCase))
            ? bundle
            : null;
    }
}
