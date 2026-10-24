using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Downloader.ViewModels;

namespace Downloader.Services;

/// <summary>
/// CAN API（https://api.ceruleanavi.net）客户端：客户端发布信息、下载中转，以及 OAuth 登录用到的请求（<see cref="OAuthLogin"/>）。
/// </summary>
public sealed class CanApi
{
    public const string ApiHost = "api.ceruleanavi.net";
    /// <summary>API 地址。环境变量 CAN_API_URL 可以指向本地运行的 can-api，仅供开发测试。</summary>
    public static readonly Uri BaseUri =
        Environment.GetEnvironmentVariable("CAN_API_URL") is { Length: > 0 } custom
        && Uri.TryCreate(custom.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            ? uri
            : new Uri($"https://{ApiHost}/");
    public static readonly Uri WebsiteUri = new("https://ceruleanavi.net/");
    public static string SignUpUrl => new Uri(WebsiteUri, "signup").ToString();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CanApi Shared { get; } = new();

    private string _configKey = "";
    private HttpClient _http;
    private HttpClient _downloads;

    private CanApi() => (_http, _downloads) = CreateClients(ProxyMode.System, null, TimeSpan.FromSeconds(15));

    /// <summary>按设置里的代理与超时重建连接；设置没变时什么都不做。进行中的下载继续使用旧连接。</summary>
    public void Configure(SettingsViewModel settings)
    {
        var mode = settings.ProxyMode switch
        {
            "不使用代理" => ProxyMode.None,
            "自定义 HTTP 代理" => ProxyMode.Custom,
            _ => ProxyMode.System
        };
        var proxy = mode == ProxyMode.Custom ? $"http://{settings.ProxyHost.Trim()}:{(int)settings.ProxyPort}" : null;
        var timeout = TimeSpan.FromSeconds(Math.Clamp((double)settings.TimeoutSeconds, 5, 120));
        var key = $"{mode}|{proxy}|{timeout}";
        if (key == _configKey)
            return;
        _configKey = key;
        (_http, _downloads) = CreateClients(mode, proxy, timeout);
        Log.Info($"网络设置：代理 {mode}{(proxy is null ? "" : " " + proxy)}，超时 {timeout.TotalSeconds} 秒");
    }

    private enum ProxyMode { None, System, Custom }

    private (HttpClient Api, HttpClient Downloads) CreateClients(ProxyMode mode, string? proxy, TimeSpan timeout)
    {
        SocketsHttpHandler Handler(bool decompress)
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = timeout,
                AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None
            };
            switch (mode)
            {
                case ProxyMode.None:
                    handler.UseProxy = false;
                    break;
                case ProxyMode.Custom when Uri.TryCreate(proxy, UriKind.Absolute, out var uri):
                    handler.Proxy = new WebProxy(uri);
                    break;
            }
            return handler;
        }

        var userAgent = new ProductInfoHeaderValue("CAN-Downloader", AppInfo.Version);
        var api = new HttpClient(Handler(decompress: true)) { BaseAddress = BaseUri, Timeout = timeout };
        api.DefaultRequestHeaders.UserAgent.Add(userAgent);
        // 下载可能持续很久，不设整体超时；连接超时由 ConnectTimeout 控制
        var downloads = new HttpClient(Handler(decompress: false)) { BaseAddress = BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        downloads.DefaultRequestHeaders.UserAgent.Add(userAgent);
        return (api, downloads);
    }

    // ========== 发布信息 ==========

    public Task<ClientsLatest> GetLatestClientsAsync(CancellationToken ct = default) =>
        SendAsync<ClientsLatest>(new HttpRequestMessage(HttpMethod.Get, "api/v1/clients/latest"), ct);

    /// <summary>下载请求，<paramref name="offset"/> 大于 0 时用 Range 续传。只读取响应头，调用方负责读取内容并释放响应。</summary>
    public async Task<HttpResponseMessage> GetDownloadAsync(Uri url, long offset, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);

        var response = await Send(_downloads, request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return response;
        using (response)
            throw await ToApiExceptionAsync(response, ct);
    }

    // ========== 登录（OAuth，见 OAuthLogin） ==========

    /// <summary>发送请求并返回原始响应（不检查状态码），网络错误与超时转换成 <see cref="ApiException"/>。调用方释放响应。</summary>
    public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct) =>
        Send(_http, request, HttpCompletionOption.ResponseContentRead, ct);

    // ========== 请求 ==========

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Send(_http, request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
            throw await ToApiExceptionAsync(response, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                   ?? throw new ApiException("invalid_response", "服务器返回了空响应。");
        }
        catch (JsonException)
        {
            throw new ApiException("invalid_response", "无法解析服务器的响应。");
        }
    }

    /// <summary>发送请求，把网络错误与超时转换成 <see cref="ApiException"/>。每个请求记一行日志（不含请求内容）。</summary>
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        using (request)
        {
            var target = request.RequestUri is { IsAbsoluteUri: true } uri ? uri.ToString() : $"{BaseUri}{request.RequestUri}";
            var range = request.Headers.Range is { } r ? $" Range {r}" : "";
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await client.SendAsync(request, option, ct);
                Log.Info($"{request.Method} {target}{range} → {(int)response.StatusCode}（{started.ElapsedMilliseconds} ms）");
                return response;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"{request.Method} {target} 超时（{started.ElapsedMilliseconds} ms）");
                throw new ApiException("timeout", $"连接 {ApiHost} 超时，请检查网络或代理设置。");
            }
            catch (HttpRequestException ex)
            {
                Log.Warn($"{request.Method} {target} 失败：{ex.Message}");
                throw new ApiException("network", $"无法连接 {request.RequestUri?.Host ?? ApiHost}：{ex.Message}");
            }
        }
    }

    private sealed class ErrorBody
    {
        public string? Error { get; set; }
        public string? Message { get; set; }
    }

    private static async Task<ApiException> ToApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ErrorBody? body = null;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // 不是 JSON（例如网关错误页）
        }

        var code = body?.Error ?? "http_" + (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var message = code switch
        {
            "rate_limited" => retryAfter is { } wait
                ? $"操作过于频繁，请 {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} 秒后再试。"
                : "操作过于频繁，请稍后再试。",
            "unavailable" => "暂时无法获取发布信息，请稍后再试。",
            "internal_error" => "服务器暂时不可用，请稍后再试。",
            "no_asset" or "unknown_client" => "服务器上没有这个软件的安装包。",
            "upstream_error" or "upstream_too_large" => "服务器无法从 GitHub 获取安装包，请稍后再试。",
            "checksum_mismatch" => "服务器校验安装包失败，请稍后再试。",
            _ => body?.Message is { Length: > 0 } text
                ? $"{text}（HTTP {(int)response.StatusCode}）"
                : $"服务器返回错误 HTTP {(int)response.StatusCode}。"
        };
        Log.Warn($"API 错误 {(int)response.StatusCode} {code}：{body?.Message}");
        return new ApiException(code, message, response.StatusCode);
    }
}
