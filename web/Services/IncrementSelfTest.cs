using System.IO;
using System.IO.Compression;

namespace ForzaGallerySync.Services;

/// <summary>
/// 增量更新自检入口（自动化验证用，正常启动不会走到）。
///
/// 为什么需要它：增量更新涉及"清单解析 → 本地比对 → 逐文件下载 → 逐文件哈希 →
/// 重新打包 → 生成替换脚本"这一整条链路，只靠单元测试覆盖不到真实下载与打包行为。
/// 这里把链路跑完并把结论写进日志，再用退出码告诉调用方成败，
/// 同时**不覆盖任何正在使用的程序文件**（不启动替换脚本）。
///
/// 触发方式：环境变量 <c>FORZA_SYNC_INC_TEST=1</c>；
/// 版本号用 <c>FORZA_SYNC_INC_TEST_VERSION</c> 指定（默认取当前程序集版本）。
/// 清单与下载地址用 <see cref="IncrementalUpdateService"/> 里那两个环境变量指定，
/// 因此可以指向本地 HTTP 服务，不必真去 GitHub 下载。
/// </summary>
public static class IncrementSelfTest
{
    public static async Task<int> RunAsync()
    {
        var version = Environment.GetEnvironmentVariable("FORZA_SYNC_INC_TEST_VERSION");
        if (string.IsNullOrWhiteSpace(version)) version = AppVersion.Current;

        Logger.Info($"=== 增量更新自检开始（目标版本 {version}）===");
        Logger.Info("自检环境变量："
            + $"MANIFEST_FILE=[{Environment.GetEnvironmentVariable("FORZA_SYNC_INC_MANIFEST_FILE")}] "
            + $"MANIFEST_URL=[{Environment.GetEnvironmentVariable("FORZA_SYNC_INC_MANIFEST_URL")}] "
            + $"DOWNLOAD_BASE=[{Environment.GetEnvironmentVariable("FORZA_SYNC_INC_DOWNLOAD_BASE")}] "
            + $"APP_DIR=[{Environment.GetEnvironmentVariable("FORZA_SYNC_APP_DIR")}]");

        try
        {
            var report = new Progress<(double Percent, string Stage)>(
                p => Logger.Info($"自检进度 {p.Percent * 100:F0}%：{p.Stage}"));

            var result = await IncrementalUpdateService.TryPrepareAsync(
                version, report, CancellationToken.None);

            if (result is null)
            {
                Logger.Error("自检失败：增量准备返回 null（清单不可用 / 无可更新文件 / 需要完整包）");
                return 2;
            }

            var (zip, prefix) = result.Value;
            if (!File.Exists(zip))
            {
                Logger.Error($"自检失败：增量包不存在 {zip}");
                return 3;
            }

            var size = new FileInfo(zip).Length;
            using var archive = ZipFile.OpenRead(zip);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

            Logger.Info($"自检：增量包 {zip}（{size / 1024.0 / 1024.0:F2} MB），{entries.Count} 个条目，"
                + $"顶层前缀 '{prefix}'");

            // 空包必须直接判失败：前缀断言在 0 条目时会"全部通过"，
            // 曾经因此漏掉过一个"打包根目录不存在 → 产出空 zip"的缺陷。
            if (entries.Count == 0)
            {
                Logger.Error("自检失败：增量包是空的（0 个条目）");
                return 7;
            }

            // 结构断言：与完整包同构 —— 顶层目录等于 artifactPath
            var expectedPrefix = prefix.TrimEnd('/') + "/";
            var badPrefix = entries.Where(e => !e.FullName.Replace('\\', '/').StartsWith(expectedPrefix)).ToList();
            if (badPrefix.Count > 0)
            {
                Logger.Error($"自检失败：{badPrefix.Count} 个条目不在预期顶层目录下，"
                    + $"例如 {badPrefix[0].FullName}");
                return 4;
            }

            foreach (var entry in entries.Take(20))
            {
                Logger.Info($"  条目 {entry.FullName}（{entry.Length} 字节）");
            }

            // 替换脚本：用增量参数生成并做语法自检（不执行）
            var script = UpdateService.WriteScriptOnly(
                zip, prefix,
                Environment.UserDomainName + "\\" + Environment.UserName,
                skipExeCheck: true,
                preserve: IncrementalUpdateService.ProtectedRelativePaths);

            var check = UpdateService.SelfCheckScript(script);
            Logger.Info($"自检：{check}");

            if (!check.Contains("语法 OK") || !check.Contains("占位符已全部替换"))
            {
                Logger.Error("自检失败：替换脚本校验未通过");
                return 5;
            }

            var text = File.ReadAllText(script);
            if (!text.Contains("-eq '1'") || !text.Contains("apply-update.ps1"))
            {
                Logger.Error("自检失败：增量参数未正确写入脚本（SKIPEXE / PRESERVE）");
                return 6;
            }

            Logger.Info($"=== 增量更新自检通过（{entries.Count} 个文件，"
                + $"{size / 1024.0 / 1024.0:F2} MB）===");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Exception("增量更新自检异常", ex);
            return 9;
        }
        finally
        {
            IncrementalUpdateService.CleanupStage();
        }
    }
}
