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
    public List<string> Notes { get; set; } = [];
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
/// 清单来源：<c>GET /api/v1/clients/latest</c> 加上 catalog.json 里的显示信息。
/// 每次获取成功后缓存到本机，离线模式显示缓存的软件列表。
/// </summary>
public static class ManifestSource
{
    private const string CacheFile = "manifest-cache.json";
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
        var manifest = Build(latest, LoadCatalog());
        LocalData.Save(CacheFile, manifest);
        return manifest;
    }

    /// <summary>上次获取的清单；没有时返回 null。</summary>
    public static UpdateManifest? LoadCached() => LocalData.Load<UpdateManifest>(CacheFile);

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

    private static UpdateManifest Build(ClientsLatest latest, Catalog catalog)
    {
        var published = latest.PublishedAt ?? DateTimeOffset.UtcNow;
        var date = published.ToLocalTime().ToString("yyyy-MM-dd");
        var entries = catalog.Products.ToDictionary(p => p.Id);
        var order = catalog.Products.Select(p => p.Id).ToList();

        var products = latest.Clients
            .OrderBy(c => order.IndexOf(c.Key) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c =>
            {
                var (id, build) = (c.Key, c.Value);
                var entry = entries.GetValueOrDefault(id);
                var version = build.Version.Length > 0 ? build.Version : latest.Version;
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
                    Size = build.Size,
                    Executable = entry?.Executable ?? (OperatingSystem.IsWindows() ? id + ".exe" : null),
                    DownloadUrl = build.Download,
                    OriginUrl = build.Origin,
                    NotesUrl = latest.Notes,
                    Requires = entry?.Requires ?? [],
                    Components = [new ManifestComponent { Id = "core", Name = "主程序", Size = build.Size, Required = true }],
                    Changelog = [new ManifestChangelog { Version = version, Date = date }]
                };
            })
            .ToList();

        var categories = catalog.Categories.ToList();
        if (products.Any(p => p.Category == OtherCategory) && categories.All(c => c.Id != OtherCategory))
            categories.Add(new ManifestCategory { Id = OtherCategory, Name = "其他" });

        return new UpdateManifest
        {
            PublishedAt = published,
            Server = CanApi.ApiHost,
            Categories = categories,
            Products = products
        };
    }
}
