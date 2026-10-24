using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;

namespace Downloader.Services;

/// <summary>下载线程与界面共享的进度，界面定时读取。</summary>
public sealed class DownloadProgress
{
    private long _downloaded;
    private long _total;

    public long Downloaded => Interlocked.Read(ref _downloaded);
    public long Total => Interlocked.Read(ref _total);

    internal void Reset(long downloaded, long total)
    {
        Interlocked.Exchange(ref _downloaded, downloaded);
        Interlocked.Exchange(ref _total, total);
    }

    internal void Add(long bytes) => Interlocked.Add(ref _downloaded, bytes);
}

/// <summary>
/// 安装包的下载（断点续传、限速）、校验、解压与卸载。CAN 客户端的安装包是 zip（Linux 版是 tar.gz），
/// 里面是一个与软件同名的目录；macOS 版是 zip 里的 .app 包。
/// </summary>
public static class PackageInstaller
{
    private const int BufferSize = 81920;

    public static string CachePath(string cacheFolder, string id, string version, string extension = ".zip") =>
        Path.Combine(cacheFolder, $"{id}-{version}{extension}");

    public static string PartialPath(string zipPath) => zipPath + ".part";

    /// <summary>
    /// 下载到 <c>.part</c> 文件，完成后改名为 <paramref name="target"/>。已有 <c>.part</c> 时用 Range 续传；
    /// 服务器不支持续传时从头下载。<paramref name="speedLimit"/> 返回本下载当前的限速（字节/秒，0 为不限）。
    /// </summary>
    public static async Task DownloadAsync(Uri url, string target, long expectedSize, DownloadProgress progress,
        Func<double> speedLimit, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && (expectedSize <= 0 || new FileInfo(target).Length == expectedSize))
        {
            var length = new FileInfo(target).Length;
            progress.Reset(length, length);
            return;
        }

        var part = PartialPath(target);
        for (var attempt = 0; ; attempt++)
        {
            var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (expectedSize > 0 && offset >= expectedSize)
            {
                File.Delete(part);
                offset = 0;
            }

            using var response = await CanApi.Shared.GetDownloadAsync(url, offset, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 缓存的半截文件与服务器上的对不上，从头下载
                File.Delete(part);
                if (attempt == 0)
                    continue;
                throw new InvalidDataException("服务器拒绝了续传请求，请重试。");
            }
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                offset = 0;

            var total = response.Content.Headers.ContentLength is { } length2 ? offset + length2 : expectedSize;
            progress.Reset(offset, total);

            await using (var file = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
                             FileShare.None, BufferSize, useAsync: true))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[BufferSize];
                var window = Stopwatch.StartNew();
                long windowBytes = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress.Add(read);

                    var limit = speedLimit();
                    if (limit <= 0)
                        continue;
                    windowBytes += read;
                    var ahead = windowBytes / limit - window.Elapsed.TotalSeconds;
                    if (ahead > 0.02)
                        await Task.Delay(TimeSpan.FromSeconds(ahead), ct);
                    // 每两秒重新计时，限速调整后很快生效
                    if (window.Elapsed.TotalSeconds > 2)
                    {
                        window.Restart();
                        windowBytes = 0;
                    }
                }
            }

            var size = new FileInfo(part).Length;
            if (expectedSize > 0 && size != expectedSize)
            {
                File.Delete(part);
                throw new InvalidDataException($"下载的文件大小不符（{size} / {expectedSize} 字节），请重试。");
            }
            File.Move(part, target, overwrite: true);
            return;
        }
    }

    /// <summary>核对 SHA-256（发布方提供的值，小写十六进制）。不一致时抛出 <see cref="InvalidDataException"/>。</summary>
    public static void VerifySha256(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"SHA-256 不符：{Path.GetFileName(path)} 应为 {expected}，实际 {actual}");
            throw new InvalidDataException("安装包的 SHA-256 与发布信息不符。");
        }
    }

    /// <summary>
    /// 从发布附带的 SHA256SUMS（sha256sum 的输出格式）里查出 <paramref name="fileName"/> 的 SHA-256。
    /// 没有清单、下载失败或清单里没有这个文件时返回 null（照常安装，只是不核对）。
    /// </summary>
    public static async Task<string?> LookupSha256Async(string checksumsUrl, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var response = await CanApi.Shared.GetDownloadAsync(new Uri(checksumsUrl), 0, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            foreach (var line in text.Split('\n'))
            {
                var fields = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2 && fields[0].Length == 64
                    && Path.GetFileName(fields[1].Trim().TrimStart('*')) == fileName)
                    return fields[0].ToLowerInvariant();
            }
            Log.Warn($"{checksumsUrl} 里没有 {fileName}，不核对 SHA-256");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or UriFormatException)
        {
            Log.Warn($"读取 {checksumsUrl} 失败，不核对 SHA-256：{ex.Message}");
        }
        return null;
    }

    /// <summary>完整读取每个文件，确认压缩包没有损坏。损坏时抛出 <see cref="InvalidDataException"/>。</summary>
    public static void Verify(string packagePath)
    {
        if (IsGzip(packagePath))
        {
            try
            {
                ReadTar(packagePath, entry => entry.DataStream?.CopyTo(Stream.Null));
            }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException)
            {
                throw new InvalidDataException($"安装包已损坏：{ex.Message}", ex);
            }
            return;
        }
        using var zip = ZipFile.OpenRead(packagePath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            using var stream = entry.Open();
            stream.CopyTo(Stream.Null);
        }
    }

    /// <summary>
    /// 解压 zip 或 tar.gz 到 <paramref name="installDir"/>，覆盖同名文件；压缩包只有一个顶层目录时去掉这一层
    /// （macOS 的 X.app 除外，整个包保留）。保留 macOS / Linux 的可执行权限。返回解压出的文件（相对安装目录）。
    /// </summary>
    public static List<string> Extract(string packagePath, string installDir)
    {
        var root = Path.GetFullPath(installDir);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var files = new List<string>();

        // 返回文件的目标路径；目录（直接创建）和要跳过的条目返回 null
        string? Destination(string name, string prefix)
        {
            var normalized = Normalize(name);
            if (IsMacMetadata(normalized))
                return null;
            var relative = normalized.StartsWith(prefix, StringComparison.Ordinal) ? normalized[prefix.Length..] : normalized;
            if (relative.Length == 0)
                return null;
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            // 防止压缩包里的 ../ 写到安装目录之外
            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"安装包包含非法路径：{name}");
            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            files.Add(relative);
            return destination;
        }

        if (IsGzip(packagePath))
        {
            var names = new List<string>();
            ReadTar(packagePath, entry => names.Add(entry.Name));
            var prefix = CommonTopFolder(names);
            ReadTar(packagePath, entry =>
            {
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Destination(entry.Name.TrimEnd('/') + "/", prefix);
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        if (Destination(entry.Name, prefix) is { } destination)
                            entry.ExtractToFile(destination, overwrite: true);
                        break;
                    default:
                        // 链接、设备文件等不安装
                        Log.Warn($"跳过安装包里的 {entry.EntryType}：{entry.Name}");
                        break;
                }
            });
            return files;
        }

        using var zip = ZipFile.OpenRead(packagePath);
        var zipPrefix = CommonTopFolder(zip.Entries.Select(e => e.FullName));
        foreach (var entry in zip.Entries)
        {
            // .NET 在 macOS / Linux 上解压时按 zip 里记录的权限设置文件权限
            if (Destination(entry.FullName, zipPrefix) is { } destination)
                entry.ExtractToFile(destination, overwrite: true);
        }
        return files;
    }

    private static bool IsGzip(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 0x1F && stream.ReadByte() == 0x8B;
    }

    private static void ReadTar(string path, Action<TarEntry> action)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                continue;
            action(entry);
        }
    }

    private static string Normalize(string name)
    {
        name = name.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
            name = name[2..];
        return name;
    }

    /// <summary>macOS 打包时可能带上的元数据（__MACOSX/、._ 开头的文件、.DS_Store），不安装。</summary>
    private static bool IsMacMetadata(string relative)
    {
        if (relative.StartsWith("__MACOSX/", StringComparison.Ordinal))
            return true;
        var name = Path.GetFileName(relative.TrimEnd('/'));
        return name.StartsWith("._", StringComparison.Ordinal) || name == ".DS_Store";
    }

    /// <summary>更新或修复后：删除旧版本有、新版本没有的文件。</summary>
    public static void RemoveStale(InstallRecord previous, IReadOnlyCollection<string> current, string installDir)
    {
        var keep = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        var sameDir = string.Equals(Path.GetFullPath(previous.Path), Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase);
        DeleteFiles(previous.Path, previous.Files.Where(f => !sameDir || !keep.Contains(f)));
    }

    /// <summary>卸载：删除记录的文件和留下的空目录。用户自己放进去的文件保留。</summary>
    public static void Uninstall(InstallRecord record) => DeleteFiles(record.Path, record.Files);

    private static void DeleteFiles(string root, IEnumerable<string> files)
    {
        var rootPath = Path.GetFullPath(root);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(rootPath, file));
            if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(path))
                File.Delete(path);
            for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length > rootPath.Length; dir = Path.GetDirectoryName(dir))
                directories.Add(dir);
        }

        // 从深到浅删除空目录，最后是安装目录本身
        foreach (var dir in directories.OrderByDescending(d => d.Length).Append(rootPath))
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    /// <summary>所有条目共同的顶层目录（带结尾的 /）；没有共同的顶层目录，或它是 macOS 的 .app 包时返回空串。</summary>
    private static string CommonTopFolder(IEnumerable<string> names)
    {
        string? top = null;
        foreach (var raw in names)
        {
            var name = Normalize(raw);
            if (name.Length == 0 || IsMacMetadata(name))
                continue;
            var slash = name.IndexOf('/');
            if (slash <= 0)
                return "";
            var folder = name[..(slash + 1)];
            if (top is null)
                top = folder;
            else if (!string.Equals(top, folder, StringComparison.Ordinal))
                return "";
        }
        return top is null || top.EndsWith(".app/", StringComparison.OrdinalIgnoreCase) ? "" : top;
    }
}

/// <summary>用系统默认方式打开网址、文件夹或程序（Windows 是 ShellExecute，macOS 是 open，Linux 是 xdg-open）。</summary>
public static class Shell
{
    public static void Open(string target, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(target) { UseShellExecute = true };
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// 启动已安装的程序。macOS 的 .app 包用 open 打开；macOS / Linux 的程序文件直接运行
    /// （交给 xdg-open 会被当成普通文件），运行前补上可执行权限。
    /// </summary>
    public static void Launch(string target, string workingDirectory)
    {
        if (OperatingSystem.IsWindows() || Directory.Exists(target))
        {
            Open(target, workingDirectory);
            return;
        }
        PlatformInfo.EnsureExecutable(target);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = workingDirectory })?.Dispose();
    }
}
