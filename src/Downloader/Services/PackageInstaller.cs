using System.Diagnostics;
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

/// <summary>安装包的下载（断点续传、限速）、校验、解压与卸载。CAN 客户端的安装包是 zip，里面是一个与软件同名的目录。</summary>
public static class PackageInstaller
{
    private const int BufferSize = 81920;

    public static string CachePath(string cacheFolder, string id, string version) =>
        Path.Combine(cacheFolder, $"{id}-{version}.zip");

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

    /// <summary>完整读取每个文件，确认压缩包没有损坏。损坏时抛出 <see cref="InvalidDataException"/>。</summary>
    public static void Verify(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            using var stream = entry.Open();
            stream.CopyTo(Stream.Null);
        }
    }

    /// <summary>
    /// 解压到 <paramref name="installDir"/>，覆盖同名文件；压缩包只有一个顶层目录时去掉这一层。
    /// 返回解压出的文件（相对安装目录）。
    /// </summary>
    public static List<string> Extract(string zipPath, string installDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var prefix = CommonTopFolder(zip.Entries);
        var root = Path.GetFullPath(installDir);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        var files = new List<string>();
        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
            if (relative.Length == 0)
                continue;
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            // 防止压缩包里的 ../ 写到安装目录之外
            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"安装包包含非法路径：{entry.FullName}");

            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            files.Add(relative);
        }
        return files;
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

    private static string CommonTopFolder(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        string? top = null;
        foreach (var entry in entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');
            if (slash <= 0)
                return "";
            var folder = name[..(slash + 1)];
            if (top is null)
                top = folder;
            else if (!string.Equals(top, folder, StringComparison.Ordinal))
                return "";
        }
        return top ?? "";
    }
}

/// <summary>用系统默认方式打开网址、文件夹或程序。</summary>
public static class Shell
{
    public static void Open(string target, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(target) { UseShellExecute = true };
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        Process.Start(info)?.Dispose();
    }
}
