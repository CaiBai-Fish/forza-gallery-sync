using System.Runtime.InteropServices;
using System.Threading;

namespace ForzaGallerySync.Services;

/// <summary>
/// 单实例限制：同一次登录会话内只允许一个窗口。
///
/// 行为（与全局约定一致）：
/// - 第一个实例持有命名互斥体，并登记主窗口句柄、挂上唤醒钩子；
/// - 第二个实例**不建窗口**，用 <c>PostMessage(HWND_BROADCAST, ...)</c> 广播唤醒请求
///   （消息值来自 <c>RegisterWindowMessage</c>，无需跨进程传窗口句柄），
///   然后**显式结束自己的进程**；
/// - 已运行的实例收到唤醒请求时前置窗口；若处于最小化状态则先还原。
///
/// 为什么必须显式退出：WinUI 3 里"不建窗口就直接返回"不会结束进程——
/// 消息循环照旧跑着，任务管理器里会残留一个没有窗口的同名进程（实测如此）。
/// 所以这里用 <c>Environment.Exit(0)</c>。
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// 单一实例广播用的窗口消息（<c>RegisterWindowMessage</c> 返回全局唯一值）。
    /// 新实例用 <c>PostMessage(HWND_BROADCAST, ...)</c> 广播，已运行实例收到后前置窗口。
    /// 用广播而不是"只通知互斥体的持有者"，是因为后者需要把窗口句柄跨进程传出来，
    /// 而广播不需要任何共享状态，也天然覆盖"互斥体持有者恰好不是窗口线程"的情况。
    /// </summary>
    private static uint _showMessage;

    /// <summary>用户级命名（<c>Local\</c>）：只限制当前登录会话，不影响其他用户同时使用。</summary>
    private const string MutexName = @"Local\ForzaGallerySync.SingleInstance.v1";

    private static Mutex? _mutex;

    /// <summary>主窗口句柄与原始窗口过程（子类化后用于转发未处理的消息）。</summary>
    private static nint _hwnd;
    private static nint _originalWndProc;

    /// <summary>
    /// 子类化用的委托。必须是静态字段：一旦被 GC 回收，原生代码回调到已释放的
    /// 委托上会直接崩进程（<c>SetWindowLongPtr</c> 不会持有托管引用）。
    /// </summary>
    private static NativeWndProc? _wndProcDelegate;

    private delegate nint NativeWndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// 尝试取得独占权。
    /// </summary>
    /// <returns><c>true</c> 表示本进程是唯一实例，可以继续建窗口；<c>false</c> 表示已有实例，调用方应立即结束进程。</returns>
    public static bool TryAcquire()
    {
        // 全局消息先注册：已运行实例与新实例都要用同一个消息值
        _showMessage = RegisterWindowMessage("ForzaGallerySync.ShowWindow.2F3A9C41");

        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out _);
            if (_mutex.WaitOne(0))
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseQuietly();
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例被强杀（没有正常退出），互斥体已被系统放弃：本次取得所有权，继续启动
            Logger.Warn("检测到上一次运行未正常退出（互斥体被放弃），本次作为唯一实例继续启动");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseQuietly();
            return true;
        }
        catch (Exception ex)
        {
            // 拿不到互斥体不阻塞启动：宁可多开一个窗口，也不要让程序起不来
            Logger.Warn($"单实例互斥体创建失败（{ex.Message}），本次不做单实例限制");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 登记主窗口并挂上唤醒钩子。
    ///
    /// 广播消息（<c>HWND_BROADCAST</c>）会送到所有顶层窗口，包括本窗口；
    /// 这里子类化窗口过程，收到唤醒消息时前置自己。失败只记日志——
    /// 单实例的"不重复启动"由互斥体保证，钩子只是让已有窗口浮到前台。
    /// </summary>
    public static void RegisterWindow(nint hwnd)
    {
        _hwnd = hwnd;
        Logger.Info($"单实例：已注册主窗口 hwnd=0x{hwnd:X}");

        try
        {
            _wndProcDelegate = SubclassWndProc;
            _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

            if (_originalWndProc == 0)
            {
                Logger.Warn($"单实例：窗口过程挂钩失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"单实例：窗口过程挂钩异常：{ex.Message}");
        }
    }

    private static nint SubclassWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (IsShowMessage(msg))
        {
            BringToFront();
            return 0;
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 把已有窗口切到前台。最小化时先还原。
    ///
    /// 失败只记日志：无人值守/自动化环境里前台锁会挡下 <c>SetForegroundWindow</c>
    /// （正常现象，日志能区分"是被前台锁挡了"还是"句柄失效"）；交互式双击启动一般会成功。
    /// </summary>
    public static void BringToFront()
    {
        if (_hwnd == 0)
        {
            Logger.Warn("单实例：收到唤醒请求，但主窗口句柄未登记，无法前置");
            return;
        }

        try
        {
            if (IsIconic(_hwnd))
            {
                ShowWindow(_hwnd, SW_RESTORE);
                Logger.Info("单实例：窗口原为最小化状态，已还原");
            }

            if (SetForegroundWindow(_hwnd))
            {
                return;
            }

            // 前台锁挡下时的补救：把窗口抬到 Z 序顶部，至少让用户看得到
            var error = Marshal.GetLastWin32Error();
            var ok = SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

            Logger.Warn($"单实例：SetForegroundWindow 失败（Win32 错误 {error}，通常是被系统前台锁挡下）；"
                + $"已改用 Z 序置顶（{(ok ? "成功" : "也失败")}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"单实例：前置窗口异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 通知已运行的实例前置窗口。收不到回执是正常现象（对方可能正在启动），不当作失败。
    /// </summary>
    public static void SignalExistingInstance()
    {
        if (_showMessage == 0) return;

        try
        {
            // HWND_BROADCAST(0xFFFF) 广播：无需知道对方窗口句柄
            var ok = PostMessage(0xFFFF, _showMessage, 0, 0);
            Logger.Info(ok
                ? "单实例：已广播唤醒请求，本实例即将退出"
                : $"单实例：广播唤醒请求失败（Win32 错误 {Marshal.GetLastWin32Error()}），本实例仍将退出");
        }
        catch (Exception ex)
        {
            Logger.Warn($"单实例：广播唤醒请求异常：{ex.Message}");
        }
    }

    /// <summary>消息值是否命中唤醒请求（供窗口过程判断）；未注册时永远为 false。</summary>
    public static bool IsShowMessage(uint msg) => _showMessage != 0 && msg == _showMessage;

    /// <summary>互斥体被放弃时算作正常：说明上一个实例已被强制结束，锁已归系统释放。</summary>
    private static void ReleaseQuietly()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
        }
        catch (ApplicationException)
        {
            // 当前线程不持有该互斥体（正常退出路径下由 ReleaseMutex 释放过一次）
        }
        catch
        {
            // 进程退出阶段，任何失败都无关紧要
        }
    }

    /// <summary>
    /// 结束本进程。
    ///
    /// <c>Environment.Exit</c> 不走 WinUI 的消息循环收尾，但会执行 <c>ProcessExit</c>，
    /// 因此互斥体仍会被正常释放；退出码 0 表示"作为第二实例正常让位"，不是错误。
    /// </summary>
    public static void ExitAsSecondary()
    {
        Logger.Info("单实例：检测到已有实例在运行，本实例不建窗口并退出");
        Environment.Exit(0);
    }

    // ---- Win32 ----

    private const int GWLP_WNDPROC = -4;
    private const int SW_RESTORE = 9;
    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
