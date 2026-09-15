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
    /// <summary>
    /// 主窗口引用。类型为具体窗口类型，便于页面调用导航 / 状态栏更新等窗口级能力
    /// （目录选择器需要窗口句柄时也能直接用）。
    /// </summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // 启动就把版本写进日志：排查"当前版本判定不对"时，这是第一手信息。
        // 版本权威来源是程序集信息（发布时由 make-gui.ps1 注入，取自 pyproject.toml）。
        Logger.Info($"程序集版本：{(string.IsNullOrEmpty(AppVersion.Current) ? "(未注入)" : AppVersion.Current)}");

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

        // 单实例：第二个实例不建窗口，唤醒已有窗口后显式结束自己。
        // 必须显式退出——WinUI 3 里只从启动回调 return 不会结束进程，
        // 消息循环照旧跑着，任务管理器里会残留一个没有窗口的进程。
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExistingInstance();
            SingleInstance.ExitAsSecondary();
            return;
        }

        // 记录 UI 线程调度器，供 ViewModel 后台线程安全更新 UI。
        Ui.Dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        MainWindow = new MainWindow();
        MainWindow.Activate();

        // 窗口句柄要等窗口真正建出来才有，且必须在 Activate 之后登记：
        // 登记过早会拿到 0，导致第二个实例的唤醒广播收不到。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
        SingleInstance.RegisterWindow(hwnd);
    }
}
