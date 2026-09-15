using Microsoft.Windows.ApplicationModel.Resources;

namespace ForzaGallerySync.Services;

/// <summary>
/// 界面文案的统一入口（i18n）。
///
/// 文案写在 <c>Strings\&lt;语言&gt;\Resources.resw</c>（.NET SDK 会自动把它们编进
/// <c>resources.pri</c>，不需要在 csproj 里显式声明 PRIResource——重复声明会报 NETSDK1022）。
/// 运行时由 MRT Core 按**系统界面语言**挑选，取不到该语言时回退到默认语言（zh-CN）。
///
/// 两种取法：
/// - XAML：给控件加 <c>x:Uid="Key_Name"</c>，再用 <c>Key_Name.Text</c> / <c>Key_Name.Content</c>
///   这样的资源名（见各页 XAML）；
/// - C#：用本类的 <see cref="Get"/> / <see cref="Format"/>。
///
/// 键名规则：<c>&lt;模块&gt;_&lt;用途&gt;</c>，例如 <c>Settings_Account_Section</c>、
/// <c>Gallery_Search_Placeholder</c>。改中文文案时**必须同步 en-us**，
/// 否则英文界面里会混入中文（缺键时按设计回退到中文，不会崩但会难看）。
/// </summary>
public static class StringLocalizer
{
    private static ResourceLoader? _loader;

    /// <summary>
    /// 资源加载器（延迟创建）。
    ///
    /// 注意：<c>ResourceLoader(string)</c> 的参数是**资源文件名**，不是语言代码——
    /// 传 "en-US" 会抛 UnauthorizedAccessException（实测踩过）。语言由 MRT Core
    /// 按系统设置决定，不需要也不应该在业务代码里手动指定。
    /// </summary>
    private static ResourceLoader Loader => _loader ??= new ResourceLoader();

    /// <summary>
    /// 取一条文案。缺失时返回键名本身（而不是空串或抛异常）——
    /// 界面上会明显看出是哪个键漏了，便于定位，同时不影响应用运行。
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";

        try
        {
            var value = Loader.GetString(key);
            if (!string.IsNullOrEmpty(value)) return value;

            Logger.Warn($"i18n 缺键：{key}（已按原样显示；请在 Strings\\zh-CN 与 en-us 里补上）");
            return key;
        }
        catch (Exception ex)
        {
            Logger.Warn($"i18n 取值失败：{key}：{ex.Message}");
            return key;
        }
    }

    /// <summary>
    /// 取一条带占位符的文案并填充。
    ///
    /// 用 <see cref="string.Format(string, object?[])"/> 而不是字符串拼接，是为了让
    /// 语序不同的语言也能翻译（例如英文 "Syncing {0}/{1}" 与中文"同步中 {0}/{1}"同形，
    /// 但换成日文可能要把数量放前面）。
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
            // 译文里的占位符写错时不要崩，退回模板本身并把问题写进日志
            Logger.Warn($"i18n 占位符不匹配：{key}（模板 \"{template}\"）：{ex.Message}");
            return template;
        }
    }
}
