namespace ForzaGallerySync.Models;

/// <summary>同步进度快照（对应 runner.SyncProgress.snapshot）。</summary>
public sealed class SyncProgressModel
{
    public bool Running { get; set; }
    public bool CancelRequested { get; set; }
    public string Game { get; set; } = "";
    public List<string> Games { get; set; } = new();
    public int Total { get; set; }
    public int Done { get; set; }
    public int Synced { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<FailedItem> FailedItems { get; set; } = new();
    public string Message { get; set; } = "";
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public bool Force { get; set; }
    public int? MaxPhotos { get; set; }
}

/// <summary>按游戏统计。</summary>
public sealed class PhotoCount
{
    public string Game { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>按 游戏/年-月 统计。</summary>
public sealed class MonthCount
{
    public string Game { get; set; } = "";
    public string Month { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>照片汇总（对应 get_status 的 photos）。</summary>
public sealed class PhotosSummary
{
    public int Total { get; set; }
    public List<PhotoCount> ByGame { get; set; } = new();
    public List<MonthCount> ByMonth { get; set; } = new();
}

/// <summary>单个游戏的同步状态记录。</summary>
public sealed class SyncStateItem
{
    public string Game { get; set; } = "";
    public string? LastSyncAt { get; set; }
    public int TotalRecords { get; set; }
    public int SyncedRecords { get; set; }
}
