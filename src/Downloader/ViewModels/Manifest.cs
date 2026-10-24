using System.Text.Json;
using System.Text.Json.Serialization;
using Downloader.Services;

namespace Downloader.ViewModels;

/// <summary>
/// 更新清单：描述可下载的分类与软件，以及下载器自身的最新版本。由 <see cref="ManifestSource"/> 根据
/// CAN API 的发布信息生成。软件库完全由清单生成——API 新增一个客户端，下载器里就多一个可安装的软件。
/// </summary>
public sealed class UpdateManifest
{
    public DateTimeOffset PublishedAt { get; set; }
    public string Server { get; set; } = "";
    /// <summary>下载器自身的最新版本。API 目前不提供，为 null 时视为已是最新。</summary>
    public LauncherRelease? Launcher { get; set; }
    public List<ManifestCategory> Categories { get; set; } = [];
    public List<ManifestProduct> Products { get; set; } = [];
}

/// <summary>下载器自身的最新版本。<see cref="Mandatory"/> 为真时启动时不能跳过。</summary>
public sealed class LauncherRelease
{
    public string Version { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public long Size { get; set; }
    public bool Mandatory { get; set; }
    public bool Prerelease { get; set; }
    public List<string> Notes { get; set; } = [];
    /// <summary>本机平台的新版下载器安装包（Windows 是内含 Downloader.exe 的 zip）的下载地址与 SHA-256。</summary>
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>没有 SHA-256 时，同一发布附带的 SHA256SUMS 地址（下载后从中查出本安装包的 SHA-256 核对）。</summary>
    public string? ChecksumsUrl { get; set; }
    /// <summary>安装包扩展名（.zip / .tar.gz），用于下载缓存的文件名。</summary>
    public string PackageExtension { get; set; } = ".zip";
}

public sealed class ManifestCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ManifestProduct
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>图标块上的两个字母。</summary>
    public string Icon { get; set; } = "";
    /// <summary>图标块颜色（#RRGGBB），由清单提供。</summary>
    public string Color { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public long Size { get; set; }
    public string? Executable { get; set; }
    /// <summary>经 api.ceruleanavi.net 中转的下载地址（下载源“本站”）。</summary>
    public string? DownloadUrl { get; set; }
    /// <summary>GitHub Releases 上的原始地址（下载源“GitHub”）。</summary>
    public string? OriginUrl { get; set; }
    /// <summary>发布页（完整的更新说明）。</summary>
    public string? NotesUrl { get; set; }
    /// <summary>安装包的 SHA-256；有的话下载后核对。</summary>
    public string? Sha256 { get; set; }
    /// <summary>没有 SHA-256 时，同一发布附带的 SHA256SUMS 地址（下载后从中查出本安装包的 SHA-256 核对）。</summary>
    public string? ChecksumsUrl { get; set; }
    /// <summary>安装包扩展名（.zip / .tar.gz），用于下载缓存的文件名。</summary>
    public string PackageExtension { get; set; } = ".zip";
    /// <summary>预发布版本。</summary>
    public bool Prerelease { get; set; }
    /// <summary>依赖的其他软件 Id。</summary>
    public List<string> Requires { get; set; } = [];
    public List<ManifestComponent> Components { get; set; } = [];
    public List<ManifestChangelog> Changelog { get; set; } = [];
}

public sealed class ManifestComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool Required { get; set; }
    public bool Default { get; set; }
}

public sealed class ManifestChangelog
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public List<string> Changes { get; set; } = [];
}

/// <summary>软件库的显示信息（Assets/catalog.json）：分类与各软件的名称、图标、简介。软件列表本身来自 API。</summary>
public sealed class Catalog
{
    public List<ManifestCategory> Categories { get; set; } = [];
    public List<CatalogEntry> Products { get; set; } = [];
}

public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Executable { get; set; }
    public List<string> Requires { get; set; } = [];
}

/// <summary>
/// 清单来源：<c>GET /api/v1/clients/latest</c>（<c>clients</c> 与 <c>apps</c>）加上 catalog.json 里的显示信息。
/// </summary>
public static class ManifestSource
{
    private const string OtherCategory = "other";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "catalog.json");

    public static async Task<UpdateManifest> FetchAsync(CancellationToken ct = default)
    {
        var latest = await CanApi.Shared.GetLatestClientsAsync(ct);
        return Build(latest, LoadCatalog());
    }

    /// <summary>程序目录下的 catalog.json 优先，没有时用嵌入 exe 的那份。</summary>
    private static Catalog LoadCatalog()
    {
        try
        {
            using var stream = File.Exists(CatalogPath)
                ? File.OpenRead(CatalogPath)
                : typeof(ManifestSource).Assembly.GetManifestResourceStream("catalog.json")
                  ?? throw new FileNotFoundException("缺少 catalog.json。");
            return JsonSerializer.Deserialize<Catalog>(stream, Options) ?? new Catalog();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 没有显示信息时仍然列出 API 里的全部软件
            Log.Warn($"读取 catalog.json 失败：{ex.Message}");
            return new Catalog();
        }
    }

    /// <summary>下载器自己在 API <c>apps</c> 里的名字，用于自更新，不放进软件库。</summary>
    public const string LauncherAppId = "can-downloader";

    private static UpdateManifest Build(ClientsLatest latest, Catalog catalog)
    {
        var published = latest.PublishedAt ?? DateTimeOffset.UtcNow;
        var entries = catalog.Products.ToDictionary(p => p.Id);
        var order = catalog.Products.Select(p => p.Id).ToList();

        // CAN 客户端合集（同一个版本）与独立发布的程序（各自的版本）。没有本机平台安装包的软件不列出
        var bundle = latest.Clients.Select(c => Package(c.Key, c.Value.Download, c.Value.Origin, c.Value.Size, null, c.Value.Platforms)
            is not { } package ? null : Product(c.Key, entries.GetValueOrDefault(c.Key),
            c.Value.Version.Length > 0 ? c.Value.Version : latest.Version, latest.PublishedAt, package,
            latest.Notes, prerelease: false, changelog: null));
        var apps = latest.Apps.Where(a => a.Key != LauncherAppId).Select(a =>
            Package(a.Key, a.Value.Download, a.Value.Origin, a.Value.Size, a.Value.Sha256, a.Value.Platforms)
            is not { } package ? null : Product(a.Key, entries.GetValueOrDefault(a.Key),
            a.Value.Version, a.Value.PublishedAt, package, a.Value.Notes, a.Value.Prerelease, a.Value.Changelog));

        var products = bundle.Concat(apps).OfType<ManifestProduct>()
            .OrderBy(p => order.IndexOf(p.Id) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var categories = catalog.Categories.ToList();
        if (products.Any(p => p.Category == OtherCategory) && categories.All(c => c.Id != OtherCategory))
            categories.Add(new ManifestCategory { Id = OtherCategory, Name = "其他" });

        return new UpdateManifest
        {
            PublishedAt = published,
            Server = CanApi.ApiHost,
            Launcher = latest.Apps.GetValueOrDefault(LauncherAppId) is { } launcher
                       && Package(LauncherAppId, launcher.Download, launcher.Origin, launcher.Size, launcher.Sha256, launcher.Platforms) is { } package
                ? Launcher(launcher, package)
                : null,
            Categories = categories,
            Products = products
        };
    }

    /// <summary>某个平台的安装包。</summary>
    private sealed record PackageSource(string Download, string? Origin, long Size, string? Sha256, string? ChecksumsUrl, string Extension);

    /// <summary>Windows 版安装包文件名的结尾，其他平台的文件名把它换成自己的平台标识（与 can-pilot、下载器自己的发布一致）。</summary>
    private const string WindowsSuffix = "-win-x64.zip";

    /// <summary>
    /// 本机用的安装包。API 给出 <c>platforms</c> 时按 <see cref="PlatformInfo.PackageKeys"/> 的顺序挑；没有时上面的地址是 Windows 版，
    /// 其他系统按发布文件名的约定（…-win-x64.zip → …-osx-universal.zip / …-linux-x64.tar.gz）换成 GitHub 上同一发布的安装包——
    /// 本站中转只有 Windows 版，只能从 GitHub 下载，SHA-256 从同一发布的 SHA256SUMS 查。都没有时返回 null（本机不能安装）。
    /// </summary>
    private static PackageSource? Package(string id, string download, string? origin, long size, string? sha256,
        Dictionary<string, PlatformBuild>? platforms)
    {
        if (platforms is { Count: > 0 })
        {
            foreach (var key in PlatformInfo.PackageKeys)
            {
                if (platforms.GetValueOrDefault(key) is { Download.Length: > 0 } build)
                    return new PackageSource(build.Download, build.Origin, build.Size, build.Sha256, null, PlatformInfo.PackageExtension(key));
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            return new PackageSource(download, origin, size, sha256, null, ".zip");
        }
        else if (origin is not null && origin.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                 && origin.EndsWith(WindowsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var key = PlatformInfo.PackageKeys[0];
            var extension = PlatformInfo.PackageExtension(key);
            var url = $"{origin[..^WindowsSuffix.Length]}-{key}{extension}";
            var checksums = origin[..(origin.LastIndexOf('/') + 1)] + "SHA256SUMS";
            return new PackageSource(url, url, 0, null, checksums, extension);
        }
        Log.Info($"{id} 没有 {PlatformInfo.Name}（{string.Join(" / ", PlatformInfo.PackageKeys)}）版本");
        return null;
    }

    private static ManifestProduct Product(string id, CatalogEntry? entry, string version, DateTimeOffset? publishedAt,
        PackageSource package, string? notesUrl, bool prerelease, string? changelog)
    {
        var size = package.Size;
        var date = Date(publishedAt);
        return new ManifestProduct
        {
            Id = id,
            Name = entry?.Name ?? id,
            Category = entry?.Category ?? OtherCategory,
            Icon = entry?.Icon ?? id[..Math.Min(2, id.Length)].ToUpperInvariant(),
            Color = entry?.Color ?? "",
            Summary = entry?.Summary ?? "",
            Description = entry?.Description ?? entry?.Summary ?? "",
            Version = version,
            ReleaseDate = date,
            Size = size,
            Prerelease = prerelease,
            Executable = PlatformInfo.ExecutableName(entry?.Executable ?? id + ".exe"),
            DownloadUrl = package.Download,
            OriginUrl = package.Origin,
            NotesUrl = notesUrl,
            Sha256 = package.Sha256,
            ChecksumsUrl = package.ChecksumsUrl,
            PackageExtension = package.Extension,
            Requires = entry?.Requires ?? [],
            Components = [new ManifestComponent { Id = "core", Name = "主程序", Size = size, Required = true }],
            Changelog = [new ManifestChangelog { Version = version, Date = date, Changes = ReleaseNotes.Summarize(changelog) }]
        };
    }

    private static LauncherRelease Launcher(AppBuild build, PackageSource package) => new()
    {
        Version = build.Version,
        ReleaseDate = Date(build.PublishedAt),
        Size = package.Size,
        Prerelease = build.Prerelease,
        Notes = ReleaseNotes.Summarize(build.Changelog),
        // 本站中转不提供时（其他平台按文件名推出的 GitHub 地址）用 GitHub 地址
        DownloadUrl = package.Download,
        Sha256 = package.Sha256,
        ChecksumsUrl = package.ChecksumsUrl,
        PackageExtension = package.Extension
    };

    private static string Date(DateTimeOffset? value) => (value ?? DateTimeOffset.UtcNow).ToLocalTime().ToString("yyyy-MM-dd");
}

/// <summary>把 GitHub 发布说明（Markdown）整理成几行纯文字，用于更新日志和下载器的更新说明。</summary>
public static class ReleaseNotes
{
    private const int MaxLines = 12;

    /// <summary>优先取列表项；没有列表时取普通段落。去掉标题、代码块和行内的 Markdown 标记。</summary>
    public static List<string> Summarize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var bullets = new List<string>();
        var paragraphs = new List<string>();
        var inCode = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode || line.Length == 0 || line.StartsWith('#') || line.StartsWith('|') || line.StartsWith('>'))
                continue;

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                bullets.Add(Plain(line[2..]));
            else if (!bullets.Any())
                paragraphs.Add(Plain(line));
        }
        return (bullets.Count > 0 ? bullets : paragraphs).Where(l => l.Length > 0).Take(MaxLines).ToList();
    }

    private static string Plain(string text) =>
        text.Replace("**", "").Replace("`", "").Replace("__", "").Trim();
}
