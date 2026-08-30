using System.Collections.ObjectModel;
using ForzaGallerySync.Models;
using ForzaGallerySync.Services;

namespace ForzaGallerySync.ViewModels;

/// <summary>仪表盘统计卡片。</summary>
public sealed class StatCardViewModel
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string Icon { get; set; } = "";
    public bool Small { get; set; }
}

/// <summary>按游戏统计条。</summary>
public sealed class GameBarViewModel
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public double Percent { get; set; }
}

/// <summary>最近同步记录。</summary>
public sealed class SyncRowViewModel
{
    public string GameName { get; set; } = "";
    public string LastSyncAt { get; set; } = "";
    public string Sub { get; set; } = "";
}

/// <summary>仪表盘：统计卡片、按游戏/月份分布、Token 状态、最近同步、快速操作。</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    private bool _loading = true;
    private string _error = "";
    private StatusModel? _data;

    // ---- Token 状态 ----
    private string _tokenStatusText = "未配置";
    private bool _tokenStatusGood;
    private string _accessTokenText = "";
    private string _refreshTokenText = "";
    private string _expiresInText = "";

    // ---- 配置概览 ----
    private string _downloadDir = "";
    private string _databasePath = "";
    private string _concurrencyText = "";

    public bool Loading
    {
        get => _loading;
        set => SetProperty(ref _loading, value);
    }

    public string Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public StatusModel? Data
    {
        get => _data;
        set => SetProperty(ref _data, value);
    }

    public string TokenStatusText { get => _tokenStatusText; set => SetProperty(ref _tokenStatusText, value); }
    public bool TokenStatusGood { get => _tokenStatusGood; set => SetProperty(ref _tokenStatusGood, value); }
    public string AccessTokenText { get => _accessTokenText; set => SetProperty(ref _accessTokenText, value); }
    public string RefreshTokenText { get => _refreshTokenText; set => SetProperty(ref _refreshTokenText, value); }
    public string ExpiresInText { get => _expiresInText; set => SetProperty(ref _expiresInText, value); }
    public string DownloadDir { get => _downloadDir; set => SetProperty(ref _downloadDir, value); }
    public string DatabasePath { get => _databasePath; set => SetProperty(ref _databasePath, value); }
    public string ConcurrencyText { get => _concurrencyText; set => SetProperty(ref _concurrencyText, value); }

    public ObservableCollection<StatCardViewModel> Stats { get; } = new();
    public ObservableCollection<GameBarViewModel> GameBars { get; } = new();
    public ObservableCollection<SyncRowViewModel> SyncRows { get; } = new();

    /// <summary>导航请求（仪表盘按钮跳转到其它页面）。</summary>
    public event Action<string>? NavigateRequested;

    public void Start() => _ = PollLoopAsync();

    public void Stop() => _cts.Cancel();

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await LoadAsync();
            try
            {
                await Task.Delay(8000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("get_status");
            var status = Json.Deserialize<StatusModel>(json) ?? new StatusModel();
            Ui.Run(() =>
            {
                Data = status;
                Error = "";
                Loading = false;
                UpdateStats(status);
            });
        }
        catch (Exception ex)
        {
            Ui.Run(() =>
            {
                Error = ex.Message;
                Loading = false;
            });
        }
    }

    private void UpdateStats(StatusModel d)
    {
        Stats.Clear();
        var total = d.Photos.Total;
        var maxMonth = d.Photos.ByMonth.FirstOrDefault();
        string lastSync = maxMonth is null
            ? "尚未同步"
            : Format.Time(d.SyncState.FirstOrDefault(s => s.Game == maxMonth.Game)?.LastSyncAt);
        bool tokenOk = d.Token.HasToken;

        Stats.Add(new StatCardViewModel
        {
            Label = "照片总数",
            Value = total.ToString("N0"),
            Icon = "🖼️",
        });
        Stats.Add(new StatCardViewModel
        {
            Label = "启用游戏",
            Value = d.Config.EnabledGames.Count.ToString(),
            Icon = "🎮",
        });
        Stats.Add(new StatCardViewModel
        {
            Label = "最近同步",
            Value = lastSync,
            Icon = "🕐",
            Small = true,
        });
        Stats.Add(new StatCardViewModel
        {
            Label = "Token 状态",
            Value = !tokenOk ? "未配置" : (d.Token.Expired ? "已过期" : "有效"),
            Icon = "🔑",
        });

        GameBars.Clear();
        var byGame = d.Photos.ByGame;
        var max = byGame.Count > 0 ? byGame.Max(x => x.Count) : 1;
        foreach (var g in byGame)
        {
            GameBars.Add(new GameBarViewModel
            {
                Name = UseGames.Name(g.Game),
                Count = g.Count,
                Percent = Math.Max(6, (double)g.Count / max * 100),
            });
        }

        SyncRows.Clear();
        foreach (var s in d.SyncState)
        {
            SyncRows.Add(new SyncRowViewModel
            {
                GameName = UseGames.Name(s.Game),
                LastSyncAt = Format.Time(s.LastSyncAt),
                Sub = $"拉取 {s.TotalRecords} · 已同步 {s.SyncedRecords}",
            });
        }

        // Token 状态卡
        TokenStatusText = !d.Token.HasToken
            ? "未配置"
            : (d.Token.Expired ? "已过期" : "有效");
        TokenStatusGood = d.Token.HasToken && !d.Token.Expired;
        AccessTokenText = "access: " + d.Token.MaskedToken;
        RefreshTokenText = "刷新 Token: " + (d.Token.HasRefreshToken ? "已配置" : "无");
        ExpiresInText = "剩余 " + Format.Duration(d.Token.ExpiresIn);

        // 配置概览
        DownloadDir = d.Config.DownloadDir;
        DatabasePath = d.Config.DatabasePath;
        ConcurrencyText = $"{d.Config.Workers} 线程 · {d.Config.Pagination}/{d.Config.PageSize} 每页";
    }

    public void Navigate(string page) => NavigateRequested?.Invoke(page);
}
