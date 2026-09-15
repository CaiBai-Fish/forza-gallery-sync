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
    /// 「下载并更新」：先给确认对话框，再下载。
    ///
    /// 确认这一步不是装饰：确认之后会**退出应用并覆盖程序目录里的文件**，
    /// 用户需要在动手前知道要发生什么。对话框同时说明哈希校验、只覆盖不删除、
    /// 以及不能自动更新时的替代做法。
    /// </summary>
    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUpdateAsync()) return;
        await VM.DownloadAndApplyUpdateAsync();
    }

    private async Task<bool> ConfirmUpdateAsync()
    {
        var canAuto = UpdateService.CanAutoUpdate(out var reason);
        var target = string.IsNullOrWhiteSpace(VM.LatestVersion) ? "新版本" : $"v{VM.LatestVersion}";

        var body = canAuto
            ? $"将下载 {target} 的发布包，校验 SHA256 后退出本程序，"
              + "覆盖程序目录里的文件（只覆盖、不删除任何文件），然后自动重启。"
              + "\n\n下载与校验都需要网络；期间请勿手动结束进程。"
            : $"无法自动更新：{reason}\n\n请在打开的下载页手动下载发布包并替换程序目录。";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = canAuto ? $"更新到 {target}？" : "需要手动更新",
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
            PrimaryButtonText = canAuto ? "下载并更新" : "打开发布页",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return false;

        if (!canAuto)
        {
            OpenUrl(VM.UpdateUrl);
            return false;
        }

        return true;
    }

    private static void OnUpdateRequested()
    {
        Logger.Info("退出应用以便更新脚本覆盖程序文件");
        // 用 Environment.Exit 而不是 Application.Exit：确保进程立刻消失，
        // 否则脚本要等满 60 秒超时才继续（文件仍被占用，覆盖会失败）。
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
