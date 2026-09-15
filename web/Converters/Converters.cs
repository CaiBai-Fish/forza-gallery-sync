using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ForzaGallerySync.Converters;

/// <summary>bool → Visibility 转换器。参数为 "invert" 时反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var invert = parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
        var b = value is true;
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool → FontWeight（true → Bold，false → Normal）。</summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? FontWeights.Bold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool → !bool（用于 IsEnabled 等需要反相的布尔属性）。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>int（集合计数）→ Visibility。参数为 "invert" 时反转（count==0 可见）。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var invert = parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
        var b = value is int i && i > 0;
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>非空字符串 → Visible（参数为 "invert" 时反转），用于可选说明文字。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var invert = parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
        var b = !string.IsNullOrWhiteSpace(value as string);
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool → 成功 / 警告语义色画刷（登录状态徽标用）。</summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var ok = value is true;
        var key = ok ? "AppSuccessBrush" : "AppWarnBrush";
        return Application.Current.Resources.TryGetValue(key, out var found) && found is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// 资源键字符串 → 该键对应的画刷。
///
/// 视图模型只暴露「用哪个语义色」的键名（如 AppSuccessBrush），实际画刷由
/// 主题资源决定，因此浅色 / 深色主题切换后依然取到正确的颜色。
/// </summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value as string;
        if (!string.IsNullOrEmpty(key) &&
            Application.Current.Resources.TryGetValue(key, out var found) &&
            found is Brush brush)
        {
            return brush;
        }

        // 兜底：跟随系统前景色，保证任何主题下都可见。
        return Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var fallback) &&
               fallback is Brush fb
            ? fb
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
