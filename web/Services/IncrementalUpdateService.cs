using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaGallerySync.Services;

/// <summary>增量更新清单里的一条文件记录（对应 CI 生成的 increment.json）。</summary>
public sealed class IncrementFile
{
    /// <summary>相对发布根目录的路径，如 <c>af-ZA/Microsoft.ui.xaml.dll.mui</c>。</summary>
    public string Path { get; set; } = "";

    /// <summary>Release 资产名（路径分隔符换成 <c>_</c>）；空表示该文件没有独立资产。</summary>
    public string Name { get; set; } = "";

    public long Size { get; set; }

    public string Sha256 { get; set; } = "";

    /// <summary>该文件独立资产的压缩后大小（用于估算本次要下载多少）。</summary>
    public long Zipped { get; set; }

    public bool HasAsset => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>
/// 打包了多个小文件的增量资产。
///
/// 小文件（低于阈值）单独发资产会有几百个（GitHub 资产上限与 Release 页面都受不了），
/// 但任何一个变化的小文件都会让增量整体失效、回退完整包，所以 CI 把<em>变化的小文件</em>
/// 打成一个包发布，客户端只在确有差异时下载它。
/// </summary>
public sealed class IncrementContainer
{
    /// <summary>Release 资产名。</summary>
    public string Name { get; set; } = "";

    /// <summary>容器类型，当前只有 <c>small-files</c>。</summary>
    public string Kind { get; set; } = "";

    /// <summary>压缩后大小（用于估算下载量）。</summary>
    public long Zipped { get; set; }

    /// <summary>包内包含的相对路径（相对发布根目录，正斜杠分隔）。</summary>
    public List<string> Files { get; set; } = new();
}

/// <summary>增量更新清单。</summary>
public sealed class IncrementManifest
{
    public string Version { get; set; } = "";

    /// <summary>低于该体积的文件不提供独立资产，遇到差异时从 <see cref="Containers"/> 取。</summary>
    public long Threshold { get; set; }

    /// <summary>完整包解压后的顶层目录名（增量包会按它构造相同结构）。</summary>
    public string ArtifactPath { get; set; } = "";

    public string Generated { get; set; } = "";

    public List<IncrementFile> Files { get; set; } = new();

    /// <summary>多文件容器（小文件包）。可能为空。</summary>
    public List<IncrementContainer> Containers { get; set; } = new();
}

/// <summary>
/// 增量更新：只为真正变化的文件下载字节，不必每次重下整个发布包。
///
/// 背景：发布目录解压后约 269 MB，其中 Python 运行时 77 MB、.NET/Windows SDK 运行时 182 MB，
/// 这些跨版本几乎不变；每次版本更新真正变的只有应用自身那几个文件。
///
/// 策略：
/// - CI 把 ≥<see cref="IncrementManifest.Threshold"/> 的文件各自发布成一个 Release 资产，
///   并生成逐文件清单 <c>increment.json</c>（在 <c>hashes</c> 分支）。
/// - 客户端取清单后**只对比本地实际文件的哈希**，不依赖"已安装版本"记录，
///   因此不存在"没有基线就不能增量"的问题。
/// - 本地缺失或哈希不符、且**有独立资产**的文件 → 下载并逐个校验 SHA256。
/// - 本地缺失或哈希不符、且**没有独立资产**（小文件）→ 交给调用方回退完整包。
/// - 任何一个文件校验失败 → 整体放弃增量，由调用方回退完整包（完整包那条路径一直是可用的）。
/// </summary>
public static class IncrementalUpdateService
{
    private const string DefaultBranchBase =
        "https://raw.githubusercontent.com/CaiBai-Fish/forza-gallery-sync/hashes";

    private const string DefaultReleaseDownloadBase =
        "https://github.com/CaiBai-Fish/forza-gallery-sync/releases/download/v{0}";

    /// <summary>
    /// 清单地址。默认是 <c>hashes</c> 分支上的 <c>increment.json</c>；可用环境变量覆盖：
    /// - <c>FORZA_SYNC_INC_MANIFEST_URL</c>：直接指定清单 URL；
    /// - <c>FORZA_SYNC_INC_MANIFEST_FILE</c>：读取本地清单文件（离线/自动化验证用）。
    /// </summary>
    private static string ManifestLocation =>
        Environment.GetEnvironmentVariable("FORZA_SYNC_INC_MANIFEST_URL")
        ?? (Environment.GetEnvironmentVariable("FORZA_SYNC_INC_MANIFEST_FILE") is { Length: > 0 } file
            ? file
            : $"{DefaultBranchBase}/increment.json");

    /// <summary>
    /// 资产下载地址前缀（<c>{0}</c> 会被渲染成版本号）。
    ///
    /// 默认走 GitHub Release，版本号是路径的一部分（<c>/download/v1.0.3</c>），必须插入；
    /// 但环境变量 <c>FORZA_SYNC_INC_DOWNLOAD_BASE</c> 覆盖的是一个**平铺的资产根地址**
    /// （本地端到端验证用 <c>http://127.0.0.1:8732</c> 直接指向资产目录），
    /// 再插版本号会变成 <c>/1.0.3/</c> 子目录而取不到文件，所以那一支返回空前缀。
    /// </summary>
    private static string DownloadPrefix(string version) =>
        Environment.GetEnvironmentVariable("FORZA_SYNC_INC_DOWNLOAD_BASE") is { Length: > 0 } custom
            ? custom.TrimEnd('/') + "/"
            : string.Format(DefaultReleaseDownloadBase, version) + "/";

    /// <summary>并发下载数：GitHub 对 raw/资产下载足够宽松，4 路能明显缩短小文件总耗时。</summary>
    private const int MaxParallel = 4;

    /// <summary>
    /// 增量更新不允许覆盖的相对路径。
    ///
    /// 替换脚本自身（<c>apply-update.ps1</c>）必须保护：它正在执行，覆盖它会让本进程的
    /// 行为不可预期；它本身也由 CI 生成（<c>make-gui.ps1</c> 从源码复制），
    /// 内容不会随版本变化。需要更新它时走完整包。
    /// </summary>
    public static readonly string[] ProtectedRelativePaths = { "apply-update.ps1" };

    /// <summary>缓存目录（系统临时目录，避免污染程序目录）。</summary>
    private static string CacheRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaGallerySync-incremental");

    /// <summary>下载清单。失败返回 null（调用方回退完整包）。</summary>
    public static async Task<IncrementManifest?> TryGetManifestAsync(CancellationToken token)
    {
        var location = ManifestLocation;
        try
        {
            string json;
            if (!location.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                json = await File.ReadAllTextAsync(location, token);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("forza-gallery-sync-updater");
                json = await http.GetStringAsync(location, token);
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            };
            var manifest = JsonSerializer.Deserialize<IncrementManifest>(json, options);

            if (manifest is null || manifest.Files.Count == 0)
            {
                Logger.Warn("增量清单为空，回退完整包");
                return null;
            }

            Logger.Info($"增量清单: v{manifest.Version}，{manifest.Files.Count} 个文件，"
                + $"{manifest.Containers.Count} 个容器，阈值 {manifest.Threshold} 字节，"
                + $"顶层目录 '{manifest.ArtifactPath}'（来源 {location}）");
            return manifest;
        }
        catch (Exception ex)
        {
            Logger.Warn($"取增量清单失败（回退完整包）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 尝试用增量方式准备更新包。
    /// </summary>
    /// <param name="version">目标版本（必须与清单版本一致，否则清单是别的版本的）。</param>
    /// <param name="progress">进度回调：0~1，附带阶段文字。</param>
    /// <returns>可直接交给替换脚本的 (zip 路径, 顶层前缀)；无法增量时返回 null。</returns>
    public static async Task<(string ZipPath, string StripPrefix)?> TryPrepareAsync(
        string version,
        IProgress<(double Percent, string Stage)>? progress,
        CancellationToken token)
    {
        var manifest = await TryGetManifestAsync(token);
        if (manifest is null) return null;

        if (!string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"增量清单版本（{manifest.Version}）与目标版本（{version}）不一致，回退完整包");
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.ArtifactPath))
        {
            Logger.Warn("增量清单缺少 artifactPath，回退完整包");
            return null;
        }

        // 资产名必须唯一，否则无法可靠映射到文件
        var duplicated = manifest.Files
            .Where(f => f.HasAsset)
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            Logger.Warn($"增量清单里资产名重复（{duplicated.Key}），回退完整包");
            return null;
        }

        progress?.Report((0, StringLocalizer.Get("Inc_CompareLocal")));

        var appDir = UpdateService.AppDirectory;
        var need = new List<IncrementFile>();
        var needSmall = new List<IncrementFile>();
        long needBytes = 0;

        // 结构变化：远端新增 / 删除的文件。任何一种都说明本地布局与目标版本不同，
        // 增量的"覆盖已有文件"语义不足以修正它，直接走完整包（完整包会补齐新增文件，
        // 但也同样不删除多余文件——多余文件无害）。小文件删除很常见（本次就删掉了几处），
        // 所以这种情况不该算失败，而是"需要完整包"的明确信号。
        if (!HasSameFileSet(manifest, appDir, out var structureNote))
        {
            Logger.Info($"增量不适用：{structureNote}");
            return null;
        }

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = manifest.Files[i];

            if (!NeedsUpdate(file, appDir))
            {
                continue;
            }

            if (file.HasAsset)
            {
                need.Add(file);
                needBytes += file.Zipped > 0 ? file.Zipped : file.Size;
            }
            else
            {
                // 小文件差异：由容器（小文件包）统一提供
                needSmall.Add(file);
            }

            if (i % 200 == 0)
            {
                progress?.Report((0, $"正在比对本地文件… {i + 1}/{manifest.Files.Count}"));
            }
        }

        // 需要小文件包时，找到覆盖这些文件的容器
        var needContainers = new List<IncrementContainer>();
        if (needSmall.Count > 0)
        {
            var smallPaths = needSmall.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var container in manifest.Containers)
            {
                if (container.Kind != "small-files") continue;
                if (container.Files.Any(smallPaths.Contains))
                {
                    needContainers.Add(container);
                    needBytes += container.Zipped;
                }
            }

            var covered = needContainers.SelectMany(c => c.Files).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var uncovered = smallPaths.Where(p => !covered.Contains(p)).ToList();
            if (uncovered.Count > 0)
            {
                Logger.Info($"有 {uncovered.Count} 个差异文件既没有独立资产也不在任何容器里"
                    + $"（示例：{uncovered[0]}），回退完整包");
                return null;
            }
        }

        if (need.Count == 0 && needContainers.Count == 0)
        {
            // 本地已经是目标版本的文件内容：无需更新
            Logger.Info("增量比对结果：本地文件与远端一致，无需更新");
            return null;
        }

        Logger.Info($"增量更新：独立资产 {need.Count} 个，小文件包 {needContainers.Count} 个，"
            + $"约 {needBytes / 1024.0 / 1024.0:F1} MB（完整包约 130 MB）");

        var stage = System.IO.Path.Combine(CacheRoot, $"stage-{Environment.ProcessId}", manifest.ArtifactPath);
        try
        {
            if (Directory.Exists(System.IO.Path.Combine(CacheRoot, $"stage-{Environment.ProcessId}")))
            {
                Directory.Delete(System.IO.Path.Combine(CacheRoot, $"stage-{Environment.ProcessId}"), true);
            }

            // stage 下必须带 artifactPath 这一层：打包时以它为顶层目录，
            // 才能让增量包与完整包同构（替换脚本按同一个前缀剥离）。
            // 少这一层会让打包根目录不存在 → 产出一个空 zip。
            Directory.CreateDirectory(stage);

            long done = 0;
            var failed = 0;
            var finished = 0;
            using var slots = new SemaphoreSlim(MaxParallel);
            var tasks = new List<Task>();

            foreach (var file in need)
            {
                await slots.WaitAsync(token);
                var captured = file;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var local = System.IO.Path.Combine(stage,
                            captured.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);

                        await DownloadAssetAsync(version, captured.Name, local, token);

                        var actual = Sha256Of(local);
                        if (!string.Equals(actual, captured.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Error($"增量文件哈希不符：{captured.Path}（期望 {captured.Sha256}，实际 {actual}）");
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        var written = Interlocked.Add(ref done, captured.Zipped > 0 ? captured.Zipped : captured.Size);
                        var index = Interlocked.Increment(ref finished);
                        var percent = needBytes > 0 ? Math.Min(1.0, (double)written / needBytes) : 0;
                        progress?.Report((percent * 0.95,
                            $"正在下载增量文件 {index}/{need.Count}… "
                            + $"{written / 1024.0 / 1024.0:F1}/{needBytes / 1024.0 / 1024.0:F1} MB"));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"增量下载失败：{captured.Path}：{ex.Message}");
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, token));
            }

            await Task.WhenAll(tasks);

            if (failed > 0)
            {
                Logger.Warn($"增量更新有 {failed} 个文件失败，回退完整包");
                CleanupStage();
                return null;
            }

            // 小文件包：下载后用容器清单逐个校验解出来的文件
            foreach (var container in needContainers)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report((0.95, $"正在下载小文件包（{container.Files.Count} 个文件，"
                    + $"{container.Zipped / 1024.0 / 1024.0:F1} MB）…"));

                // 注意：容器是"多文件 zip"，必须整包落盘，不能用 DownloadAssetAsync
                // （那个是给"单文件 zip"解包的，会把容器解压成里面第一个文件）
                var containerZip = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"ForzaGallerySync-handoff-{container.Name}");
                await DownloadToFileAsync(version, container.Name, containerZip, token);

                var stageRoot = System.IO.Path.Combine(CacheRoot, $"stage-{Environment.ProcessId}",
                    manifest.ArtifactPath);
                try
                {
                    using var stream = new FileStream(containerZip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    var expectedByPath = manifest.Files
                        .Where(f => container.Files.Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                        .ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);

                    var mismatched = 0;
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // 目录条目

                        var rel = entry.FullName.Replace('\\', '/');
                        var dest = System.IO.Path.Combine(stageRoot,
                            rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, true);

                        if (!expectedByPath.TryGetValue(rel, out var expected))
                        {
                            Logger.Warn($"小文件包里出现了清单没有的文件：{rel}");
                            continue;
                        }

                        var actual = Sha256Of(dest);
                        if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Error($"小文件哈希不符：{rel}");
                            mismatched++;
                        }
                    }

                    if (mismatched > 0)
                    {
                        Logger.Warn($"小文件包有 {mismatched} 个文件哈希不符，回退完整包");
                        CleanupStage();
                        return null;
                    }
                }
                finally
                {
                    try { if (File.Exists(containerZip)) File.Delete(containerZip); } catch { /* 清理失败忽略 */ }
                }
            }

            progress?.Report((0.97, StringLocalizer.Get("Inc_Packing")));
            var zipPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ForzaGallerySync-incremental-{version}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            // 压缩**发布目录本身**并把它的名字作为包内顶层目录，
            // 这样增量包与完整包同构（`<artifactPath>/...`），替换脚本用同一个前缀即可。
            ZipFile.CreateFromDirectory(
                System.IO.Path.Combine(CacheRoot, $"stage-{Environment.ProcessId}", manifest.ArtifactPath),
                zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);

            Logger.Info($"增量更新包已生成：{zipPath}（{new FileInfo(zipPath).Length / 1024.0 / 1024.0:F1} MB）");
            return (zipPath, manifest.ArtifactPath.Replace('\\', '/') + "/");
        }
        catch (OperationCanceledException)
        {
            CleanupStage();
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"增量更新准备失败（回退完整包）：{ex.Message}");
            CleanupStage();
            return null;
        }
    }

    /// <summary>
    /// 本地文件是否需要更新。
    ///
    /// - 本地不存在 → 需要；
    /// - 体积不同（有记录时）→ 需要；
    /// - 否则算 SHA256 比对。
    ///
    /// **一律做哈希比对**，不按体积走捷径：小文件改变内容而体积不变是很常见的
    /// （改个常量、改段文本），只比体积会静默漏掉这类更新。开销也划得来——
    /// 发布目录约 269 MB / 3682 个文件，全量哈希在本地磁盘上是秒级，
    /// 而它换来的判定与"完整包安装后的状态"完全一致。
    /// </summary>
    private static bool NeedsUpdate(IncrementFile file, string appDir)
    {
        var local = System.IO.Path.Combine(appDir, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        try
        {
            var info = new FileInfo(local);
            if (!info.Exists) return true;
            if (info.Length != file.Size) return true;

            var actual = Sha256Of(local);
            return !string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warn($"比对本地文件失败（{file.Path}）：{ex.Message}，按需要更新处理");
            return true;
        }
    }

    /// <summary>
    /// 增量路线是否可行。
    ///
    /// 增量的语义是"覆盖变化的文件"，它不负责补齐远端新增、也不负责删除远端删掉的文件。
    /// 但这两件事都不该成为回退完整包的理由：
    /// - 远端新增的文件，本地没有 → 会被判为"需要下载"，大文件有独立资产、小文件在容器里；
    /// - 远端删掉的文件，本地多出来 → 留着无害（完整包路径同样"只覆盖不删除"）。
    ///
    /// 真正无法增量处理的是：**本地缺失、又没有独立资产、也不在任何容器里的小文件**——
    /// 这种文件没有下载来源，只能靠完整包。这里只拦这一种情况。
    /// </summary>
    private static bool HasSameFileSet(IncrementManifest manifest, string appDir, out string note)
    {
        if (!Directory.Exists(appDir))
        {
            note = $"程序目录不存在：{appDir}";
            return false;
        }

        var containerPaths = manifest.Containers
            .Where(c => c.Kind == "small-files")
            .SelectMany(c => c.Files)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files)
        {
            // 大文件、或自带独立资产的文件：总有下载来源
            if (file.Size >= manifest.Threshold || file.HasAsset) continue;

            var rel = file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (File.Exists(System.IO.Path.Combine(appDir, rel))) continue;
            if (containerPaths.Contains(file.Path)) continue;

            note = $"本地缺失的小文件不在任何容器里：{file.Path}";
            return false;
        }

        note = "";
        return true;
    }

    /// <summary>下载单个增量资产（单文件 zip）并解到目标路径。</summary>
    private static async Task DownloadAssetAsync(
        string version, string assetName, string destination, CancellationToken token)
    {
        var zip = destination + ".zip";
        try
        {
            await DownloadToFileAsync(version, assetName, zip, token);

            // 每个增量资产是"单文件 zip"（小文件包除外，它由调用方自行解压）
            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name))
                ?? throw new UpdateException($"增量资产 {assetName} 是空包");

            var temp = destination + ".tmp";
            entry.ExtractToFile(temp, true);
            File.Move(temp, destination, true);
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch { /* 清理失败不影响主流程 */ }
        }
    }

    /// <summary>把 Release 资产下载到指定路径。</summary>
    private static async Task DownloadToFileAsync(
        string version, string assetName, string destination, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("forza-gallery-sync-updater");

        var url = GetAssetUrl(version, assetName);
        Logger.Info($"增量下载: {url}");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);

        if (!resp.IsSuccessStatusCode)
        {
            throw new UpdateException($"下载 {assetName} 失败：HTTP {(int)resp.StatusCode}（{url}）");
        }

        await using var source = await resp.Content.ReadAsStreamAsync(token);
        await using (var target = File.Create(destination))
        {
            await source.CopyToAsync(target, token);
        }

        var written = new FileInfo(destination).Length;
        Logger.Info($"增量资产已落盘: {destination}（{written} 字节，"
            + $"Content-Length={resp.Content.Headers.ContentLength}）");
    }

    /// <summary>
    /// Release 资产下载地址。
    ///
    /// GitHub 会把资产名里不安全的字符替换成 <c>.</c>（实测空格变点号），
    /// 因此这里对资产名做同样的转义，再拼到下载前缀后面。
    /// 用 tag（<c>v1.0.3</c>）而不是 <c>latest</c>：必须下载与清单一致的版本。
    ///
    /// 清单里的 <c>name</c> 是<em>资产名</em>（不带扩展名），实际发布出来的文件名是
    /// <c>&lt;资产名&gt;.zip</c>（CI 用 <c>Compress-Archive</c> 产生），这里补上后缀。
    /// </summary>
    private static string GetAssetUrl(string version, string assetName) =>
        DownloadPrefix(version) + Uri.EscapeDataString(assetName + ".zip").Replace("%20", ".");

    /// <summary>计算文件的 SHA256（小写十六进制）。</summary>
    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>清理增量缓存（下载阶段结束后调用；替换脚本用的是另一个文件）。</summary>
    public static void CleanupStage()
    {
        var stage = System.IO.Path.Combine(CacheRoot, $"stage-{Environment.ProcessId}");
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"清理增量缓存失败：{ex.Message}");
        }
    }
}
