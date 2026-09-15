using System.Collections.ObjectModel;
using ForzaGallerySync.Models;
using ForzaGallerySync.Services;

namespace ForzaGallerySync.ViewModels;

/// <summary>设置：浏览器登录、Token 刷新、同步参数配置。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    private ConfigModel _config = new();
    private AuthModel _auth = new();
    private bool _loading = true;
    private bool _saving;
    private bool _refreshing;
    private string _error = "";
    private string _okMsg = "";
    private string _loginState = "idle";
    private string _loginMsg = "";

    // ---- 表单字段 ----
    private string _downloadDir = "";
    private string _pageSize = "50";
    private string _pagination = "auto";
    private string _timeout = "30";
    private string _retries = "3";
    private string _workers = "4";
    private bool _verifySsl = true;
    private string _userAgent = "";
    private bool _checkingUpdate;
    private string _updateText = "";
    private bool _hasUpdate;
    private string _currentVersion = "";
    private string _latestVersion = "";
    private string _updateUrl = "";

    public bool Loading
    {
        get => _loading;
        set => SetProperty(ref _loading, value);
    }

    public bool Saving
    {
        get => _saving;
        set => SetProperty(ref _saving, value);
    }

    public bool Refreshing
    {
        get => _refreshing;
        set => SetProperty(ref _refreshing, value);
    }

    public string Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>是否存在错误提示（页面据此显示提示条）。</summary>
    public bool HasError => !string.IsNullOrEmpty(_error);

    public string OkMsg
    {
        get => _okMsg;
        set
        {
            if (SetProperty(ref _okMsg, value)) OnPropertyChanged(nameof(HasOkMsg));
        }
    }

    /// <summary>是否存在成功提示（页面据此显示提示条）。</summary>
    public bool HasOkMsg => !string.IsNullOrEmpty(_okMsg);

    public string LoginState
    {
        get => _loginState;
        set
        {
            if (SetProperty(ref _loginState, value)) OnPropertyChanged(nameof(IsLoggingIn));
        }
    }

    public string LoginMsg
    {
        get => _loginMsg;
        set => SetProperty(ref _loginMsg, value);
    }

    public bool IsLoggingIn => LoginState == "running";

    public ConfigModel Config
    {
        get => _config;
        set
        {
            if (SetProperty(ref _config, value))
            {
                OnPropertyChanged(nameof(DatabasePath));
                OnPropertyChanged(nameof(ConfigPath));
            }
        }
    }

    /// <summary>SQLite 数据库路径（只读展示）。</summary>
    public string DatabasePath => Config.DatabasePath;

    /// <summary>配置文件路径（只读展示）。</summary>
    public string ConfigPath => Config.ConfigPath;

    public AuthModel Auth
    {
        get => _auth;
        set
        {
            if (SetProperty(ref _auth, value))
            {
                OnPropertyChanged(nameof(TokenStatusText));
                OnPropertyChanged(nameof(TokenStatusGood));
            }
        }
    }

    public string TokenStatusText =>
        !Auth.HasToken ? "未配置" : (Auth.Expired ? "已过期" : "有效");

    public bool TokenStatusGood => Auth.HasToken && !Auth.Expired;

    // ---- 表单属性 ----
    public string DownloadDir { get => _downloadDir; set => SetProperty(ref _downloadDir, value); }
    public string PageSize { get => _pageSize; set => SetProperty(ref _pageSize, value); }
    public string Pagination { get => _pagination; set => SetProperty(ref _pagination, value); }
    public string Timeout { get => _timeout; set => SetProperty(ref _timeout, value); }
    public string Retries { get => _retries; set => SetProperty(ref _retries, value); }
    public string Workers { get => _workers; set => SetProperty(ref _workers, value); }
    public bool VerifySsl { get => _verifySsl; set => SetProperty(ref _verifySsl, value); }
    public string UserAgent { get => _userAgent; set => SetProperty(ref _userAgent, value); }

    public bool CheckingUpdate
    {
        get => _checkingUpdate;
        set
        {
            if (SetProperty(ref _checkingUpdate, value)) OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    public string UpdateText
    {
        get => _updateText;
        set => SetProperty(ref _updateText, value);
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        set
        {
            if (SetProperty(ref _hasUpdate, value)) OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        set => SetProperty(ref _currentVersion, value);
    }

    public string LatestVersion
    {
        get => _latestVersion;
        set => SetProperty(ref _latestVersion, value);
    }

    public string UpdateUrl
    {
        get => _updateUrl;
        set => SetProperty(ref _updateUrl, value);
    }

    /// <summary>可多选的启用游戏 Toggle 集合（绑定页面 ToggleButton）。</summary>
    public ObservableCollection<GameToggleViewModel> GameToggles { get; } = new();

    private List<string> EnabledGames =>
        GameToggles.Where(t => t.IsChecked).Select(t => t.Id).ToList();

    public List<string> PaginationOptions { get; } = new()
    {
        "auto", "page", "skip", "offset", "page_number", "none",
    };

    public bool IsGameEnabled(string id) => GameToggles.Any(t => t.Id == id && t.IsChecked);

    public void ToggleGame(string id)
    {
        var t = GameToggles.FirstOrDefault(x => x.Id == id);
        if (t is not null) t.IsChecked = !t.IsChecked;
    }

    /// <summary>请求打开目录选择器。</summary>
    public event Action? PickDirRequested;

    public void Start() => _ = PollLoginLoopAsync();

    public void Stop() => _cts.Cancel();

    private async Task PollLoginLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await PollLoginAsync();
            try
            {
                await Task.Delay(2000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollLoginAsync()
    {
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("auth_login_status");
            var state = Json.Deserialize<Dictionary<string, object?>>(json) ?? new();
            Ui.Run(() =>
            {
                LoginState = state.GetValueOrDefault("state")?.ToString() ?? "idle";
                LoginMsg = state.GetValueOrDefault("message")?.ToString() ?? "";
                if (LoginState == "success")
                {
                    _ = LoadAsync(); // 登录成功，刷新配置与 Token 状态
                }
            });
        }
        catch
        {
            // 忽略轮询错误。
        }
    }

    public async Task LoadAsync()
    {
        Loading = true;
        Error = "";
        try
        {
            var cfgJson = await PyBridge.Instance.CallJsonAsync("get_config");
            var authJson = await PyBridge.Instance.CallJsonAsync("auth_status");
            var cfg = Json.Deserialize<ConfigModel>(cfgJson) ?? new ConfigModel();
            var auth = Json.Deserialize<AuthModel>(authJson) ?? new AuthModel();
            Ui.Run(() =>
            {
                Config = cfg;
                Auth = auth;
                DownloadDir = cfg.DownloadDir;
                PageSize = cfg.PageSize.ToString();
                Pagination = cfg.Pagination;
                Timeout = cfg.Timeout.ToString();
                Retries = cfg.Retries.ToString();
                Workers = cfg.Workers.ToString();
                VerifySsl = cfg.VerifySsl;
                UserAgent = cfg.UserAgent;
                GameToggles.Clear();
                foreach (var g in cfg.SupportedGames)
                {
                    var toggle = new GameToggleViewModel
                    {
                        Id = g.Id,
                        Name = g.Name,
                        IsChecked = cfg.EnabledGames.Contains(g.Id),
                    };
                    GameToggles.Add(toggle);
                }
            });
        }
        catch (Exception ex)
        {
            Ui.Run(() => Error = ex.Message);
        }
        finally
        {
            Ui.Run(() => Loading = false);
        }
    }

    public async Task SaveAsync()
    {
        Saving = true;
        Error = "";
        OkMsg = "";
        try
        {
            var values = new Dictionary<string, object?>
            {
                ["download_dir"] = DownloadDir,
                ["page_size"] = int.TryParse(PageSize, out var ps) ? ps : 50,
                ["pagination"] = Pagination,
                ["timeout"] = int.TryParse(Timeout, out var to) ? to : 30,
                ["retries"] = int.TryParse(Retries, out var rt) ? rt : 3,
                ["workers"] = int.TryParse(Workers, out var wk) ? wk : 4,
                ["verify_ssl"] = VerifySsl,
                ["user_agent"] = UserAgent,
                ["enabled_games"] = EnabledGames,
            };
            var json = await PyBridge.Instance.CallJsonAsync("update_config", Json.Serialize(new { values }));
            var cfg = Json.Deserialize<ConfigModel>(json) ?? Config;
            Ui.Run(() =>
            {
                Config = cfg;
                OkMsg = "设置已保存";
                _ = ClearOkMsgAsync();
            });
        }
        catch (Exception ex)
        {
            Ui.Run(() => Error = ex.Message);
        }
        finally
        {
            Ui.Run(() => Saving = false);
        }
    }

    private async Task ClearOkMsgAsync()
    {
        await Task.Delay(2500);
        Ui.Run(() => OkMsg = "");
    }

    public async Task RefreshTokenAsync()
    {
        Refreshing = true;
        Error = "";
        OkMsg = "";
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("auth_refresh");
            var res = Json.Deserialize<Dictionary<string, object?>>(json) ?? new();
            var msg = res.GetValueOrDefault("message")?.ToString() ?? "Token 已刷新";
            Ui.Run(() => OkMsg = msg);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Ui.Run(() => Error = ex.Message);
        }
        finally
        {
            Ui.Run(() => Refreshing = false);
        }
    }

    public async Task LoadVersionAsync()
    {
        // 权威来源是程序集信息（发布脚本注入，取自 pyproject.toml）；
        // 后端版本只作兜底——内嵌 Python 包落后于 GUI 时不该显示旧版本号。
        var fromAssembly = AppVersion.Current;
        if (!string.IsNullOrWhiteSpace(fromAssembly))
        {
            Ui.Run(() => CurrentVersion = fromAssembly);
        }

        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("app_version");
            var res = Json.Deserialize<Dictionary<string, object?>>(json);
            var v = res?.GetValueOrDefault("version")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(fromAssembly) && !string.IsNullOrWhiteSpace(v))
            {
                Ui.Run(() => CurrentVersion = v);
            }
        }
        catch
        {
            // 版本获取失败忽略（已有程序集版本可用）。
        }
    }

    public async Task CheckUpdateAsync()
    {
        CheckingUpdate = true;
        UpdateText = "正在检查更新…";
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("check_update");
            var info = Json.Deserialize<UpdateInfoModel>(json) ?? new UpdateInfoModel();

            // 当前版本以程序集为准：Python 的 __version__ 只在程序集版本缺失时兜底，
            // 否则内嵌包版本落后会让界面把"已是最新"显示成"有新版本"。
            var myVersion = AppVersion.Current;
            if (!string.IsNullOrWhiteSpace(myVersion))
            {
                info.Current = myVersion;
                if (!string.IsNullOrWhiteSpace(info.Latest))
                {
                    info.HasUpdate = CompareVersions(info.Latest, myVersion) > 0;
                }
            }

            Ui.Run(() =>
            {
                CurrentVersion = info.Current;
                LatestVersion = info.Latest;
                UpdateUrl = info.Url;
                HasUpdate = info.HasUpdate;
                UpdateText = BuildUpdateText(info);

                // 注意：这两个属性不是 SetProperty 管理的字段，必须显式通知；
                // 否则 x:Bind(OneWay) 仍显示初始值，界面看上去"没有更新日志"。
                UpdateNotes = info.Notes;
                OnPropertyChanged(nameof(UpdateNotes));
                OnPropertyChanged(nameof(HasUpdateNotes));
            });
        }
        catch (Exception ex)
        {
            Ui.Run(() =>
            {
                HasUpdate = false;
                UpdateText = "检查更新失败";
                Error = ex.Message;
            });
        }
        finally
        {
            Ui.Run(() => CheckingUpdate = false);
        }
    }

    private static string BuildUpdateText(UpdateInfoModel info)
    {
        if (!string.IsNullOrEmpty(info.Error))
            return $"检查更新失败：{info.Error}";
        if (info.HasUpdate)
            return $"发现新版本 v{info.Latest}（当前 v{info.Current}）";
        return $"已是最新版本（v{info.Current}）";
    }

    /// <summary>最新版本的更新说明（取自 CHANGELOG 对应章节）。</summary>
    public string UpdateNotes { get; private set; } = "";

    /// <summary>是否有更新说明可展示。</summary>
    public bool HasUpdateNotes => !string.IsNullOrWhiteSpace(UpdateNotes);

    // ---- 自动更新 ----

    private bool _downloading;
    private double _downloadProgress;
    private string _updateActionMsg = "";
    private bool _updateBusy;
    private readonly CancellationTokenSource _updateCts = new();

    /// <summary>是否正在下载更新包。</summary>
    public bool Downloading
    {
        get => _downloading;
        private set
        {
            if (SetProperty(ref _downloading, value)) OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    /// <summary>下载进度（0~100）。</summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    /// <summary>更新流程的状态文字（下载中 / 校验中 / 失败原因）。</summary>
    public string UpdateActionMsg
    {
        get => _updateActionMsg;
        private set
        {
            if (SetProperty(ref _updateActionMsg, value)) OnPropertyChanged(nameof(HasUpdateActionMsg));
        }
    }

    public bool HasUpdateActionMsg => !string.IsNullOrEmpty(_updateActionMsg);

    /// <summary>可以点「下载并更新」：有新版本、没在下载、也没在检查。</summary>
    public bool CanDownloadUpdate => HasUpdate && !_downloading && !CheckingUpdate && !_updateBusy;

    private bool _preferIncremental = true;

    /// <summary>
    /// 优先增量更新：只下载真正变化的文件，失败时自动回退完整包。
    ///
    /// 发布目录解压后约 269 MB，其中 Python 运行时与 .NET/Windows SDK 运行时跨版本几乎不变，
    /// 每次更新真正变的只有应用自身那几个文件，所以增量通常能省掉绝大部分流量。
    /// </summary>
    public bool PreferIncremental
    {
        get => _preferIncremental;
        set
        {
            if (SetProperty(ref _preferIncremental, value))
            {
                Logger.Info($"增量更新开关：{(value ? "启用" : "停用")}");
            }
        }
    }

    /// <summary>下载 + 校验 + 启动替换脚本，随后由页面退出应用。</summary>
    public async Task DownloadAndApplyUpdateAsync()
    {
        if (!HasUpdate || string.IsNullOrWhiteSpace(LatestVersion)) return;

        if (!UpdateService.CanAutoUpdate(out var reason))
        {
            UpdateActionMsg = reason;
            return;
        }

        _updateBusy = true;
        Downloading = true;
        DownloadProgress = 0;
        UpdateActionMsg = $"正在准备更新 {LatestVersion}…";

        try
        {
            // ---- 默认方式：下载官方安装程序并运行它 ----
            //
            // 为什么改成安装程序而不是"解压覆盖"：安装程序是官方发布产物，自带卸载入口与
            // 注册表记录，装完的程序在"设置 → 应用"里能正常管理；覆盖式替换只在便携版
            // 语义下成立。安装位置由 UpdateService.IsInstalled 决定：
            //   - 免安装版（zip 解压运行）→ /DIR=<当前目录>，装回原处，升级后目录不变；
            //   - 安装版 → 不带 /DIR，交给 Inno 用它记录的目录，避免出现两个安装位置。
            if (PreferIncremental)
            {
                var installed = await TryInstallerUpdateAsync();
                if (installed) return;
                Logger.Info("安装程序方式不可用，回退到增量更新");
            }

            // 1) 再试增量：拿清单 → 比对本地文件 → 只下变化的文件 → 逐个校验哈希。
            //    任何一步不成立（无清单、版本不符、小文件有差异、下载失败）都返回 null，
            //    接下来原样走完整包那条路径——所以增量失败不会让用户无法更新。
            if (PreferIncremental)
            {
                var incremental = await TryIncrementalAsync();
                if (incremental is { } inc)
                {
                    UpdateActionMsg = "增量更新已校验，正在准备替换…";
                    Logger.Info($"增量更新就绪：{inc.ZipPath}（前缀 '{inc.Prefix}'）");

                    if (Environment.GetEnvironmentVariable("FORZA_SYNC_UPDATE_SIMULATE") == "1")
                    {
                        var s = UpdateService.WriteScriptOnly(
                            inc.ZipPath, inc.Prefix,
                            Environment.UserDomainName + "\\" + Environment.UserName,
                            skipExeCheck: true, preserve: IncrementalUpdateService.ProtectedRelativePaths);
                        var check = UpdateService.SelfCheckScript(s);
                        UpdateActionMsg = $"模拟模式（增量）：{check}";
                        Logger.Info($"模拟模式（增量）结果：zip={inc.ZipPath}, {check}");
                        return;
                    }

                    UpdateService.LaunchReplaceAndRestart(
                        inc.ZipPath, inc.Prefix,
                        Environment.UserDomainName + "\\" + Environment.UserName,
                        skipExeCheck: true, preserve: IncrementalUpdateService.ProtectedRelativePaths);

                    UpdateActionMsg = "即将退出并完成增量更新…";
                    UpdateRequested?.Invoke();
                    return;
                }

                Logger.Info("增量更新不可用，回退完整包");
            }

            // 2) 完整包
            var progress = new Progress<double>(p => Ui.Run(() =>
            {
                if (p >= 0)
                {
                    DownloadProgress = Math.Round(p * 100, 0);
                    UpdateActionMsg = $"正在下载完整包 {LatestVersion}… {DownloadProgress:F0}%";
                }
                else
                {
                    UpdateActionMsg = "正在下载完整包…";
                }
            }));

            var (zip, prefix) = await UpdateService.DownloadVerifiedAsync(
                LatestVersion, progress, _updateCts.Token);

            if (Environment.GetEnvironmentVariable("FORZA_SYNC_UPDATE_SIMULATE") == "1")
            {
                var script = UpdateService.WriteScriptOnly(zip, prefix,
                    Environment.UserDomainName + "\\" + Environment.UserName);
                var check = UpdateService.SelfCheckScript(script);
                UpdateActionMsg = $"模拟模式：下载与哈希已校验；{check}";
                Logger.Info($"模拟模式结果：zip={zip}, prefix='{prefix}', {check}");
                return;
            }

            UpdateActionMsg = "下载完成，哈希校验通过，正在准备替换…";
            Logger.Info($"准备应用更新（完整包）：{zip}，剥离前缀 '{prefix}'");

            UpdateService.LaunchReplaceAndRestart(zip, prefix,
                Environment.UserDomainName + "\\" + Environment.UserName);

            UpdateActionMsg = "即将退出并完成更新…";
            UpdateRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            UpdateActionMsg = "已取消下载。";
        }
        catch (Exception ex)
        {
            UpdateActionMsg = ex.Message;
            Logger.Exception("自动更新失败", ex);
        }
        finally
        {
            IncrementalUpdateService.CleanupStage();
            _updateBusy = false;
            Downloading = false;
            OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    /// <summary>
    /// 首选更新方式：下载官方安装程序 → 校验 SHA256 → 生成脚本，让它在应用退出后静默安装。
    /// 返回 true 表示已接手（调用方应立即结束本次流程）；false 表示应回退到增量/完整包。
    ///
    /// 安装位置：免安装版带 <c>/DIR=&lt;当前目录&gt;</c> 装回原处；安装版交给 Inno 记录的目录。
    /// </summary>
    private async Task<bool> TryInstallerUpdateAsync()
    {
        try
        {
            var installedLocation = "";
            var isInstalled = UpdateService.IsInstalled(out installedLocation);
            var dirOverride = isInstalled ? null : UpdateService.AppDirectory;

            Logger.Info(isInstalled
                ? $"检测到安装版（注册表记录位置：{installedLocation}），安装程序将使用该目录"
                : $"检测到免安装版，安装程序将装回当前目录：{dirOverride}");

            UpdateActionMsg = $"正在下载安装程序 {LatestVersion}…";

            var progress = new Progress<double>(p => Ui.Run(() =>
            {
                if (p >= 0)
                {
                    DownloadProgress = Math.Round(p * 100, 0);
                    UpdateActionMsg = $"正在下载安装程序 {LatestVersion}… {DownloadProgress:F0}%";
                }
                else
                {
                    UpdateActionMsg = "正在下载安装程序…";
                }
            }));

            var installer = await UpdateService.DownloadInstallerAsync(
                LatestVersion, progress, _updateCts.Token);

            // 模拟模式：只生成并自检脚本，不真的安装（不动程序文件）
            if (Environment.GetEnvironmentVariable("FORZA_SYNC_UPDATE_SIMULATE") == "1")
            {
                var script = UpdateService.WriteInstallerScript(installer, dirOverride);
                var check = UpdateService.SelfCheckScript(script);
                UpdateActionMsg = $"模拟模式（安装程序）：下载与哈希已校验；{check}";
                Logger.Info($"模拟模式（安装程序）结果：installer={installer}, dir={dirOverride ?? "(默认)"}, {check}");
                return true;
            }

            UpdateActionMsg = "安装程序已校验，即将退出并开始安装…";
            Logger.Info($"准备运行安装程序：{installer}（目录覆盖：{dirOverride ?? "(Inno 默认)"}）");

            UpdateService.LaunchInstaller(installer, dirOverride);

            UpdateActionMsg = "应用即将退出并完成安装…";
            UpdateRequested?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 不在这里直接报错：让调用方回退到增量/完整包，用户仍有别的更新途径
            Logger.Warn($"安装程序方式失败（将回退）：{ex.Message}");
            UpdateActionMsg = "安装程序方式不可用，改用其他方式…";
            return false;
        }
    }

    /// <summary>
    /// 尝试增量更新；返回 null 表示应当回退完整包。
    /// 进度会换算到 0~100 写到 <see cref="DownloadProgress"/>。
    /// </summary>
    private async Task<(string ZipPath, string Prefix)?> TryIncrementalAsync()
    {
        try
        {
            var report = new Progress<(double Percent, string Stage)>(p => Ui.Run(() =>
            {
                DownloadProgress = Math.Round(p.Percent * 100, 0);
                UpdateActionMsg = p.Stage;
            }));

            var result = await IncrementalUpdateService.TryPrepareAsync(
                LatestVersion, report, _updateCts.Token);

            if (result is null)
            {
                Logger.Info("增量更新未命中（本地已是最新 / 需要完整包），回退完整包");
                UpdateActionMsg = "增量更新不适用，改用完整包…";
                return null;
            }

            return (result.Value.ZipPath, result.Value.StripPrefix);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"增量更新异常，回退完整包：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 比较版本号（与后端 <c>updates._parse_version</c> 同规则）：
    /// 忽略 <c>v</c> 前缀与 <c>-hash</c> 后缀，按数字段逐段比较，缺失段按 0。
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        static int[] Parts(string text)
        {
            var head = (text ?? "").TrimStart('v', 'V').Split('-', 2)[0];
            var numbers = System.Text.RegularExpressions.Regex.Matches(head, @"\d+");
            if (numbers.Count == 0) return new[] { 0 };

            var result = new int[numbers.Count];
            for (var i = 0; i < numbers.Count; i++)
            {
                _ = int.TryParse(numbers[i].Value, out result[i]);
            }
            return result;
        }

        var left = Parts(a);
        var right = Parts(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }

    /// <summary>页面收到此事件后应退出应用，让替换脚本接管。</summary>
    public event Action? UpdateRequested;

    public async Task StartLoginAsync()
    {
        Error = "";
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("auth_login");
            var res = Json.Deserialize<Dictionary<string, object?>>(json) ?? new();
            Ui.Run(() =>
            {
                LoginState = "running";
                LoginMsg = res.GetValueOrDefault("message")?.ToString() ?? "正在打开浏览器…";
            });
        }
        catch (Exception ex)
        {
            Ui.Run(() => Error = ex.Message);
        }
    }

    public void PickDir() => PickDirRequested?.Invoke();
}
