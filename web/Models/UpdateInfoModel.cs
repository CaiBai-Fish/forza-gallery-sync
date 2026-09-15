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

    /// <summary>最新版本的更新说明（取自 CHANGELOG 对应章节）。</summary>
    public string Notes { get; set; } = "";

    /// <summary>版本信息来源：changelog / changelog-local / github-api。</summary>
    public string Source { get; set; } = "";

    public string Error { get; set; } = "";
}
