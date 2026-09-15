using ForzaGallerySync.Services;
using ForzaGallerySync.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ForzaGallerySync.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel VM { get; } = new();

    private PhotoItemViewModel? _previewItem;
    private Windows.Foundation.Rect? _previewFromRect;

    /// <summary>预览会话号：递增即可让仍在飞行中的异步流程作废。</summary>
    private int _previewSession;

    public DashboardPage()
    {
        InitializeComponent();
        VM.NavigateRequested += OnNavigate;
        VM.PhotoOpenRequested += OnOpenPhoto;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => VM.Start();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VM.Stop();
        ClosePreviewImmediately();
    }

    private void OnNavigate(string page) => App.MainWindow?.NavigateTo(page);

    // ---- 页面内导航 ----
    private void OnGoSettings(object sender, RoutedEventArgs e) => OnNavigate("settings");
    private void OnGoSync(object sender, RoutedEventArgs e) => OnNavigate("sync");
    private void OnGoGallery(object sender, RoutedEventArgs e) => OnNavigate("gallery");
    private async void OnRefresh(object sender, RoutedEventArgs e) => await VM.LoadAsync();

    private void OnRecentPhotoClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItemViewModel item)
        {
            // 记下缩略图位置，作为共享元素转场的起点。
            _previewFromRect = HeroTransition.GetRect(sender as FrameworkElement, RootGrid);
            VM.OpenPhoto(item);
        }
    }

    // ================= 照片预览（共享元素转场） =================

    /// <summary>
    /// 打开预览。
    ///
    /// 时序很关键：卡片框架用**缩略图**立即显示并与 Hero 动画同时起帧，
    /// 全尺寸原图在后台加载完成后再静默替换。若先 await 原图再显示预览层，
    /// 用户会先看到"页面空着"、然后整块弹出来，放大动画仿佛没播放完就到了目标页面。
    /// </summary>
    private async void OnOpenPhoto(PhotoItemViewModel item)
    {
        if (PreviewLayer.Visibility == Visibility.Visible) return;

        _previewItem = item;
        _previewSession++;
        var session = _previewSession;

        // 1) 立即用缩略图把卡片铺好：框架先隐藏（随动画淡入），大图交给 Hero 图层"长大"。
        PreviewTitle.Text = string.IsNullOrEmpty(item.Title) ? "无标题" : item.Title;
        PreviewGame.Text = item.GameName;
        PreviewMeta.Text = $"{item.GameName} · 上传 {Format.Time(item.SubmissionTimeUtc)} · 下载 {Format.Time(item.DownloadedAt)}";
        PreviewPath.Text = string.IsNullOrEmpty(item.LocalPath) ? "—" : item.LocalPath;
        PreviewImage.Source = item.Thumbnail;   // 先低分辨率占位，动画期间不可见
        PreviewImage.Opacity = 0;

        PreviewLayer.Visibility = Visibility.Visible;
        PreviewLayer.IsHitTestVisible = true;
        PreviewScrim.Opacity = 1;

        // 卡片框架（标题栏 / 底部操作栏）随动画一起淡入：否则目标页面看上去"已经加载完成"，
        // 放大动画会显得像是在事后补播。
        PreviewCard.Opacity = 1;
        PreviewHeader.Opacity = 0;
        PreviewFooter.Opacity = 0;

        RootGrid.UpdateLayout(); // 确保目标矩形已经完成布局

        // 2) 后台加载全尺寸原图，不阻塞动画起帧。
        var fullImageTask = item.LoadFullImageAsync();

        // 3) 立刻起帧。hero 图层在动画结束后不立即隐藏，等目标内容真正显示出来再交接。
        var from = _previewFromRect;
        var toRect = HeroTransition.GetRect(PreviewImage, RootGrid);
        if (item.Thumbnail is not null && from is { Width: > 0 } fr && toRect is { Width: > 0 } tr)
        {
            await HeroTransition.PlayAsync(HeroImage, fr, tr, RootGrid, item.Thumbnail,
                opening: true, hideWhenDone: false);
        }
        else
        {
            // 拿不到可信的起止矩形时不做转场，但记录一次以便定位（正常情况下不该出现）。
            Logger.Warn($"[Hero] 打开预览未播放转场：缩略图={Describe(from)}，目标={Describe(toRect)}");
            HeroTransition.Hide(HeroImage);
        }

        if (session != _previewSession) return; // 动画期间已被关闭

        // 4) 交接：优先直接用原图收尾，避免"先缩略图、再原图"两次画面变化。
        var full = await WaitForAsync(fullImageTask, TimeSpan.FromMilliseconds(450));
        if (session != _previewSession || PreviewLayer.Visibility != Visibility.Visible) return;

        PreviewImage.Source = full ?? item.Thumbnail;
        PreviewImage.Opacity = 0;
        await Task.WhenAll(
            FadeInAsync(PreviewImage, 160),
            FadeInAsync(PreviewHeader, 160),
            FadeInAsync(PreviewFooter, 160));

        if (session != _previewSession) return;

        // 等这一帧真正画出来，再撤掉 hero 覆盖层：否则两者之间有"什么都没画"的一帧，看起来就是闪一下。
        await NextFrameAsync();
        HeroTransition.Hide(HeroImage);

        // 若原图比动画慢，再补一次交叉淡入（此时下层已有内容，不会闪）。
        if (full is null)
        {
            var late = await fullImageTask;
            if (session == _previewSession && PreviewLayer.Visibility == Visibility.Visible && late is not null)
            {
                await CrossfadeAsync(PreviewImage, late, (ImageSource?)PreviewImage.Source, 180);
            }
        }
    }

    /// <summary>让出一帧，等 UI 线程完成绘制。</summary>
    private static async Task NextFrameAsync()
    {
        await Task.Delay(32);
    }

    /// <summary>在给定时间内等待任务完成；未完成则返回 null（不取消原任务）。</summary>
    private static async Task<ImageSource?> WaitForAsync(Task<ImageSource?> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task ? await task : null;
    }

    /// <summary>
    /// 交叉淡入替换图片：用当前画面（或给定的衬图）垫在下层，新图在上层淡入。
    /// 这样替换过程中下层始终有内容，不会出现"先空白再淡入"的二次闪烁。
    /// </summary>
    private static async Task CrossfadeAsync(Image target, ImageSource next, ImageSource? holdover, int durationMs)
    {
        if (target.Parent is not Grid host) { target.Source = next; return; }

        var cover = holdover ?? target.Source;
        target.Source = cover;
        target.Opacity = 1;

        var overlay = new Image
        {
            Source = next,
            Stretch = target.Stretch,
            HorizontalAlignment = target.HorizontalAlignment,
            VerticalAlignment = target.VerticalAlignment,
            Margin = target.Margin,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        host.Children.Add(overlay);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double elapsed;
            do
            {
                elapsed = sw.Elapsed.TotalMilliseconds;
                overlay.Opacity = Math.Min(1.0, elapsed / durationMs);
                await Task.Delay(16);
            } while (elapsed < durationMs);

            target.Source = next;   // 收尾：把新图落回主图
            target.Opacity = 1;
        }
        finally
        {
            if (host.Children.Contains(overlay)) host.Children.Remove(overlay);
        }
    }

    private static string Describe(Windows.Foundation.Rect? r) =>
        r is { } v ? $"({v.X:F0},{v.Y:F0},{v.Width:F0}x{v.Height:F0})" : "无";

    /// <summary>逐帧淡入，避免原图替换时出现生硬跳变。</summary>
    private static async Task FadeInAsync(UIElement target, int durationMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double elapsed;
        do
        {
            elapsed = sw.Elapsed.TotalMilliseconds;
            target.Opacity = Math.Min(1.0, elapsed / durationMs);
            await Task.Delay(16);
        } while (elapsed < durationMs);
        target.Opacity = 1;
    }

    private async void OnClosePreview(object sender, RoutedEventArgs e) => await ClosePreviewAsync();

    private void OnPreviewScrimTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        _ = ClosePreviewAsync();

    private async Task ClosePreviewAsync()
    {
        if (PreviewLayer.Visibility != Visibility.Visible) return;

        _previewSession++; // 让仍在飞行中的打开流程作废
        var item = _previewItem;
        var fromRect = HeroTransition.GetRect(PreviewImage, RootGrid);
        var toRect = _previewFromRect;

        if (item?.Thumbnail is not null && fromRect is { Width: > 0 } from && toRect is { Width: > 0 } target)
        {
            // 大图与卡片一起隐去，让 Hero 图层从大图位置缩回缩略图。
            PreviewImage.Opacity = 0;
            PreviewCard.Opacity = 0;
            PreviewScrim.Opacity = 0;
            await HeroTransition.PlayAsync(HeroImage, from, target, RootGrid, item.Thumbnail, opening: false);
        }

        ClosePreviewImmediately();
    }

    /// <summary>立即收起覆盖层并复位透明度。</summary>
    private void ClosePreviewImmediately()
    {
        PreviewLayer.Visibility = Visibility.Collapsed;
        PreviewLayer.IsHitTestVisible = false;
        PreviewCard.Opacity = 1;
        PreviewHeader.Opacity = 1;
        PreviewFooter.Opacity = 1;
        PreviewScrim.Opacity = 1;
        PreviewImage.Opacity = 0;
        PreviewImage.Source = null;
        _previewItem = null;
        _previewFromRect = null;
    }

    private void OnPreviewOpenFile(object sender, RoutedEventArgs e) =>
        PhotoActions.OpenWithDefaultApp(_previewItem?.LocalPath ?? "");

    // ---- 缩略图与预览图的右键菜单 ----
    private async void OnMenuCopyImage(object sender, RoutedEventArgs e)
    {
        if (PhotoActions.ItemFrom(sender) is { } item) await PhotoActions.CopyImageAsync(item);
    }

    private void OnMenuOpenWithDefault(object sender, RoutedEventArgs e)
    {
        if (PhotoActions.ItemFrom(sender) is { } item) PhotoActions.OpenWithDefaultApp(item.LocalPath);
    }

    private void OnMenuRevealInExplorer(object sender, RoutedEventArgs e)
    {
        if (PhotoActions.ItemFrom(sender) is { } item) PhotoActions.RevealInExplorer(item.LocalPath);
    }

    private void OnPreviewOpenFolder(object sender, RoutedEventArgs e) =>
        PhotoActions.RevealInExplorer(_previewItem?.LocalPath ?? "");

    private async void OnPreviewCopyImage(object sender, RoutedEventArgs e)
    {
        if (_previewItem is { } item) await PhotoActions.CopyImageAsync(item);
    }

    private void OnPreviewOpenWithDefault(object sender, RoutedEventArgs e) =>
        PhotoActions.OpenWithDefaultApp(_previewItem?.LocalPath ?? "");
}
