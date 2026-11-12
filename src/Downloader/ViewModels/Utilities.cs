using System.Reflection;

namespace Downloader.ViewModels;

/// <summary>下载器自身信息。</summary>
public static class AppInfo
{
    public const string Name = "CAN 下载器";

    /// <summary>取自 Downloader.csproj 的 &lt;Version&gt;（去掉编译时附加的 +提交号）。</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}

/// <summary>
/// 版本号比较（语义化版本的优先级）：主体按点分段逐段比较数字（1.10.0 &gt; 1.9.2，AIRAC 2611 &gt; 2610，缺的段按 0）；
/// 主体相同时正式版高于预发布版（3.1.0 &gt; 3.1.0-rc.1），预发布标识逐段比较（数字段按数值、低于文字段）。
/// 前缀 v 与 +构建信息不参与比较。
/// </summary>
public static class VersionUtil
{
    public static int Compare(string? a, string? b)
    {
        var (leftCore, leftPre) = Split(a);
        var (rightCore, rightPre) = Split(b);

        var left = leftCore.Split('.');
        var right = rightCore.Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var result = CompareIdentifier(i < left.Length ? left[i] : "0", i < right.Length ? right[i] : "0");
            if (result != 0)
                return result;
        }

        // 主体相同：没有预发布标识的（正式版）更高
        if (leftPre is null || rightPre is null)
            return (leftPre is null ? 1 : 0) - (rightPre is null ? 1 : 0);

        var lp = leftPre.Split('.');
        var rp = rightPre.Split('.');
        for (var i = 0; i < Math.Min(lp.Length, rp.Length); i++)
        {
            var x = lp[i];
            var y = rp[i];
            var xNumeric = long.TryParse(x, out _);
            var yNumeric = long.TryParse(y, out _);
            // 数字标识低于文字标识（rc.1 < rc.beta）
            var result = xNumeric != yNumeric ? (xNumeric ? -1 : 1) : CompareIdentifier(x, y);
            if (result != 0)
                return result;
        }
        return lp.Length.CompareTo(rp.Length);
    }

    /// <summary>拆成主体和预发布标识（没有时为 null），去掉前缀 v 和 +构建信息。</summary>
    private static (string Core, string? Prerelease) Split(string? version)
    {
        var text = (version ?? "").Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        if (text.IndexOf('+') is var plus and >= 0)
            text = text[..plus];
        var dash = text.IndexOf('-');
        return dash < 0 ? (text, null) : (text[..dash], text[(dash + 1)..]);
    }

    private static int CompareIdentifier(string x, string y) =>
        long.TryParse(x, out var nx) && long.TryParse(y, out var ny)
            ? nx.CompareTo(ny)
            : string.CompareOrdinal(x, y);

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
