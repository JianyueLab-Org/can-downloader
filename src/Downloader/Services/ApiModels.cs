using System.Net;

namespace Downloader.Services;

/// <summary>CAN API 返回的会员信息（<c>/api/v1/auth/session</c>、登录接口的 <c>user</c>）。</summary>
public sealed class ApiUser
{
    /// <summary>CAN ID。</summary>
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public int Rating { get; set; } = -1;
    public bool EmailVerified { get; set; }
}

public sealed class SessionResponse
{
    public ApiUser? User { get; set; }
}

public sealed class CodeRequestResponse
{
    public string RequestId { get; set; } = "";
}

/// <summary><c>GET /api/v1/clients/latest</c>：CAN 客户端的最新发布。</summary>
public sealed class ClientsLatest
{
    public string Version { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>发布页（更新说明）的网址。</summary>
    public string? Notes { get; set; }
    /// <summary>以客户端名称为键（audio-for-can 等）。某个构建失败时服务器不会列出它。</summary>
    public Dictionary<string, ClientBuild> Clients { get; set; } = [];

    /// <summary>
    /// 独立发布的程序（can-pilot、can-downloader），各自有版本号。旧版 API 没有这一项。
    /// can-downloader 是下载器自己，用于自更新，不出现在软件库里。
    /// </summary>
    public Dictionary<string, AppBuild> Apps { get; set; } = [];
}

/// <summary><c>clients/latest</c> 里 <c>apps</c> 的一项。</summary>
public sealed class AppBuild
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public bool Prerelease { get; set; }
    public long Size { get; set; }
    /// <summary>安装包的 SHA-256（小写十六进制），下载后核对。</summary>
    public string? Sha256 { get; set; }
    /// <summary>经 api.ceruleanavi.net 中转的下载地址。</summary>
    public string Download { get; set; } = "";
    /// <summary>GitHub 地址；私有仓库没有。</summary>
    public string? Origin { get; set; }
    /// <summary>发布页网址。</summary>
    public string? Notes { get; set; }
    /// <summary>发布说明正文（Markdown）。</summary>
    public string? Changelog { get; set; }
}

public sealed class ClientBuild
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public long Size { get; set; }
    /// <summary>经 api.ceruleanavi.net 中转的下载地址（固定版本）。</summary>
    public string Download { get; set; } = "";
    /// <summary>GitHub 上的原始地址。</summary>
    public string? Origin { get; set; }
}

/// <summary>API 错误响应 <c>{"error": "...", "message": "..."}</c>，以及网络错误。<see cref="Exception.Message"/> 是可以直接显示的中文。</summary>
public sealed class ApiException(string code, string message, HttpStatusCode? status = null) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>管制等级名称，与 can-api 的 internal/store/rating.go 一致。</summary>
public static class Ratings
{
    private static readonly Dictionary<int, (string Short, string Chinese)> Names = new()
    {
        [-1] = ("INAC", "未激活"),
        [0] = ("SUS", "已暂停"),
        [1] = ("OBS", "观察员"),
        [2] = ("S1", "塔台学员"),
        [3] = ("S2", "塔台管制员"),
        [4] = ("S3", "高级学员"),
        [5] = ("C1", "航路管制员"),
        [6] = ("C2", "中级管制员"),
        [7] = ("C3", "高级管制员"),
        [8] = ("I1", "教员"),
        [9] = ("I2", "中级教员"),
        [10] = ("I3", "高级教员"),
        [11] = ("SUP", "监督员"),
        [12] = ("ADM", "管理员")
    };

    public static string Describe(int rating) =>
        Names.TryGetValue(rating, out var name) ? $"{name.Short} {name.Chinese}" : $"未知（{rating}）";
}
