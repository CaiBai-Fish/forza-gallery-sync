namespace ForzaGallerySync.ViewModels;

/// <summary>时间 / 时长格式化（与旧 Vue 前端一致的中文本地化显示）。</summary>
public static class Format
{
    /// <summary>ISO 时间 → 本地时间字符串（zh-CN，24 小时制）；空返回 "—"。</summary>
    public static string Time(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "—";
        if (DateTimeOffset.TryParse(iso, out var dto))
        {
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
        return iso;
    }

    /// <summary>秒数 → 人类可读时长。</summary>
    public static string Duration(long? seconds)
    {
        if (seconds is null) return "未知";
        var s = seconds.Value;
        if (s >= 3600) return $"{s / 3600} 小时 {(s % 3600) / 60} 分";
        if (s >= 60) return $"{s / 60} 分钟";
        return $"{s} 秒";
    }
}
