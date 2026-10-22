using System.Text.Json;
using System.Text.Json.Serialization;

namespace Downloader.ViewModels;

/// <summary>
/// 更新清单：由更新服务器下发，描述下载器自身的最新版本，以及可下载的分类与软件。
/// 软件库完全由清单生成——服务器新增一个条目，下载器里就多一个可安装的软件，无需更新下载器。
/// </summary>
public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string Server { get; set; } = "";
    public LauncherRelease Launcher { get; set; } = new();
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

/// <summary>
/// 清单来源。界面原型：读取程序目录下的 manifest.json 代替网络请求；
/// 修改该文件后点“检查更新”，软件库会按新清单自动增删、升级。
/// </summary>
public static class ManifestSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "manifest.json");

    /// <summary>演示：在清单上叠加一个新软件和一个新版本，模拟服务器端有新发布。</summary>
    public static bool IncludeDemoRelease { get; set; }

    public static UpdateManifest Load()
    {
        using var stream = File.OpenRead(FilePath);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(stream, Options)
                       ?? throw new InvalidDataException("更新清单为空。");
        if (IncludeDemoRelease)
            ApplyDemoRelease(manifest);
        return manifest;
    }

    private static void ApplyDemoRelease(UpdateManifest manifest)
    {
        if (manifest.Products.Find(p => p.Id == "controller-client") is { } controller)
        {
            controller.Version = "2.1.0";
            controller.ReleaseDate = "2026-10-23";
            controller.Changelog.Insert(0, new ManifestChangelog
            {
                Version = "2.1.0",
                Date = "2026-10-23",
                Changes = ["新增音频压缩设置", "内话面板支持分页快捷键"]
            });
        }

        if (manifest.Products.Exists(p => p.Id == "radar-client"))
            return;

        manifest.Products.Insert(3, new ManifestProduct
        {
            Id = "radar-client",
            Name = "雷达管制客户端",
            Category = "clients",
            Icon = "RD",
            Color = "#E2C766",
            Summary = "雷达显示、标牌与管制移交（预览版）",
            Description = "CAN 雷达管制客户端预览版：雷达画面、航空器标牌、飞行进程单与管制移交，与管制员客户端配合使用。",
            Version = "0.1.0",
            ReleaseDate = "2026-10-23",
            Size = 47185920,
            Executable = "RadarClient.exe",
            Requires = ["controller-client", "sector-files"],
            Components =
            [
                new() { Id = "core", Name = "主程序", Size = 41943040, Required = true },
                new() { Id = "maps", Name = "视频地图", Size = 5242880, Default = true }
            ],
            Changelog = [new() { Version = "0.1.0", Date = "2026-10-23", Changes = ["首个预览版"] }]
        });
    }
}
