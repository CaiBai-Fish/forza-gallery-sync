using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ForzaGallerySync.ViewModels;

namespace ForzaGallerySync.Views;

public sealed partial class SyncPage : Page
{
    public SyncViewModel VM { get; } = new();

    public SyncPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await VM.LoadConfigAsync();
        VM.Start(); // 启动 1 秒进度轮询
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => VM.Stop();

    private async void OnStart(object sender, RoutedEventArgs e) => await VM.StartAsync();

    private async void OnStop(object sender, RoutedEventArgs e) => await VM.StopAsync();
}
