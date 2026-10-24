using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Downloader.Services;

/// <summary>OAuth 令牌。access 令牌一小时有效；refresh 令牌每次刷新都会换新（旧的随即作废）。</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// 用 CAN 账号登录（OAuth 2.1 授权码 + PKCE，公开客户端），应用在 https://platform.ceruleanavi.net 注册。
/// 在 127.0.0.1 的随机端口上等待回调（RFC 8252：环回地址只忽略端口，注册的回调地址是 http://127.0.0.1/callback），
/// 用系统浏览器打开授权页；成员在网站上登录并同意后，用授权码换取令牌。下载器自己从不接触密码。
/// </summary>
public static class OAuthLogin
{
    /// <summary>在 platform.ceruleanavi.net 注册的下载器 client_id（公开客户端）。环境变量 CAN_OAUTH_CLIENT_ID 可临时覆盖。</summary>
    private const string RegisteredClientId = "can_IWsJIKxarUpt55X-YP46Og2d";

    public static string ClientId =>
        Environment.GetEnvironmentVariable("CAN_OAUTH_CLIENT_ID") is { Length: > 0 } custom ? custom.Trim() : RegisteredClientId;

    /// <summary>offline_access 才会发 refresh 令牌，用于“自动登录”。</summary>
    private const string Scope = "openid profile email offline_access";

    private const string CallbackPath = "/callback";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Discovery? _discovery;

    /// <summary>本次登录的令牌，退出登录时吊销。</summary>
    public static OAuthTokens? Current { get; private set; }

    private sealed class Discovery
    {
        [JsonPropertyName("authorization_endpoint")] public string AuthorizationEndpoint { get; set; } = "";
        [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; set; } = "";
        [JsonPropertyName("userinfo_endpoint")] public string UserinfoEndpoint { get; set; } = "";
        [JsonPropertyName("revocation_endpoint")] public string? RevocationEndpoint { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    private sealed class Userinfo
    {
        [JsonPropertyName("sub")] public string Sub { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("rating")] public int? Rating { get; set; }
    }

    /// <summary>
    /// 完整的浏览器登录：打开授权页，等待回调，换取令牌。<paramref name="ct"/> 取消即放弃等待。
    /// <paramref name="openBrowser"/> 默认用系统浏览器打开授权页（测试时可替换）。
    /// </summary>
    public static async Task<OAuthTokens> SignInAsync(CancellationToken ct, Action<string>? openBrowser = null)
    {
        if (ClientId.Length == 0)
            throw new ApiException("oauth_not_configured", "下载器还没有配置 OAuth client_id（在 platform.ceruleanavi.net 注册后填入）。");

        var discovery = await GetDiscoveryAsync(ct);
        var verifier = RandomUrlSafe(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomUrlSafe(16);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";

        var authorize = discovery.AuthorizationEndpoint
                        + (discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?")
                        + Query(("response_type", "code"), ("client_id", ClientId), ("redirect_uri", redirectUri),
                            ("scope", Scope), ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"));
        Log.Info($"OAuth：打开浏览器授权，回调端口 {port}");
        (openBrowser ?? (url => Shell.Open(url)))(authorize);

        var code = await WaitForCodeAsync(listener, state, ct);
        Log.Info("OAuth：收到授权码，换取令牌");
        return await RequestTokensAsync(discovery, ct,
            ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectUri),
            ("client_id", ClientId), ("code_verifier", verifier));
    }

    /// <summary>用保存的 refresh 令牌换一组新令牌（旧的 refresh 令牌随即作废，要保存新的）。</summary>
    public static async Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var discovery = await GetDiscoveryAsync(ct);
        return await RequestTokensAsync(discovery, ct,
            ("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("client_id", ClientId));
    }

    /// <summary>读取账号信息（CAN ID、姓名、邮箱、等级）。</summary>
    public static async Task<ApiUser> GetUserAsync(string accessToken, CancellationToken ct = default)
    {
        var discovery = await GetDiscoveryAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, discovery.UserinfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await CanApi.Shared.SendRawAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException("invalid_token", "登录已失效，请重新登录。", response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<Userinfo>(Json, ct)
                   ?? throw new ApiException("invalid_response", "服务器没有返回账号信息。");
        return new ApiUser { Username = info.Sub, Name = info.Name ?? "", Email = info.Email, Rating = info.Rating ?? -1 };
    }

    /// <summary>退出登录：吊销本次登录的 refresh 令牌。失败不影响本机退出。</summary>
    public static async Task SignOutAsync()
    {
        var refreshToken = Current?.RefreshToken;
        Current = null;
        if (string.IsNullOrEmpty(refreshToken) || ClientId.Length == 0)
            return;
        try
        {
            var discovery = await GetDiscoveryAsync(CancellationToken.None);
            if (discovery.RevocationEndpoint is not { Length: > 0 } endpoint)
                return;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent([new("token", refreshToken), new("token_type_hint", "refresh_token"), new("client_id", ClientId)])
            };
            using var _ = await CanApi.Shared.SendRawAsync(request, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            Log.Warn($"OAuth：吊销令牌失败：{ex.Message}");
        }
    }

    private static async Task<Discovery> GetDiscoveryAsync(CancellationToken ct)
    {
        if (_discovery is { } cached)
            return cached;
        using var request = new HttpRequestMessage(HttpMethod.Get, ".well-known/openid-configuration");
        using var response = await CanApi.Shared.SendRawAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException("discovery", $"无法读取登录服务的配置（HTTP {(int)response.StatusCode}）。", response.StatusCode);
        var discovery = await response.Content.ReadFromJsonAsync<Discovery>(Json, ct);
        if (discovery is null || discovery.AuthorizationEndpoint.Length == 0 || discovery.TokenEndpoint.Length == 0)
            throw new ApiException("discovery", "登录服务的配置不完整。");
        return _discovery = discovery;
    }

    private static async Task<OAuthTokens> RequestTokensAsync(Discovery discovery, CancellationToken ct, params (string Key, string Value)[] form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)))
        };
        using var response = await CanApi.Shared.SendRawAsync(request, ct);
        TokenResponse? body = null;
        try { body = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct); }
        catch (JsonException) { /* 非 JSON 的错误页 */ }

        if (!response.IsSuccessStatusCode || body?.AccessToken is not { Length: > 0 } access)
        {
            var error = body?.Error ?? "http_" + (int)response.StatusCode;
            Log.Warn($"OAuth：令牌请求失败 {error}：{body?.ErrorDescription}");
            throw new ApiException(error, error switch
            {
                "invalid_grant" => "登录已过期或已在网站上撤销，请重新登录。",
                "invalid_client" or "unauthorized_client" => "下载器的 OAuth 配置无效（client_id），请联系管理员。",
                "rate_limited" => "操作过于频繁，请稍后再试。",
                _ => $"登录失败：{body?.ErrorDescription ?? error}"
            }, response.StatusCode);
        }
        return Current = new OAuthTokens(access, body.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, body.ExpiresIn)));
    }

    /// <summary>同时处理的回调连接数上限（防止本机程序开大量空连接占用资源），超出的排队等待。</summary>
    private const int MaxConnections = 8;

    /// <summary>
    /// 等待浏览器回调 GET /callback?code=…&amp;state=…，返回授权码。其他请求（如 favicon）回 404 后继续等。
    /// 连接要并发处理：浏览器常先开一条不发数据的预连接，真正的回调走另一条，逐个处理会被空连接拖住。
    /// 只有 state 正确的回调才能结束等待，第一个有效回调生效。
    /// </summary>
    private static async Task<string> WaitForCodeAsync(TcpListener listener, string state, CancellationToken ct)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var slots = new SemaphoreSlim(MaxConnections);
        var accepting = AcceptLoopAsync(listener, state, result, slots, done.Token);
        try
        {
            return await result.Task.WaitAsync(ct);
        }
        finally
        {
            // 拿到结果后立即停止监听，关闭其余连接
            done.Cancel();
            listener.Stop();
            await accepting;
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener, string state, TaskCompletionSource<string> result,
        SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // 名额用完时先不接新连接，让它在系统队列里等空连接超时让出名额（不丢掉真正的回调）
                await slots.WaitAsync(ct);
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleConnectionAsync(client, state, result, slots, ct);
            }
        }
        catch (Exception ex)
        {
            // 登录结束时停止监听会走到这里；其他情况（监听出错）结束等待
            if (!ct.IsCancellationRequested)
                result.TrySetException(ex);
        }
    }

    private static async Task HandleConnectionAsync(TcpClient client, string state, TaskCompletionSource<string> result,
        SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, ct);
                if (target is null || !target.StartsWith(CallbackPath, StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "Not Found", "", ct);
                    return;
                }

                var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
                if (query.GetValueOrDefault("state") != state)
                {
                    // 不是本次登录发起的回调，忽略
                    await RespondAsync(stream, 400, "Bad Request", Page("登录请求无效", "这不是下载器发起的登录，请回到 CAN 下载器重新登录。"), ct);
                    return;
                }
                if (query.GetValueOrDefault("error") is { } error)
                {
                    try { await RespondAsync(stream, 200, "OK", Page("未完成登录", "可以关闭这个页面，回到 CAN 下载器。"), ct); }
                    finally
                    {
                        result.TrySetException(new ApiException(error,
                            error == "access_denied" ? "已取消授权。" : $"登录失败：{query.GetValueOrDefault("error_description") ?? error}"));
                    }
                    return;
                }
                if (query.GetValueOrDefault("code") is not { Length: > 0 } code)
                {
                    await RespondAsync(stream, 400, "Bad Request", Page("登录请求无效", "回调里没有授权码，请回到 CAN 下载器重新登录。"), ct);
                    return;
                }

                // 此时还没换到令牌，只说已授权，结果在下载器里显示；页面没发出去（浏览器已断开）也照样继续登录
                try { await RespondAsync(stream, 200, "OK", Page("已授权", "可以关闭这个页面，回到 CAN 下载器继续。"), ct); }
                finally { result.TrySetResult(code); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // 连接断开，或登录已结束
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>读取请求行（GET /callback?... HTTP/1.1）和请求头，返回请求目标。空连接 5 秒后放弃。</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0)
                    break;
                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var line = head.Split("\r\n", 2)[0].Split(' ');
        return line.Length >= 2 && line[0] == "GET" ? line[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string reason, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    /// <summary>回调后浏览器里显示的页面，与下载器同样的深色极简风格。</summary>
    private static string Page(string title, string message) =>
        "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + " - CAN 下载器</title>" +
        "<style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:#16181c;color:#d7dae0;" +
        "font:13px system-ui,-apple-system,'Segoe UI','Microsoft YaHei','PingFang SC','Noto Sans CJK SC',sans-serif}main{text-align:center}h1{font-size:16px;font-weight:600;margin:0 0 8px}" +
        "p{margin:0;color:#8b9098}</style></head><body><main><h1>" + WebUtility.HtmlEncode(title) + "</h1><p>" +
        WebUtility.HtmlEncode(message) + "</p></main></body></html>";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    private static string Query(params (string Key, string Value)[] items) =>
        string.Join('&', items.Select(i => $"{Uri.EscapeDataString(i.Key)}={Uri.EscapeDataString(i.Value)}"));

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
