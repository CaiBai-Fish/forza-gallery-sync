using Python.Runtime;

namespace ForzaGallerySync.Services;

/// <summary>Python 调用失败的桥接异常（携带 Python 侧异常消息）。</summary>
public sealed class PyCallException : Exception
{
    public PyCallException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Python 后端桥接层。封装 :mod:`forza_sync.service` 的 call_service / call_bytes，
/// 以 async/await 暴露给 ViewModel。
///
/// 线程模型：
///   - 首次调用时在后台线程完成解释器初始化（可重试）；
///   - 每次调用通过 Task.Run 到后台线程并获取 GIL，避免阻塞 UI 线程。
/// </summary>
public sealed class PyBridge
{
    public static PyBridge Instance { get; } = new();

    private readonly object _initLock = new();
    private Task? _initTask;
    private PyModule? _service;

    private PyBridge() { }

    /// <summary>初始化任务；失败后下次访问自动重试。</summary>
    private Task InitTask
    {
        get
        {
            lock (_initLock)
            {
                if (_initTask is null || _initTask.IsFaulted)
                {
                    _initTask = Task.Run(PythonHost.InitializeAndImport)
                        .ContinueWith(t => _service = t.Result, TaskScheduler.Default);
                }
                return _initTask;
            }
        }
    }

    /// <summary>调用 service.call_service(name, argsJson)，返回解析后的字符串（JSON）。</summary>
    public async Task<string> CallJsonAsync(string name, string argsJson = "{}")
    {
        await InitTask;
        return await Task.Run(() => CallJsonSync(name, argsJson));
    }

    /// <summary>调用 service.call_bytes(name, argsJson)，返回原始字节（如图片）。</summary>
    public async Task<byte[]> CallBytesAsync(string name, string argsJson = "{}")
    {
        await InitTask;
        return await Task.Run(() => CallBytesSync(name, argsJson));
    }

    private string CallJsonSync(string name, string argsJson)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic module = _service!;
                var result = module.call_service(name, argsJson);
                return (string)result;
            }
            catch (PythonException ex)
            {
                throw new PyCallException(ex.Message, ex);
            }
        }
    }

    private byte[] CallBytesSync(string name, string argsJson)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic module = _service!;
                byte[] data = module.call_bytes(name, argsJson);
                return data;
            }
            catch (PythonException ex)
            {
                throw new PyCallException(ex.Message, ex);
            }
        }
    }
}
