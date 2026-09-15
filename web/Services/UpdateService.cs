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
    /// <summary>发布包下载地址（与 CHANGELOG / 仓库布局约定一致）。</summary>
    private const string ReleaseZipUrlFormat =
        "https://github.com/CaiBai-Fish/forza-gallery-sync/releases/download/v{0}/ForzaGallerySync-{0}-win-x64.zip";

    /// <summary>
    /// 产物哈希清单地址。
    ///
    /// 清单发布在独立的 `hashes` 分支（见 build-release.yml）：独立分支不会随
    /// `git clone` 下到工作区，历史也不会挤进主分支；这里用 raw URL 只取这一个文件。
    /// </summary>
    private const string HashesUrl =
        "https://raw.githubusercontent.com/CaiBai-Fish/forza-gallery-sync/hashes/hashes.json";

    /// <summary>发布包名（构造下载地址与在哈希清单里查表都用它）。</summary>
    private static string ArtifactName(string version) => $"ForzaGallerySync-{version}-win-x64.zip";

    /// <summary>
    /// 取得更新包的期望 SHA256。两个来源，按可靠性排序：
    ///
    /// 1. **GitHub Releases API 的 `digest` 字段**（优先）：这是发布平台自己算的哈希，
    ///    与上传时的字节绑定，最权威；缺点是要走 API（匿名限流 60 次/小时）。
    /// 2. **`hashes` 分支的清单**（兜底）：CI 自己算并发布到独立分支，
    ///    用 raw URL 取静态文件，不消耗 API 配额；API 限流或不可用时仍能校验。
    ///
    /// 两个来源都拿不到时返回 null，由调用方决定是否放行（当前策略：跳过校验但记告警）。
    /// </summary>
    public static async Task<string?> TryGetExpectedHashAsync(string version, CancellationToken token)
    {
        var fromApi = await TryGetHashFromApiAsync(version, token);
        if (fromApi is not null) return fromApi;

        return await TryGetHashFromHashesBranchAsync(version, token);
    }

    /// <summary>从 Releases API 的 digest 字段取哈希。</summary>
    private static async Task<string?> TryGetHashFromApiAsync(string version, CancellationToken token)
    {
        var artifact = ArtifactName(version);
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

                Logger.Warn($"Release 资产 {artifact} 没有 digest 字段，尝试 hashes 分支");
                return null;
            }

            Logger.Warn($"Release v{version} 中没有资产 {artifact}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"从 Release API 取哈希失败（将回退 hashes 分支）：{ex.Message}");
            return null;
        }
    }

    /// <summary>从 hashes 分支的清单取哈希（静态文件，不消耗 API 配额）。</summary>
    private static async Task<string?> TryGetHashFromHashesBranchAsync(string version, CancellationToken token)
    {
        var artifact = ArtifactName(version);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("forza-gallery-sync-updater");

            var json = await http.GetStringAsync(HashesUrl, token);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("artifacts", out var artifacts) ||
                !artifacts.TryGetProperty(artifact, out var entry) ||
                !entry.TryGetProperty("sha256", out var sha))
            {
                Logger.Warn($"哈希清单里没有 {artifact}，将跳过哈希校验");
                return null;
            }

            var value = sha.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                Logger.Warn($"哈希清单中 {artifact} 的 sha256 为空，将跳过哈希校验");
                return null;
            }

            Logger.Info($"期望哈希（hashes 分支）{artifact} = {value}");
            return value.Trim();
        }
        catch (Exception ex)
        {
            Logger.Warn($"获取哈希清单失败（将跳过校验）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 计算文件的 SHA256 并与期望值比较。
    /// <paramref name="expected"/> 为空（清单不可用）时返回 true 并记录告警——
    /// 不因为拿不到清单就阻断整个更新流程。
    /// </summary>
    public static bool VerifyHash(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            Logger.Warn("未取得期望哈希，跳过校验（建议确认网络与哈希分支可用）");
            return true;
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

    /// <summary>应用目录（exe 所在目录）。</summary>
    public static string AppDirectory =>
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;

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
    /// </summary>
    /// <returns>已校验的 zip 路径与 zip 内需要剥离的顶层目录，可直接交给替换脚本。</returns>
    public static async Task<(string ZipPath, string StripPrefix)> DownloadVerifiedAsync(
        string version,
        IProgress<double>? progress,
        CancellationToken token)
    {
        // 1) 先取期望哈希（清单不可用时返回 null，验证会被跳过并留下告警）
        var expected = await TryGetExpectedHashAsync(version, token);

        // 2) 下载
        var zip = await DownloadAsync(version, progress, token);

        // 3) 哈希校验：不一致必须删掉重下，绝不能拿去覆盖正在运行的程序
        if (!VerifyHash(zip, expected))
        {
            SafeDelete(zip);
            throw new UpdateException(
                "更新包哈希校验失败，文件可能下载不完整或已被篡改，已删除。请重试；若反复失败请到 Releases 页手动下载。");
        }

        // 4) 结构校验：确认确实是本程序的发布包，并取得需要剥离的顶层目录
        var prefix = ValidateArchive(zip);
        return (zip, prefix);
    }

    /// <summary>
    /// 下载指定版本的发布包。
    /// </summary>
    /// <param name="progress">进度回调（0~1，未知长度时为 -1 表示只报告已下载量）。</param>
    public static async Task<string> DownloadAsync(string version, IProgress<double>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new UpdateException("缺少目标版本号，无法构造下载地址。");
        }

        var url = string.Format(ReleaseZipUrlFormat, version);
        var target = DownloadPath(version);

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
                throw new UpdateException("下载到的文件为空。");
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
        if (!File.Exists(zipPath)) throw new UpdateException("更新包不存在。");

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries;
            if (entries.Count == 0) throw new UpdateException("更新包是空的。");

            // 发布包形如 ForzaGallerySync-<版本>-win-x64/forza-gallery-sync.exe
            var exe = entries.FirstOrDefault(e =>
                e.FullName.EndsWith("forza-gallery-sync.exe", StringComparison.OrdinalIgnoreCase));

            if (exe is null)
            {
                throw new UpdateException("更新包里找不到 forza-gallery-sync.exe，可能下载到了错误的文件。");
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
    public static void LaunchReplaceAndRestart(string zipPath, string stripPrefix, string identity)
    {
        var script = WriteScriptOnly(zipPath, stripPrefix, identity);

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
    /// 只展开替换脚本、不启动它，返回脚本路径。
    ///
    /// 独立出来是为了支持模拟模式（<c>FORZA_SYNC_UPDATE_SIMULATE=1</c>）：
    /// 可以把下载、哈希校验、脚本生成整条链路走完，但不覆盖正在运行的程序文件。
    /// </summary>
    public static string WriteScriptOnly(string zipPath, string stripPrefix, string identity)
    {
        var script = Path.Combine(Path.GetTempPath(), $"ForzaGallerySync-update-{Environment.ProcessId}.ps1");
        var log = Path.Combine(Path.GetTempPath(), "ForzaGallerySync-update.log");
        var stage = Path.Combine(Path.GetTempPath(), $"ForzaGallerySync-stage-{Environment.ProcessId}");

        try
        {
            var content = LoadScriptTemplate()
                .Replace("{{APP}}", EscapeForSingleQuotedPs(AppDirectory))
                .Replace("{{ZIP}}", EscapeForSingleQuotedPs(zipPath))
                .Replace("{{PREFIX}}", EscapeForSingleQuotedPs(stripPrefix))
                .Replace("{{STAGE}}", EscapeForSingleQuotedPs(stage))
                .Replace("{{LOG}}", EscapeForSingleQuotedPs(log))
                .Replace("{{PID}}", Environment.ProcessId.ToString())
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

    /// <summary>读取内嵌的替换脚本模板。</summary>
    private static string LoadScriptTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("apply-update.ps1", StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new UpdateException("找不到内嵌的更新脚本资源 apply-update.ps1。");
        }

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new UpdateException("无法打开内嵌的更新脚本资源。");
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
