namespace ForzaGallerySync.Models;

/// <summary>更新检查结果（对应 service.check_update）。</summary>
public sealed class UpdateInfoModel
{
    public string Current { get; set; } = "";
    public string Latest { get; set; } = "";
    public bool HasUpdate { get; set; }
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublishedAt { get; set; } = "";
    public string Error { get; set; } = "";
}
