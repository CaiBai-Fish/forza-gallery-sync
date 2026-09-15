using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;

namespace ForzaGallerySync.Services;

/// <summary>更新流程中的可预期失败（提示给用户，不当作崩溃）。</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 自动更新：下载发布包、覆盖程序文件、重启应用。
///
/// 更新方式与发布布局一致（`make-gui.ps1` 输出的是自包含目录 + zip），
/// 因此这里做的是"用新版本的文件覆盖程序目录"，而不是安装器那套逻辑：
/// - **只覆盖 zip 中包含的文件，不删除任何东西**：多余文件（用户自己的东西）保留，
///   同时也避免删错文件导致程序起不来。
/// - 覆盖由独立的 CMD 脚本完成，它会先等本进程退出——运行中的 exe / dll 被占用，
///   应用自己无法覆盖自身。
/// - 应用目录里的 `python-runtime.zip` 也会被更新，重启后
///   `PythonHost` 靠内嵌 zip 的 SHA256 标记自动重新解压运行时，
///   因此新版后端源码会一并生效，不需要单独处理 Python 运行时。
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// 下载基地址。默认指向官方 Release；可用环境变量 <c>FORZA_SYNC_UPDATE_BASE_URL</c>
    /// 覆盖成任意 HTTP 根（如本地 <c>http://127.0.0.1:8733</c>），
    /// 这样"下载 → 校验 → 生成安装脚本"整条链路可以在离线环境端到端验证，
    /// 不必真的去 GitHub 拉 77 MB 的安装包，也不必等到有新版发布。
    /// </summary>
    private static string DownloadBase =>
        Environment.GetEnvironmentVariable("FORZA_SYNC_UPDATE_BASE_URL") is { Length: > 0 } custom
            ? custom.TrimEnd('/')
            : "https://github.com/CaiBai-Fish/forza-gallery-sync/releases/download";

    /// <summary>发布包下载地址（与 CHANGELOG / 仓库布局约定一致）。</summary>
    private static string ReleaseZipUrlFormat => $"{DownloadBase}/v{{0}}/ForzaGallerySync-{{0}}-win-x64.zip";

    /// <summary>
    /// 产物哈希清单地址（独立 `hashes` 分支，见 build-release.yml）。
    ///
    /// 两个清单都发在同一个分支上，客户端按顺序取：
    /// 1. `<版本>.txt` —— 规范格式，每行 `<sha256>  <文件名>`（与 `sha256sum` 输出一致）；
    /// 2. `hashes.json` —— 带版本号与体积的 JSON，`.txt` 不可用时兜底。
    ///
    /// 两者都是 raw URL 静态文件，**不消耗 GitHub API 配额**，因此是日常路径。
    /// </summary>
    /// <summary>
    /// 哈希清单基地址。默认是 <c>hashes</c> 分支的 raw URL；
    /// 可用 <c>FORZA_SYNC_HASHES_BASE_URL</c> 覆盖（配合 <see cref="DownloadBase"/>
    /// 就能在本地模拟一整个 Release，端到端验证更新链路）。
    /// </summary>
    private static string HashesBranchBase =>
        Environment.GetEnvironmentVariable("FORZA_SYNC_HASHES_BASE_URL") is { Length: > 0 } custom
            ? custom.TrimEnd('/')
            : "https://raw.githubusercontent.com/CaiBai-Fish/forza-gallery-sync/hashes";

    /// <summary>发布包名（免安装版完整包，增量更新不可用时的备选）。</summary>
    private static string ZipArtifactName(string version) => $"ForzaGallerySync-{version}-win-x64.zip";

    /// <summary>
    /// 安装程序名。**更新默认走它**：下载 → 校验 SHA256 → 独立脚本静默安装 → 重启。
    ///
    /// 为什么优先安装程序而不是"解压覆盖"：安装程序是官方发布产物、自带卸载入口与
    /// 注册表记录，装完的程序在"设置 → 应用"里能正常管理；而覆盖式替换只在
    /// 便携版语义下成立，装到别处会出现"文件更新了但卸载记录还是旧的"。
    /// </summary>
    private static string SetupArtifactName(string version) => $"ForzaGallerySync-{version}-setup.exe";

    /// <summary>安装程序下载地址。</summary>
    private static string ReleaseSetupUrlFormat => $"{DownloadBase}/v{{0}}/ForzaGallerySync-{{0}}-setup.exe";

    /// <summary>
    /// 取得指定产物的期望 SHA256。三个来源，按"会不会被限流"排序：
    ///
    /// 1. **`hashes` 分支的 `<版本>.txt`**（优先）：规范清单，静态文件，不消耗 API 配额；
    /// 2. **`hashes` 分支的 `hashes.json`**（次选）：同样静态，JSON 解析；
    /// 3. **Releases API 的 `digest` 字段**（兜底）：平台计算、与上传字节绑定，同样权威，
    ///    但要走 API（匿名 60 次/小时），因此只在前两者不可用时使用。
    ///
    /// 三个来源都拿不到时返回 null。**调用方不得据此放行安装**：拿不到清单就不自动安装，
    /// 只留手动下载出口。
    /// </summary>
    public static Task<string?> TryGetExpectedHashAsync(string version, CancellationToken token) =>
        TryGetExpectedHashAsync(version, ZipArtifactName(version), token);

    public static async Task<string?> TryGetExpectedHashAsync(string version, string artifact, CancellationToken token)
    {
        var fromTxt = await TryGetHashFromManifestTextAsync(version, artifact, token);
        if (fromTxt is not null) return fromTxt;

        var fromJson = await TryGetHashFromHashesJsonAsync(version, artifact, token);
        if (fromJson is not null) return fromJson;

        Logger.Warn($"hashes 分支的两个清单都取不到 {artifact} 的哈希，回退 Release API digest");
        return await TryGetHashFromApiAsync(version, artifact, token);
    }

    /// <summary>
    /// 从 hashes 分支的 `<版本>.txt` 取哈希：每行 `<sha256>  <文件名>`（两个空格分隔）。
    /// 跳过空行与 `#` 注释行。
    /// </summary>
    private static async Task<string?> TryGetHashFromManifestTextAsync(string version, string artifact, CancellationToken token)
    {
        var url = $"{HashesBranchBase}/{version}.txt";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("forza-gallery-sync-updater");

            var text = await http.GetStringAsync(url, token);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                // "<sha256>  <文件名>"：以空白切分，最后一列是文件名，第一列是哈希
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var fileName = string.Join(' ', parts[1..]);
                if (!string.Equals(fileName, artifact, StringComparison.OrdinalIgnoreCase)) continue;

                if (parts[0].Length != 64)
                {
                    Logger.Warn($"清单 {version}.txt 里 {artifact} 的哈希长度异常（{parts[0].Length}）");
                    return null;
                }

                Logger.Info($"期望哈希（hashes/{version}.txt）{artifact} = {parts[0]}");
                return parts[0].ToLowerInvariant();
            }

            Logger.Warn($"清单 {version}.txt 里没有 {artifact}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"取 hashes/{version}.txt 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>从 Releases API 的 digest 字段取哈希。</summary>
    private static async Task<string?> TryGetHashFromApiAsync(string version, string artifact, CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("forza-gallery-sync-updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/CaiBai-Fish/forza-gallery-sync/releases/tags/v{version}";
            var json = await http.GetStringAsync(url, token);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("assets", out var assets))
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), artifact, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 形如 "sha256:<hex>"；老仓库可能没有该字段
                if (asset.TryGetProperty("digest", out var digest) &&
                    digest.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var raw = digest.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var value = raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            ? raw["sha256:".Length..]
                            : raw;
                        Logger.Info($"期望哈希（Release API digest）{artifact} = {value}");
                        return value.Trim();
                    }
                }

                Logger.Warn($"Release 资产 {artifact} 没有 digest 字段");
                return null;
            }

            Logger.Warn($"Release v{version} 中没有资产 {artifact}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"从 Release API 取哈希失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>从 hashes 分支的 hashes.json 取哈希（静态文件，不消耗 API 配额）。</summary>
    private static async Task<string?> TryGetHashFromHashesJsonAsync(string version, string artifact, CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("forza-gallery-sync-updater");

            var json = await http.GetStringAsync($"{HashesBranchBase}/hashes.json", token);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("artifacts", out var artifacts) ||
                !artifacts.TryGetProperty(artifact, out var entry) ||
                !entry.TryGetProperty("sha256", out var sha))
            {
                Logger.Warn($"hashes.json 里没有 {artifact}");
                return null;
            }

            var value = sha.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                Logger.Warn($"hashes.json 中 {artifact} 的 sha256 为空");
                return null;
            }

            Logger.Info($"期望哈希（hashes/hashes.json）{artifact} = {value}");
            return value.Trim();
        }
        catch (Exception ex)
        {
            Logger.Warn($"取 hashes/hashes.json 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 计算文件的 SHA256 并与期望值比较。
    /// <paramref name="expected"/> 为空时返回 false：**没有期望哈希就不算通过**，
    /// 由调用方按"拿不到清单不自动安装"处理（宁可让用户手动下载，也不装来源不明的文件）。
    /// </summary>
    public static bool VerifyHash(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            Logger.Error("未取得期望哈希，无法校验更新包（拒绝自动安装）");
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();

        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Logger.Error($"哈希校验失败：期望 {expected}，实际 {actual}");
            return false;
        }

        Logger.Info($"哈希校验通过：{actual}");
        return true;
    }

    /// <summary>
    /// 应用目录（exe 所在目录）。
    ///
    /// 可用环境变量 <c>FORZA_SYNC_APP_DIR</c> 覆盖：自动化验证时把"程序目录"指向一个
    /// 临时目录，就能在不动真实安装的前提下验证增量比对与替换逻辑。
    /// 正常运行时该变量不存在，行为与之前完全一致。
    /// </summary>
    public static string AppDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("FORZA_SYNC_APP_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>下载 zip 的目标路径（放在系统临时目录，避免污染程序目录）。</summary>
    public static string DownloadPath(string version) =>
        Path.Combine(Path.GetTempPath(), $"ForzaGallerySync-update-{version}.zip");

    /// <summary>
    /// 能否自动更新：程序目录必须可写（否则覆盖会失败）。
    /// 用实际试写探测——Windows 受限令牌下 os.access 之类的检查会误报。
    /// </summary>
    public static bool CanAutoUpdate(out string reason)
    {
        var dir = AppDirectory;
        var probe = Path.Combine(dir, $".update-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"程序目录不可写（{dir}）：{ex.Message}。请手动下载更新包替换。";
            return false;
        }
    }

    /// <summary>
    /// 下载 + 哈希校验 + 结构校验，一步到位。
    ///
    /// 顺序刻意如此：**先校验后解压**——只有在确认字节与 CI 发布的产物一致之后，
    /// 才把它交给解压与覆盖流程。
    ///
    /// 哈希策略（与全局约定一致）：
    /// - 哈希不匹配 → 删除下载的文件并拒绝安装；
    /// - **拿不到任何哈希清单 → 同样拒绝自动安装**，让用户走手动下载出口。
    /// </summary>
    /// <returns>已校验的 zip 路径与 zip 内需要剥离的顶层目录，可直接交给替换脚本。</returns>
    public static async Task<(string ZipPath, string StripPrefix)> DownloadVerifiedAsync(
        string version,
        IProgress<double>? progress,
        CancellationToken token)
    {
        // 1) 先取期望哈希
        var expected = await TryGetExpectedHashAsync(version, token);

        // 2) 下载（即使预期拿不到清单也先下，好让用户能拿到文件路径手动处理；
        //    但绝不安装——见第 3 步）
        var zip = await DownloadAsync(version, progress, token);

        // 3) 哈希校验
        if (string.IsNullOrWhiteSpace(expected))
        {
            Logger.Error($"未能取得 v{version} 的哈希清单，拒绝自动安装");
            throw new UpdateException(
                $"无法取得 v{version} 的哈希清单（hashes 分支的 {version}.txt / hashes.json 与 Release API 都不可用），"
                + "为安全起见不自动安装。请到 Releases 页手动下载，或稍后重试。");
        }

        if (!VerifyHash(zip, expected))
        {
            SafeDelete(zip);
            throw new UpdateException(
                StringLocalizer.Get("Upd_HashMismatch"));
        }

        // 4) 结构校验：确认确实是本程序的发布包，并取得需要剥离的顶层目录
        var prefix = ValidateArchive(zip);
        return (zip, prefix);
    }

    /// <summary>
    /// 下载并校验**安装程序**，返回其路径。更新默认走这条路。
    ///
    /// 与 <see cref="DownloadVerifiedAsync"/>（完整包）的差别：安装程序不需要结构校验
    /// （它就是单个 exe），但**同样必须过哈希**——拿不到清单就不放行。
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        string version, IProgress<double>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new UpdateException(StringLocalizer.Get("Upd_NoVersion"));
        }

        var artifact = SetupArtifactName(version);
        var expected = await TryGetExpectedHashAsync(version, artifact, token);
        var target = InstallerDownloadPath(version);

        var path = await DownloadToFileAsync(
            string.Format(ReleaseSetupUrlFormat, version), target, progress, token);

        if (string.IsNullOrWhiteSpace(expected))
        {
            Logger.Error($"未能取得 {artifact} 的哈希清单，拒绝自动安装");
            SafeDelete(path);
            throw new UpdateException(
                $"无法取得 {artifact} 的哈希清单（hashes 分支清单与 Release API 都不可用），"
                + "为安全起见不自动安装。请到 Releases 页手动下载，或稍后重试。");
        }

        if (!VerifyHash(path, expected))
        {
            SafeDelete(path);
            throw new UpdateException(
                "安装程序哈希校验失败，文件可能下载不完整或已被篡改，已删除。请重试；"
                + "若反复失败请到 Releases 页手动下载。");
        }

        Logger.Info($"安装程序已下载并校验：{path}");
        return path;
    }

    /// <summary>安装程序下载到系统临时目录（避免污染程序目录）。</summary>
    public static string InstallerDownloadPath(string version) =>
        Path.Combine(Path.GetTempPath(), $"ForzaGallerySync-{version}-setup.exe");

    /// <summary>
    /// 判断当前是不是"安装版"（有卸载注册表项）。
    ///
    /// 用途：升级时决定安装位置 —— 安装版让 Inno 用它记录的目录；免安装版
    /// （zip 直接解压）则用 <c>/DIR=</c> 指定到它当前所在目录，保持原样。
    /// 判据用注册表而不是"目录里有没有 unins000.exe"：后者在便携目录被解压过
    /// 安装包的情况下会误判。
    /// </summary>
    public static bool IsInstalled(out string installedLocation)
    {
        installedLocation = "";
        const string appId = "{8F3A9C41-2B7E-4D5A-9C1F-6E0B7A4D3C52}_is1";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{appId}");
            if (key is null) return false;

            installedLocation = key.GetValue("InstallLocation") as string ?? "";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取卸载注册表项失败（按免安装版处理）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下载指定版本的免安装完整包（增量与安装程序都不可用时的最后备选）。
    /// </summary>
    public static async Task<string> DownloadAsync(string version, IProgress<double>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new UpdateException(StringLocalizer.Get("Upd_NoVersion"));
        }

        var url = string.Format(ReleaseZipUrlFormat, version);
        var target = DownloadPath(version);
        return await DownloadToFileAsync(url, target, progress, token);
    }

    /// <summary>
    /// 把 URL 下到指定路径（带进度上报与失败清理）。
    /// 完整包与安装程序共用这一段，避免两处各写一遍下载循环。
    /// </summary>
    /// <param name="progress">进度回调（0~1，未知长度时为 -1 表示只报告已下载量）。</param>
    private static async Task<string> DownloadToFileAsync(
        string url, string target, IProgress<double>? progress, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("forza-gallery-sync-updater");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!resp.IsSuccessStatusCode)
            {
                throw new UpdateException(
                    $"下载失败：HTTP {(int)resp.StatusCode}。地址：{url}");
            }

            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var source = await resp.Content.ReadAsStreamAsync(token);
            await using var file = File.Create(target);

            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, count), token);
                read += count;
                progress?.Report(total > 0 ? (double)read / total : -1);
            }

            await file.FlushAsync(token);

            if (read == 0)
            {
                throw new UpdateException(StringLocalizer.Get("Upd_EmptyDownload"));
            }

            Logger.Info($"更新包已下载: {target}（{read} 字节）");
            return target;
        }
        catch (OperationCanceledException)
        {
            SafeDelete(target);
            throw;
        }
        catch (UpdateException)
        {
            SafeDelete(target);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(target);
            throw new UpdateException($"下载更新包失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 校验下载的 zip 结构：必须含可执行文件，并返回 zip 内需要剥离的顶层目录前缀。
    /// </summary>
    public static string ValidateArchive(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new UpdateException(StringLocalizer.Get("Upd_NoPackage"));

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries;
            if (entries.Count == 0) throw new UpdateException(StringLocalizer.Get("Upd_EmptyPackage"));

            // 发布包形如 ForzaGallerySync-<版本>-win-x64/forza-gallery-sync.exe
            var exe = entries.FirstOrDefault(e =>
                e.FullName.EndsWith("forza-gallery-sync.exe", StringComparison.OrdinalIgnoreCase));

            if (exe is null)
            {
                throw new UpdateException(StringLocalizer.Get("Upd_NoExeInPackage"));
            }

            var slash = exe.FullName.LastIndexOf('/');
            var prefix = slash >= 0 ? exe.FullName[..(slash + 1)] : "";
            Logger.Info($"更新包校验通过：{entries.Count} 个条目，顶层前缀 '{prefix}'");
            return prefix;
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UpdateException($"更新包已损坏或不完整：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 展开并启动替换脚本，然后调用方应立即退出应用。
    ///
    /// 脚本流程：等本进程退出 → 解压更新包 → 覆盖程序目录中的文件 → 启动新版本。
    /// 脚本本体是内嵌资源 <c>Resources/apply-update.ps1</c>，这里只把占位符替换成实际路径；
    /// 用 PowerShell 而不是 CMD，控制流与错误处理更清晰。
    /// </summary>
    /// <param name="identity">
    /// 当前进程身份（域名\用户名），仅写入日志便于排查；等待退出不依赖它——
    /// tasklist 对更高完整性级别的进程也能查到 PID。
    /// </param>
    /// <param name="skipExeCheck">
    /// 增量更新包只含变化的文件，可能没有 exe，此时跳过"必须能定位 exe"的检查。
    /// </param>
    /// <param name="preserve">
    /// 不允许被覆盖的相对路径（如正在运行的更新脚本自身）。传 null 表示不保护任何文件
    /// （完整包路径的行为，与之前一致）。
    /// </param>
    public static void LaunchReplaceAndRestart(
        string zipPath, string stripPrefix, string identity,
        bool skipExeCheck = false, IReadOnlyList<string>? preserve = null)
    {
        var script = WriteScriptOnly(zipPath, stripPrefix, identity, skipExeCheck, preserve);

        // 模拟模式：只生成并自检脚本，不启动它。用于在不动程序文件的前提下
        // 验证占位符替换与 PowerShell 语法是否正确。
        if (Environment.GetEnvironmentVariable("FORZA_SYNC_UPDATE_SIMULATE") == "1")
        {
            Logger.Info($"模拟模式：脚本未执行。{SelfCheckScript(script)}");
            return;
        }

        try
        {
            // UseShellExecute=false 直接起 powershell，脚本与当前进程无父子依赖，可独立存活
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            Logger.Info("替换脚本已启动，应用即将退出以便覆盖程序文件");
        }
        catch (Exception ex)
        {
            throw new UpdateException($"启动更新脚本失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 检查生成的脚本：占位符是否全部替换、PowerShell 语法是否合法。
    /// 返回一行结论，写进日志供排查。
    /// </summary>
    public static string SelfCheckScript(string scriptPath)
    {
        try
        {
            var text = File.ReadAllText(scriptPath);

            var leftover = System.Text.RegularExpressions.Regex.Matches(text, @"\{\{[A-Z]+\}\}")
                .Select(m => m.Value)
                .Distinct()
                .ToList();

            // 用 PowerShell 自己的解析器校验语法，比正则可靠
            var escaped = scriptPath.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command "
                    + $"\"$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('{escaped}', [ref]$null, [ref]$e); "
                    + "if ($e -and $e.Count -gt 0) { $e | ForEach-Object { $_.Message } } else { 'SYNTAX_OK' }\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(15000);

            var syntax = stdout.Contains("SYNTAX_OK") ? "语法 OK" : $"语法错误：{stdout}";
            var placeholders = leftover.Count == 0 ? "占位符已全部替换" : $"残留占位符 {string.Join(",", leftover)}";

            return $"脚本自检：{syntax}；{placeholders}（{scriptPath}）";
        }
        catch (Exception ex)
        {
            return $"脚本自检失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 展开"运行安装程序"的脚本并启动它，随后调用方应立即退出应用。
    ///
    /// 脚本流程：等应用退出 → 静默运行安装程序 → 成功则启动新版本、失败则启动原版本。
    /// 安装位置由 <paramref name="installDirOverride"/> 决定：
    /// - 有值（免安装版）：<c>/DIR=&lt;应用当前目录&gt;</c>，装回原处，升级后仍在同一目录；
    /// - 空（安装版）：不带 <c>/DIR</c>，交给 Inno 用它记录的安装目录。
    /// </summary>
    public static void LaunchInstaller(string installerPath, string? installDirOverride)
    {
        var script = WriteInstallerScript(installerPath, installDirOverride);

        // 模拟模式：只生成脚本，不真的安装（用于在不动程序文件的前提下验证脚本）
        if (Environment.GetEnvironmentVariable("FORZA_SYNC_UPDATE_SIMULATE") == "1")
        {
            Logger.Info($"模拟模式：安装脚本未执行。{SelfCheckScript(script)}");
            return;
        }

        try
        {
            // UseShellExecute=false 直接起 powershell：脚本与当前进程无父子依赖，可独立存活
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            Logger.Info("安装脚本已启动，应用即将退出以便安装程序替换文件");
        }
        catch (Exception ex)
        {
            throw new UpdateException($"启动安装脚本失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 只生成"运行安装程序"的脚本、不启动它，返回脚本路径。
    /// 与 <see cref="WriteScriptOnly"/> 同理由：支持模拟模式验证脚本正确性。
    /// </summary>
    public static string WriteInstallerScript(string installerPath, string? installDirOverride)
    {
        var script = Path.Combine(Path.GetTempPath(), $"ForzaGallerySync-install-{Environment.ProcessId}.ps1");
        var log = Path.Combine(Path.GetTempPath(), "ForzaGallerySync-update.log");

        var mode = string.IsNullOrWhiteSpace(installDirOverride) ? "installed" : "portable";

        try
        {
            var content = LoadScriptTemplate("run-installer.ps1")
                .Replace("{{INSTALLER}}", EscapeForSingleQuotedPs(installerPath))
                .Replace("{{LOG}}", EscapeForSingleQuotedPs(log))
                .Replace("{{MODE}}", mode)
                .Replace("{{DIR}}", EscapeForSingleQuotedPs(installDirOverride ?? ""))
                .Replace("{{APP}}", EscapeForSingleQuotedPs(AppDirectory))
                .Replace("{{PID}}", Environment.ProcessId.ToString());

            // UTF-8 带 BOM：脚本由 powershell.exe（Windows PowerShell 5.1）执行，
            // 无 BOM 时它会按系统 ANSI 代码页解码，脚本里的中文会乱码并破坏引号配对。
            File.WriteAllText(script, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Logger.Info($"安装脚本已写入: {script}（模式 {mode}，日志 {log}）");
            return script;
        }
        catch (Exception ex)
        {
            throw new UpdateException($"生成安装脚本失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 只展开替换脚本、不启动它，返回脚本路径。
    ///
    /// 独立出来是为了支持模拟模式（<c>FORZA_SYNC_UPDATE_SIMULATE=1</c>）：
    /// 可以把下载、哈希校验、脚本生成整条链路走完，但不覆盖正在运行的程序文件。
    /// </summary>
    public static string WriteScriptOnly(
        string zipPath, string stripPrefix, string identity,
        bool skipExeCheck = false, IReadOnlyList<string>? preserve = null)
    {
        var script = Path.Combine(Path.GetTempPath(), $"ForzaGallerySync-update-{Environment.ProcessId}.ps1");
        var log = Path.Combine(Path.GetTempPath(), "ForzaGallerySync-update.log");
        var stage = Path.Combine(Path.GetTempPath(), $"ForzaGallerySync-stage-{Environment.ProcessId}");

        // 保护名单用分号分隔（文件路径里不会出现分号，逗号则可能）
        var preserveValue = preserve is null || preserve.Count == 0
            ? ""
            : string.Join(';', preserve);

        try
        {
            var content = LoadScriptTemplate()
                .Replace("{{APP}}", EscapeForSingleQuotedPs(AppDirectory))
                .Replace("{{ZIP}}", EscapeForSingleQuotedPs(zipPath))
                .Replace("{{PREFIX}}", EscapeForSingleQuotedPs(stripPrefix))
                .Replace("{{STAGE}}", EscapeForSingleQuotedPs(stage))
                .Replace("{{LOG}}", EscapeForSingleQuotedPs(log))
                .Replace("{{PID}}", Environment.ProcessId.ToString())
                .Replace("{{SKIPEXE}}", skipExeCheck ? "1" : "0")
                .Replace("{{PRESERVE}}", EscapeForSingleQuotedPs(preserveValue))
                .Replace("{{IDENTITY}}", EscapeForSingleQuotedPs(identity));

            // 必须写成 UTF-8 **带 BOM**：脚本由 powershell.exe（Windows PowerShell 5.1）
            // 执行，无 BOM 时它会按系统 ANSI 代码页解码，文件里的中文会变成乱码，
            // 进而破坏引号配对、导致整个脚本语法错误而无法运行。
            File.WriteAllText(script, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Logger.Info($"更新脚本已写入: {script}（日志: {log}）");
            return script;
        }
        catch (Exception ex)
        {
            throw new UpdateException($"生成更新脚本失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 转义成可安全嵌入 PowerShell 单引号字符串的形式。
    /// 路径里可能出现单引号（用户名、目录名），不转义会把脚本的字符串截断、破坏语法。
    /// </summary>
    private static string EscapeForSingleQuotedPs(string value) =>
        (value ?? "").Replace("'", "''");

    /// <summary>读取内嵌的脚本模板（默认是覆盖式替换脚本）。</summary>
    private static string LoadScriptTemplate(string resourceFileName = "apply-update.ps1")
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new UpdateException($"找不到内嵌的脚本资源 {resourceFileName}。");
        }

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new UpdateException($"无法打开内嵌的脚本资源 {resourceFileName}。");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
