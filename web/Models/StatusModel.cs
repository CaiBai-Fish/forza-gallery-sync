namespace ForzaGallerySync.Models;

/// <summary>综合状态（对应 service.get_status）。</summary>
public sealed class StatusModel
{
    public ConfigModel Config { get; set; } = new();
    public AuthModel Token { get; set; } = new();
    public PhotosSummary Photos { get; set; } = new();
    public List<SyncStateItem> SyncState { get; set; } = new();
    public SyncProgressModel Sync { get; set; } = new();
}
