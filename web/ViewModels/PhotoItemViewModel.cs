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

    /// <summary>异步加载本地图片字节并解码为 BitmapImage（缩略图用，缩小解码尺寸）。</summary>
    public async Task LoadThumbnailAsync()
    {
        if (_thumbnail is not null) return;
        try
        {
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
            var bytes = await PyBridge.Instance.CallBytesAsync(
                "photo_image",
                Models.Json.Serialize(new { photo_id = PhotoId }));
            if (bytes.Length == 0) return null;
            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            using var ras = ms.AsRandomAccessStream();
            await bitmap.SetSourceAsync(ras);
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
