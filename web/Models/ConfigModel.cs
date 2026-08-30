namespace ForzaGallerySync.Models;

/// <summary>游戏基本信息（id 为代码，name 为显示名）。</summary>
public sealed class GameInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>配置信息（对应 service.get_config / _cfg_dict）。</summary>
public sealed class ConfigModel
{
    public string Token { get; set; } = "";
    public string MaskedToken { get; set; } = "";
    public bool HasToken { get; set; }
    public bool HasRefreshToken { get; set; }
    public string MaskedRefreshToken { get; set; } = "";
    public string DownloadDir { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public int PageSize { get; set; } = 50;
    public string Pagination { get; set; } = "auto";
    public int Timeout { get; set; } = 30;
    public int Retries { get; set; } = 3;
    public int Workers { get; set; } = 4;
    public bool VerifySsl { get; set; } = true;
    public string UserAgent { get; set; } = "";
    public List<string> EnabledGames { get; set; } = new();
    public string ConfigPath { get; set; } = "";
    public List<GameInfo> SupportedGames { get; set; } = new();
}

/// <summary>Token 状态（对应 service.auth_status / TokenManager.status）。</summary>
public sealed class AuthModel
{
    public bool HasToken { get; set; }
    public bool HasRefreshToken { get; set; }
    public bool Expired { get; set; }
    public long? ExpiresIn { get; set; }
    public string MaskedToken { get; set; } = "";
    public string MaskedRefreshToken { get; set; } = "";
}
