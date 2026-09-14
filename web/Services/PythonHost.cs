using System.IO.Compression;
using Python.Runtime;

namespace ForzaGallerySync.Services;

/// <summary>
/// 定位并初始化嵌入式 Python 运行时（自 0.5.0 起由程序自己准备运行时，不再依赖安装程序）。
///
/// 运行时根目录（runtime root，内含 <c>python313.dll</c> / <c>Lib</c> / <c>DLLs</c> / <c>forza_sync</c>）：
///   1. 便携：程序目录只含发布文件（见 <see cref="IsCleanDirectory"/>）→ 程序目录
///   2. 默认：否则 → 默认安装目录（<c>%LOCALAPPDATA%\Programs\ForzaGallerySync</c>，
///      可用环境变量 <c>FORZA_SYNC_INSTALL_DIR</c> 覆盖）
///
/// Python home 定位优先级：
///   1. 首选根目录的 <c>python\</c>（与内嵌 zip 哈希一致时直接复用）
///   2. 程序目录的 <c>python\</c>（兼容旧安装器布局；便携模式下与 1 相同）
///   3. 环境变量 <c>FORZA_SYNC_PYTHON_HOME</c> 指定的 Python 环境（开发用）
///   4. 内嵌资源 zip（<c>python-runtime.zip</c>）解压到首选根目录 <c>python\</c>；
///      首选根目录不可写时回退到程序目录
///   5. 均失败时抛出明确错误
///
/// 项目根（含 forza_sync 包）：
///   1. 运行时根目录（<c>python\forza_sync</c>）
///   2. exe 所在目录
///   3. 环境变量 <c>FORZA_SYNC_PROJECT_ROOT</c>
///   4. 从 exe 所在目录向上查找 <c>forza_sync/__init__.py</c>
///   5. 当前工作目录
/// </summary>
public static class PythonHost
{
    /// <summary>嵌入程序集内的 Python 运行时资源名。</summary>
    private const string RuntimeResourceName = "ForzaGallerySync.python-runtime.zip";

    /// <summary>发布清单文件名（由 <c>web\make-gui.ps1</c> 生成），用于判定「干净目录」。</summary>
    private const string ManifestFileName = "app-files.txt";

    /// <summary>运行时目录名（位于运行时根目录下）。</summary>
    private const string RuntimeDirName = "python";

    /// <summary>记录运行时来源 zip 哈希的标记文件（位于 <c>python\</c> 内）。</summary>
    private const string RuntimeIdFileName = ".runtime-id";

    /// <summary>覆盖默认安装目录的环境变量。</summary>
    private const string InstallDirEnvVar = "FORZA_SYNC_INSTALL_DIR";

    public static string PythonHome { get; private set; } = "";
    public static string ProjectRoot { get; private set; } = "";

    /// <summary>当前使用的运行时根目录（程序目录或安装目录）。</summary>
    public static string RuntimeRoot { get; private set; } = "";

    /// <summary>是否运行在便携布局（程序目录为干净目录，运行时放在程序目录）。</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>默认安装目录：<c>%LOCALAPPDATA%\Programs\ForzaGallerySync</c>。</summary>
    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ForzaGallerySync");

    /// <summary>初始化解释器并导入 service 模块。必须在后台线程调用一次。</summary>
    public static PyModule InitializeAndImport()
    {
        // 程序目录：整个发布目录（exe + .NET/WinAppSDK 文件 + 发布清单）。
        var programDir = TrimEndingSeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var installDir = ResolveInstallDir();

        // 干净目录（只含发布文件）→ 便携模式：运行时解压到程序目录，
        // 整个目录可整体搬移（U 盘 / 自定义文件夹）。
        // 否则 → 运行时解压到默认安装目录，程序文件与运行时解耦。
        IsPortable = IsCleanDirectory(programDir);
        var preferredRoot = IsPortable ? programDir : installDir;

        var runtime = ResolveRuntime(preferredRoot, programDir);
        PythonHome = runtime.Home;
        RuntimeRoot = runtime.Root;
        ProjectRoot = ResolveProjectRoot();

        // 运行时位于程序自己的目录（便携目录或默认安装目录）时，把该目录暴露给
        // Python 侧（FORZA_SYNC_APP_DIR），供数据库等用户数据默认落在同一目录。
        // 开发环境（FORZA_SYNC_PYTHON_HOME）不设置，数据库仍走用户配置目录。
        if (RuntimeRoot.Length > 0 &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FORZA_SYNC_APP_DIR")))
        {
            Environment.SetEnvironmentVariable("FORZA_SYNC_APP_DIR", RuntimeRoot);
        }
        Logger.Info(
            $"运行时布局: {(IsPortable ? "便携（程序目录）" : "默认安装目录")}"
            + $", runtimeRoot={RuntimeRoot}, appDir={Environment.GetEnvironmentVariable("FORZA_SYNC_APP_DIR")}");

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
            // 环境变量双写：嵌入式解释器读到的是 C 运行时的环境块，.NET 侧
            // Environment.SetEnvironmentVariable 写入的变量在解释器内可能不可见，
            // 这里显式同步到 os.environ（forza_sync.config 依赖 FORZA_SYNC_APP_DIR）。
            var appDir = Environment.GetEnvironmentVariable("FORZA_SYNC_APP_DIR");
            if (!string.IsNullOrWhiteSpace(appDir))
            {
                using PyObject os = Py.Import("os");
                using PyObject environ = os.GetAttr("environ");
                environ.SetItem("FORZA_SYNC_APP_DIR".ToPython(), appDir.ToPython());
            }

            using dynamic sys = Py.Import("sys");
            sys.path.insert(0, ProjectRoot);
            var module = (PyModule)Py.Import("forza_sync.service");
            Logger.Info($"Python 初始化完成: home={PythonHome}, root={ProjectRoot}");
            return module;
        }
    }

    /// <summary>解析运行时根目录与 Python home（优先级见类注释）。</summary>
    private static (string Home, string Root) ResolveRuntime(string preferredRoot, string programDir)
    {
        // 内嵌 zip 的哈希：用于识别已解压运行时是否与当前程序版本一致。
        var embeddedHash = ComputeEmbeddedRuntimeHash();

        // 1. 首选根目录（便携→程序目录；否则→默认安装目录）已有一致的运行时。
        var home = TryUseExistingRuntime(Path.Combine(preferredRoot, RuntimeDirName), embeddedHash);
        if (home is not null)
        {
            return (home, preferredRoot);
        }

        // 2. 程序目录 python\（兼容旧安装器「运行时与程序同目录」的布局）。
        if (!IsSamePath(preferredRoot, programDir))
        {
            home = TryUseExistingRuntime(Path.Combine(programDir, RuntimeDirName), embeddedHash);
            if (home is not null)
            {
                return (home, programDir);
            }
        }

        // 3. 环境变量 FORZA_SYNC_PYTHON_HOME（开发环境，不参与哈希校验）。
        var env = Environment.GetEnvironmentVariable("FORZA_SYNC_PYTHON_HOME");
        if (!string.IsNullOrWhiteSpace(env) && IsValidPythonHome(env))
        {
            return (TrimEndingSeparator(Path.GetFullPath(env)), "");
        }

        // 4. 内嵌 zip 解压到首选根目录；首选目录不可写（含只读介质）时回退程序目录。
        if (TryExtractEmbeddedRuntime(preferredRoot, embeddedHash, out var extracted))
        {
            return (extracted, preferredRoot);
        }
        if (!IsSamePath(preferredRoot, programDir) &&
            TryExtractEmbeddedRuntime(programDir, embeddedHash, out extracted))
        {
            return (extracted, programDir);
        }

        // 5. 未找到任何可用运行时：抛出明确错误（开发环境需配置，避免硬编码本机路径）。
        throw new InvalidOperationException(
            "未找到 Python 运行时，内嵌运行时解压也失败。"
            + $"已尝试：{Path.Combine(preferredRoot, RuntimeDirName)}、{Path.Combine(programDir, RuntimeDirName)}。"
            + "请确认程序或安装目录可写（可用 FORZA_SYNC_INSTALL_DIR 指定可写目录），"
            + "开发时也可用 FORZA_SYNC_PYTHON_HOME 指向本地 Python 环境。");
    }

    /// <summary>
    /// 复用已解压的运行时：目录有效、含 <c>forza_sync</c> 包，且来源哈希与当前内嵌 zip 一致。
    /// 哈希不一致（重新打包 / 升级）时返回 null，交由调用方重新解压。
    /// </summary>
    private static string? TryUseExistingRuntime(string runtimeDir, string? embeddedHash)
    {
        if (!IsValidPythonHome(runtimeDir) || !Directory.Exists(Path.Combine(runtimeDir, "forza_sync")))
        {
            return null;
        }

        if (embeddedHash is not null)
        {
            var markerPath = Path.Combine(runtimeDir, RuntimeIdFileName);
            if (!File.Exists(markerPath) ||
                !string.Equals(File.ReadAllText(markerPath).Trim(), embeddedHash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return runtimeDir;
    }

    private static string ResolveProjectRoot()
    {
        // 1. 运行时根目录 python\forza_sync（与运行时配套，安装版/便携版直接使用）。
        if (RuntimeRoot.Length > 0)
        {
            var runtimePython = Path.Combine(RuntimeRoot, RuntimeDirName);
            if (Directory.Exists(Path.Combine(runtimePython, "forza_sync")))
            {
                return runtimePython;
            }
        }

        // 2. FORZA_SYNC_PYTHON_HOME 环境（forza_sync 随该环境一起提供时）。
        if (Directory.Exists(Path.Combine(PythonHome, "forza_sync")))
        {
            return PythonHome;
        }

        // 3. exe 目录（forza_sync 直接放在 exe 目录时）。
        var programDir = TrimEndingSeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (File.Exists(Path.Combine(programDir, "forza_sync", "__init__.py")))
        {
            return programDir;
        }

        // 4. 环境变量 FORZA_SYNC_PROJECT_ROOT。
        var env = Environment.GetEnvironmentVariable("FORZA_SYNC_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        // 5. 从 exe 所在目录向上查找 forza_sync/__init__.py（开发：web\bin\... → 仓库根）。
        var dir = new DirectoryInfo(programDir);
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
    /// 判断程序目录是否为「干净目录」：除发布清单（<c>app-files.txt</c>，由 make-gui.ps1 生成）
    /// 所列文件与程序自己生成的文件（<c>python\</c>、数据库、配置、日志）外，没有其他文件。
    /// 干净目录说明用户把整个发布目录放在独立位置 → 便携模式，运行时解压到程序目录。
    /// 无发布清单（开发构建）时视为不干净 → 运行时使用默认安装目录。
    /// </summary>
    private static bool IsCleanDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return false;
            }

            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                Logger.Info($"未找到发布清单 {ManifestFileName}，按非便携模式处理: {dir}");
                return false;
            }

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(manifestPath))
            {
                var rel = NormalizeRelativePath(line);
                if (rel.Length > 0)
                {
                    expected.Add(rel);
                }
            }
            if (expected.Count == 0)
            {
                return false;
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            {
                var rel = NormalizeRelativePath(Path.GetRelativePath(dir, path));

                // 程序自己生成的文件（运行时、数据库、配置、日志、清单本身）不算外来文件。
                if (IsGeneratedPath(rel))
                {
                    continue;
                }

                if (Directory.Exists(path))
                {
                    // 目录：清单中存在以其为前缀的文件，即视为发布目录。
                    var prefix = rel + "/";
                    if (!expected.Any(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }
                    continue;
                }

                if (!expected.Contains(rel))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Exception($"判定干净目录失败: {dir}", ex);
            return false;
        }
    }

    /// <summary>程序自己生成的文件/目录（不参与干净目录判定）。</summary>
    private static bool IsGeneratedPath(string rel) =>
        rel.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)
        || rel.Equals(RuntimeDirName, StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith(RuntimeDirName + "/", StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith("logs", StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith("forza_sync.db", StringComparison.OrdinalIgnoreCase)
        || rel.Equals("config.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>统一为「/ 分隔、无前导 BOM 与斜杠」的相对路径。</summary>
    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('\uFEFF').Trim().TrimStart('/');

    /// <summary>默认安装目录（可由 <c>FORZA_SYNC_INSTALL_DIR</c> 覆盖）。</summary>
    private static string ResolveInstallDir()
    {
        var env = Environment.GetEnvironmentVariable(InstallDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return TrimEndingSeparator(Path.GetFullPath(env));
        }
        return DefaultInstallDir;
    }

    private static string TrimEndingSeparator(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool IsSamePath(string a, string b) =>
        string.Equals(
            TrimEndingSeparator(Path.GetFullPath(a)),
            TrimEndingSeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>内嵌 zip 的 SHA256（无内嵌运行时资源时返回 null）。</summary>
    private static string? ComputeEmbeddedRuntimeHash()
    {
        using var stream = OpenRuntimeStream();
        if (stream is null)
        {
            return null;
        }
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>
    /// 把内嵌运行时解压到 <c>root\python</c>（先清理旧目录），并写入哈希标记。
    /// 解压后校验目录完整性；失败返回 false（由调用方尝试其它根目录）。
    /// </summary>
    private static bool TryExtractEmbeddedRuntime(string root, string? embeddedHash, out string home)
    {
        home = "";
        var runtimeDir = Path.Combine(root, RuntimeDirName);

        try
        {
            // 哈希不一致或目录不完整：清掉旧目录，全新解压（避免残留旧文件）。
            if (Directory.Exists(runtimeDir))
            {
                try { Directory.Delete(runtimeDir, recursive: true); }
                catch { /* 删除失败（可能被占用）则覆盖式解压 */ }
            }
            Directory.CreateDirectory(runtimeDir);

            using var stream = OpenRuntimeStream();
            if (stream is null)
            {
                return false;
            }

            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var dest = Path.Combine(runtimeDir, entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            if (embeddedHash is not null)
            {
                File.WriteAllText(Path.Combine(runtimeDir, RuntimeIdFileName), embeddedHash);
            }

            if (!IsValidPythonHome(runtimeDir) || !Directory.Exists(Path.Combine(runtimeDir, "forza_sync")))
            {
                Logger.Error($"Python 运行时解压结果不完整: {runtimeDir}");
                return false;
            }

            Logger.Info($"Python 运行时已从内嵌资源解压: {runtimeDir}");
            home = runtimeDir;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Exception($"解压内嵌 Python 运行时失败: {runtimeDir}", ex);
            return false;
        }
    }

    /// <summary>打开内嵌 Python 运行时资源流（不存在返回 null）。</summary>
    private static Stream? OpenRuntimeStream() =>
        typeof(PythonHost).Assembly.GetManifestResourceStream(RuntimeResourceName);
}
