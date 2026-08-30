using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ForzaGallerySync.Views;

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

    public MainWindow()
    {
        InitializeComponent();
        Title = "Forza Gallery Sync 控制台";

        // 默认进入仪表盘
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            if (_pages.TryGetValue(tag, out var pageType))
            {
                ContentFrame.Navigate(pageType);
            }
        }
    }
}
