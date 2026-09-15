using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using ForzaGallerySync.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace ForzaGallerySync.Services;

/// <summary>
/// 照片的通用操作：用系统默认应用打开、在资源管理器中定位、复制图片到剪贴板。
///
/// 总览页的最新照片预览、照片库的缩略图与详情大图都复用这里，
/// 避免每个页面各写一套 shell / 剪贴板调用。
/// </summary>
public static class PhotoActions
{
    /// <summary>用系统默认的图片应用打开。</summary>
    public static void OpenWithDefaultApp(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // UseShellExecute = true 且只传路径：交给系统按 .jpg 的默认程序打开。
            // 不要再走 explorer（那是"选中文件"，不是打开图片）。
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Exception($"用默认应用打开失败: {path}", ex);
        }
    }

    /// <summary>在资源管理器中定位该文件（选中它）。</summary>
    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Exception($"在资源管理器中定位失败: {path}", ex);
        }
    }

    /// <summary>用资源管理器打开目录。</summary>
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Exception($"打开目录失败: {path}", ex);
        }
    }

    /// <summary>
    /// 把图片本身复制到剪贴板（不是文件路径），可直接粘贴到聊天 / 画图 / Office。
    /// 失败时退化为复制文件路径，保证总有可用结果。
    /// </summary>
    public static async Task CopyImageAsync(PhotoItemViewModel item)
    {
        try
        {
            var bytes = await item.LoadFullBytesAsync();
            if (bytes.Length == 0)
            {
                CopyText(item.LocalPath);
                return;
            }

            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            // 流的位置必须回到开头，否则读到的内容为空。

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

            // 同时放入「图片」与「文件路径」：图片可粘进聊天 / 画图 / Office，
            // 路径则让只接受文本的程序（终端、编辑器）也能拿到内容。
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            if (!string.IsNullOrEmpty(item.LocalPath))
            {
                package.SetText(item.LocalPath);
            }

            Clipboard.SetContent(package);

            Logger.Info($"已复制图片到剪贴板: {item.PhotoId} ({bytes.Length} 字节)");
        }
        catch (Exception ex)
        {
            Logger.Exception($"复制图片到剪贴板失败: {item.PhotoId}", ex);
            CopyText(item.LocalPath);
        }
    }

    /// <summary>复制文本到剪贴板（失败静默）。</summary>
    public static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Logger.Exception("复制文本到剪贴板失败", ex);
        }
    }

    /// <summary>从菜单项的 DataContext 取照片（菜单挂在缩略图 / 图片元素上时用）。</summary>
    public static PhotoItemViewModel? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as PhotoItemViewModel;

    /// <summary>该照片是否有可用的本地文件。</summary>
    public static bool HasLocalFile(PhotoItemViewModel? item) =>
        item is not null && !string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath);
}
