using System.Security.Cryptography;
using System.Text;

namespace Downloader.Services;

/// <summary>入队时固定的发布身份，清单刷新和下载源设置变化不会修改已有任务。</summary>
public sealed record ReleaseSnapshot(string Id, string Version, Uri Url, string? Sha256,
    string? ChecksumsUrl, string ChecksumFileName, long Size, string Extension, string InstallPath)
{
    public string CachePath(string folder)
    {
        var identity = System.Text.Json.JsonSerializer.Serialize(new { Id, Version, Url, Sha256, ChecksumsUrl, Size, Extension });
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(folder, $"{hash}{Extension}");
    }
}
