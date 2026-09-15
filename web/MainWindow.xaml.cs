using ForzaGallerySync.Models;
using ForzaGallerySync.Services;
using ForzaGallerySync.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ForzaGallerySync;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Type> _pages = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["gallery"] = typeof(GalleryPage),
        ["sync"] = typeof(SyncPage),
        ["settings"] = typeof(SettingsPage),
    };

    /// <summary>内容显示后启动的轻量状态轮询（标题栏状态区）。</summary>
    private CancellationTokenSource? _statusCts;

    /// <summary>防止「导航项选中」与「页面内部导航请求」互相触发造成重复导航。</summary>
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Forza Gallery Sync 控制台";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Activated += OnActivated;

        // 标题栏左留白：与导航栏的标准内缩宽度对齐（见 AlignTitleBarContent）。
        if (RootGrid is { } root)
        {
            root.SizeChanged += (_, _) => AlignTitleBarContent();
        }

        // 默认进入总览
        NavView.SelectedItem = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .First(i => (i.Tag as string) == "dashboard");
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    /// <summary>供页面请求跳转到指定页面（同时同步左侧选中项）。</summary>
    public void NavigateTo(string tag)
    {
        if (!_pages.TryGetValue(tag, out var pageType)) return;

        var target = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .Concat(NavView.FooterMenuItems.OfType<NavigationViewItem>())
            .FirstOrDefault(i => (i.Tag as string) == tag);

        _syncingSelection = true;
        try
        {
            if (target is not null) NavView.SelectedItem = target;
            if (ContentFrame.CurrentSourcePageType != pageType) ContentFrame.Navigate(pageType);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        _statusCts = new CancellationTokenSource();
        _ = PollStatusLoopAsync(_statusCts.Token);
    }

    /// <summary>
    /// 标题栏内容的左侧留白。
    ///
    /// 取 NavigationView 的标准内缩宽度（CompactPaneLength），让标题与导航栏的
    /// 左侧节奏一致；不使用系统标题栏按钮的内缩量——那个值在有多个窗口按钮时
    /// 接近 140px，会让标题显得被"推"到中间。
    /// </summary>
    private void AlignTitleBarContent()
    {
        var left = NavView?.CompactPaneLength ?? 48;
        AppTitleBar.Padding = new Thickness(left > 0 ? left : 48, 0, 12, 0);
    }

    private async Task PollStatusLoopAsync(CancellationToken token)
    {
        // 首次初始化 Python 运行时可能耗时较久；失败按间隔重试。
        while (!token.IsCancellationRequested)
        {
            try
            {
                var json = await PyBridge.Instance.CallJsonAsync("sync_progress");
                var status = Json.Deserialize<SyncProgressModel>(json);
                if (status is not null) UpdateStatus(status);
            }
            catch
            {
                // 状态轮询失败不影响导航，静默重试。
            }

            // 账号状态是全局信息：由窗口统一维护，页面不各自判断，
            // 避免某个页面在数据尚未加载时用默认值把它覆盖掉。
            // auth_status 只读配置、本地判断是否过期，不会触发网络刷新。
            try
            {
                var json = await PyBridge.Instance.CallJsonAsync("auth_status");
                // 注意：必须用 Models.Json（SnakeCaseLower 命名策略）来反序列化，
                // 裸的 JsonSerializer.Deserialize 无法把 Python 的 has_token
                // 映射到 HasToken，会静默拿到默认值（全 false）。
                var auth = Json.Deserialize<AuthModel>(json);
                if (auth is not null)
                {
                    SetAccountStatus(auth.HasToken, auth.Expired);
                }
                else
                {
                    Logger.Warn($"auth_status 反序列化失败: {json}");
                }
            }
            catch (Exception ex)
            {
                Logger.Exception("账号状态轮询失败", ex);
            }

            try
            {
                await Task.Delay(3000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>更新标题栏状态区（每 3 秒刷新一次）。</summary>
    private void UpdateStatus(SyncProgressModel status)
    {
        if (status.Running)
        {
            TitleStatusPanel.Visibility = Visibility.Visible;
            TitleStatusRing.Visibility = Visibility.Visible;
            TitleStatusRing.IsActive = true;
            TitleStatusIcon.Visibility = Visibility.Collapsed;
            TitleStatusText.Text = status.Done > 0 && status.Total > 0
                ? $"同步中 {status.Done}/{status.Total}"
                : "同步中";
        }
        else
        {
            TitleStatusRing.IsActive = false;
            TitleStatusRing.Visibility = Visibility.Collapsed;

            if (status.CancelRequested)
            {
                TitleStatusPanel.Visibility = Visibility.Visible;
                TitleStatusIcon.Visibility = Visibility.Visible;
                TitleStatusIcon.Glyph = "\uE711";
                TitleStatusText.Text = "正在取消";
            }
            else
            {
                TitleStatusPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>页脚账号状态（由窗口轮询统一维护，页面无需调用）。</summary>
    private void SetAccountStatus(bool hasToken, bool expired)
    {
        if (hasToken && !expired) SetAccount("已登录", "\uE77B", "AppSuccessBrush");
        else if (hasToken) SetAccount("Token 已过期", "\uE7BA", "AppWarnBrush");
        else SetAccount("未登录", "\uE7BA", "AppDangerBrush");
    }

    private void SetAccount(string text, string glyph, string brushKey)
    {
        AccountItem.Content = text;
        AccountIcon.Glyph = glyph;

        if (Application.Current.Resources.TryGetValue(brushKey, out var value) && value is Brush brush)
        {
            AccountIcon.Foreground = brush;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            if (_pages.TryGetValue(tag, out var pageType) && ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType);
            }
        }
    }
}
