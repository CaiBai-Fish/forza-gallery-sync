using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ForzaGallerySync.Services;
using ForzaGallerySync.ViewModels;

namespace ForzaGallerySync.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel VM { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        VM.PickDirRequested += OnPickDirRequested;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await VM.LoadAsync();
        await VM.LoadVersionAsync();
        VM.Start(); // 启动 2 秒登录状态轮询
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => VM.Stop();

    private async void OnLogin(object sender, RoutedEventArgs e) => await VM.StartLoginAsync();

    private async void OnRefreshToken(object sender, RoutedEventArgs e) => await VM.RefreshTokenAsync();

    private async void OnSave(object sender, RoutedEventArgs e) => await VM.SaveAsync();

    private async void OnReload(object sender, RoutedEventArgs e) => await VM.LoadAsync();

    private async void OnCheckUpdate(object sender, RoutedEventArgs e) => await VM.CheckUpdateAsync();

    private void OnOpenUpdate(object sender, RoutedEventArgs e) => OpenUrl(VM.UpdateUrl);

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer",
                UseShellExecute = false,
            };
            psi.Arguments = $"\"{url}\"";
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // 打开失败忽略。
        }
    }

    private async void OnPickDirRequested()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");

            // WinUI 3 的 FolderPicker 需要关联窗口句柄。
            if (App.MainWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                VM.DownloadDir = folder.Path;
            }
        }
        catch
        {
            // 用户取消或初始化失败。
        }
    }

    private void OnPickDir(object sender, RoutedEventArgs e) => OnPickDirRequested();
}
