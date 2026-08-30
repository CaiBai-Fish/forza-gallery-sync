using System.Collections.ObjectModel;
using ForzaGallerySync.Models;
using ForzaGallerySync.Services;

namespace ForzaGallerySync.ViewModels;

/// <summary>月份筛选项。</summary>
public sealed class MonthOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    public override string ToString() => Label;
}

/// <summary>分页按钮项。</summary>
public sealed class PageButtonViewModel
{
    public int Page { get; set; }
    public string Label => (Page + 1).ToString();
    public bool IsCurrent { get; set; }
}

/// <summary>照片库：搜索 / 筛选 / 分页网格 + 详情。</summary>
public sealed class GalleryViewModel : ObservableObject
{
    public const int PageSize = 48;

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _searchDebounce;

    private bool _loading = true;
    private string _error = "";
    private int _total;
    private int _page;
    private string _game = "";
    private string _month = "";
    private string _query = "";
    private string _downloadDir = "";

    public GalleryViewModel()
    {
        // 照片集合变化时更新空状态显示。
        Photos.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmpty));
    }

    public bool Loading
    {
        get => _loading;
        set
        {
            if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    public string Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>是否显示空状态：非加载中、无错误、且没有照片。</summary>
    public bool ShowEmpty => !Loading && !HasError && Photos.Count == 0;

    public int Total
    {
        get => _total;
        set
        {
            if (SetProperty(ref _total, value))
            {
                OnPropertyChanged(nameof(TotalText));
                OnPropertyChanged(nameof(HasNext));
            }
        }
    }

    public int Page
    {
        get => _page;
        set
        {
            if (SetProperty(ref _page, value))
            {
                OnPropertyChanged(nameof(HasPrev));
                OnPropertyChanged(nameof(HasNext));
            }
        }
    }

    public string Game
    {
        get => _game;
        set
        {
            if (SetProperty(ref _game, value)) { Page = 0; _ = ReloadAsync(); }
        }
    }

    public string Month
    {
        get => _month;
        set
        {
            if (SetProperty(ref _month, value)) { Page = 0; _ = ReloadAsync(); }
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value)) { DebounceSearch(); }
        }
    }

    public string DownloadDir
    {
        get => _downloadDir;
        set
        {
            if (SetProperty(ref _downloadDir, value)) OnPropertyChanged(nameof(HasDownloadDir));
        }
    }

    public bool HasDownloadDir => !string.IsNullOrEmpty(_downloadDir);
    public string TotalText => $"共 {_total} 张";
    public bool HasPrev => _page > 0;
    public bool HasNext => _page < Pages - 1;

    public ObservableCollection<PhotoItemViewModel> Photos { get; } = new();
    public ObservableCollection<GameInfo> GamesList { get; } = new();
    public ObservableCollection<MonthOption> Months { get; } = new();
    public ObservableCollection<PageButtonViewModel> PageButtons { get; } = new();

    /// <summary>请求打开照片详情对话框。</summary>
    public event Action<PhotoItemViewModel>? OpenDetailRequested;

    /// <summary>请求在资源管理器中打开路径（目录或文件）。</summary>
    public event Action<string, bool>? OpenPathRequested;

    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    public void Start()
    {
        _ = LoadGamesAsync();
        _ = ReloadAsync();
    }

    public void Stop()
    {
        _cts.Cancel();
        _searchDebounce?.Cancel();
    }

    /// <summary>加载支持的游戏列表与下载目录（用于筛选与打开目录）。</summary>
    public async Task LoadGamesAsync()
    {
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("get_config");
            var cfg = Json.Deserialize<ConfigModel>(json);
            if (cfg is null) return;
            Ui.Run(() =>
            {
                GamesList.Clear();
                GamesList.Add(new GameInfo { Id = "", Name = "全部" });
                foreach (var g in cfg.SupportedGames) GamesList.Add(g);
                DownloadDir = cfg.DownloadDir;
            });
        }
        catch
        {
            // 游戏列表加载失败不影响照片浏览。
        }
    }

    private void DebounceSearch()
    {
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        var token = _searchDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                {
                    Ui.Run(() => { Page = 0; });
                    await LoadPhotosAsync();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task ReloadAsync()
    {
        _searchDebounce?.Cancel();
        await LoadPhotosAsync();
    }

    public async Task GoToPageAsync(int page)
    {
        if (page < 0 || page >= Pages) return;
        Page = page;
        await LoadPhotosAsync();
    }

    private async Task LoadPhotosAsync()
    {
        if (Loading && Photos.Count > 0) return;
        Loading = true;
        Error = "";
        try
        {
            var args = new
            {
                game = string.IsNullOrEmpty(Game) ? null : Game,
                month = string.IsNullOrEmpty(Month) ? null : Month,
                q = string.IsNullOrEmpty(Query) ? null : Query,
                limit = PageSize,
                offset = Page * PageSize,
            };
            var json = await PyBridge.Instance.CallJsonAsync("list_photos", Json.Serialize(args));
            var page = Json.Deserialize<PhotoPage>(json) ?? new PhotoPage();
            Ui.Run(() =>
            {
                Photos.Clear();
                foreach (var p in page.Items) Photos.Add(ToItem(p));
                Total = page.Total;
                CollectMonths(page.Items);
                UpdatePageButtons();
                foreach (var item in Photos)
                {
                    _ = ThumbnailLoader.LoadAsync(item);
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

    private void CollectMonths(List<PhotoInfo> items)
    {
        // 累积所有已见过的月份（跨分页保留），排除占位的"全部"项。
        var set = new HashSet<string>(Months.Where(m => !string.IsNullOrEmpty(m.Value)).Select(m => m.Value));
        foreach (var p in items)
        {
            if (!string.IsNullOrEmpty(p.Month)) set.Add(p.Month);
        }
        Months.Clear();
        Months.Add(new MonthOption { Value = "", Label = "全部" });
        foreach (var m in set.OrderByDescending(x => x))
        {
            Months.Add(new MonthOption
            {
                Value = m,
                Label = $"{m[..4]} 年 {int.Parse(m[5..7])} 月",
            });
        }
    }

    private void UpdatePageButtons()
    {
        PageButtons.Clear();
        var p = Page;
        var last = Pages - 1;
        for (var i = Math.Max(0, p - 2); i <= Math.Min(last, p + 2); i++)
        {
            PageButtons.Add(new PageButtonViewModel { Page = i, IsCurrent = i == p });
        }
    }

    public async Task OpenDetailAsync(PhotoItemViewModel item)
    {
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync(
                "photo_meta",
                Json.Serialize(new { photo_id = item.PhotoId }));
            var meta = Json.Deserialize<PhotoInfo>(json);
            if (meta is not null)
            {
                item.Url = meta.Url;
                item.LocalPath = meta.LocalPath;
                item.Description = meta.Description;
                item.Title = meta.Title;
            }
        }
        catch
        {
            // meta 加载失败不影响详情（图片仍可显示）。
        }
        OpenDetailRequested?.Invoke(item);
    }

    public void OpenPath(string path, bool isFile)
    {
        if (string.IsNullOrEmpty(path)) return;
        OpenPathRequested?.Invoke(path, isFile);
    }
}
