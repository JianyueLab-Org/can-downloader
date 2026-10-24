using System.Runtime.InteropServices;

namespace Downloader.Services;

/// <summary>
/// 当前系统与发布包的对应关系。发布文件名里的平台标识与 can-pilot 一致：
/// <c>win-x64</c>、<c>osx-universal</c>（Apple Silicon 和 Intel 通用）、<c>osx-arm64</c>、<c>osx-x64</c>、<c>linux-x64</c>、<c>linux-arm64</c>。
/// </summary>
public static class PlatformInfo
{
    public static string Name { get; } =
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>本机能运行的安装包，按优先顺序（通用包优先；Windows ARM 和 Apple Silicon 也能运行 x64 版）。</summary>
    public static IReadOnlyList<string> PackageKeys { get; } = DetectPackageKeys();

    /// <summary>macOS 上从 .app 包里运行时为包目录（…/CAN Downloader.app），否则为 null。</summary>
    public static string? MacAppBundle { get; } = DetectMacAppBundle();

    private static string[] DetectPackageKeys()
    {
        var arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return arm64 ? ["win-arm64", "win-x64"] : ["win-x64"];
        if (OperatingSystem.IsMacOS())
            return arm64 ? ["osx-universal", "osx-arm64", "osx-x64"] : ["osx-universal", "osx-x64"];
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
