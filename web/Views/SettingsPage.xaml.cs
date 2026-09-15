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
        // 替换脚本会等本进程退出再覆盖文件，所以收到通知后必须真正结束进程
        VM.UpdateRequested += OnUpdateRequested;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 「下载并安装」：直接走更新流程（下载安装程序 → 校验 SHA256 → 静默安装 → 重启），
    /// **不再弹二次确认**。
    ///
    /// 为什么去掉确认框：点这个按钮本身就是用户的确认动作，再问一次是多余的一步
    /// （而且确认之后的行为已经固定：退出应用、由安装程序接管）。可能造成麻烦的
    /// 前提——程序目录不可写、拿不到官方哈希清单——都会在流程内部被拦住并给出提示，
    /// 见 <see cref="UpdateService.CanAutoUpdate"/> 与
    /// <see cref="UpdateService.DownloadInstallerAsync"/> 的校验。
    /// </summary>
    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        // 唯一需要提前拦的情况：程序目录不可写（装了也覆盖不了），直接给手动出口
        if (!UpdateService.CanAutoUpdate(out var reason))
        {
            await ShowManualUpdateDialogAsync(reason);
            return;
        }

        await VM.DownloadAndApplyUpdateAsync();
    }

    /// <summary>无法自动更新时的手动出口（只有这一种情况才弹对话框）。</summary>
    private async Task ShowManualUpdateDialogAsync(string reason)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "需要手动更新",
            Content = new TextBlock
            {
                Text = $"无法自动更新：{reason}\n\n可以在打开的下载页手动下载安装包并运行。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            },
            PrimaryButtonText = "打开发布页",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            OpenUrl(VM.UpdateUrl);
        }
    }

    private static void OnUpdateRequested()
    {
        Logger.Info("退出应用，把控制权交给安装脚本（它等本进程退出后静默运行安装程序）");
        // 用 Environment.Exit 而不是 Application.Exit：确保进程立刻消失，
        // 否则脚本要等满 60 秒超时才继续（文件仍被占用，安装会失败）。
        Environment.Exit(0);
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
