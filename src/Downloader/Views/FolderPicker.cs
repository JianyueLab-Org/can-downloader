using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Downloader.Views;

/// <summary>系统的选择文件夹对话框。</summary>
public static class FolderPicker
{
    /// <summary>返回选中的本地目录；取消时返回 null。<paramref name="start"/> 不存在时从它最近的已存在的上级目录开始。</summary>
    public static async Task<string?> PickAsync(Visual owner, string title, string? start)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanPickFolder: true } storage)
            return null;

        IStorageFolder? startFolder = null;
        for (var dir = start; !string.IsNullOrEmpty(dir) && startFolder is null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir))
                startFolder = await storage.TryGetFolderFromPathAsync(dir);
        }

        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
