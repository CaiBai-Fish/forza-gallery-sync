using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

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
