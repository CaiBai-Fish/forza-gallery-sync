using System.IO;
using System.Text.Json;

namespace ForzaGallerySync.Services;

/// <summary>
/// 界面文案的统一入口（i18n）。
///
/// 文案的**唯一来源**是 <c>web/Strings/&lt;语言&gt;/Resources.resw</c>；
/// C# 侧不直接读 resw，而是读由 `tools/i18n_make_json.py` 生成的同目录
/// <c>strings.json</c>（随程序发布，见 csproj 的 Content 项）。
///
/// 为什么不用 WinRT 的 <c>ResourceLoader</c>：未打包 WinUI 3 下它对同一份 PRI 里的键
/// 取不到值（XAML 的 x:Uid 用同一份 PRI 却正常，说明是 ResourceLoader 侧的解析问题）。
/// 界面文案不能依赖一个"取不到就静默回空"的 API —— 症状是界面上直接显示键名或空白，
/// 排查起来很费劲，因此改成读一个行为完全可预期的 JSON 字典。
///
/// 取值顺序：当前系统界面语言 → 回退 zh-CN → 回退键名本身（并在日志里记一条告警）。
/// </summary>
public static class StringLocalizer
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _current;
    private static Dictionary<string, string>? _fallback;

    /// <summary>当前界面语言（取系统 UI 语言；不认识的语言回退到 zh-CN）。</summary>
    private static string Language
    {
        get
        {
            var name = System.Globalization.CultureInfo.CurrentUICulture.Name;   // 形如 zh-CN / en-US
            if (string.IsNullOrEmpty(name)) return "zh-CN";
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-us";
        }
    }

    private static Dictionary<string, string> Load(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Strings", language, "strings.json");
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);

            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Logger.Warn($"i18n 读取 {language} 文案失败：{ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (Gate)
            {
                _current ??= Load(Language);
                Logger.Info($"界面语言：{Language}（文案来自 Strings\\{Language}\\strings.json，"
                    + $"{_current.Count} 条）");
            }
            return _current;
        }
    }

    private static Dictionary<string, string> Fallback
    {
        get
        {
            if (_fallback is not null) return _fallback;
            lock (Gate)
            {
                _fallback ??= Load("zh-CN");
            }
            return _fallback;
        }
    }

    /// <summary>
    /// 取一条文案。缺失时依次回退：当前语言 → zh-CN → 键名本身。
    ///
    /// 回退到键名是刻意的：界面上会明显看出漏了哪个键（便于发现），
    /// 但调用方若在意观感可以自行判断（例如窗口标题就用了自己的默认值）。
    /// </summary>
    /// <param name="key">裸键，如 <c>Main_Title</c>（不带属性后缀）。</param>
    /// <param name="suffix">保留参数以便兼容 XAML 侧命名习惯，本地字典按裸键取，忽略它。</param>
    public static string Get(string key, string suffix = "Text")
    {
        _ = suffix;
        if (string.IsNullOrEmpty(key)) return "";

        if (Current.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)) return value;
        if (Fallback.TryGetValue(key, out var back) && !string.IsNullOrEmpty(back)) return back;

        Logger.Warn($"i18n 缺键：{key}（当前语言 {Language} 与 zh-CN 都没有；已按原样显示）");
        return key;
    }

    /// <summary>
    /// 取一条带占位符的文案并填充（占位符写成 <c>{0}</c> / <c>{1}</c>）。
    ///
    /// 用格式化而不是字符串拼接，是为了让语序不同的语言也能翻译。
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException ex)
        {
            Logger.Warn($"i18n 占位符不匹配：{key}（模板 \"{template}\"）：{ex.Message}");
            return template;
        }
    }
}
