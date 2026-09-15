using System.Collections.ObjectModel;
using ForzaGallerySync.Models;
using ForzaGallerySync.Services;

namespace ForzaGallerySync.ViewModels;

/// <summary>总览统计卡片。</summary>
public sealed class StatCardViewModel
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Segoe Fluent Icons 字形（换成图标字体，避免 emoji 跨主题渲染不一致）。</summary>
    public string Glyph { get; set; } = "";
    /// <summary>次要说明文字。</summary>
    public string Caption { get; set; } = "";
    /// <summary>图标前景色资源键（AppAccentBrush / AppSuccessBrush / …）。</summary>
    public string AccentKey { get; set; } = "AppAccentBrush";
}

/// <summary>按游戏统计条。</summary>
public sealed class GameBarViewModel
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public int Count { get; set; }
    public double Percent { get; set; }
    public string CountText => $"{Count:N0} 张";
}

/// <summary>最近同步记录。</summary>
public sealed class SyncRowViewModel
{
    public string GameName { get; set; } = "";
    public string LastSyncAt { get; set; } = "";
    public string Sub { get; set; } = "";
}

/// <summary>
/// 总览页：只读统计与快捷入口。
///
/// 职责划分：Token / 下载目录 / 并发等「可配置项」统一在设置页展示，
/// 本页只呈现照片分布、最近同步与最新照片，避免与其它页面重复。
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    /// <summary>总览页展示的最新照片数量。</summary>
    private const int RecentPhotoCount = 8;

    private readonly CancellationTokenSource _cts = new();

    private bool _loading = true;
    private string _error = "";
    private string _heroTotal = "0";
    private string _heroCaption = "尚未同步任何照片";
    private string _lastSyncText = "—";
    private bool _tokenOk;
    private string _tokenSummary = "未配置";

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

    /// <summary>主视觉大数字：照片总数。</summary>
    public string HeroTotal
    {
        get => _heroTotal;
        set => SetProperty(ref _heroTotal, value);
    }

    /// <summary>主视觉副标题（覆盖多少个游戏 / 多少个月）。</summary>
    public string HeroCaption
    {
        get => _heroCaption;
        set => SetProperty(ref _heroCaption, value);
    }

    /// <summary>最近一次同步时间。</summary>
    public string LastSyncText
    {
        get => _lastSyncText;
        set => SetProperty(ref _lastSyncText, value);
    }

    /// <summary>Token 是否有效。</summary>
    public bool TokenOk
    {
        get => _tokenOk;
        set
        {
            if (SetProperty(ref _tokenOk, value)) OnPropertyChanged(nameof(TokenNeedsAttention));
        }
    }

    /// <summary>Token 摘要（如「未配置」/「已过期」）。</summary>
    public string TokenSummary
    {
        get => _tokenSummary;
        set => SetProperty(ref _tokenSummary, value);
    }

    /// <summary>需要提醒用户前往设置页处理 Token。</summary>
    public bool TokenNeedsAttention => !_tokenOk;

    public ObservableCollection<StatCardViewModel> Stats { get; } = new();
    public ObservableCollection<GameBarViewModel> GameBars { get; } = new();
    public ObservableCollection<SyncRowViewModel> SyncRows { get; } = new();
    public ObservableCollection<PhotoItemViewModel> RecentPhotos { get; } = new();

    public bool HasGameBars => GameBars.Count > 0;
    public bool HasSyncRows => SyncRows.Count > 0;
    public bool HasRecentPhotos => RecentPhotos.Count > 0;

    /// <summary>导航请求（总览页按钮跳转到其它页面）。</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>请求预览某张最新照片。</summary>
    public event Action<PhotoItemViewModel>? PhotoOpenRequested;

    public void Start() => _ = PollLoopAsync();

    public void Stop() => _cts.Cancel();

    private async Task PollLoopAsync()
    {
        // 首屏先加载一次，随后低频轮询（统计变化不频繁）。
        await LoadAsync();
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(8000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await LoadAsync();
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
                Error = "";
                Loading = false;
                UpdateStats(status);
            });
            await LoadRecentPhotosAsync();
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
        var total = d.Photos.Total;
        var gamesWithPhotos = d.Photos.ByGame.Count;
        var months = d.Photos.ByMonth.Select(m => m.Month).Distinct().Count();

        // 最近同步时间：在同步状态里找最新一条。
        var latest = d.SyncState
            .Where(s => !string.IsNullOrEmpty(s.LastSyncAt))
            .OrderByDescending(s => s.LastSyncAt, StringComparer.Ordinal)
            .FirstOrDefault();
        LastSyncText = latest is null ? "—" : Format.Time(latest.LastSyncAt);

        HeroTotal = total.ToString("N0");
        HeroCaption = total == 0
            ? "尚未同步任何照片，先登录并执行一次同步"
            : $"来自 {gamesWithPhotos} 个游戏 · 覆盖 {months} 个月份";

        TokenOk = d.Token.HasToken && !d.Token.Expired;
        TokenSummary = !d.Token.HasToken
            ? "未配置 Token"
            : (d.Token.Expired ? "Token 已过期" : "Token 有效");

        var pendingGames = d.Config.EnabledGames.Count - gamesWithPhotos;

        Stats.Clear();
        Stats.Add(new StatCardViewModel
        {
            Label = "照片总数",
            Value = total.ToString("N0"),
            Glyph = "\uEB9F",
            Caption = months > 0 ? $"覆盖 {months} 个月" : "暂无数据",
            AccentKey = "AppAccentBrush",
        });
        Stats.Add(new StatCardViewModel
        {
            Label = "已同步游戏",
            Value = $"{gamesWithPhotos} / {d.Config.EnabledGames.Count}",
            Glyph = "\uE7FC",
            Caption = pendingGames > 0 ? $"{pendingGames} 个启用游戏还没有照片" : "全部启用游戏均有照片",
            AccentKey = "AppBrandBrush",
        });
        Stats.Add(new StatCardViewModel
        {
            Label = "最近同步",
            Value = LastSyncText,
            Glyph = "\uE823",
            Caption = latest is null ? "尚未执行过同步" : UseGames.Name(latest.Game),
            AccentKey = "AppWarnBrush",
        });
        Stats.Add(new StatCardViewModel
        {
            Label = "账号状态",
            Value = TokenSummary,
            Glyph = TokenOk ? "\uE72E" : "\uE7BA",
            Caption = TokenOk ? "Token 可正常调用接口" : "前往设置页完成登录",
            AccentKey = TokenOk ? "AppSuccessBrush" : "AppDangerBrush",
        });

        GameBars.Clear();
        var byGame = d.Photos.ByGame;
        var max = byGame.Count > 0 ? byGame.Max(x => x.Count) : 1;
        foreach (var g in byGame.OrderByDescending(x => x.Count))
        {
            GameBars.Add(new GameBarViewModel
            {
                Name = UseGames.Name(g.Game),
                Code = g.Game,
                Count = g.Count,
                // 最小 4% 保证极少量数据也能看到进度条。
                Percent = Math.Max(4, (double)g.Count / max * 100),
            });
        }
        OnPropertyChanged(nameof(HasGameBars));

        SyncRows.Clear();
        foreach (var s in d.SyncState.OrderByDescending(s => s.LastSyncAt ?? "", StringComparer.Ordinal))
        {
            SyncRows.Add(new SyncRowViewModel
            {
                GameName = UseGames.Name(s.Game),
                LastSyncAt = Format.Time(s.LastSyncAt),
                Sub = $"拉取 {s.TotalRecords} 条 · 已同步 {s.SyncedRecords} 条",
            });
        }
        OnPropertyChanged(nameof(HasSyncRows));

        OnPropertyChanged(nameof(TokenNeedsAttention));
    }

    /// <summary>加载最新的若干张照片（缩略图），点击可查看大图。</summary>
    private async Task LoadRecentPhotosAsync()
    {
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync(
                "list_photos",
                Json.Serialize(new { limit = RecentPhotoCount, offset = 0 }));
            var page = Json.Deserialize<PhotoPage>(json) ?? new PhotoPage();

            Ui.Run(() =>
            {
                RecentPhotos.Clear();
                foreach (var p in page.Items) RecentPhotos.Add(ToItem(p));
                OnPropertyChanged(nameof(HasRecentPhotos));
            });

            foreach (var item in RecentPhotos.ToList())
            {
                await ThumbnailLoader.LoadAsync(item);
            }
        }
        catch
        {
            // 最新照片加载失败不影响统计展示。
        }
    }

    private static PhotoItemViewModel ToItem(PhotoInfo p) => new()
    {
        PhotoId = p.PhotoId,
        Game = p.Game,
        Title = p.Title,
        Description = p.Description,
        SubmissionTimeUtc = p.SubmissionTimeUtc,
        Month = p.Month,
        LocalPath = p.LocalPath,
        DownloadedAt = p.DownloadedAt,
        Url = p.Url,
    };

    public void Navigate(string page) => NavigateRequested?.Invoke(page);

    public void OpenPhoto(PhotoItemViewModel item) => PhotoOpenRequested?.Invoke(item);
}
