using System.Reflection;

namespace Downloader.ViewModels;

/// <summary>下载器自身信息。自更新目前是模拟的：完成后直接改写版本号，模拟重启后的新版本。</summary>
public static class AppInfo
{
    public const string Name = "CAN 下载器";

    /// <summary>取自 Downloader.csproj 的 &lt;Version&gt;（去掉编译时附加的 +提交号）。</summary>
    public static string Version { get; set; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}

/// <summary>版本号比较：按点分段逐段比较数字（1.10.0 &gt; 1.9.2，AIRAC 2611 &gt; 2610）。</summary>
public static class VersionUtil
{
    public static int Compare(string? a, string? b)
    {
        var left = (a ?? "").Split('.', '-');
        var right = (b ?? "").Split('.', '-');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : "0";
            var y = i < right.Length ? right[i] : "0";
            var result = int.TryParse(x, out var nx) && int.TryParse(y, out var ny)
                ? nx.CompareTo(ny)
                : string.CompareOrdinal(x, y);
            if (result != 0)
                return result;
        }
        return 0;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;
}

public static class Format
{
    public static string Size(double bytes) => bytes switch
    {
        >= 1024d * 1024 * 1024 => $"{bytes / 1024 / 1024 / 1024:0.00} GB",
        >= 1024d * 1024 => $"{bytes / 1024 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024:0} KB",
        _ => $"{bytes:0} B"
    };

    public static string Speed(double bytesPerSecond) => Size(bytesPerSecond) + "/s";

    public static string Remaining(double seconds)
    {
        var s = (int)Math.Ceiling(seconds);
        return s switch
        {
            < 1 => "即将完成",
            < 60 => $"剩余 {s} 秒",
            < 3600 => $"剩余 {s / 60} 分 {s % 60:00} 秒",
            _ => $"剩余 {s / 3600} 小时 {s % 3600 / 60:00} 分"
        };
    }
}
