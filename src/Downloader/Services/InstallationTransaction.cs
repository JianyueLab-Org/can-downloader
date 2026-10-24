using System.Text.Json;

namespace Downloader.Services;

/// <summary>暂存完整解压结果；先备份所有受影响文件，再提交。记录的事务号是提交完成标志。</summary>
public static class InstallationTransaction
{
    public sealed class Change
    {
        public string Target { get; set; } = "";
        public string? Backup { get; set; }
        public string? Source { get; set; }
    }

    public sealed class Journal
    {
        public string Id { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string Work { get; set; } = "";
        public List<string> InstallRoots { get; set; } = [];
        public List<Change> Changes { get; set; } = [];
        public List<string> CreatedDirectories { get; set; } = [];
    }

    private static string JournalRoot => Path.Combine(LocalData.Root, "transactions");
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Install(string package, ReleaseSnapshot release)
    {
        lock (LocalInstalls.Sync)
        {
            var previous = LocalInstalls.Find(release.Id);
            // 上一次恢复失败时不能继续覆盖备份涉及的目录。
            Recover();
            PlatformInfo.ThrowIfRunning(release.InstallPath);
            if (previous is not null) PlatformInfo.ThrowIfRunning(previous.Path);
            var root = Path.GetFullPath(release.InstallPath);
            var parent = Path.GetDirectoryName(root) ?? throw new IOException("不能安装到文件系统根目录。");
            Directory.CreateDirectory(parent);
            var id = Guid.NewGuid().ToString("N");
            var journal = new Journal { Id = id, ProductId = release.Id, Work = Path.Combine(parent, $".can-install-{id}") };
            journal.InstallRoots.Add(root);
            if (previous is not null) journal.InstallRoots.Add(previous.Path);
            var journalPath = Path.Combine(JournalRoot, id + ".json");
            var prepared = false;
            var committed = false;
            try
            {
                var stage = Path.Combine(journal.Work, "stage");
                var files = PackageInstaller.Extract(package, stage);
                var changes = new Dictionary<string, Change>(Paths);
                foreach (var file in files)
                {
                    var target = SafePath(root, file);
                    changes.Add(target, new Change { Target = target, Source = SafePath(stage, file) });
                }
                if (previous is not null)
                    foreach (var file in previous.Files)
                    {
                        var target = SafePath(previous.Path, file);
                        changes.TryAdd(target, new Change { Target = target });
                    }

                var backupRoot = Path.Combine(journal.Work, "backup");
                Directory.CreateDirectory(backupRoot);
                foreach (var change in changes.Values)
                {
                    // 不穿过符号链接，也不覆盖用户的目录。冲突在修改任何正式文件前发现。
                    for (var path = change.Target; path is not null; path = Path.GetDirectoryName(path))
                        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException($"安装路径包含链接：{path}");
                    if (Directory.Exists(change.Target))
                        throw new IOException($"安装目标是目录，无法替换为文件：{change.Target}");
                    if (File.Exists(change.Target))
                    {
                        change.Backup = Path.Combine(backupRoot, journal.Changes.Count.ToString());
                    }
                    for (var dir = Path.GetDirectoryName(change.Target); dir is not null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
                    {
                        if (File.Exists(dir)) throw new IOException($"安装路径被文件占用：{dir}");
                        if (!journal.CreatedDirectories.Contains(dir, Paths)) journal.CreatedDirectories.Add(dir);
                    }
                    journal.Changes.Add(change);
                }

                Directory.CreateDirectory(JournalRoot);
                LocalData.Save(Path.Combine("transactions", id + ".json"), journal, throwOnError: true);
                prepared = true;
                PlatformInfo.ThrowIfRunning(root);
                if (previous is not null) PlatformInfo.ThrowIfRunning(previous.Path);
                foreach (var change in journal.Changes)
                {
                    // 移动原文件后才写入新文件；移动失败时原文件保持原样，恢复时也无需触碰它。
                    if (change.Backup is not null) File.Move(change.Target, change.Backup);
                    if (change.Source is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(change.Target)!);
                        File.Move(change.Source, change.Target);
                    }
                }
                LocalInstalls.Set(new InstallRecord
                {
                    Id = release.Id, Version = release.Version, Path = root, Files = files,
                    InstalledAt = DateTimeOffset.Now, TransactionId = id
                });
                committed = true;
            }
            catch (Exception failure)
            {
                if (prepared)
                {
                    try { Rollback(journal); }
                    catch (Exception rollback)
                    {
                        throw new IOException($"安装失败且恢复未完成，备份保留在 {journal.Work}，请关闭软件并重启下载器恢复。", new AggregateException(failure, rollback));
                    }
                }
                Cleanup(journal, journalPath);
                throw;
            }
            if (committed) Cleanup(journal, journalPath);
        }
    }

    /// <summary>在加载安装记录、过滤不存在的安装目录前恢复中断的事务。</summary>
    public static void Recover()
    {
        if (!Directory.Exists(JournalRoot)) return;
        var records = LocalData.Load<List<InstallRecord>>("installs.json") ?? [];
        foreach (var path in Directory.EnumerateFiles(JournalRoot, "*.json"))
        {
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                          ?? throw new IOException($"无法读取安装恢复日志：{path}");
            if (!records.Any(r => r.Id == journal.ProductId && r.TransactionId == journal.Id))
            {
                foreach (var root in journal.InstallRoots.Concat(journal.Changes.Select(c => Path.GetDirectoryName(c.Target)!)).Distinct(Paths))
                    PlatformInfo.ThrowIfRunning(root);
                Rollback(journal);
            }
            Cleanup(journal, path);
        }
    }

    private static void Rollback(Journal journal)
    {
        var failures = new List<Exception>();
        foreach (var change in journal.Changes.AsEnumerable().Reverse())
        {
            try
            {
                if (change.Backup is null) File.Delete(change.Target);
                else if (File.Exists(change.Backup))
                {
                    if (File.Exists(change.Target) && EqualFiles(change.Target, change.Backup)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Target)!);
                    File.Copy(change.Backup, change.Target, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("部分文件恢复失败，备份已保留。", failures);
        foreach (var directory in journal.CreatedDirectories.OrderByDescending(p => p.Length))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static bool EqualFiles(string left, string right)
    {
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        return a.Length == b.Length && System.Security.Cryptography.SHA256.HashData(a).AsSpan()
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    private static string SafePath(string root, string relative)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"安装包或安装记录包含非法路径：{relative}");
        return full;
    }

    private static void Cleanup(Journal journal, string path)
    {
        try
        {
            // 日志先删除：清理备份中断后不会把不完整备份用于回滚。
            File.Delete(path);
            if (Directory.Exists(journal.Work)) Directory.Delete(journal.Work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warn($"安装临时文件清理失败：{ex.Message}"); }
    }
}
