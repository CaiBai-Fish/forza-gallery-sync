namespace ForzaGallerySync.Models;

/// <summary>游戏代码 → 显示名映射（与 forza_sync.config.GAME_DISPLAY_NAMES 一致）。</summary>
public static class UseGames
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["FH6"] = "Forza Horizon 6",
        ["FM"] = "Forza Motorsport",
        ["FH5"] = "Forza Horizon 5",
        ["FH4"] = "Forza Horizon 4",
        ["FM7"] = "Forza Motorsport 7",
    };

    public static string Name(string? code) =>
        string.IsNullOrEmpty(code) ? "" : (Names.TryGetValue(code, out var n) ? n : code);
}
