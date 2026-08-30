using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace ForzaGallerySync.Setup;

/// <summary>
/// Forza Gallery Sync 安装程序。
///
/// 单文件自包含 exe，内嵌 payload.zip（WinUI 应用 + .NET runtime + Python runtime，
/// 由 web\make-installer.ps1 打包）。安装时把 payload 解压到安装目录
/// （默认 %LOCALAPPDATA%\Programs\ForzaGallerySync），程序运行时直接使用
/// 安装目录里的环境（安装目录\python 等），不再依赖系统环境或本地缓存。
///
/// 用法：
///   ForzaGallerySync-Setup.exe                交互式安装
///   ForzaGallerySync-Setup.exe --install [dir] 静默安装到指定目录
///   ForzaGallerySync-Setup.exe --uninstall     卸载
///   ForzaGallerySync-Setup.exe --silent        配合 --install / --uninstall 使用，无提示
///   ForzaGallerySync-Setup.exe --desktop       静默安装时额外创建桌面快捷方式
///   ForzaGallerySync-Setup.exe --help          帮助
/// </summary>
internal static class Program
{
    private const string AppName = "Forza Gallery Sync";
    private const string ExeName = "forza-gallery-sync.exe";
    private const string Version = "0.4.0";
    private const string PayloadResource = "ForzaGallerySync.Setup.payload.zip";
    private const string DefaultInstallDir = "%LOCALAPPDATA%\\Programs\\ForzaGallerySync";
    private const string UninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForzaGallerySync";
    private const string StartMenuFolder = "Forza Gallery Sync";

    private static bool _silent;
    private static bool _desktop;
    private static string _selfPath = Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "ForzaGallerySync-Setup.exe");

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? action = null; // install | uninstall | help
        string? installDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--install":
                case "-i":
                    action = "install";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        installDir = args[++i];
                    break;
                case "--uninstall":
                case "-u":
                    action = "uninstall";
                    break;
                case "--silent":
                case "-s":
                    _silent = true;
                    break;
                case "--desktop":
                    _desktop = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                case "--version":
                    action = "help";
                    break;
            }
        }

        try
        {
            switch (action)
            {
                case "install": return Install(installDir);
                case "uninstall": return Uninstall();
                case "help": PrintHelp(); return 0;
                default: return Interactive();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[错误] " + ex.Message);
            if (Environment.GetEnvironmentVariable("FGS_SETUP_DEBUG") == "1")
                Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static int Interactive()
    {
        PrintBanner();
        Console.WriteLine();
        Console.WriteLine("  1) 安装");
        Console.WriteLine("  2) 卸载");
        Console.WriteLine("  3) 退出");
        Console.Write("  请选择: ");
        var choice = Console.ReadLine()?.Trim();
        switch (choice)
        {
            case "1": return Install(null);
            case "2": return Uninstall();
            default:
                Console.WriteLine("  已取消。");
                return 0;
        }
    }

    private static int Install(string? dirArg)
    {
        var installDir = dirArg;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            var def = ExpandEnv(DefaultInstallDir);
            if (_silent)
            {
                installDir = def;
            }
            else
            {
                Console.Write($"  安装目录 [{def}]: ");
                var input = Console.ReadLine()?.Trim();
                installDir = string.IsNullOrWhiteSpace(input) ? def : ExpandEnv(input);
            }
        }
        else
        {
            installDir = ExpandEnv(installDir);
        }

        installDir = Path.GetFullPath(installDir);

        if (Directory.Exists(installDir) && Directory.GetFileSystemEntries(installDir).Length > 0)
        {
            if (_silent)
            {
                Console.WriteLine($"[提示] 目标目录已存在，覆盖安装: {installDir}");
            }
            else
            {
                Console.Write($"  目标目录已存在（{installDir}），是否覆盖安装？[y/N] ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
                {
                    Console.WriteLine("  已取消。");
                    return 0;
                }
            }
        }

        Directory.CreateDirectory(installDir);

        Console.WriteLine($"[1/4] 解压运行时与应用 -> {installDir}");
        ExtractPayload(installDir);

        Console.WriteLine("[2/4] 创建快捷方式 ...");
        CreateShortcuts(installDir);

        Console.WriteLine("[3/4] 写入卸载信息 ...");
        WriteUninstallInfo(installDir);

        Console.WriteLine("[4/4] 完成。");
        Console.WriteLine($"  安装位置: {installDir}");

        if (!_silent)
        {
            Console.Write("  是否立即启动应用？[y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y")
                Launch(installDir);
        }
        return 0;
    }

    private static int Uninstall()
    {
        string? installDir = null;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallRegPath))
        {
            installDir = key?.GetValue("InstallLocation") as string;
        }

        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            Console.WriteLine("  未检测到已安装的 Forza Gallery Sync（注册表或安装目录不存在）。");
            return 0;
        }

        if (!_silent)
        {
            Console.Write($"  确定要卸载 {AppName}（{installDir}）吗？[y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
            {
                Console.WriteLine("  已取消。");
                return 0;
            }
        }

        Console.WriteLine("[1/3] 删除快捷方式 ...");
        RemoveShortcuts();

        Console.WriteLine("[2/3] 保留数据库并删除安装目录 ...");
        try
        {
            PreserveDatabase(installDir);
            Directory.Delete(installDir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [警告] 部分文件删除失败（应用可能正在运行）: {ex.Message}");
        }

        Console.WriteLine("[3/3] 删除卸载注册表项 ...");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [警告] 注册表项删除失败: {ex.Message}");
        }

        Console.WriteLine("  卸载完成。");
        return 0;
    }

    /// <summary>卸载前保留用户数据：把安装目录中的数据库（forza_sync.db*）移到用户配置目录，避免卸载丢失照片记录。</summary>
    private static void PreserveDatabase(string installDir)
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "forza-sync");
            Directory.CreateDirectory(dataDir);

            foreach (var name in new[] { "forza_sync.db", "forza_sync.db-wal", "forza_sync.db-shm" })
            {
                var src = Path.Combine(installDir, name);
                var dst = Path.Combine(dataDir, name);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    File.Move(src, dst);
                    Console.WriteLine($"  [保留] 数据库已移至 {dst}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [警告] 数据库保留失败: {ex.Message}");
        }
    }

    /// <summary>从内嵌资源解压 payload 到目标目录（含进度）。</summary>
    private static void ExtractPayload(string destDir)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException(
                "安装包内未找到 payload（payload.zip 未嵌入）。请通过 web\\make-installer.ps1 重新构建安装程序。");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var total = archive.Entries.Count;
        int done = 0;

        Console.Write($"    解压中: 0%");
        foreach (var entry in archive.Entries)
        {
            var dest = Path.Combine(destDir, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(dest);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            done++;
            if (done % 100 == 0 || done == total)
                Console.Write($"\r    解压中: {done * 100 / total}%  ({done}/{total})");
        }
        Console.WriteLine();
    }

    private static void CreateShortcuts(string installDir)
    {
        var startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuFolder);
        Directory.CreateDirectory(startMenuDir);

        var appExe = Path.Combine(installDir, ExeName);

        // 开始菜单：应用
        CreateShortcut(
            Path.Combine(startMenuDir, $"{AppName}.lnk"),
            appExe, installDir, appExe, AppName);

        // 开始菜单：卸载（指向安装程序自身）
        CreateShortcut(
            Path.Combine(startMenuDir, "卸载 Forza Gallery Sync.lnk"),
            _selfPath, Path.GetDirectoryName(_selfPath) ?? installDir,
            icon: null, desc: "卸载 Forza Gallery Sync", args: "--uninstall");

        // 桌面快捷方式：交互安装询问，静默安装需 --desktop
        if (!_silent)
        {
            Console.Write("    是否创建桌面快捷方式？[y/N] ");
            _desktop = Console.ReadLine()?.Trim().ToLowerInvariant() == "y";
        }
        if (_desktop)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(
                Path.Combine(desktopDir, $"{AppName}.lnk"),
                appExe, installDir, appExe, AppName);
        }
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir,
        string? icon, string? desc, string? args = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建快捷方式（WScript.Shell 不可用）。");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = workDir;
        if (!string.IsNullOrEmpty(icon)) lnk.IconLocation = icon;
        if (!string.IsNullOrEmpty(desc)) lnk.Description = desc;
        if (!string.IsNullOrEmpty(args)) lnk.Arguments = args;
        lnk.Save();
    }

    private static void RemoveShortcuts()
    {
        try
        {
            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuFolder);
            if (Directory.Exists(startMenuDir))
                Directory.Delete(startMenuDir, recursive: true);
        }
        catch { /* 忽略单个删除失败 */ }

        try
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");
            if (File.Exists(desktop))
                File.Delete(desktop);
        }
        catch { }
    }

    private static void WriteUninstallInfo(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegPath);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Forza Gallery Sync");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", Path.Combine(installDir, ExeName));
        key.SetValue("UninstallString", $"\"{_selfPath}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{_selfPath}\" --uninstall --silent");
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 1);
    }

    private static void Launch(string installDir)
    {
        var appExe = Path.Combine(installDir, ExeName);
        if (File.Exists(appExe))
            Process.Start(new ProcessStartInfo(appExe) { WorkingDirectory = installDir });
    }

    private static void PrintBanner()
    {
        Console.WriteLine($"========================================");
        Console.WriteLine($"  {AppName} 安装程序 v{Version}");
        Console.WriteLine($"========================================");
    }

    private static void PrintHelp()
    {
        PrintBanner();
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ForzaGallerySync-Setup.exe                     交互式安装");
        Console.WriteLine("  ForzaGallerySync-Setup.exe --install [dir]     静默安装到指定目录");
        Console.WriteLine("  ForzaGallerySync-Setup.exe --uninstall         卸载");
        Console.WriteLine("  ForzaGallerySync-Setup.exe --silent            与 --install/--uninstall 搭配，无交互提示");
        Console.WriteLine("  ForzaGallerySync-Setup.exe --desktop           静默安装时额外创建桌面快捷方式");
        Console.WriteLine("  ForzaGallerySync-Setup.exe --help              显示本帮助");
        Console.WriteLine();
        Console.WriteLine($"默认安装目录: {ExpandEnv(DefaultInstallDir)}");
        Console.WriteLine("程序安装后直接使用安装目录内的 Python/.NET 运行时，不依赖系统环境。");
    }

    private static string ExpandEnv(string path) => Environment.ExpandEnvironmentVariables(path);
}
