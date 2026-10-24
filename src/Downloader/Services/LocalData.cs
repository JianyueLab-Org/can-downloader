using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Downloader.Services;

/// <summary>
/// 下载器使用的目录，默认都在程序所在目录下（便携，复制整个目录即可带走）：
/// data（账号、安装记录、设置、清单缓存）、logs（按日期分文件的日志）、downloads（安装包缓存）、apps（默认安装目录）。
/// macOS 的 .app 包里不能写东西，Linux 用系统包装在 /opt 时程序目录不可写，这两种情况放到用户目录：
/// ~/Library/Application Support/CAN Downloader、$XDG_DATA_HOME（默认 ~/.local/share）/CAN Downloader。Windows 始终在程序目录。
/// </summary>
public static class AppPaths
{
    /// <summary>程序所在目录。</summary>
    public static string Program { get; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>数据目录的根，下面是 data、logs、downloads、apps。设置里的相对路径相对于它。</summary>
    public static string Base { get; } = ChooseBase();

    public static string Data => Path.Combine(Base, "data");
    public static string Logs => Path.Combine(Base, "logs");
    public static string Downloads => Path.Combine(Base, "downloads");
    public static string Apps => Path.Combine(Base, "apps");

    /// <summary>设置里填的目录：展开环境变量和 ~，相对路径按数据目录解析。</summary>
    public static string Resolve(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!OperatingSystem.IsWindows() && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.GetFullPath(path, Base);
    }

    public static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ChooseBase()
    {
        if (OperatingSystem.IsWindows() || PlatformInfo.MacAppBundle is null && IsWritable(Program))
            return Program;
        // .NET 在 macOS 上是 ~/Library/Application Support，Linux 上是 $XDG_DATA_HOME 或 ~/.local/share
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var directory = Path.Combine(root, "CAN Downloader");
        Directory.CreateDirectory(directory);
        return directory;
    }
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
    public static void Save<T>(string name, T value, bool throwOnError = false)
    {
        try
        {
            var path = Path.Combine(Root, name);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            }
            else
            {
                // 账号文件里有 refresh 令牌，macOS / Linux 上没有 DPAPI，只让当前用户读写
                Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                using (var file = new FileStream(temp, new FileStreamOptions
                       {
                           Mode = FileMode.Create, Access = FileAccess.Write,
                           UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                       }))
                using (var writer = new StreamWriter(file))
                    writer.Write(JsonSerializer.Serialize(value, Json));
            }
            if (throwOnError)
            {
                using var durable = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None);
                durable.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (throwOnError)
                throw;
            // 保存失败不影响本次使用
            Log.Warn($"保存 {name} 失败：{ex.Message}");
        }
    }
}

/// <summary>登录选项与自动登录用的 OAuth refresh 令牌。</summary>
public sealed class AccountSettings
{
    public bool AutoLogin { get; set; } = true;

    /// <summary>refresh 令牌（每次刷新都会换新）。Windows 上用 DPAPI 加密（只有当前用户能解开），macOS / Linux 上文件权限为只有当前用户可读写（600）。</summary>
    public string? RefreshToken { get; set; }
}

public static class AccountStore
{
    private const string FileName = "account.json";
    private static readonly byte[] Entropy = "CAN-Downloader-oauth"u8.ToArray();

    public static AccountSettings Load() => LocalData.Load<AccountSettings>(FileName) ?? new AccountSettings();

    public static void Save(AccountSettings settings) => LocalData.Save(FileName, settings);

    public static string? LoadRefreshToken(AccountSettings settings)
    {
        if (settings.RefreshToken is not { Length: > 0 } stored)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(stored);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>保存或清除（<paramref name="refreshToken"/> 为 null）refresh 令牌，并写入文件。</summary>
    public static void SaveRefreshToken(AccountSettings settings, string? refreshToken)
    {
        if (refreshToken is null)
        {
            settings.RefreshToken = null;
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(refreshToken);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            settings.RefreshToken = Convert.ToBase64String(bytes);
        }
        Save(settings);
    }
}

/// <summary>一个已安装的软件：版本、位置和安装的文件（卸载、更新时删除旧文件用）。</summary>
public sealed class InstallRecord
{
    public string? TransactionId { get; set; }
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
    internal static readonly object Sync = new();
    private const string FileName = "installs.json";
    private static Dictionary<string, InstallRecord>? _records;

    private static Dictionary<string, InstallRecord> Records
    {
        get
        {
            if (_records is not null)
                return _records;
            InstallationTransaction.Recover();
            var loaded = LocalData.Load<List<InstallRecord>>(FileName) ?? [];
            _records = loaded
                .Where(r => r.Id.Length > 0 && Directory.Exists(r.Path))
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.Last());
            return _records;
        }
    }

    public static InstallRecord? Find(string id)
    {
        lock (Sync) return Records.GetValueOrDefault(id);
    }

    public static void Set(InstallRecord record)
    {
        lock (Sync)
        {
            var next = new Dictionary<string, InstallRecord>(Records) { [record.Id] = record };
            LocalData.Save(FileName, next.Values.ToList(), throwOnError: true);
            _records = next;
        }
    }

    public static void Remove(string id)
    {
        lock (Sync)
        {
            var next = new Dictionary<string, InstallRecord>(Records);
            if (!next.Remove(id)) return;
            LocalData.Save(FileName, next.Values.ToList(), throwOnError: true);
            _records = next;
        }
    }

}
