using System.Globalization;
using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Platform;

namespace Downloader.Services;

/// <summary>
/// 软件图标（Assets/icons/*.svg）。只支持线条图标：viewBox 0 0 24 24、只用 &lt;path d&gt;，
/// 颜色由图标块决定（SVG 里写 currentColor），描边宽度取根元素的 stroke-width。
/// </summary>
public sealed record SvgIcon(Geometry Data, double StrokeWidth)
{
    private static readonly Dictionary<string, SvgIcon?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>catalog.json 的 icon 以 .svg 结尾时当作图标文件名。</summary>
    public static bool IsSvgName(string? icon) =>
        icon is not null && icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取嵌入的图标，失败时返回 null（界面改用文字）。</summary>
    public static SvgIcon? Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            SvgIcon? icon = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Downloader/Assets/icons/{name}"));
                var root = XDocument.Load(stream).Root ?? throw new FormatException("空文件");
                // 每段 d 都以 M 开头，拼成一个 StreamGeometry，多个界面共用同一份
                var data = string.Join(" ", root.Descendants()
                    .Where(e => e.Name.LocalName == "path")
                    .Select(e => (string?)e.Attribute("d"))
                    .Where(d => !string.IsNullOrWhiteSpace(d)));
                if (data.Length == 0) throw new FormatException("没有 path");
                var width = double.TryParse((string?)root.Attribute("stroke-width"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var w) ? w : 1.6;
                icon = new SvgIcon(StreamGeometry.Parse(data), width);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取图标 {name} 失败：{ex.Message}");
            }
            Cache[name] = icon;
            return icon;
        }
    }
}
