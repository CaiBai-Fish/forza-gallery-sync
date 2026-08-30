using System.IO.Compression;
using Python.Runtime;

namespace ForzaGallerySync.Services;

/// <summary>
/// 定位并初始化嵌入式 Python 运行时（与旧 Tauri/PyO3 的 init_python 逻辑等价）。
///
/// Python home 定位优先级：
///   1. 安装目录 <c>python\</c>（安装程序解压到安装目录的运行时，安装版直接使用）
///   2. 环境变量 <c>FORZA_SYNC_PYTHON_HOME</c> 指定 Python 运行时目录
///   3. 内嵌资源 zip（解压到本地缓存，开发/便携回退）
///   4. 均未找到时抛出明确错误（开发需设置 FORZA_SYNC_PYTHON_HOME 或运行 make-runtime.ps1）
/// 项目根（含 forza_sync 包）：
///   1. 安装目录 <c>python\</c>
///   2. exe 所在目录
///   3. 环境变量 <c>FORZA_SYNC_PROJECT_ROOT</c>
///   4. 内嵌资源 zip 解压目录
///   5. 从 exe 所在目录向上查找 <c>forza_sync/__init__.py</c>
/// </summary>
public static class PythonHost
{
    /// <summary>嵌入程序集内的 Python 运行时资源名。</summary>
    private const string RuntimeResourceName = "ForzaGallerySync.python-runtime.zip";

    public static string PythonHome { get; private set; } = "";
    public static string ProjectRoot { get; private set; } = "";

    /// <summary>内嵌运行时解压后的目录（null 表示无内嵌运行时）。</summary>
    private static string? _embeddedRuntime;

    /// <summary>初始化解释器并导入 service 模块。必须在后台线程调用一次。</summary>
    public static PyModule InitializeAndImport()
    {
        PythonHome = ResolvePythonHome();
        ProjectRoot = ResolveProjectRoot();

        // 安装版布局（Python 运行时位于 exe 目录 python\）时，把安装目录暴露给
        // Python 侧（FORZA_SYNC_APP_DIR），供数据库等用户数据默认落在安装目录（便携化）。
        var installLayout = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "python"));
        if (string.Equals(Path.GetFullPath(PythonHome), installLayout, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("FORZA_SYNC_APP_DIR", AppContext.BaseDirectory);
        }

        // 嵌入式 Python 需显式指定 home（含标准库 Lib/ 与 site-packages），
        // 否则解释器无法定位 Lib 目录。
        Environment.SetEnvironmentVariable("PYTHONHOME", PythonHome);
        Environment.SetEnvironmentVariable("PYTHONNOUSERSITE", "1");

        // 指定要加载的 Python 原生 DLL（conda 环境根目录）。
        Runtime.PythonDLL = Path.Combine(PythonHome, "python313.dll");

        // 扩展模块（_sqlite3 等）依赖的 DLL 搜索目录加入 PATH。
        var searchDirs = new[]
        {
            Path.Combine(PythonHome, "Library", "bin"),
            Path.Combine(PythonHome, "DLLs"),
            PythonHome,
        };
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable(
            "PATH",
            string.Join(Path.PathSeparator, searchDirs.Concat(new[] { currentPath })));

        // 初始化解释器并释放 GIL，供多线程（Py.GIL()）竞争使用。
        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();

        // 把项目根插入 sys.path 并导入 service 模块。
        using (Py.GIL())
        {
            using dynamic sys = Py.Import("sys");
            sys.path.insert(0, ProjectRoot);
            var module = (PyModule)Py.Import("forza_sync.service");
            Logger.Info($"Python 初始化完成: home={PythonHome}, root={ProjectRoot}");
            return module;
        }
    }

    private static string ResolvePythonHome()
    {
        // 1. 安装目录 python/（安装程序解压到安装目录的运行时，安装版直接使用）。
        var installDir = Path.Combine(AppContext.BaseDirectory, "python");
        if (IsValidPythonHome(installDir))
        {
            return installDir;
        }

        // 2. 环境变量 FORZA_SYNC_PYTHON_HOME。
        var env = Environment.GetEnvironmentVariable("FORZA_SYNC_PYTHON_HOME");
        if (!string.IsNullOrWhiteSpace(env) && IsValidPythonHome(env))
        {
            return env;
        }

        // 3. 嵌入资源 zip -> 解压到本地缓存（开发/便携回退）。
        _embeddedRuntime = ExtractEmbeddedRuntime();
        if (_embeddedRuntime is not null)
        {
            return _embeddedRuntime;
        }

        // 4. 未找到任何可用运行时：抛出明确错误（开发环境需配置，避免硬编码本机路径）。
        throw new InvalidOperationException(
            "未找到 Python 运行时。请设置环境变量 FORZA_SYNC_PYTHON_HOME 指向有效的 Python 环境，"
            + "或在 web 目录执行 make-runtime.ps1 生成内嵌运行时后重试。");
    }

    private static string ResolveProjectRoot()
    {
        // 1. 安装目录 python/forza_sync（与安装目录运行时配套，安装版直接使用）。
        var installPython = Path.Combine(AppContext.BaseDirectory, "python");
        if (Directory.Exists(Path.Combine(installPython, "forza_sync")))
        {
            return installPython;
        }

        // 2. exe 目录（forza_sync 直接打包在 exe 目录时）。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "forza_sync", "__init__.py")))
        {
            return AppContext.BaseDirectory;
        }

        // 3. 环境变量 FORZA_SYNC_PROJECT_ROOT。
        var env = Environment.GetEnvironmentVariable("FORZA_SYNC_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        // 4. 内嵌运行时（zip 内含 forza_sync 包）。
        if (_embeddedRuntime is not null && Directory.Exists(Path.Combine(_embeddedRuntime, "forza_sync")))
        {
            return _embeddedRuntime;
        }

        // 5. 从 exe 所在目录向上查找 forza_sync/__init__.py。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "forza_sync", "__init__.py")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>判断目录是否为可用的 Python home（含 python313.dll 与 Lib）。</summary>
    private static bool IsValidPythonHome(string dir) =>
        File.Exists(Path.Combine(dir, "python313.dll")) &&
        Directory.Exists(Path.Combine(dir, "Lib"));

    /// <summary>
    /// 从嵌入资源提取 Python 运行时并解压到本地缓存。
    /// 缓存按内嵌 zip 的 SHA256 做一致性校验：zip 更新（重新打包 / 升级）后自动重新解压。
    /// </summary>
    private static string? ExtractEmbeddedRuntime()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForzaGallerySync", "runtime", "v1");
        var markerPath = Path.Combine(cacheDir, ".runtime-id");

        // 计算内嵌 zip 的哈希（用于识别运行时是否已更新）。
        string? hashHex = null;
        using (var probe = OpenRuntimeStream())
        {
            if (probe is null)
            {
                return null;
            }
            using var sha = System.Security.Cryptography.SHA256.Create();
            hashHex = Convert.ToHexString(sha.ComputeHash(probe));
        }

        // 已解压且与当前内嵌 zip 一致则直接复用。
        if (IsValidPythonHome(cacheDir) &&
            Directory.Exists(Path.Combine(cacheDir, "forza_sync")) &&
            File.Exists(markerPath) &&
            File.ReadAllText(markerPath) == hashHex)
        {
            return cacheDir;
        }

        try
        {
            // 哈希不一致：清掉旧缓存，全新解压（避免残留旧文件）。
            if (Directory.Exists(cacheDir))
            {
                try { Directory.Delete(cacheDir, recursive: true); }
                catch { /* 删除失败（可能被占用）则覆盖式解压 */ }
            }
            Directory.CreateDirectory(cacheDir);

            using var stream = OpenRuntimeStream();
            if (stream is null)
            {
                return null;
            }

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var dest = Path.Combine(cacheDir, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            File.WriteAllText(markerPath, hashHex);
            Logger.Info($"Python 运行时已从内嵌资源解压: {cacheDir}");
            return IsValidPythonHome(cacheDir) ? cacheDir : null;
        }
        catch (Exception ex)
        {
            Logger.Exception("解压内嵌 Python 运行时失败", ex);
            return null;
        }
    }

    /// <summary>打开内嵌 Python 运行时资源流（不存在返回 null）。</summary>
    private static Stream? OpenRuntimeStream() =>
        typeof(PythonHost).Assembly.GetManifestResourceStream(RuntimeResourceName);
}
