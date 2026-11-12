using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

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

    /// <summary>.part 对应的服务器文件标识（强 ETag 或 Last-Modified），续传时用 If-Range 确认还是同一个文件。</summary>
    private static string ValidatorPath(string zipPath) => PartialPath(zipPath) + ".id";

    /// <summary>删除下载了一半的文件及其标识。</summary>
    public static void DeletePartial(string zipPath)
    {
        foreach (var path in new[] { PartialPath(zipPath), ValidatorPath(zipPath) })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 删不掉不影响 */ }
        }
    }

    /// <summary>
    /// 下载到 <c>.part</c> 文件，完成后改名为 <paramref name="target"/>。已有 <c>.part</c> 时用 Range 续传；
    /// 服务器不支持续传时从头下载。<paramref name="speedLimit"/> 返回本下载当前的限速（字节/秒，0 为不限）。
    /// <para>
    /// 写入数据之前先确认服务器给的还是要下载的那个文件：旧版本的下载地址会被重定向到最新发布，
    /// 大小与发布信息（<paramref name="expectedSize"/>）不符时抛出 <see cref="ReleaseChangedException"/>，调用方应重新获取发布信息；
    /// 续传时核对 Content-Range，并用 If-Range 让服务器在文件变了时返回完整内容，不把两个版本的数据拼在一起。
    /// </para>
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
        var validatorPath = ValidatorPath(target);
        for (var attempt = 0; ; attempt++)
        {
            var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            var validator = offset > 0 ? ReadValidator(validatorPath) : null;
            // 大小未知又没有文件标识时无法确认 .part 属于同一个文件，从头下载
            if (offset > 0 && (expectedSize > 0 ? offset >= expectedSize : validator is null))
            {
                DeletePartial(target);
                offset = 0;
                validator = null;
            }

            using var response = await CanApi.Shared.GetDownloadAsync(url, offset, validator, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 缓存的半截文件与服务器上的对不上，从头下载
                DeletePartial(target);
                if (attempt == 0)
                    continue;
                throw new InvalidDataException("服务器拒绝了续传请求，请重试。");
            }

            var partial = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            long total;
            if (partial)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is not { HasRange: true } || range.From != offset)
                {
                    // 续传的位置不对，不能接在 .part 后面
                    DeletePartial(target);
                    if (attempt == 0)
                        continue;
                    throw new InvalidDataException("服务器返回的续传数据与已下载的部分不一致，请重试。");
                }
                total = range.Length ?? (response.Content.Headers.ContentLength is { } rest ? offset + rest : expectedSize);
            }
            else
            {
                offset = 0;
                total = response.Content.Headers.ContentLength ?? expectedSize;
            }

            if (expectedSize > 0 && total > 0 && total != expectedSize)
            {
                // 一个字节也不写：服务器上已经是另一个发布（例如旧版本地址被重定向到新版本）
                DeletePartial(target);
                Log.Warn($"{url} 的大小 {total} 与发布信息 {expectedSize} 不符，发布可能已更新");
                throw new ReleaseChangedException(expectedSize, total);
            }

            if (!partial)
                SaveValidator(validatorPath, response);
            progress.Reset(offset, total);

            await using (var file = new FileStream(part, partial ? FileMode.Append : FileMode.Create, FileAccess.Write,
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
                DeletePartial(target);
                throw new InvalidDataException($"下载的文件大小不符（{size} / {expectedSize} 字节），请重试。");
            }
            File.Move(part, target, overwrite: true);
            DeletePartial(target);
            return;
        }
    }

    /// <summary>记下服务器文件的标识，只用强 ETag 或 Last-Modified（If-Range 不接受弱 ETag）。</summary>
    private static void SaveValidator(string path, HttpResponseMessage response)
    {
        var value = response.Headers.ETag is { IsWeak: false } etag ? "etag " + etag.Tag
            : response.Content.Headers.LastModified is { } modified ? "date " + modified.ToString("R")
            : null;
        try
        {
            if (value is null)
                File.Delete(path);
            else
                File.WriteAllText(path, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 没有标识时只是不能续传 */ }
    }

    private static RangeConditionHeaderValue? ReadValidator(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            if (text.StartsWith("etag ", StringComparison.Ordinal))
                return new RangeConditionHeaderValue(text[5..]);
            if (text.StartsWith("date ", StringComparison.Ordinal)
                && DateTimeOffset.TryParse(text[5..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return new RangeConditionHeaderValue(date);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { /* 当作没有标识 */ }
        return null;
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
        var buffer = new byte[BufferSize];
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            // 解压时不核对 CRC32：存储（不压缩）的条目内容被改了也能正常读出，这里逐个计算并核对
            var crc = Crc32.Initial;
            long length = 0;
            using (var stream = entry.Open())
            {
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    crc = Crc32.Append(crc, buffer.AsSpan(0, read));
                    length += read;
                }
            }
            if (length != entry.Length || Crc32.Finish(crc) != entry.Crc32)
                throw new InvalidDataException($"安装包已损坏：{entry.FullName} 校验失败。");
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

/// <summary>服务器上的安装包与发布信息不符（发布已更新），需要重新获取发布信息后再下载。</summary>
public sealed class ReleaseChangedException(long expected, long actual)
    : Exception($"服务器上的安装包已更新（{actual} 字节，发布信息为 {expected} 字节），需要重新获取发布信息。");

/// <summary>zip 使用的 CRC-32（IEEE 802.3，多项式 0xEDB88320）。</summary>
internal static class Crc32
{
    public const uint Initial = 0xFFFFFFFF;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;
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
