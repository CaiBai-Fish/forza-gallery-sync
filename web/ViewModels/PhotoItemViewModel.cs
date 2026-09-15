using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using ForzaGallerySync.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ForzaGallerySync.ViewModels;

/// <summary>照片网格项：绑定显示信息 + 延迟加载缩略图。</summary>
public sealed class PhotoItemViewModel : ObservableObject
{
    public string PhotoId { get; set; } = "";
    public string Game { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string SubmissionTimeUtc { get; set; } = "";
    public string Month { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string DownloadedAt { get; set; } = "";
    public string Url { get; set; } = "";

    public string GameName => Models.UseGames.Name(Game);

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>原图字节缓存：复制到剪贴板 / 保存时复用，避免反复跨 Python 取数据。</summary>
    private byte[]? _fullBytes;

    private Task<byte[]>? _fullBytesTask;

    /// <summary>
    /// 取原图字节（带缓存，并发调用会共用同一次请求）。
    /// </summary>
    public Task<byte[]> LoadFullBytesAsync()
    {
        if (_fullBytes is { Length: > 0 }) return Task.FromResult(_fullBytes);

        return _fullBytesTask ??= LoadFullBytesCoreAsync();
    }

    private async Task<byte[]> LoadFullBytesCoreAsync()
    {
        try
        {
            var bytes = await PyBridge.Instance.CallBytesAsync(
                "photo_image",
                Models.Json.Serialize(new { photo_id = PhotoId }));
            _fullBytes = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            Logger.Exception($"读取图片字节失败 {PhotoId}", ex);
            _fullBytesTask = null;   // 允许下次重试
            return Array.Empty<byte>();
        }
    }

    /// <summary>异步加载本地图片字节并解码为 BitmapImage（缩略图用，缩小解码尺寸）。</summary>
    public async Task LoadThumbnailAsync()
    {
        if (_thumbnail is not null) return;
        try
        {
            // 缩略图只取一次字节、用完即弃：不写进原图缓存，
            // 否则一页几十张缩略图会让每个条目都常驻一份全尺寸原图。
            var bytes = await PyBridge.Instance.CallBytesAsync(
                "photo_image",
                Models.Json.Serialize(new { photo_id = PhotoId }));
            if (bytes.Length == 0) return;
            // 只解码到缩略图所需尺寸，避免全尺寸解码大图时失败 / 卡顿 / 内存暴涨。
            var bitmap = new BitmapImage { DecodePixelWidth = 480 };
            using var ms = new MemoryStream(bytes);
            using var ras = ms.AsRandomAccessStream();
            await bitmap.SetSourceAsync(ras);
            Ui.Run(() => Thumbnail = bitmap);
        }
        catch (Exception ex)
        {
            Logger.Exception($"缩略图加载失败 {PhotoId}", ex);
        }
    }

    /// <summary>加载全尺寸原图（详情大图用），失败返回 null。</summary>
    public async Task<ImageSource?> LoadFullImageAsync()
    {
        try
        {
            var bytes = await LoadFullBytesAsync();
            if (bytes.Length == 0) return null;
            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            using var ras = ms.AsRandomAccessStream();
            await bitmap.SetSourceAsync(ras);
            // 触摸一次像素尺寸，确保解码真正完成再返回；否则调用方可能在
            // 图片"还没画出来"时就把它当作已就绪，导致画面先空白再跳出。
            _ = bitmap.PixelWidth;
            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.Exception($"全图加载失败 {PhotoId}", ex);
            return null;
        }
    }
}

/// <summary>带并发限制的缩略图加载器（避免同时发起大量 Python 调用）。</summary>
public static class ThumbnailLoader
{
    private static readonly SemaphoreSlim Gate = new(4, 4);

    public static async Task LoadAsync(PhotoItemViewModel item)
    {
        if (item.Thumbnail is not null) return;
        await Gate.WaitAsync();
        try
        {
            await item.LoadThumbnailAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
