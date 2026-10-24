using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text.Json;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Downloader.Services;
using Downloader.ViewModels;

// 子进程在记录提交前退出，另一个新进程只凭磁盘日志恢复。
if (args.FirstOrDefault() == "--recover")
{
    InstallationTransaction.Recover();
    return;
}
if (args.FirstOrDefault() == "--interrupt")
{
    var dir = args[1];
    var work = args[2];
    Directory.CreateDirectory(work);
    var target = Path.Combine(dir, "a.txt");
    var backup = Path.Combine(work, "old-a");
    LocalData.Save("transactions/process-interrupted.json", new InstallationTransaction.Journal
    {
        Id = "process-interrupted", ProductId = args[3], Work = work, InstallRoots = [dir],
        Changes = [new() { Target = target, Backup = backup }, new() { Target = Path.Combine(dir, "new.txt") }]
    }, true);
    File.Move(target, backup);
    File.WriteAllText(target, "PARTIALLY_UPDATED");
    File.WriteAllText(Path.Combine(dir, "new.txt"), "UNTRACKED_NEW_FILE");
    Environment.Exit(91);
}

// 无真实账号、无线上请求；通过临时文件和真实安装器验证故障恢复。
var root = Path.Combine(Path.GetTempPath(), "can-installer-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
try
{
    foreach (var mode in new[] { "downloading", "queued", "running" })
    foreach (var withHash in new[] { false, true })
    {
    if (mode == "running" && !OperatingSystem.IsWindows()) continue;
    Run($"清单刷新模拟（状态={mode}, SHA256={withHash}）", () =>
    {
        var package = File.ReadAllBytes(Package(("a.txt", "VERSION_ONE")));
        using var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var server = new HttpListener();
        var address = $"http://127.0.0.1:{port}/";
        server.Prefixes.Add(address);
        server.Start();
        var received = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var serve = Task.Run(async () =>
        {
            var context = await server.GetContextAsync();
            context.Response.ContentLength64 = package.Length;
            await context.Response.OutputStream.WriteAsync(package.AsMemory(0, 1));
            await context.Response.OutputStream.FlushAsync();
            received.SetResult();
            await finish.Task;
            await context.Response.OutputStream.WriteAsync(package.AsMemory(1));
            context.Response.Close();
        });
        var id = "regression-manifest-" + mode + withHash;
        var settings = new SettingsViewModel { InstallRoot = root, CacheFolder = Path.Combine(root, "cache"), ProxyMode = "不使用代理" };
        UpdateManifest Manifest(string version, string? hash) => new()
        {
            Products = [new() { Id = id, Name = "测试", Version = version, DownloadUrl = address + "v" + version + ".zip", Sha256 = hash,
                Components = [new() { Id = "main", Required = true, Size = version == "1" ? package.Length : package.Length + 10 }] }]
        };
        var hash = withHash ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(package)) : null;
        var vm = new MainWindowViewModel(Manifest("1", hash), settings, new UserAccount("test", "测试", null, 0));
        var product = vm.Products.Single();
        ProductViewModel? blocker = null;
        if (mode == "queued")
        {
            settings.MaxConcurrentDownloads = 1;
            blocker = new ProductViewModel("blocker") { State = ProductState.Downloading };
            vm.Products.Add(blocker);
        }
        vm.Install(product, product.Components.ToList(), Path.Combine(root, id), false);
        if (mode == "queued") Check(product.State == ProductState.Queued);
        else Wait(() => received.Task.IsCompleted);
        typeof(MainWindowViewModel).GetMethod("ApplyManifest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [Manifest("2", withHash ? new string('0', 64) : null), false]);
        if (blocker is not null)
        {
            vm.Products.Remove(blocker);
            vm.TogglePause(product);
            Check(product.State == ProductState.Paused);
            vm.TogglePause(product);
            Wait(() => received.Task.IsCompleted);
        }
        Process? running = null;
        if (mode == "running")
        {
            Directory.CreateDirectory(product.InstallPath);
            var exe = Path.Combine(product.InstallPath, "running.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
            running = Process.Start(new ProcessStartInfo(exe, "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true })!;
            Thread.Sleep(200);
            Check(!running.HasExited);
        }
        try
        {
        finish.SetResult();
        Wait(() => !product.IsBusy);
        serve.GetAwaiter().GetResult();
        if (running is not null)
        {
            Check(!File.Exists(Path.Combine(product.InstallPath, "a.txt")));
            Check(LocalInstalls.Find(id) is null);
            Check(vm.Notices.Any(n => n.Text.Contains("正在运行")));
            return;
        }
        if (!File.Exists(Path.Combine(product.InstallPath, "a.txt")))
            throw new Exception("安装未产生预期文件：" + string.Join("；", vm.Notices.Select(n => n.Text)));
        Check(File.ReadAllText(Path.Combine(product.InstallPath, "a.txt")) == "VERSION_ONE");
        Check(LocalInstalls.Find(id)?.Version == "1");
        Check(product.LatestVersion == "2" && product.HasUpdate);
        }
        finally
        {
            if (running is not null)
            {
                if (!running.HasExited) running.Kill(true);
                running.WaitForExit();
                running.Dispose();
            }
        }
    });
    }
    Run("正常更新、清理旧文件、保留用户文件", () =>
    {
        var (release, dir) = Setup("success");
        InstallationTransaction.Install(Package(("a.txt", "NEW")), release);
        Check(File.ReadAllText(Path.Combine(dir, "a.txt")) == "NEW");
        Check(!File.Exists(Path.Combine(dir, "stale.txt")));
        Check(File.ReadAllText(Path.Combine(dir, "user.txt")) == "USER");
        Check(LocalInstalls.Find(release.Id)?.Version == "2");
    });
    Run("tar.gz 安装与 Unix 可执行权限", () =>
    {
        var (release, dir) = Setup("tar");
        var path = Path.Combine(root, "package.tar.gz");
        using (var output = File.Create(path))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "app/a.txt")
            {
                DataStream = new MemoryStream("TAR"u8.ToArray()),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            };
            tar.WriteEntry(entry);
        }
        InstallationTransaction.Install(path, release with { Extension = ".tar.gz" });
        var target = Path.Combine(dir, "a.txt");
        Check(File.ReadAllText(target) == "TAR");
        if (!OperatingSystem.IsWindows()) Check((File.GetUnixFileMode(target) & UnixFileMode.UserExecute) != 0);
    });
    if (!OperatingSystem.IsWindows()) Run("允许安装目录祖先链接并拒绝目录内链接", () =>
    {
        var (release, dir) = Setup("ancestor-link");
        var alias = Path.Combine(root, "root-alias");
        Directory.CreateSymbolicLink(alias, root);
        try
        {
            var aliased = release with { InstallPath = Path.Combine(alias, "ancestor-link") };
            // 避免同一个目录的两个文本路径形成重复更新，本场景记录也使用别名。
            LocalInstalls.Set(new InstallRecord { Id = release.Id, Version = "1", Path = aliased.InstallPath, Files = ["a.txt", "stale.txt"] });
            InstallationTransaction.Install(Package(("a.txt", "NEW")), aliased);
            Check(File.ReadAllText(Path.Combine(dir, "a.txt")) == "NEW");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(dir, "linked"), outside);
            Throws(() => InstallationTransaction.Install(Package(("linked/file.txt", "BAD")), aliased));
            Check(!File.Exists(Path.Combine(outside, "file.txt")));
        }
        finally { Directory.Delete(alias); }
    });
    Run("目录冲突不改动旧安装", () =>
    {
        var (release, dir) = Setup("conflict");
        Directory.CreateDirectory(Path.Combine(dir, "b.txt"));
        Throws(() => InstallationTransaction.Install(Package(("a.txt", "NEW"), ("b.txt", "NEW")), release));
        Old(release, dir);
    });
    if (OperatingSystem.IsWindows()) Run("记录提交失败回滚新增、覆盖和旧文件删除", () =>
    {
        var (release, dir) = Setup("record-failure");
        using (var locked = new FileStream(Path.Combine(LocalData.Root, "installs.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => InstallationTransaction.Install(Package(("a.txt", "NEW"), ("new.txt", "NEW")), release));
        Old(release, dir);
        Check(!File.Exists(Path.Combine(dir, "new.txt")));
    });
    if (OperatingSystem.IsWindows()) Run("Windows 后续文件独占锁定回滚前续替换", () =>
    {
        var (release, dir) = Setup("locked");
        using (var locked = new FileStream(Path.Combine(dir, "stale.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws(() => InstallationTransaction.Install(Package(("a.txt", "NEW"), ("stale.txt", "NEW")), release));
        Old(release, dir);
    });
    Run("事务日志恢复且恢复可重复执行", () =>
    {
        var (release, dir) = Setup("recover");
        var work = Path.Combine(root, "recovery-work");
        Directory.CreateDirectory(work);
        var backup = Path.Combine(work, "a.bak");
        File.Move(Path.Combine(dir, "a.txt"), backup);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "NEW");
        var journal = new InstallationTransaction.Journal
        {
            Id = "interrupted", ProductId = release.Id, Work = work,
            Changes = [new() { Target = Path.Combine(dir, "a.txt"), Backup = backup }]
        };
        LocalData.Save("transactions/interrupted.json", journal, true);
        InstallationTransaction.Recover();
        InstallationTransaction.Recover();
        Old(release, dir);
    });
    Run("已提交事务重启恢复保留新版", () =>
    {
        var (release, dir) = Setup("committed");
        var work = Path.Combine(root, "committed-work");
        Directory.CreateDirectory(work);
        var backup = Path.Combine(work, "a.bak");
        File.Move(Path.Combine(dir, "a.txt"), backup);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "NEW");
        LocalInstalls.Set(new InstallRecord { Id = release.Id, Version = "2", Path = dir,
            TransactionId = "committed", Files = ["a.txt"] });
        LocalData.Save("transactions/committed.json", new InstallationTransaction.Journal
        {
            Id = "committed", ProductId = release.Id, Work = work,
            Changes = [new() { Target = Path.Combine(dir, "a.txt"), Backup = backup }]
        }, true);
        InstallationTransaction.Recover();
        Check(File.ReadAllText(Path.Combine(dir, "a.txt")) == "NEW");
        Check(LocalInstalls.Find(release.Id)?.Version == "2");
        Check(!Directory.Exists(work));
    });
    Run("准备后未修改的事务恢复不删除旧文件", () =>
    {
        var (release, dir) = Setup("prepared");
        var work = Path.Combine(root, "prepared-work");
        Directory.CreateDirectory(work);
        LocalData.Save("transactions/prepared.json", new InstallationTransaction.Journal
        {
            Id = "prepared", ProductId = release.Id, Work = work,
            Changes = [new() { Target = Path.Combine(dir, "a.txt"), Backup = Path.Combine(work, "not-yet-moved") }]
        }, true);
        InstallationTransaction.Recover();
        Old(release, dir);
    });
    Run("独立进程中断后由新进程从磁盘恢复", () =>
    {
        var (release, dir) = Setup("process-recovery");
        var work = Path.Combine(root, "process-work");
        Check(Child("--interrupt", dir, work, release.Id) == 91);
        Check(File.ReadAllText(Path.Combine(dir, "a.txt")) == "PARTIALLY_UPDATED");
        Check(File.Exists(Path.Combine(dir, "new.txt")));
        Check(Child("--recover") == 0);
        Old(release, dir);
        Check(!File.Exists(Path.Combine(dir, "new.txt")));
        Check(!Directory.Exists(work));
        var saved = LocalData.Load<List<InstallRecord>>("installs.json")!.Single(r => r.Id == release.Id);
        Check(saved.Version == "1");
    });
    Run("缓存身份隔离版本、地址和校验值", () =>
    {
        var snapshot = new ReleaseSnapshot("snapshot", "1", new Uri("https://example.test/v1.zip"), null, null, "v1.zip", 1, ".zip", root);
        Check(snapshot.CachePath(root) != (snapshot with { Version = "2" }).CachePath(root));
        Check(snapshot.CachePath(root) != (snapshot with { Url = new Uri("https://example.test/other.zip") }).CachePath(root));
        Check(snapshot.CachePath(root) != (snapshot with { Sha256 = "new" }).CachePath(root));
        Check(snapshot.Version == "1");
    });
    if (OperatingSystem.IsWindows()) Run("运行中的真实进程阻止安装", () =>
    {
        var (release, dir) = Setup("running");
        var exe = Path.Combine(dir, "running.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        using var process = Process.Start(new ProcessStartInfo(exe, "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true })!;
        try
        {
            Thread.Sleep(300);
            Check(!process.HasExited);
            Throws(() => InstallationTransaction.Install(Package(("a.txt", "NEW")), release));
            Old(release, dir);
            var manifest = new UpdateManifest { Products = [new() { Id = release.Id, Name = "测试", Version = "2", DownloadUrl = "https://example.test/package.zip" }] };
            var vm = new MainWindowViewModel(manifest, new SettingsViewModel(), new UserAccount("test", "测试", null, 0));
            var product = vm.Products.Single();
            vm.Update(product);
            Check(!product.IsBusy && vm.Notices.Any(n => n.Text.Contains("正在运行")));
            vm.Repair(product);
            Check(!product.IsBusy && vm.Notices.Count == 2);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); }
    });
    Console.WriteLine($"PASS: {count} regression scenarios");
}
finally
{
    foreach (var record in LocalData.Load<List<InstallRecord>>("installs.json") ?? [])
        if (record.Id.StartsWith("regression-", StringComparison.Ordinal)) LocalInstalls.Remove(record.Id);
    Directory.Delete(root, true);
}

void Run(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
int Child(params string[] arguments)
{
    var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var child = Process.Start(info)!;
    if (!child.WaitForExit(15000)) { child.Kill(true); throw new TimeoutException("恢复子进程超时"); }
    return child.ExitCode;
}
void Wait(Func<bool> condition)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        Dispatcher.UIThread.RunJobs();
        if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("等待异步流程超时");
        Thread.Sleep(10);
    }
}
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Throws(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
    throw new Exception("Expected IOException");
}
(ReleaseSnapshot, string) Setup(string name)
{
    var dir = Path.Combine(root, name);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "a.txt"), "OLD");
    File.WriteAllText(Path.Combine(dir, "stale.txt"), "STALE");
    File.WriteAllText(Path.Combine(dir, "user.txt"), "USER");
    var id = "regression-" + name;
    LocalInstalls.Set(new InstallRecord { Id = id, Version = "1", Path = dir, Files = ["a.txt", "stale.txt"] });
    return (new ReleaseSnapshot(id, "2", new Uri("https://example.test/package.zip"), null, null, "package.zip", 0, ".zip", dir), dir);
}
void Old(ReleaseSnapshot release, string dir)
{
    Check(File.ReadAllText(Path.Combine(dir, "a.txt")) == "OLD");
    Check(File.ReadAllText(Path.Combine(dir, "stale.txt")) == "STALE");
    Check(LocalInstalls.Find(release.Id)?.Version == "1");
}
string Package(params (string Name, string Content)[] entries)
{
    var path = Path.Combine(root, Guid.NewGuid() + ".zip");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (name, content) in entries)
    {
        using var writer = new StreamWriter(zip.CreateEntry("app/" + name).Open());
        writer.Write(content);
    }
    return path;
}
