using System.Collections.ObjectModel;
using ForzaGallerySync.Models;
using ForzaGallerySync.Services;

namespace ForzaGallerySync.ViewModels;

/// <summary>同步：选择游戏、参数、启动/停止、实时进度。</summary>
public sealed class SyncViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    private ConfigModel _config = new();
    private bool _force;
    private string _maxPhotos = "";
    private string _pageSize = "";
    private SyncProgressModel? _prog;
    private string _message = "";
    private string _actionError = "";
    private bool _loading = true;
    private bool _busy;

    public bool Loading
    {
        get => _loading;
        set => SetProperty(ref _loading, value);
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanStart));
        }
    }

    public bool CanStart => !_busy && !Running;

    // ---- 进度展示（顶层属性，避免 x:Bind 嵌套 null 崩溃） ----
    public bool CancelRequested => Prog?.CancelRequested ?? false;
    public string GameName => Models.UseGames.Name(Prog?.Game);
    public string ProgCountText => Prog is null ? "" : $"{Prog.Done} / {Prog.Total}";
    public string PercentText => $"{Percent}%";
    public string SyncedText => Prog is null ? "" : $"+{Prog.Synced}";
    public string SkippedText => Prog is null ? "" : $"⏭ {Prog.Skipped}";
    public string FailedText => Prog is null ? "" : $"✕ {Prog.Failed}";
    public string ProgMessage => Prog?.Message ?? "当前没有运行中的任务";
    public string FinishedText =>
        Prog is { FinishedAt: not null } ? $"完成时间 {Format.Time(Prog.FinishedAt)}" : "";

    public ObservableCollection<FailedItem> FailedItems { get; } = new();

    public bool Force
    {
        get => _force;
        set => SetProperty(ref _force, value);
    }

    public string MaxPhotos
    {
        get => _maxPhotos;
        set => SetProperty(ref _maxPhotos, value);
    }

    public string PageSize
    {
        get => _pageSize;
        set => SetProperty(ref _pageSize, value);
    }

    public SyncProgressModel? Prog
    {
        get => _prog;
        set
        {
            if (SetProperty(ref _prog, value))
            {
                OnPropertyChanged(nameof(Running));
                OnPropertyChanged(nameof(Percent));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CancelRequested));
                OnPropertyChanged(nameof(GameName));
                OnPropertyChanged(nameof(ProgCountText));
                OnPropertyChanged(nameof(PercentText));
                OnPropertyChanged(nameof(SyncedText));
                OnPropertyChanged(nameof(SkippedText));
                OnPropertyChanged(nameof(FailedText));
                OnPropertyChanged(nameof(ProgMessage));
                OnPropertyChanged(nameof(FinishedText));
                if (value is not null)
                {
                    FailedItems.Clear();
                    foreach (var f in value.FailedItems) FailedItems.Add(f);
                }
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string ActionError
    {
        get => _actionError;
        set => SetProperty(ref _actionError, value);
    }

    public bool Running => Prog?.Running ?? false;

    public int Percent =>
        Prog is { Total: > 0 } ? (int)Math.Round(Prog.Done / (double)Prog.Total * 100) : 0;

    /// <summary>可多选的游戏 Toggle 集合（绑定页面 ToggleButton）。</summary>
    public ObservableCollection<GameToggleViewModel> GameToggles { get; } = new();

    private List<string> SelectedGames =>
        GameToggles.Where(t => t.IsChecked).Select(t => t.Id).ToList();

    public void Start() => _ = PollLoopAsync();

    public void Stop() => _cts.Cancel();

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await PollProgressAsync();
            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollProgressAsync()
    {
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("sync_progress");
            var prog = Json.Deserialize<SyncProgressModel>(json);
            Ui.Run(() => Prog = prog);
        }
        catch
        {
            // 忽略轮询错误。
        }
    }

    public async Task LoadConfigAsync()
    {
        Loading = true;
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("get_config");
            var cfg = Json.Deserialize<ConfigModel>(json) ?? new ConfigModel();
            Ui.Run(() =>
            {
                _config = cfg;
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
                ActionError = "";
            });
        }
        catch (Exception ex)
        {
            Ui.Run(() => ActionError = ex.Message);
        }
        finally
        {
            Ui.Run(() => Loading = false);
        }
    }

    public async Task StartAsync()
    {
        ActionError = "";
        Busy = true;
        try
        {
            var args = new Dictionary<string, object?>
            {
                ["games"] = SelectedGames.Count > 0 ? SelectedGames.ToList() : null,
                ["force"] = Force,
            };
            if (int.TryParse(MaxPhotos, out var mp) && mp > 0) args["max_photos"] = mp;
            if (int.TryParse(PageSize, out var ps) && ps > 0) args["page_size"] = ps;

            var json = await PyBridge.Instance.CallJsonAsync("sync_start", Json.Serialize(args));
            var res = Json.Deserialize<Dictionary<string, object?>>(json) ?? new();
            Message = res.GetValueOrDefault("message")?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            ActionError = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task StopAsync()
    {
        ActionError = "";
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("sync_stop");
            var res = Json.Deserialize<Dictionary<string, object?>>(json) ?? new();
            Message = res.GetValueOrDefault("message")?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            ActionError = ex.Message;
        }
    }
}
