using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ForzaGallerySync.Services;

/// <summary>
/// 共享元素（Hero）转场动画。
///
/// 缩略图与详情大图之间做「共享元素」过渡：把一张覆盖全页的 Image 从源矩形
/// （缩略图位置）逐帧缩放平移到目标矩形（大图位置），从而让缩略图看起来
/// 直接"长大"成大图。关闭时反向播放，缩回原缩略图。
///
/// 实现要点：
/// - 缩放 / 平移全部走 RenderTransform，不触发布局重排，动画更平滑；
/// - 由毫秒级计时器逐帧驱动，而不是 Storyboard，因此能从"当前实际位置"
///   测量起止矩形（缩略图可能被滚动、窗口可能被缩放）；
/// - 用一个令牌支持取消，避免用户快速连点时多次动画互相打架；
/// - 动画期间用不透明度补偿，避免低分辨率缩略图被放大后显得过于模糊。
/// </summary>
public static class HeroTransition
{
    /// <summary>单程动画时长（毫秒）。</summary>
    private const int DurationMs = 300;

    /// <summary>每帧间隔（毫秒），约 60fps。</summary>
    private const int FrameMs = 16;

    private static CancellationTokenSource? _cts;

    /// <summary>
    /// 播放一次共享元素转场。
    /// </summary>
    /// <param name="hero">覆盖在整个页面之上的 Image（动画载体）。</param>
    /// <param name="source">起始矩形，坐标相对 <paramref name="root"/>。</param>
    /// <param name="target">目标矩形，坐标相对 <paramref name="root"/>。</param>
    /// <param name="root">测量坐标系所在的容器（通常是页面根 Grid）。</param>
    /// <param name="image">缩略图位图。</param>
    /// <param name="opening">
    /// true 表示「展开」（缩略图 → 大图，由半透明变清晰）；
    /// false 表示「收起」（大图 → 缩略图，逐渐淡出）。
    /// </param>
    /// <param name="hideWhenDone">
    /// 动画结束后是否立即隐藏 hero 图层。
    /// 传 false 时由调用方负责隐藏——用于让 hero 一直覆盖到目标内容真正显示出来，
    /// 避免两者交接的空档造成闪一下。
    /// </param>
    public static async Task PlayAsync(
        Image hero,
        Rect source,
        Rect target,
        FrameworkElement root,
        ImageSource? image,
        bool opening,
        bool hideWhenDone = true)
    {
        if (hero is null || root is null) return;
        if (!IsUsable(source) || !IsUsable(target)) return;

        // 取消上一次未完成的动画，避免互相干扰。
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        // 把相对 root 的坐标折算到 hero 的父级坐标系（父级可能有 Padding）。
        var offset = PaddingOffset(hero, root);
        double fx = source.X - offset.X, fy = source.Y - offset.Y;
        double tx = target.X - offset.X, ty = target.Y - offset.Y;

        // 布局尺寸固定为目标尺寸，缩放 / 平移走 RenderTransform。
        hero.Width = target.Width;
        hero.Height = target.Height;
        hero.Source = image;
        hero.Opacity = opening ? 0.35 : 1.0;
        hero.Visibility = Visibility.Visible;

        var scale = EnsureScale(hero);
        var translate = EnsureTranslate(hero);

        double sx = source.Width / target.Width;
        double sy = source.Height / target.Height;
        scale.ScaleX = sx;
        scale.ScaleY = sy;
        translate.X = fx;
        translate.Y = fy;

        const double minOpacity = 0.35;
        var sw = Stopwatch.StartNew();
        double elapsed;
        do
        {
            elapsed = sw.Elapsed.TotalMilliseconds;
            var eased = EaseInOutCubic(Math.Min(1.0, elapsed / DurationMs));

            scale.ScaleX = sx + (1.0 - sx) * eased;
            scale.ScaleY = sy + (1.0 - sy) * eased;
            translate.X = fx + (tx - fx) * eased;
            translate.Y = fy + (ty - fy) * eased;
            hero.Opacity = opening
                ? minOpacity + (1.0 - minOpacity) * eased   // 展开：由半透明到清晰
                : 1.0 - (1.0 - minOpacity) * eased;          // 收起：逐渐淡出

            try
            {
                await Task.Delay(FrameMs, token);
            }
            catch (OperationCanceledException)
            {
                return; // 已被新的动画接管，不再改动可视化状态。
            }
        } while (elapsed < DurationMs);

        // 落到终点并复位。
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = tx;
        translate.Y = ty;
        hero.Opacity = 1;

        if (hideWhenDone)
        {
            hero.Visibility = Visibility.Collapsed;
            hero.Source = null;
        }

        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>隐藏 hero 图层并清空其图像（交接完成后调用）。</summary>
    public static void Hide(Image hero)
    {
        if (hero is null) return;
        hero.Visibility = Visibility.Collapsed;
        hero.Source = null;
    }

    /// <summary>取元素相对指定容器的矩形；布局未完成或坐标非法时返回 null。</summary>
    public static Rect? GetRect(FrameworkElement? element, FrameworkElement? relativeTo)
    {
        try
        {
            if (element is null || relativeTo is null) return null;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;

            var topLeft = element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
            if (!IsFinite(topLeft.X) || !IsFinite(topLeft.Y)) return null;

            return new Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
        }
        catch
        {
            // 布局未完成时 TransformToVisual 可能抛异常，视为"暂无矩形"。
            return null;
        }
    }

    private static bool IsUsable(Rect r) =>
        r.Width > 0 && r.Height > 0 &&
        IsFinite(r.X) && IsFinite(r.Y) && IsFinite(r.Width) && IsFinite(r.Height);

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>hero 相对 root 的 Padding 偏移量（坐标折算用）。</summary>
    private static Point PaddingOffset(FrameworkElement hero, FrameworkElement root)
    {
        double left = 0, top = 0;
        // WinUI 里 Panel 本身没有 Padding，承载内边距的是 Grid / StackPanel / Border。
        if (ReferenceEquals(hero.Parent, root))
        {
            switch (root)
            {
                case Grid grid:
                    left = grid.Padding.Left;
                    top = grid.Padding.Top;
                    break;
                case StackPanel stack:
                    left = stack.Padding.Left;
                    top = stack.Padding.Top;
                    break;
                case Border border:
                    left = border.Padding.Left;
                    top = border.Padding.Top;
                    break;
            }
        }
        return new Point(left, top);
    }

    private static ScaleTransform EnsureScale(Image hero)
    {
        var (scale, _) = EnsureTransforms(hero);
        return scale;
    }

    private static TranslateTransform EnsureTranslate(Image hero)
    {
        var (_, translate) = EnsureTransforms(hero);
        return translate;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransforms(Image hero)
    {
        if (hero.RenderTransform is TransformGroup group &&
            group.Children.Count >= 2 &&
            group.Children[0] is ScaleTransform s &&
            group.Children[1] is TranslateTransform t)
        {
            return (s, t);
        }

        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        var created = new TransformGroup();
        created.Children.Add(scale);
        created.Children.Add(translate);
        hero.RenderTransform = created;
        return (scale, translate);
    }

    /// <summary>三次缓入缓出（页面内其它逐帧动画也复用，保证节奏一致）。</summary>
    public static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
}
