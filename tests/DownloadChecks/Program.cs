using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Downloader.Services;
using Downloader.ViewModels;

// Real VM, HTTP, zip verification, transactional installer and persisted records; no login.
using var server = new ReleaseServer();
Environment.SetEnvironmentVariable("CAN_API_URL", server.Origin);
AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
using var stop = new CancellationTokenSource();
var exitCode = 0;
Dispatcher.UIThread.Post(async () =>
{
    try
    {
        await Race(false, false, false);
        await Race(true, false, false);
        await Race(true, true, false);
        await Race(true, false, true);
        Console.WriteLine("PASS all download identity checks");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        exitCode = 1;
    }
    finally { stop.Cancel(); }
});
Dispatcher.UIThread.MainLoop(stop.Token);
return exitCode;

async Task Race(bool hashed, bool pause, bool queued)
{
    var id = $"identity-{(hashed ? "sha" : "no-sha")}-{(pause ? "pause" : queued ? "queued" : "active")}";
    Check(LocalInstalls.Find(id) is null && LocalInstalls.Find(id + "-blocker") is null,
        "check IDs already have installation records; refusing to change them");
    var root = Path.Combine(RepositoryRoot(), ".temp", "download-checks", id);
    Directory.CreateDirectory(root);
    var settings = new SettingsViewModel
    {
        InstallRoot = Path.Combine(root, "apps"), CacheFolder = Path.Combine(root, "cache"),
        ProxyMode = "不使用代理", LaunchAfterInstall = false, DeleteCacheAfterInstall = false,
        VerifyAfterDownload = false, MaxConcurrentDownloads = 1, DownloadSource = SettingsViewModel.SourceGitHub
    };
    server.Configure(id, hashed, queued);
    MainWindowViewModel? vm = null;
    try
    {
        vm = new MainWindowViewModel(null, settings, new UserAccount("", "Local check", null, 0));
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Check(vm.Products.Count == (queued ? 2 : 1), "initial manifest not loaded");
        var product = vm.Products.Single(p => p.Id == id);
        ProductViewModel? blocker = null;
        if (queued)
        {
            blocker = vm.Products.Single(p => p.Id == id + "-blocker");
            vm.Install(blocker, blocker.Components, blocker.InstallPath, false);
            await server.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        vm.Install(product, product.Components, product.InstallPath, false);
        if (!queued)
            await server.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (pause)
        {
            await ChangedUntil(product, () => product.Progress > 0);
            vm.TogglePause(product);
            Check(product.State == ProductState.Paused, "transfer did not pause");
        }
        server.Version = 2;
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Check(product.LatestVersion == "2.0.0", "refresh did not apply v2");
        Check(product.TotalBytes == server.V1.Length, "refresh changed transfer size");
        settings.CacheFolder = Path.Combine(root, "new-cache");
        settings.DownloadSource = SettingsViewModel.SourceSite;
        var completed = ChangedUntil(vm, () => !vm.HasTransfers);
        if (pause)
            vm.TogglePause(product);
        server.Release.TrySetResult();
        await completed;
        Check(vm.Notices.All(n => n.Kind != NoticeKind.Warning), "transfer failed: " + string.Join("; ", vm.Notices.Select(n => n.Text)));
        Check(File.ReadAllBytes(Path.Combine(product.InstallPath, "payload.bin")).SequenceEqual(server.Payload1), "v1 bytes not installed");
        Check(product.InstalledVersion == "1.0.0", "v1 transfer recorded a refreshed version");
        Check(LocalInstalls.Find(id)?.Version == "1.0.0", "in-memory installed record is not v1");
        using (var records = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "installs.json"))))
            Check(records.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id).GetProperty("version").GetString() == "1.0.0", "persisted installed record is not v1");
        Check(product.LatestVersion == "2.0.0" && product.HasUpdate && product.State == ProductState.UpdateAvailable && vm.HasUpdates,
            "v2 is not available after v1 installation");
        Check(File.ReadAllBytes(Path.Combine(root, "cache", id + "-1.0.0.zip")).SequenceEqual(server.V1), "cache no longer identifies v1");
        Check(!Directory.Exists(settings.CacheFolderPath), "transfer moved to refreshed cache setting");
        Check(server.Requests.All(r => r.Path == $"/origin/{id}/1" || r.Path == $"/origin/{id}-blocker/1"), "queued/resumed transfer changed release URL or source");
        if (pause)
            Check(server.Requests.Any(r => r.Offset > 0), "resume did not send Range for retained v1 partial");
        var updated = ChangedUntil(product, () => product.InstalledVersion == "2.0.0" && !product.IsBusy);
        vm.Update(product);
        await updated;
        Check(LocalInstalls.Find(id)?.Version == "2.0.0" && product.State == ProductState.Installed,
            "next transfer did not use refreshed release");
        Check(File.ReadAllBytes(Path.Combine(settings.CacheFolderPath, id + "-2.0.0.zip")).SequenceEqual(server.V2),
            "next transfer did not use new cache identity");
        Check(server.Requests.Last().Path == $"/site/{id}/2", "next transfer did not use new source URL");
        Console.WriteLine($"PASS {id}");
    }
    finally
    {
        server.Release.TrySetResult();
        if (vm is not null)
        {
            // Test cleanup owns the timer; do not invoke logout or any credential endpoint.
            var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            ((DispatcherTimer)typeof(MainWindowViewModel).GetField("_timer", fields)!.GetValue(vm)!).Stop();
            var transfers = (System.Collections.IDictionary)typeof(MainWindowViewModel).GetField("_transfers", fields)!.GetValue(vm)!;
            var runs = transfers.Values.Cast<object>()
                .Select(t => t.GetType().GetProperty("Run")!.GetValue(t)).OfType<Task>().ToArray();
            foreach (var p in vm.Products.Where(p => p.IsTransferring).ToList()) vm.Cancel(p);
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(20));
        }
        LocalInstalls.Remove(id);
        LocalInstalls.Remove(id + "-blocker");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static async Task ChangedUntil(INotifyPropertyChanged source, Func<bool> done)
{
    if (done()) return;
    var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnChanged(object? sender, PropertyChangedEventArgs args) { if (done()) signal.TrySetResult(); }
    source.PropertyChanged += OnChanged;
    try { if (done()) return; await signal.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
    finally { source.PropertyChanged -= OnChanged; }
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string RepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CanDownloader.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run from a checkout");
}

sealed class ReleaseServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accept;
    private string _id = "";
    private bool _hashed;
    private bool _queued;
    public int Version { get; set; }
    public byte[] Payload1 { get; } = RandomNumberGenerator.GetBytes(256 * 1024);
    public byte[] V1 { get; }
    public byte[] V2 { get; }
    public TaskCompletionSource FirstRequest { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(string Path, long Offset)> Requests { get; } = [];
    public string Origin { get; }
    public ReleaseServer()
    {
        V1 = Zip(Payload1);
        V2 = Zip(RandomNumberGenerator.GetBytes(384 * 1024));
        _listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _accept = Accept();
    }
    public void Configure(string id, bool hashed, bool queued)
    {
        _id = id; _hashed = hashed; _queued = queued; Version = 1;
        FirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Clear();
    }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(Serve(client));
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync() ?? throw new IOException("Missing request");
                var path = request.Split(' ')[1];
                long offset = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) offset = long.Parse(line[13..].TrimEnd('-'));
                if (path.StartsWith("/api/v1/clients/latest"))
                {
                    var version = Version;
                    var bytes = version == 1 ? V1 : V2;
                    object Build(string id) => new
                    {
                        version = version == 1 ? "1.0.0" : "2.0.0", size = bytes.Length,
                        download = Origin + $"site/{id}/{version}", origin = Origin + $"origin/{id}/{version}",
                        sha256 = _hashed ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null
                    };
                    var apps = new Dictionary<string, object> { [_id] = Build(_id) };
                    if (_queued) apps[_id + "-blocker"] = Build(_id + "-blocker");
                    var json = JsonSerializer.SerializeToUtf8Bytes(new { apps });
                    await Headers(stream, 200, json.Length);
                    await stream.WriteAsync(json);
                    return;
                }
                Requests.Add((path, offset));
                var body = path.EndsWith("/1") ? V1 : V2;
                await Headers(stream, offset > 0 ? 206 : 200, body.Length - offset, offset, body.Length);
                var prefix = Math.Min(128 * 1024, body.Length - (int)offset);
                await stream.WriteAsync(body.AsMemory((int)offset, prefix));
                await stream.FlushAsync();
                FirstRequest.TrySetResult();
                await Release.Task.WaitAsync(_stop.Token);
                await stream.WriteAsync(body.AsMemory((int)offset + prefix));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
    }
    private static async Task Headers(Stream stream, int status, long length, long offset = 0, long total = 0)
    {
        var range = status == 206 ? $"Content-Range: bytes {offset}-{total - 1}/{total}\r\n" : "";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Length: {length}\r\nContent-Type: application/json\r\n{range}Connection: close\r\n\r\n"));
    }
    private static byte[] Zip(byte[] payload)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry("client/payload.bin", CompressionLevel.NoCompression).Open()) entry.Write(payload);
        return stream.ToArray();
    }
    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop();
        _accept.GetAwaiter().GetResult();
        Task.WhenAll(_connections).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
