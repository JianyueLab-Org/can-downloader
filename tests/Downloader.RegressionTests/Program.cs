using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text.Json;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Downloader.Services;
using Downloader.ViewModels;

// 子进程在记录提交前退出，另一个新进程只凭磁盘日志恢复。
if (args.FirstOrDefault() == "--recover")
{
    InstallationTransaction.Recover();
    return;
}
// 单实例：取得锁后保持指定毫秒数；退出码 0 表示取得了锁，1 表示已有实例。
if (args.FirstOrDefault() == "--instance")
{
    if (!SingleInstance.TryAcquire())
        Environment.Exit(1);
    Thread.Sleep(int.Parse(args[1]));
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
    Run("C# 客户端清单保留独立版本、摘要和可执行文件且不重复", () =>
    {
        var latest = JsonSerializer.Deserialize<ClientsLatest>("""
            {"clients":{"audio-for-can":{"version":"2.2.36","download":"https://example.invalid/retired.zip"}},
             "apps":{
               "audio-for-can":{"version":"3.0.8","size":101,"sha256":"audio-digest","download":"https://api.ceruleanavi.net/api/v1/clients/download/audio-for-can?v=3.0.8"},
               "atis-for-can":{"version":"3.0.5","size":202,"sha256":"atis-digest","download":"https://api.ceruleanavi.net/api/v1/clients/download/atis-for-can?v=3.0.5"},
               "can-downloader":{"version":"3.0.2","size":303,"sha256":"launcher-digest","download":"https://api.ceruleanavi.net/api/v1/clients/download/can-downloader?v=3.0.2"}}}
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        // 用真实显示目录，在任意运行平台注入该平台包；不访问线上发布。
        using var catalogStream = typeof(ManifestSource).Assembly.GetManifestResourceStream("catalog.json")!;
        var catalog = JsonSerializer.Deserialize<Catalog>(catalogStream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        })!;
        foreach (var app in latest.Apps.Values)
            app.Platforms = new()
            {
                [PlatformInfo.PackageKeys[0]] = new() { Download = app.Download, Size = app.Size, Sha256 = app.Sha256 }
            };
        var manifest = (UpdateManifest)typeof(ManifestSource).GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [latest, catalog])!;
        Check(manifest.Products.Count == 2);
        var audio = manifest.Products.Single(p => p.Id == "audio-for-can");
        var atis = manifest.Products.Single(p => p.Id == "atis-for-can");
        Check(audio.Version == "3.0.8" && audio.Size == 101 && audio.Sha256 == "audio-digest");
        Check(audio.Executable == PlatformInfo.ExecutableName("ControllerClient.exe"));
        Check(audio.DownloadUrl == latest.Apps["audio-for-can"].Download);
        Check(atis.Version == "3.0.5" && atis.Size == 202 && atis.Sha256 == "atis-digest");
        Check(atis.Executable == PlatformInfo.ExecutableName("atis-for-can.exe"));
        Check(manifest.Launcher is { Version: "3.0.2", Size: 303, Sha256: "launcher-digest" });
        if (!OperatingSystem.IsWindows())
        {
            foreach (var app in latest.Apps.Values)
                app.Platforms = new()
                {
                    ["win-x64"] = new() { Download = app.Download, Size = app.Size, Sha256 = app.Sha256 }
                };
            var windowsOnly = (UpdateManifest)typeof(ManifestSource).GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [latest, catalog])!;
            Check(windowsOnly.Products.Count == 0 && windowsOnly.Launcher is null);
        }
    });
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
        // 前面场景留下的安装记录会作为“已下架”出现在软件库里
        var product = vm.Products.Single(p => p.Id == id);
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
            var product = vm.Products.Single(p => p.Id == release.Id);
            vm.Update(product);
            Check(!product.IsBusy && vm.Notices.Any(n => n.Text.Contains("正在运行")));
            vm.Repair(product);
            Check(!product.IsBusy && vm.Notices.Count == 2);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); }
    });
    Run("版本比较：正式版高于同号预发布版（#12）", () =>
    {
        Check(VersionUtil.Compare("3.1.0", "3.1.0-rc.1") > 0 && VersionUtil.Compare("3.1.0-rc.1", "3.1.0") < 0);
        Check(VersionUtil.Compare("1.10.0", "1.9.2") > 0 && VersionUtil.Compare("2611", "2610") > 0);
        Check(VersionUtil.Compare("1.0", "1.0.0") == 0 && VersionUtil.Compare("v1.2.0+abc", "1.2.0") == 0);
        Check(VersionUtil.Compare("1.0.0-rc.2", "1.0.0-rc.10") < 0);
        Check(VersionUtil.Compare("1.0.0-alpha", "1.0.0-alpha.1") < 0 && VersionUtil.Compare("1.0.0-1", "1.0.0-alpha") < 0);
        Check(VersionUtil.Compare("3.2.0-rc.1", "3.1.0") > 0);
        var id = "regression-prerelease";
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        LocalInstalls.Set(new InstallRecord { Id = id, Version = "3.1.0-rc.1", Path = dir, Files = [] });
        var manifest = new UpdateManifest { Products = [new() { Id = id, Name = "测试", Version = "3.1.0", DownloadUrl = "https://example.test/x.zip" }] };
        var vm = new MainWindowViewModel(manifest, new SettingsViewModel(), new UserAccount("test", "测试", null, 0));
        Check(vm.Products.Single(p => p.Id == id) is { HasUpdate: true, State: ProductState.UpdateAvailable });
        LocalInstalls.Remove(id);
    });
    Run("自定义代理：IPv6 地址加方括号，无效地址不悄悄改用系统代理（#32）", () =>
    {
        Check(CanApi.ProxyUri("::1", 8080)?.ToString() == "http://[::1]:8080/");
        Check(CanApi.ProxyUri("[::1]", 8080)?.ToString() == "http://[::1]:8080/");
        Check(CanApi.ProxyUri(" 127.0.0.1 ", 7890)?.ToString() == "http://127.0.0.1:7890/");
        Check(CanApi.ProxyUri("http://proxy.local", 3128)?.ToString() == "http://proxy.local:3128/");
        Check(CanApi.ProxyUri("bad host", 1) is null && CanApi.ProxyUri("", 1) is null && CanApi.ProxyUri("a/b", 1) is null);
        var settings = new SettingsViewModel { ProxyMode = "自定义 HTTP 代理", ProxyHost = "bad host" };
        CanApi.Shared.Configure(settings);
        try
        {
            var error = Assert<ApiException>(() => CanApi.Shared.GetDownloadAsync(new Uri("http://127.0.0.1:1/x"), 0, CancellationToken.None).GetAwaiter().GetResult());
            Check(error.Code == "proxy");
        }
        finally { CanApi.Shared.Configure(new SettingsViewModel { ProxyMode = "不使用代理" }); }
    });
    Run("zip 校验发现 CRC32 不符的存储条目（#8）", () =>
    {
        var path = Path.Combine(root, "stored.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("app/a.txt", CompressionLevel.NoCompression).Open()))
            writer.Write("PAYLOAD_PAYLOAD_PAYLOAD");
        PackageInstaller.Verify(path);
        var bytes = File.ReadAllBytes(path);
        var at = bytes.AsSpan().IndexOf("PAYLOAD"u8);
        bytes[at] = (byte)'X';
        File.WriteAllBytes(path, bytes);
        Assert<InvalidDataException>(() => PackageInstaller.Verify(path));
    });
    Run("发布已更新（大小不符）时一个字节也不写入并要求重新获取发布信息（#9）", () =>
    {
        var body = new byte[1000];
        using var server = Serve(async context =>
        {
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }, out var address);
        var target = Path.Combine(root, "rollover.zip");
        // Task.Run：测试线程上有 Avalonia 的同步上下文，直接阻塞等待会死锁
        Assert<ReleaseChangedException>(() => Task.Run(() => PackageInstaller.DownloadAsync(new Uri(address + "old.zip"), target, 900,
            new DownloadProgress(), () => 0, CancellationToken.None)).GetAwaiter().GetResult());
        Check(!File.Exists(target) && !File.Exists(PackageInstaller.PartialPath(target)));
    });
    Run("续传时服务器文件已变化则从头下载，不拼接两个版本（#9）", () =>
    {
        var body = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        string? ifRange = null;
        using var server = Serve(async context =>
        {
            // 文件标识对不上：按 If-Range 的规定返回完整内容
            ifRange = context.Request.Headers["If-Range"];
            context.Response.Headers["ETag"] = "\"new\"";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }, out var address);
        var target = Path.Combine(root, "resume.zip");
        File.WriteAllBytes(PackageInstaller.PartialPath(target), new byte[400]);
        File.WriteAllText(PackageInstaller.PartialPath(target) + ".id", "etag \"old\"");
        Task.Run(() => PackageInstaller.DownloadAsync(new Uri(address + "x.zip"), target, body.Length, new DownloadProgress(), () => 0, CancellationToken.None))
            .GetAwaiter().GetResult();
        Check(ifRange == "\"old\"");
        Check(File.ReadAllBytes(target).AsSpan().SequenceEqual(body));
        Check(!File.Exists(PackageInstaller.PartialPath(target)) && !File.Exists(PackageInstaller.PartialPath(target) + ".id"));
    });
    Run("数据目录内的安装位置存为相对路径（#7）", () =>
    {
        var id = "regression-portable";
        var dir = Path.Combine(AppPaths.Apps, id);
        Directory.CreateDirectory(dir);
        try
        {
            LocalInstalls.Set(new InstallRecord { Id = id, Version = "1", Path = dir, Files = [] });
            var stored = LocalData.Load<List<InstallRecord>>("installs.json")!.Single(r => r.Id == id);
            Check(!Path.IsPathRooted(stored.Path) && AppPaths.Resolve(stored.Path) == dir);
            Check(LocalInstalls.Find(id)?.Path == dir);
            var outside = LocalData.Load<List<InstallRecord>>("installs.json")!.First(r => r.Id.StartsWith("regression-") && r.Id != id);
            Check(Path.IsPathRooted(outside.Path));
        }
        finally
        {
            LocalInstalls.Remove(id);
            Directory.Delete(dir);
        }
    });
    Run("重启后已下架的已安装软件仍在软件库中（#10）", () =>
    {
        var id = "regression-delisted";
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        LocalInstalls.Set(new InstallRecord { Id = id, Version = "1.0.0", Path = dir, Files = [] });
        var vm = new MainWindowViewModel(new UpdateManifest(), new SettingsViewModel(), new UserAccount("test", "测试", null, 0));
        var product = vm.Products.Single(p => p.Id == id);
        Check(product is { IsDelisted: true, IsInstalled: true, State: ProductState.Installed, CanDownload: false, HasMenu: true });
        Check(product.InstallPath == dir && product.InstalledVersion == "1.0.0");
        LocalInstalls.Remove(id);
    });
    Run("启动时已有新版本也会自动更新（#11）", () =>
    {
        var id = "regression-autoupdate";
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        LocalInstalls.Set(new InstallRecord { Id = id, Version = "1.0.0", Path = dir, Files = [] });
        var settings = new SettingsViewModel { AutoUpdateInstalled = true, CacheFolder = Path.Combine(root, "cache"), ProxyMode = "不使用代理" };
        var manifest = new UpdateManifest { Products = [new() { Id = id, Name = "测试", Version = "2.0.0", DownloadUrl = "http://127.0.0.1:1/x.zip" }] };
        var vm = new MainWindowViewModel(manifest, settings, new UserAccount("test", "测试", null, 0));
        var product = vm.Products.Single(p => p.Id == id);
        Check(product.IsBusy && product.CurrentTask == ProductTask.Update);
        Check(vm.Notices.Any(n => n.Text.StartsWith("已开始自动更新")));
        Wait(() => !product.IsBusy);
        LocalInstalls.Remove(id);
    });
    Run("离开主界面后旧界面不再在后台安装（#13）", () =>
    {
        var package = File.ReadAllBytes(Package(("a.txt", "NEW")));
        var received = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var server = Serve(async context =>
        {
            context.Response.ContentLength64 = package.Length;
            await context.Response.OutputStream.WriteAsync(package.AsMemory(0, 1));
            await context.Response.OutputStream.FlushAsync();
            received.TrySetResult();
            await release.Task;
            try
            {
                await context.Response.OutputStream.WriteAsync(package.AsMemory(1));
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { /* 客户端已取消 */ }
        }, out var address);
        var id = "regression-stop";
        var settings = new SettingsViewModel { InstallRoot = root, CacheFolder = Path.Combine(root, "cache"), ProxyMode = "不使用代理" };
        var manifest = new UpdateManifest
        {
            Products = [new() { Id = id, Name = "测试", Version = "1", DownloadUrl = address + "x.zip",
                Components = [new() { Id = "main", Required = true, Size = package.Length }] }]
        };
        var vm = new MainWindowViewModel(manifest, settings, new UserAccount("test", "测试", null, 0));
        var product = vm.Products.Single(p => p.Id == id);
        vm.Install(product, product.Components.ToList(), Path.Combine(root, id), false);
        Wait(() => received.Task.IsCompleted);
        var stop = vm.StopAsync();
        release.SetResult();
        Wait(() => stop.IsCompleted);
        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();
        Check(LocalInstalls.Find(id) is null && !File.Exists(Path.Combine(root, id, "a.txt")));
    });
    Run("设置保存失败时保持未保存并报错（#31）", () =>
    {
        var path = Path.Combine(LocalData.Root, "settings.json");
        var aside = path + ".regression";
        if (File.Exists(path)) File.Move(path, aside, true);
        Directory.CreateDirectory(path);
        try
        {
            var settings = new SettingsViewModel();
            settings.MarkSaved();
            settings.MaxConcurrentDownloads = 4;
            Check(settings.IsDirty);
            Assert<IOException>(settings.Save, allowDerived: true, alsoAllow: typeof(UnauthorizedAccessException));
            Check(settings.IsDirty);
            Check(!Directory.EnumerateFiles(LocalData.Root, "settings.json.*.tmp").Any());
        }
        finally
        {
            Directory.Delete(path);
            if (File.Exists(aside)) File.Move(aside, path);
        }
    });
    Run("自更新先完整写出新版本再替换，失败时原程序不变（#4）", () =>
    {
        var replace = typeof(SelfUpdater).GetMethod("ReplaceFile", BindingFlags.Static | BindingFlags.NonPublic)!;
        var dir = Path.Combine(root, "self-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "Downloader.exe");
        var source = target + ".new";
        File.WriteAllText(target, "OLD");
        File.WriteAllText(source, "NEW");
        replace.Invoke(null, [source, target]);
        Check(File.ReadAllText(target) == "NEW" && File.ReadAllText(target + ".old") == "OLD" && !File.Exists(target + ".tmp"));
        File.Delete(target + ".old");
        // 复制失败（新版本不存在）：原程序不动
        File.Delete(source);
        try { replace.Invoke(null, [source, target]); throw new Exception("应当失败"); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
        Check(File.ReadAllText(target) == "NEW" && !File.Exists(target + ".tmp"));
    });
    Run("同一数据目录只运行一个下载器（#6）", () =>
    {
        var first = StartChild("--instance", "4000");
        try
        {
            Thread.Sleep(1500);
            Check(!first.HasExited);
            Check(Child("--instance", "0") == 1);
            Check(first.WaitForExit(15000) && first.ExitCode == 0);
            // 第一个进程退出后锁自动释放
            Check(Child("--instance", "0") == 0);
        }
        finally { if (!first.HasExited) first.Kill(true); first.Dispose(); }
    });
    Run("对话框打开时键盘只作用于对话框，Esc 关闭并还原焦点（#29）", () =>
    {
        var vm = new MainWindowViewModel(new UpdateManifest(), new SettingsViewModel(), new UserAccount("test", "测试", null, 0));
        var window = new Downloader.Views.MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var search = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>().First();
        search.Focus();
        Dispatcher.UIThread.RunJobs();
        Check(window.FocusManager?.GetFocusedElement() == search);
        var dialog = new Downloader.Views.DialogView("测试", new Avalonia.Controls.TextBlock { Text = "内容" }, "确定");
        var result = window.ShowDialogAsync(dialog);
        Dispatcher.UIThread.RunJobs();
        Check(window.FocusManager?.GetFocusedElement() is Avalonia.Visual focused && dialog.IsVisualAncestorOf(focused));
        window.KeyTextInput("pilot");
        Dispatcher.UIThread.RunJobs();
        Check(vm.SearchText == "");
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        Wait(() => result.IsCompleted);
        Check(result.Result == Downloader.Views.DialogResult.None);
        Dispatcher.UIThread.RunJobs();
        Check(window.FocusManager?.GetFocusedElement() == search);
        window.KeyTextInput("pilot");
        Dispatcher.UIThread.RunJobs();
        Check(vm.SearchText == "pilot");
        window.Close();
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
Process StartChild(params string[] arguments)
{
    var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return Process.Start(info)!;
}
int Child(params string[] arguments)
{
    using var child = StartChild(arguments);
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
T Assert<T>(Action action, bool allowDerived = false, Type? alsoAllow = null) where T : Exception
{
    try { action(); }
    catch (Exception ex) when (ex.GetType() == typeof(T) || allowDerived && ex is T || alsoAllow?.IsInstanceOfType(ex) == true)
    {
        return ex as T ?? null!;
    }
    throw new Exception($"Expected {typeof(T).Name}");
}
// 本机 HTTP 服务：每个请求交给 handler 处理
HttpListener Serve(Func<HttpListenerContext, Task> handler, out string address)
{
    using var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    var listener = new HttpListener();
    address = $"http://127.0.0.1:{port}/";
    listener.Prefixes.Add(address);
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => handler(context));
        }
    });
    return listener;
}
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
