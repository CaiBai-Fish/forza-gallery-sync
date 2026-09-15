using System.Reflection;

namespace ForzaGallerySync.Services;

/// <summary>
/// 应用自身版本号。
///
/// 权威来源是**程序集信息**（由发布脚本 <c>web/make-gui.ps1</c> 用
/// <c>-p:Version=&lt;x.y.z&gt;</c> 注入，取自 <c>pyproject.toml</c>）：
/// 界面上显示的"当前版本"和判断"有没有新版本"都读它，
/// 避免"内嵌的 Python 包版本落后于 GUI"时把当前版本判低、误报有更新。
///
/// exe 的"属性 → 详细信息"里显示的也是同一个值，两者永远一致。
/// </summary>
public static class AppVersion
{
    /// <summary>形如 <c>1.0.3</c> 的语义化版本；取不到时返回空串（调用方回退到后端版本）。</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();

            // 开发态（dotnet build 未注入 Version）会得到 1.0.0.0，这时用
            // InformationalVersion 更接近真实版本；两者都没有就返回空串。
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            var version = assembly.GetName().Version;
            var text = version is not null
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : "";

            // InformationalVersion 形如 "1.0.3+<commit>"；没有 "+" 时通常是 SDK 的默认值
            if (informational is not null && informational.Contains('+'))
            {
                var core = informational.Split('+')[0];
                if (core.Count(c => c == '.') == 2) text = core;
            }

            Logger.Info($"程序集版本: {text}（InformationalVersion={informational ?? "无"}）");
            return text;
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取程序集版本失败：{ex.Message}");
            return "";
        }
    }
}
