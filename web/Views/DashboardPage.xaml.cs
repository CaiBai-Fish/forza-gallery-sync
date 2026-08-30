using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ForzaGallerySync.ViewModels;

namespace ForzaGallerySync.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel VM { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();
        VM.NavigateRequested += OnNavigate;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => VM.Start();

    private void OnUnloaded(object sender, RoutedEventArgs e) => VM.Stop();

    private void OnNavigate(string page)
    {
        Frame.Navigate(page switch
        {
            "gallery" => typeof(GalleryPage),
            "sync" => typeof(SyncPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        });
    }

    private void OnGoSettings(object sender, RoutedEventArgs e) => OnNavigate("settings");
    private void OnGoSync(object sender, RoutedEventArgs e) => OnNavigate("sync");
    private void OnGoGallery(object sender, RoutedEventArgs e) => OnNavigate("gallery");

    private async void OnRefresh(object sender, RoutedEventArgs e) => await VM.LoadAsync();
}
