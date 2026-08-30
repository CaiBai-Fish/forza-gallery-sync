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
        set => SetProperty(ref _error, value);
    }

    public string OkMsg
    {
        get => _okMsg;
        set => SetProperty(ref _okMsg, value);
    }

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
        set => SetProperty(ref _config, value);
    }

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
        set => SetProperty(ref _checkingUpdate, value);
    }

    public string UpdateText
    {
        get => _updateText;
        set => SetProperty(ref _updateText, value);
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        set => SetProperty(ref _hasUpdate, value);
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
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("app_version");
            var res = Json.Deserialize<Dictionary<string, object?>>(json);
            var v = res?.GetValueOrDefault("version")?.ToString() ?? "";
            Ui.Run(() => CurrentVersion = v);
        }
        catch
        {
            // 版本获取失败忽略。
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
            Ui.Run(() =>
            {
                CurrentVersion = info.Current;
                LatestVersion = info.Latest;
                UpdateUrl = info.Url;
                HasUpdate = info.HasUpdate;
                UpdateText = BuildUpdateText(info);
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
