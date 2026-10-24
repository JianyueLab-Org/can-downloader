using System.IO.Compression;
using System.Text.Json;
using Downloader.Services;

// This executable owns only its output directory's data/ and the repository's .temp/installer-checks/.
// Run checks sequentially. No network, credentials, or GUI is used.
var cases = new (string Name, Action<Fixture> Run)[]
{
    ("SuccessfulUpdateRemovesStaleAndPreservesUserFiles", f =>
    {
        f.Seed();
        f.Zip(("client/a.txt", "new-a"), ("client/nested/new.txt", "new-file"));
        var installed = f.Install();
        f.Content("a.txt", "new-a");
        f.Content("nested/new.txt", "new-file");
        f.Content("user.txt", "user-data");
        Check(!File.Exists(f.PathFor("stale.txt")), "stale file survived update");
        Check(installed.Version == "2.0.0", "returned version was not committed");
        f.Record("2.0.0", f.Root, "a.txt", "nested/new.txt");
        PackageInstaller.Uninstall(installed);
        f.Content("user.txt", "user-data");
        Check(!File.Exists(f.PathFor("a.txt")), "uninstall left managed file");
    }),
    ("ReplacementDirectoryConflictRestoresOverwrittenFile", f =>
    {
        f.Seed();
        Directory.CreateDirectory(f.PathFor("b.txt"));
        File.WriteAllText(f.PathFor("b.txt/user.txt"), "inside-conflict");
        f.Zip(("client/a.txt", "new-a"), ("client/new.txt", "new-file"), ("client/b.txt", "new-b"));
        f.ExpectFailure();
        f.OldState();
        f.Content("b.txt/user.txt", "inside-conflict");
        Check(!File.Exists(f.PathFor("new.txt")), "new file leaked after replacement failure");
    }),
    ("StaleRemovalFailureRestoresEarlierDeletedFiles", f =>
    {
        f.Seed("stale.txt", "blocked.txt");
        File.Delete(f.PathFor("blocked.txt"));
        Directory.CreateDirectory(f.PathFor("blocked.txt"));
        File.WriteAllText(f.PathFor("blocked.txt/user.txt"), "keep-me");
        f.Zip(("client/a.txt", "new-a"), ("client/new.txt", "new-file"));
        f.ExpectFailure();
        f.OldState();
        f.Content("blocked.txt/user.txt", "keep-me");
        Check(!File.Exists(f.PathFor("new.txt")), "new file leaked after stale removal failure");
    }),
    ("RecordPersistenceFailureRestoresFilesAndRecord", f =>
    {
        f.Seed();
        f.Zip(("client/a.txt", "new-a"), ("client/new/deep.txt", "new-file"));
        Directory.CreateDirectory(f.RecordPath + ".tmp");
        try { f.ExpectFailure(); }
        finally { Directory.Delete(f.RecordPath + ".tmp"); }
        f.OldState();
        Check(!Directory.Exists(f.PathFor("new")), "new directories leaked after rollback");
    }),
    ("FirstInstallFailureLeavesNoRecordOrManagedFiles", f =>
    {
        Directory.CreateDirectory(f.Root);
        File.WriteAllText(f.PathFor("user.txt"), "user-data");
        Directory.CreateDirectory(f.PathFor("b.txt"));
        f.Zip(("client/a.txt", "new-a"), ("client/b.txt", "new-b"));
        f.ExpectFailure();
        Check(LocalInstalls.Find(f.Id) is null, "failed first install registered in memory");
        Check(!f.DiskRecords().Any(r => r.Id == f.Id), "failed first install registered on disk");
        Check(!File.Exists(f.PathFor("a.txt")), "first install left managed file");
        f.Content("user.txt", "user-data");
    }),
    ("FirstInstallRecordFailureRemovesNewInstallationDirectory", f =>
    {
        f.Zip(("client/a.txt", "new-a"));
        var oldRecords = File.Exists(f.RecordPath) ? File.ReadAllBytes(f.RecordPath) : null;
        Directory.CreateDirectory(f.RecordPath + ".tmp");
        try { f.ExpectFailure(); }
        finally { Directory.Delete(f.RecordPath + ".tmp"); }
        Check(!Directory.Exists(f.Root), "failed first install left its new directory");
        Check(LocalInstalls.Find(f.Id) is null, "failed first install registered in memory");
        Check(!f.DiskRecords().Any(r => r.Id == f.Id), "failed first install registered on disk");
        Check(oldRecords is null ? !File.Exists(f.RecordPath) : oldRecords.SequenceEqual(File.ReadAllBytes(f.RecordPath)),
            "failed first install changed other installation records");
    }),
    ("FirstInstallCommitsFilesAndRecord", f =>
    {
        f.Zip(("client/a.txt", "new-a"));
        f.Install();
        f.Content("a.txt", "new-a");
        f.Record("2.0.0", f.Root, "a.txt");
    }),
    ("RepairRestoresMissingFiles", f =>
    {
        f.Seed();
        File.Delete(f.PathFor("a.txt"));
        f.Zip(("client/a.txt", "repaired-a"), ("client/stale.txt", "repaired-stale"));
        PackageInstaller.Install(f.ZipPath, f.Id, "1.0.0", f.Root);
        f.Content("a.txt", "repaired-a");
        f.Content("user.txt", "user-data");
        f.Record("1.0.0", f.Root, "a.txt", "stale.txt");
    }),
    ("PathChangeCommitsNewLocationAndPreservesOldUserFiles", f =>
    {
        f.Seed();
        f.Zip(("client/a.txt", "new-a"));
        var destination = f.Root + "-moved";
        PackageInstaller.Install(f.ZipPath, f.Id, "2.0.0", destination);
        Check(File.ReadAllText(Path.Combine(destination, "a.txt")) == "new-a", "new location missing package");
        Check(!File.Exists(f.PathFor("a.txt")), "old managed file survived move");
        f.Content("user.txt", "user-data");
        f.Record("2.0.0", destination, "a.txt");
    }),
    ("PathChangeRecordFailureRestoresBothLocations", f =>
    {
        f.Seed();
        f.Zip(("client/a.txt", "new-a"));
        var destination = f.Root + "-moved";
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "user.txt"), "destination-user");
        Directory.CreateDirectory(f.RecordPath + ".tmp");
        try { Throws(() => PackageInstaller.Install(f.ZipPath, f.Id, "2.0.0", destination)); }
        finally { Directory.Delete(f.RecordPath + ".tmp"); }
        f.OldState();
        Check(!File.Exists(Path.Combine(destination, "a.txt")), "new location leaked managed file");
        Check(File.ReadAllText(Path.Combine(destination, "user.txt")) == "destination-user", "destination user file changed");
    }),
    ("PathChangeRecordFailureRecreatesRemovedOldDirectory", f =>
    {
        f.Seed();
        File.Delete(f.PathFor("user.txt"));
        var oldRecords = File.ReadAllBytes(f.RecordPath);
        f.Zip(("client/a.txt", "new-a"));
        var destination = f.Root + "-moved";
        Directory.CreateDirectory(f.RecordPath + ".tmp");
        try { Throws(() => PackageInstaller.Install(f.ZipPath, f.Id, "2.0.0", destination)); }
        finally { Directory.Delete(f.RecordPath + ".tmp"); }
        f.Content("a.txt", "old-a");
        f.Content("stale.txt", "old-stale.txt");
        f.Record("1.0.0", f.Root, "a.txt", "stale.txt");
        Check(oldRecords.SequenceEqual(File.ReadAllBytes(f.RecordPath)), "old record bytes changed");
        Check(!Directory.Exists(destination), "failed move left a new installation directory");
    }),
    ("UnmanagedFileCollisionIsNotOverwritten", f =>
    {
        f.Seed();
        File.WriteAllText(f.PathFor("b.txt"), "user-b");
        f.Zip(("client/a.txt", "new-a"), ("client/b.txt", "new-b"));
        f.ExpectFailure();
        f.OldState();
        f.Content("b.txt", "user-b");
    }),
    ("InvalidArchivePathDoesNotTouchInstallation", f =>
    {
        f.Seed();
        f.Zip(("client/a.txt", "new-a"), ("client/../../escape.txt", "escape"));
        f.ExpectFailure();
        f.OldState();
        Check(!File.Exists(Path.Combine(Path.GetDirectoryName(f.Root)!, "escape.txt")), "archive escaped destination");
    }),
    ("ArchiveConflictIsStagedBeforeLiveReplacement", f =>
    {
        f.Seed();
        f.Zip(("client/a.txt", "new-a"), ("client/b", "file"), ("client/b/c.txt", "child"));
        f.ExpectFailure();
        f.OldState();
    })
};

if (OperatingSystem.IsWindows())
{
    cases = [.. cases,
        ("WindowsLockedReplacementRollsBackEarlierReplacement", f =>
        {
            f.Seed();
            f.Zip(("client/a.txt", "new-a"), ("client/stale.txt", "new-stale"));
            using (File.Open(f.PathFor("stale.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                f.ExpectFailure();
            f.OldState();
        }),
        ("WindowsLockedRecordRestoresFilesAndRecord", f =>
        {
            f.Seed();
            f.Zip(("client/a.txt", "new-a"), ("client/new.txt", "new-file"));
            using (File.Open(f.RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                f.ExpectFailure();
            f.OldState();
            Check(!File.Exists(f.PathFor("new.txt")), "new file leaked after record replacement failure");
        }),
        ("WindowsLockedStaleFileRestoresEarlierRemoval", f =>
        {
            f.Seed("stale.txt", "locked.txt");
            f.Zip(("client/a.txt", "new-a"), ("client/new.txt", "new-file"));
            using (File.Open(f.PathFor("locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                f.ExpectFailure();
            f.OldState();
            f.Content("locked.txt", "old-locked.txt");
            Check(!File.Exists(f.PathFor("new.txt")), "new file leaked after locked stale removal");
        })];
}
else
    Console.WriteLine("SKIP WindowsLockedReplacementRollsBackEarlierReplacement, WindowsLockedRecordRestoresFilesAndRecord, WindowsLockedStaleFileRestoresEarlierRemoval (requires Windows file sharing semantics)");

var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var scratch = Path.Combine(repository, ".temp", "installer-checks", Guid.NewGuid().ToString("N"));
var data = Path.Combine(AppContext.BaseDirectory, "data");
if (Directory.Exists(data))
    throw new InvalidOperationException($"Refusing to modify pre-existing test data: {data}. Remove only this test executable's data directory before running.");
var failures = 0;
try
{
    foreach (var (name, run) in cases)
    {
        var fixture = new Fixture(Path.Combine(scratch, name), name);
        try
        {
            run(fixture);
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {name}: {ex}");
        }
    }
}
finally
{
    if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
    if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
}
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
    throw new InvalidOperationException("Expected installation to fail");
}

sealed class Fixture
{
    public string Root { get; }
    public string Id { get; }
    public string ZipPath { get; }
    public string RecordPath => Path.Combine(LocalData.Root, "installs.json");
    private byte[]? _oldRecord;

    public Fixture(string root, string id)
    {
        Root = Path.Combine(root, "app");
        Id = id;
        ZipPath = Path.Combine(root, "package.zip");
        Directory.CreateDirectory(root);
    }

    public string PathFor(string path) => Path.Combine(Root, path);

    public void Seed(params string[] stale)
    {
        if (stale.Length == 0) stale = ["stale.txt"];
        Directory.CreateDirectory(Root);
        File.WriteAllText(PathFor("a.txt"), "old-a");
        File.WriteAllText(PathFor("user.txt"), "user-data");
        foreach (var path in stale) File.WriteAllText(PathFor(path), "old-" + path);
        LocalInstalls.Set(new InstallRecord
        {
            Id = Id, Version = "1.0.0", Path = Root, Files = ["a.txt", .. stale],
            InstalledAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z")
        });
        _oldRecord = File.ReadAllBytes(RecordPath);
    }

    public void Zip(params (string Path, string Content)[] entries)
    {
        using var archive = ZipFile.Open(ZipPath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open());
            writer.Write(content);
        }
    }

    public InstallRecord Install() => PackageInstaller.Install(ZipPath, Id, "2.0.0", Root);

    public void ExpectFailure()
    {
        try { Install(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
        throw new InvalidOperationException("Expected installation to fail");
    }

    public void Content(string path, string expected)
    {
        if (File.ReadAllText(PathFor(path)) != expected)
            throw new InvalidOperationException($"Unexpected contents: {path}");
    }

    public List<InstallRecord> DiskRecords() => File.Exists(RecordPath)
        ? JsonSerializer.Deserialize<List<InstallRecord>>(File.ReadAllText(RecordPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
        : [];

    public void Record(string version, string path, params string[] files)
    {
        foreach (var record in new[] { LocalInstalls.Find(Id), DiskRecords().SingleOrDefault(r => r.Id == Id) })
            if (record is null || record.Version != version || record.Path != path || !record.Files.Order().SequenceEqual(files.Order()))
                throw new InvalidOperationException("Installation record does not match committed files");
    }

    public void OldState()
    {
        Content("a.txt", "old-a");
        Content("stale.txt", "old-stale.txt");
        Content("user.txt", "user-data");
        var memory = LocalInstalls.Find(Id);
        if (memory is null || memory.Path != Root || memory.InstalledAt != DateTimeOffset.Parse("2026-10-01T00:00:00Z")
            || !memory.Files.Contains("a.txt") || !memory.Files.Contains("stale.txt"))
            throw new InvalidOperationException("Failed installation changed the in-memory record");
        if (LocalInstalls.Find(Id)?.Version != "1.0.0" || !_oldRecord!.SequenceEqual(File.ReadAllBytes(RecordPath)))
            throw new InvalidOperationException("Failed installation changed the old record");
    }
}
