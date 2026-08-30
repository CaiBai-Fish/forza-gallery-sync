namespace ForzaGallerySync.Models;

/// <summary>照片信息（对应 service.list_photos / photo_meta 返回项）。</summary>
public sealed class PhotoInfo
{
    public string PhotoId { get; set; } = "";
    public string Game { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string SubmissionTimeUtc { get; set; } = "";
    public string Month { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string DownloadedAt { get; set; } = "";
    /// <summary>仅 photo_meta 返回。</summary>
    public string Url { get; set; } = "";
}

/// <summary>分页照片列表（对应 service.list_photos 返回）。</summary>
public sealed class PhotoPage
{
    public int Total { get; set; }
    public List<PhotoInfo> Items { get; set; } = new();
}

/// <summary>同步失败项。</summary>
public sealed class FailedItem
{
    public string Url { get; set; } = "";
    public string Reason { get; set; } = "";
}
