using ForzaGallerySync.Models;
using ForzaGallerySync.Services;
using ForzaGallerySync.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ForzaGallerySync.Views;

public sealed partial class GalleryPage : Page
{
    public GalleryViewModel VM { get; } = new();

    private PhotoItemViewModel? _detailItem;
    private bool _sidebarExpanded = true;
    private Windows.Foundation.Rect? _heroFromRect; // 点击缩略图的位置（RootGrid 坐标），用于 Hero 转场

    /// <summary>详情会话号：递增即可让仍在飞行中的异步流程作废。</summary>
    private int _detailSession;

    /// <summary>详情信息栏展开时的宽度。</summary>
    private const double DetailWidth = 360;

    public GalleryPage()
    {
        InitializeComponent();
        VM.OpenPathRequested += OnOpenPath;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 下拉列表填充后，在下一帧默认选中「全部」，避免在集合修改同步阶段设置
        // SelectedItem 触发越界异常。
        VM.GamesList.CollectionChanged += (_, _) =>
        {
            if (VM.GamesList.Count > 0)
            {
                Ui.Dispatcher?.TryEnqueue(() =>
                {
                    try { GameCombo.SelectedItem = VM.GamesList[0]; } catch { }
                });
            }
        };
        VM.Months.CollectionChanged += (_, _) =>
        {
            if (VM.Months.Count > 0)
            {
                Ui.Dispatcher?.TryEnqueue(() =>
                {
                    try { MonthCombo.SelectedItem = VM.Months[0]; } catch { }
                });
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => VM.Start();

    private void OnUnloaded(object sender, RoutedEventArgs e) => VM.Stop();

    // ---- 工具栏 ----
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            VM.Query = sender.Text;
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        VM.Query = sender.Text;
        _ = VM.GoToPageAsync(0);
    }

    private void OnGameChanged(object sender, SelectionChangedEventArgs e) =>
        VM.Game = GameCombo.SelectedItem is GameInfo g ? g.Id : "";

    private void OnMonthChanged(object sender, SelectionChangedEventArgs e) =>
        VM.Month = MonthCombo.SelectedItem is MonthOption m ? m.Value : "";

    private async void OnRefresh(object sender, RoutedEventArgs e) => await VM.GoToPageAsync(VM.Page);

    private void OnOpenDownloadDir(object sender, RoutedEventArgs e) => VM.OpenPath(VM.DownloadDir, false);

    /// <summary>
    /// 缩略图按可用宽度自适应：先按目标宽度估算列数，再把宽度均分到整列，
    /// 让每行末尾不留大小不一的空隙。
    ///
    /// 尺寸设在 ItemsWrapGrid 上（而不是 GridView.ItemWidth）——后者会让本项目的
    /// XAML 编译器在生成代码阶段失败。
    /// </summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width.Equals(e.PreviousSize.Width)) return;

        if (FindWrapPanel() is not { } panel) return;

        const double gap = 8;      // GridViewItem 左右各 4 的外边距
        const double target = 188; // 目标缩略图宽度

        var usable = e.NewSize.Width - gap;
        if (usable <= target) return;

        var columns = Math.Max(1, Math.Floor(usable / target));
        var width = Math.Floor(usable / columns);

        panel.ItemWidth = width;
        panel.ItemHeight = Math.Round(width * 0.66);
    }

    /// <summary>取出 GridView 实际使用的 ItemsWrapGrid 面板。</summary>
    private ItemsWrapGrid? FindWrapPanel()
    {
        if (PhotoGrid.ItemsPanelRoot is ItemsWrapGrid direct) return direct;

        // 面板尚未创建时，沿可视树找一次。
        return FindDescendant<ItemsWrapGrid>(PhotoGrid);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---- 分页 ----
    private async void OnPageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int page)
        {
            await VM.GoToPageAsync(page);
        }
    }

    private async void OnPrevPage(object sender, RoutedEventArgs e) => await VM.GoToPageAsync(VM.Page - 1);

    private async void OnNextPage(object sender, RoutedEventArgs e) => await VM.GoToPageAsync(VM.Page + 1);

    // ---- 照片点击 / 右键菜单 ----
    private async void OnPhotoClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PhotoItemViewModel item)
        {
            _heroFromRect = GetItemRect(item);
            await OpenDetailAsync(item);
        }
    }

    private async void OnMenuViewDetail(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItemViewModel item)
        {
            _heroFromRect = GetItemRect(item);
            await OpenDetailAsync(item);
        }
    }

    private void OnMenuOpenDir(object sender, RoutedEventArgs e)
    {
        if (PhotoActions.ItemFrom(sender) is { } item) PhotoActions.RevealInExplorer(item.LocalPath);
    }

    private async void OnMenuCopyImage(object sender, RoutedEventArgs e)
    {
        if (PhotoActions.ItemFrom(sender) is { } item) await PhotoActions.CopyImageAsync(item);
    }

    private void OnMenuOpenWithDefault(object sender, RoutedEventArgs e)
    {
        if (PhotoActions.ItemFrom(sender) is { } item) PhotoActions.OpenWithDefaultApp(item.LocalPath);
    }

    // ---- 详情大图的菜单与按钮 ----
    private async void OnDetailCopyImage(object sender, RoutedEventArgs e)
    {
        if (_detailItem is { } item) await PhotoActions.CopyImageAsync(item);
    }

    private void OnDetailRevealInExplorer(object sender, RoutedEventArgs e)
    {
        if (_detailItem is { } item) PhotoActions.RevealInExplorer(item.LocalPath);
    }

    // ---- 照片详情 / Hero 转场 ----

    /// <summary>
    /// 打开详情。
    ///
    /// 时序很关键：先用**已缓存的缩略图 + 当前已知字段**铺好详情布局并立刻起帧，
    /// 再把 photo_meta 与全尺寸原图放到后台加载、就绪后替换。若先 await 这两步，
    /// 用户点击后会先停顿一下、然后详情整块出现，放大动画便像是事后补播。
    /// </summary>
    private async Task OpenDetailAsync(PhotoItemViewModel item)
    {
        _detailSession++;
        var session = _detailSession;
        _detailItem = item;

        try
        {
            // 1) 立刻用已知信息铺满详情：图片先用缩略图占位（动画期间不可见），
            //    信息栏也先隐藏，随动画一起淡入。
            DetailImage.Source = item.Thumbnail;
            FillDetailFields(item);

            ShowDetailLayout();
            DetailImage.Opacity = 0;
            DetailSidebar.Opacity = 0;

            // 强制布局，确保 DetailImage 已完成布局、取到正确的目标矩形。
            RootGrid.UpdateLayout();

            // 2) 后台加载元数据与原图，不阻塞起帧。
            var metaTask = VM.OpenDetailAsync(item);
            var fullImageTask = item.LoadFullImageAsync();

            // 3) 立刻起帧。hero 图层在动画结束后不立即隐藏，等目标内容真正显示出来再交接。
            var fromRect = _heroFromRect;
            var toRect = HeroTransition.GetRect(DetailImage, RootGrid);
            if (item.Thumbnail is not null && fromRect is { Width: > 0 } fr && toRect is { Width: > 0 } tr)
            {
                await HeroTransition.PlayAsync(HeroImage, fr, tr, RootGrid, item.Thumbnail,
                    opening: true, hideWhenDone: false);
            }
            else
            {
                // 拿不到可信的起止矩形时不做转场，但记录一次以便定位（正常情况下不该出现）。
                Logger.Warn($"[Hero] 打开详情未播放转场：缩略图={Describe(fromRect)}，目标={Describe(toRect)}");
                HeroTransition.Hide(HeroImage);
            }

            if (session != _detailSession) return; // 动画期间已被关闭或切换

            // 4) 交接：优先直接用原图收尾，避免"先缩略图、再原图"两次画面变化。
            await metaTask;
            var fullImage = await WaitForAsync(fullImageTask, TimeSpan.FromMilliseconds(450));
            if (session != _detailSession) return;

            DetailImage.Source = fullImage ?? item.Thumbnail;
            DetailImage.Opacity = 0;
            await Task.WhenAll(FadeInAsync(DetailImage, 160), FadeInAsync(DetailSidebar, 160));

            if (session != _detailSession) return;

            // 等这一帧真正画出来，再撤掉 hero 覆盖层：否则两者之间有"什么都没画"的一帧。
            await Task.Delay(32);
            HeroTransition.Hide(HeroImage);

            // 若原图比动画慢，再补一次交叉淡入（此时下层已有内容，不会闪）。
            if (fullImage is null)
            {
                var late = await fullImageTask;
                if (session == _detailSession && late is not null && !ReferenceEquals(DetailImage.Source, late))
                {
                    await CrossfadeAsync(DetailImage, late, (ImageSource?)DetailImage.Source, 180);
                }
            }

            FillDetailFields(item);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开详情失败: {ex}");
            // 即使异常也保证详情布局正常显示，避免崩溃。
            ShowDetailLayout();
            DetailImage.Opacity = 1;
        }
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

    /// <summary>把照片信息写入详情栏（元数据加载完成后会再填一次）。</summary>
    private void FillDetailFields(PhotoItemViewModel item)
    {
        DetailTitle.Text = string.IsNullOrEmpty(item.Title) ? "无标题" : item.Title;
        DetailGame.Text = item.GameName;
        DetailPhotoId.Text = item.PhotoId;
        DetailSubmitted.Text = Format.Time(item.SubmissionTimeUtc);
        DetailDownloaded.Text = Format.Time(item.DownloadedAt);
        DetailPath.Text = string.IsNullOrEmpty(item.LocalPath) ? "—" : item.LocalPath;
        DetailDesc.Text = string.IsNullOrEmpty(item.Description) ? "无描述" : item.Description;
    }

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

    /// <summary>显示详情布局（隐藏图库 / 状态层 / 分页，显示返回按钮，重置信息栏）。</summary>
    private void ShowDetailLayout()
    {
        DetailView.Visibility = Visibility.Visible;
        PhotoGrid.Visibility = Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Collapsed;
        Pager.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Visible;

        // 每次打开详情时重置为展开状态。
        _sidebarExpanded = true;
        DetailColumn.Width = new GridLength(DetailWidth);
        ToggleSidebarIcon.Glyph = "\uE76C";
    }

    private async void OnDetailClose(object sender, RoutedEventArgs e) => await ShowGalleryAsync();

    /// <summary>返回图库：共享元素动画从大图缩小回缩略图位置。</summary>
    private async Task ShowGalleryAsync()
    {
        _detailSession++; // 作废仍在飞行中的打开流程

        try
        {
            var item = _detailItem;
            var fromRect = HeroTransition.GetRect(DetailImage, RootGrid);
            var toRect = _heroFromRect;

            if (item?.Thumbnail is not null && fromRect is { Width: > 0 } fr && toRect is { Width: > 0 } tr)
            {
                // 大图先隐藏，由 Hero 图层从大图位置缩回缩略图。
                DetailImage.Opacity = 0;
                ShowGallery();
                await HeroTransition.PlayAsync(HeroImage, fr, tr, RootGrid, item.Thumbnail, opening: false);
            }
            else
            {
                ShowGallery();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"返回图库失败: {ex}");
            ShowGallery();
        }
    }

    private void ShowGallery()
    {
        DetailView.Visibility = Visibility.Collapsed;
        DetailSidebar.Opacity = 0;   // 下次打开时重新淡入
        PhotoGrid.Visibility = Visibility.Visible;
        StatusOverlay.Visibility = Visibility.Visible;
        Pager.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Collapsed;
    }

    private static string Describe(Windows.Foundation.Rect? r) =>
        r is { } v ? $"({v.X:F0},{v.Y:F0},{v.Width:F0}x{v.Height:F0})" : "无";

    /// <summary>切换信息栏的展开 / 收起（详情列宽平滑动画，图片自适应窗口）。</summary>
    private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar(!_sidebarExpanded);

    private void ToggleSidebar(bool expand)
    {
        _sidebarExpanded = expand;

        // 详情列宽平滑动画：展开 DetailWidth / 收起 0；图片列自动扩展，图片自适应窗口。
        _ = AnimateColumnWidthAsync(DetailColumn, expand ? 0 : DetailWidth, expand ? DetailWidth : 0, 260);

        ToggleSidebarIcon.Glyph = expand ? "\uE76C" : "\uE76B";
    }

    /// <summary>定时器逐帧驱动列宽动画（WinUI 无内置 GridLength 平滑动画）。</summary>
    private async Task AnimateColumnWidthAsync(ColumnDefinition column, double from, double to, int durationMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double elapsed;
        do
        {
            elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / durationMs);
            var eased = HeroTransition.EaseInOutCubic(t);
            column.Width = new GridLength(from + (to - from) * eased);
            await Task.Delay(16);
        } while (elapsed < durationMs);
        column.Width = new GridLength(to);
    }

    // ---- Hero 转场辅助 ----
    /// <summary>取缩略图所在容器在 RootGrid 坐标系中的矩形。</summary>
    private Windows.Foundation.Rect? GetItemRect(PhotoItemViewModel item) =>
        PhotoGrid.ContainerFromItem(item) is FrameworkElement container
            ? HeroTransition.GetRect(container, RootGrid)
            : null;

    private void OnDetailOpenFile(object sender, RoutedEventArgs e)
    {
        // 用系统默认的图片应用打开（不是 explorer 的"选中文件"）。
        if (_detailItem is { } item) PhotoActions.OpenWithDefaultApp(item.LocalPath);
    }

    /// <summary>打开下载目录等路径（仅用于"打开目录"按钮）。</summary>
    private void OnOpenPath(string path, bool isFile)
    {
        if (isFile) PhotoActions.OpenWithDefaultApp(path);
        else PhotoActions.OpenFolder(path);
    }
}
