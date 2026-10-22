using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Downloader.Services;

/// <summary>
/// 下载器使用的目录，默认都在程序所在目录下（便携，复制整个目录即可带走）：
/// data（账号、安装记录、设置、清单缓存）、logs（按日期分文件的日志）、downloads（安装包缓存）、apps（默认安装目录）。
/// </summary>
public static class AppPaths
{
    public static string Base { get; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string Data => Path.Combine(Base, "data");
    public static string Logs => Path.Combine(Base, "logs");
    public static string Downloads => Path.Combine(Base, "downloads");
    public static string Apps => Path.Combine(Base, "apps");

    /// <summary>设置里填的目录：展开环境变量，相对路径按程序目录解析。</summary>
    public static string Resolve(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()), Base);
}

/// <summary>下载器在本机保存的数据：程序目录下 data 里的 JSON 文件。</summary>
public static class LocalData
{
    // 中文原样写出，方便直接查看、修改
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static string Root => AppPaths.Data;

    public static T? Load<T>(string name) where T : class
    {
        try
        {
            var path = Path.Combine(Root, name);
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 文件损坏或无法读取时当作没有
            Log.Warn($"读取 {name} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>先写临时文件再替换，避免写到一半时程序退出留下损坏的文件。</summary>
    public static void Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, name);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存失败不影响本次使用
            Log.Warn($"保存 {name} 失败：{ex.Message}");
        }
    }
}

/// <summary>登录窗口的选项与自动登录用的会话。</summary>
public sealed class AccountSettings
{
    public string RememberedId { get; set; } = "";
    public bool RememberAccount { get; set; } = true;
    public bool AutoLogin { get; set; }
    public bool UseCodeLogin { get; set; }

    /// <summary>会话 cookie。Windows 上用 DPAPI 加密（只有当前用户能解开），其他系统依赖用户目录权限。</summary>
    public string? Session { get; set; }
    public string? SessionDomain { get; set; }
    public DateTimeOffset? SessionExpires { get; set; }
}

public static class AccountStore
{
    private const string FileName = "account.json";
    private static readonly byte[] Entropy = "CAN-Downloader-session"u8.ToArray();

    public static AccountSettings Load() => LocalData.Load<AccountSettings>(FileName) ?? new AccountSettings();

    public static void Save(AccountSettings settings) => LocalData.Save(FileName, settings);

    public static SavedSession? LoadSession(AccountSettings settings)
    {
        if (settings.Session is not { Length: > 0 } stored || settings.SessionDomain is not { Length: > 0 } domain)
            return null;
        if (settings.SessionExpires is { } expires && expires <= DateTimeOffset.UtcNow)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(stored);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return new SavedSession(Encoding.UTF8.GetString(bytes), domain, settings.SessionExpires);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>保存或清除（<paramref name="session"/> 为 null）会话。</summary>
    public static void SetSession(AccountSettings settings, SavedSession? session)
    {
        if (session is null)
        {
            settings.Session = null;
            settings.SessionDomain = null;
            settings.SessionExpires = null;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(session.Token);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        settings.Session = Convert.ToBase64String(bytes);
        settings.SessionDomain = session.Domain;
        settings.SessionExpires = session.Expires;
    }

    /// <summary>退出登录：清除保存的会话，保留记住的账号。</summary>
    public static void ForgetSession()
    {
        var settings = Load();
        SetSession(settings, null);
        Save(settings);
    }
}

/// <summary>一个已安装的软件：版本、位置和安装的文件（卸载、更新时删除旧文件用）。</summary>
public sealed class InstallRecord
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>相对安装目录的文件路径。</summary>
    public List<string> Files { get; set; } = [];
    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>本机已安装的软件，保存在 installs.json。安装目录已被手动删除的记录在读取时丢弃。</summary>
public static class LocalInstalls
{
    private const string FileName = "installs.json";
    private static Dictionary<string, InstallRecord>? _records;

    private static Dictionary<string, InstallRecord> Records
    {
        get
        {
            if (_records is not null)
                return _records;
            var loaded = LocalData.Load<List<InstallRecord>>(FileName) ?? [];
            _records = loaded
                .Where(r => r.Id.Length > 0 && Directory.Exists(r.Path))
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.Last());
            return _records;
        }
    }

    public static InstallRecord? Find(string id) => Records.GetValueOrDefault(id);

    public static void Set(InstallRecord record)
    {
        Records[record.Id] = record;
        Save();
    }

    public static void Remove(string id)
    {
        if (Records.Remove(id))
            Save();
    }

    private static void Save() => LocalData.Save(FileName, Records.Values.ToList());
}
