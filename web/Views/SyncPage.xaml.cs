using ForzaGallerySync.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ForzaGallerySync.Views;

public sealed partial class SyncPage : Page
{
    public SyncViewModel VM { get; } = new();

    public SyncPage()
    {
        InitializeComponent();

        // InfoBar.IsOpen 用经典 Binding 绑定（x:Bind 指向该属性会让本项目的
        // XAML 代码生成阶段失败），因此这里必须设置 DataContext。
        DataContext = VM;

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
