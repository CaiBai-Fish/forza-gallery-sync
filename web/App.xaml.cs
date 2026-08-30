using Microsoft.UI.Xaml;
using ForzaGallerySync.Services;

namespace ForzaGallerySync;

/// <summary>
/// 应用入口。架构：WinUI 3（C# + XAML）窗口，通过 Python.NET 在进程内
/// 嵌入 Python 解释器，直接调用 :mod:`forza_sync.service` 的纯函数。
/// 完全无 HTTP 服务、无端口、无网络监听（与旧 Tauri/PyO3 架构一致）。
/// </summary>
public partial class App : Application
{
    /// <summary>主窗口引用（供目录选择器等需要窗口句柄的场景使用）。</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // 捕获未处理异常：记录到日志并阻止应用崩溃。
        UnhandledException += (_, e) =>
        {
            Logger.Exception("未处理异常", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Logger.Info("应用启动");

        // 记录 UI 线程调度器，供 ViewModel 后台线程安全更新 UI。
        Ui.Dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
