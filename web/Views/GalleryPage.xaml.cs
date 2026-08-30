using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ForzaGallerySync.Services;
using ForzaGallerySync.ViewModels;

namespace ForzaGallerySync.Views;

public sealed partial class GalleryPage : Page
{
    public GalleryViewModel VM { get; } = new();

    private PhotoItemViewModel? _detailItem;
    private bool _sidebarExpanded = true;
    private Windows.Foundation.Rect? _heroFromRect; // 点击缩略图的位置（RootGrid 坐标），用于 Hero 转场

    public GalleryPage()
    {
        InitializeComponent();
        VM.OpenPathRequested += OnOpenPath;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 下拉列表填充后，在下一帧（异步）默认选中"全部"，
        // 避免在集合修改同步阶段设置 SelectedItem 触发越界异常。
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

    private void OnGameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameCombo.SelectedItem is Models.GameInfo g)
        {
            VM.Game = g.Id;
        }
        else
        {
            VM.Game = "";
        }
    }

    private void OnMonthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonthCombo.SelectedItem is MonthOption m)
        {
            VM.Month = m.Value;
        }
        else
        {
            VM.Month = "";
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await VM.GoToPageAsync(VM.Page);

    private void OnOpenDownloadDir(object sender, RoutedEventArgs e) => VM.OpenPath(VM.DownloadDir, false);

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

    private void OnMenuOpenFile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItemViewModel item)
        {
            VM.OpenPath(item.LocalPath, true);
        }
    }

    private void OnMenuOpenDir(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItemViewModel item)
        {
            var idx = Math.Max(item.LocalPath.LastIndexOf('\\'), item.LocalPath.LastIndexOf('/'));
            var dir = idx > 0 ? item.LocalPath[..idx] : "";
            VM.OpenPath(dir, false);
        }
    }

    // ---- 照片详情 / Hero 转场 ----
    private async Task OpenDetailAsync(PhotoItemViewModel item)
    {
        try
        {
            await VM.OpenDetailAsync(item); // 加载 photo_meta
            var fullImage = await item.LoadFullImageAsync(); // 全尺寸原图（详情大图）

            DetailImage.Source = fullImage ?? item.Thumbnail;
            DetailTitle.Text = string.IsNullOrEmpty(item.Title) ? "无标题" : item.Title;
            DetailGame.Text = item.GameName;
            DetailPhotoId.Text = item.PhotoId;
            DetailSubmitted.Text = Format.Time(item.SubmissionTimeUtc);
            DetailDownloaded.Text = Format.Time(item.DownloadedAt);
            DetailPath.Text = string.IsNullOrEmpty(item.LocalPath) ? "—" : item.LocalPath;
            DetailDesc.Text = string.IsNullOrEmpty(item.Description) ? "无描述" : item.Description;
            _detailItem = item;

            // 切换到详情布局，但大图先隐藏，供 Hero 动画过渡。
            ShowDetailLayout();
            DetailImage.Opacity = 0;

            // 强制布局，确保 DetailImage 已完成布局、取到正确的目标矩形。
            RootGrid.UpdateLayout();

            // Hero 动画：从缩略图位置放大到大图位置（动画期间半透明）。
            if (item.Thumbnail is not null && _heroFromRect is Windows.Foundation.Rect fr && fr.Width > 0)
            {
                var toRect = GetRect(DetailImage, RootGrid);
                if (toRect is Windows.Foundation.Rect tr)
                {
                    HeroImage.Source = item.Thumbnail;
                    await AnimateHeroAsync(fr, tr);
                }
            }
            DetailImage.Opacity = 1;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开详情失败: {ex}");
            // 即使异常也保证详情布局正常显示，避免崩溃。
            ShowDetailLayout();
            DetailImage.Opacity = 1;
        }
    }

    /// <summary>显示详情布局（隐藏图库 / 状态层 / 分页，显示返回按钮，重置侧边栏）。</summary>
    private void ShowDetailLayout()
    {
        DetailView.Visibility = Visibility.Visible;
        PhotoGrid.Visibility = Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Collapsed;
        Pager.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Visible;

        // 每次打开详情时重置为展开状态。
        _sidebarExpanded = true;
        DetailColumn.Width = new GridLength(340);
        ToggleSidebarIcon.Text = ">";
    }

    private async void OnDetailClose(object sender, RoutedEventArgs e) => await ShowGalleryAsync();

    /// <summary>返回图库：Hero 动画从大图缩小到缩略图位置。</summary>
    private async Task ShowGalleryAsync()
    {
        try
        {
            var item = _detailItem;
            if (item?.Thumbnail is not null && _heroFromRect is Windows.Foundation.Rect fr && fr.Width > 0 &&
                GetRect(DetailImage, RootGrid) is Windows.Foundation.Rect fromRect)
            {
                DetailImage.Opacity = 0;
                HeroImage.Source = item.Thumbnail;
                ShowGallery();
                await AnimateHeroAsync(fromRect, fr);
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
        PhotoGrid.Visibility = Visibility.Visible;
        StatusOverlay.Visibility = Visibility.Visible;
        Pager.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Collapsed;
    }

    /// <summary>切换信息侧边栏的展开 / 收纳状态（详情列宽平滑动画，图片自适应窗口）。</summary>
    private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar(!_sidebarExpanded);

    private void ToggleSidebar(bool expand)
    {
        _sidebarExpanded = expand;

        // 详情列宽平滑动画：展开 340 / 收起 0；图片列自动扩展，图片自适应窗口。
        _ = AnimateColumnWidthAsync(DetailColumn, expand ? 0 : 340, expand ? 340 : 0, 260);

        ToggleSidebarIcon.Text = expand ? ">" : "<";
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
            var eased = EaseInOutCubic(t);
            column.Width = new GridLength(from + (to - from) * eased);
            await Task.Delay(16);
        } while (elapsed < durationMs);
        column.Width = new GridLength(to);
    }

    private static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    // ---- Hero 转场辅助 ----
    /// <summary>取缩略图所在容器在 RootGrid 坐标系中的矩形。</summary>
    private Windows.Foundation.Rect? GetItemRect(PhotoItemViewModel item)
    {
        if (PhotoGrid.ContainerFromItem(item) is FrameworkElement container)
        {
            return GetRect(container, RootGrid);
        }
        return null;
    }

    private Windows.Foundation.Rect? GetRect(FrameworkElement element, FrameworkElement relativeTo)
    {
        try
        {
            if (element is null || relativeTo is null) return null;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
            var topLeft = element.TransformToVisual(relativeTo)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            // 布局未完成时坐标可能为 NaN / Infinity，构造 Rect 会抛“值不在预期范围内”异常。
            if (double.IsNaN(topLeft.X) || double.IsNaN(topLeft.Y) ||
                double.IsInfinity(topLeft.X) || double.IsInfinity(topLeft.Y))
            {
                return null;
            }
            return new Windows.Foundation.Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把图片从 from 矩形逐帧平移到 to 矩形（RenderTransform 缩放平移，不触发布局，动画流畅）。</summary>
    private async Task AnimateHeroAsync(Windows.Foundation.Rect from, Windows.Foundation.Rect to)
    {
        // 无效矩形（布局未完成时 ActualWidth/Height 可能为 NaN / 0）直接跳过动画，避免越界异常。
        if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0 ||
            double.IsNaN(from.X) || double.IsNaN(from.Y) || double.IsNaN(from.Width) || double.IsNaN(from.Height) ||
            double.IsNaN(to.X) || double.IsNaN(to.Y) || double.IsNaN(to.Width) || double.IsNaN(to.Height) ||
            double.IsInfinity(from.X) || double.IsInfinity(from.Y) || double.IsInfinity(to.X) || double.IsInfinity(to.Y))
        {
            return;
        }

        // HeroImage 位于 RootGrid 的 Padding 内容区，把相对 RootGrid 的坐标折算到内容区。
        double padLeft = RootGrid.Padding.Left;
        double padTop = RootGrid.Padding.Top;
        double fx = from.X - padLeft, fy = from.Y - padTop;
        double tx = to.X - padLeft, ty = to.Y - padTop;

        // 布局尺寸固定为目标尺寸，缩放/平移走 RenderTransform（不触发布局重排，更流畅）。
        HeroImage.Width = to.Width;
        HeroImage.Height = to.Height;
        HeroImage.Opacity = 0.5; // 动画期间半透明
        HeroImage.Visibility = Visibility.Visible;

        double sx0 = from.Width / to.Width;
        double sy0 = from.Height / to.Height;
        HeroScale.ScaleX = sx0;
        HeroScale.ScaleY = sy0;
        HeroTransform.X = fx;
        HeroTransform.Y = fy;

        const int durationMs = 300;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double elapsed;
        do
        {
            elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / durationMs);
            var eased = EaseInOutCubic(t);
            HeroScale.ScaleX = sx0 + (1.0 - sx0) * eased;
            HeroScale.ScaleY = sy0 + (1.0 - sy0) * eased;
            HeroTransform.X = fx + (tx - fx) * eased;
            HeroTransform.Y = fy + (ty - fy) * eased;
            await Task.Delay(16);
        } while (elapsed < durationMs);

        HeroScale.ScaleX = 1;
        HeroScale.ScaleY = 1;
        HeroTransform.X = tx;
        HeroTransform.Y = ty;
        HeroImage.Visibility = Visibility.Collapsed;
        HeroImage.Source = null;
    }

    private void OnDetailOpenFile(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not null)
        {
            VM.OpenPath(_detailItem.LocalPath, true);
        }
    }

    private void OnOpenPath(string path, bool isFile)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer",
                UseShellExecute = false,
            };
            psi.Arguments = isFile ? $"/select,\"{path}\"" : $"\"{path}\"";
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // 打开失败忽略。
        }
    }
}
