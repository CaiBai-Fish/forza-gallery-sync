using Microsoft.UI.Dispatching;

namespace ForzaGallerySync.Services;

/// <summary>
/// UI 线程调度帮助：让 ViewModel 在后台线程安全地更新 UI 属性。
/// </summary>
public static class Ui
{
    /// <summary>主窗口所在线程的 DispatcherQueue，在 App.OnLaunched 中赋值。</summary>
    public static DispatcherQueue? Dispatcher { get; set; }

    /// <summary>在 UI 线程执行 action；已在 UI 线程则直接执行。</summary>
    public static void Run(Action action)
    {
        if (Dispatcher is null || Dispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        _ = Dispatcher.TryEnqueue(() => action());
    }
}
