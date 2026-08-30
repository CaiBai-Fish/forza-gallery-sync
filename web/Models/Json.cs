using System.Text.Json;

namespace ForzaGallerySync.Models;

/// <summary>全局 JSON 序列化选项：Python 侧使用 snake_case，C# 侧使用 PascalCase。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);
}
