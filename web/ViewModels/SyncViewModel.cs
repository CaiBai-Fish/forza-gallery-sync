using System.Collections.ObjectModel;
using ForzaGallerySync.Models;
using ForzaGallerySync.Services;

namespace ForzaGallerySync.ViewModels;

/// <summary>
/// 同步页：本次运行的参数（处理哪些游戏、数量上限、是否强制重下）与实时进度。
///
/// 职责划分：本页只放「本次同步」的参数；每页数量 / 并发 / 超时 / 重试等
/// 影响全局行为的配置统一放在设置页，避免两处重复维护同一份配置。
/// </summary>
public sealed class SyncViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    private bool _force;
    private string _maxPhotos = "";
    private SyncProgressModel? _prog;
    private string _message = "";
    private string _actionError = "";
    private bool _loading = true;
    private bool _busy;
    private string _elapsedText = "";
    private string _etaText = "";
    private DateTimeOffset? _startedAt;

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
    public string GamesText => Prog is { Games.Count: > 0 }
        ? string.Join(" · ", Prog.Games.Select(Models.UseGames.Name))
        : "";
    public string ProgCountText => Prog is null ? "" : $"{Prog.Done} / {Prog.Total} 张";
    public string PercentText => $"{Percent}%";
    public string SyncedText => Prog is null ? "0" : Prog.Synced.ToString();
    public string SkippedText => Prog is null ? "0" : Prog.Skipped.ToString();
    public string FailedText => Prog is null ? "0" : Prog.Failed.ToString();
    public string ProgMessage => Prog?.Message ?? "当前没有运行中的任务";
    public string FinishedText =>
        Prog is { FinishedAt: not null } ? $"完成时间 {Format.Time(Prog.FinishedAt)}" : "";

    /// <summary>已运行时长（每秒刷新）。</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        set => SetProperty(ref _elapsedText, value);
    }

    /// <summary>预计剩余时间（依据当前速度估算）。</summary>
    public string EtaText
    {
        get => _etaText;
        set => SetProperty(ref _etaText, value);
    }

    /// <summary>是否存在失败项（用于决定是否展示失败明细区）。</summary>
    public bool HasFailures => FailedItems.Count > 0;

    /// <summary>已有上次同步结果可展示。</summary>
    public bool HasResult => Prog is { FinishedAt: not null };

    public ObservableCollection<FailedItem> FailedItems { get; } = new();

    public bool Force
    {
        get => _force;
        set => SetProperty(ref _force, value);
    }

    /// <summary>本次同步的照片数量上限（留空表示不限制）。</summary>
    public string MaxPhotos
    {
        get => _maxPhotos;
        set => SetProperty(ref _maxPhotos, value);
    }

    /// <summary>勾选情况提示（勾选默认对齐设置页的启用游戏）。</summary>
    public string EnabledGamesHint =>
        GameToggles.Count == 0
            ? "正在加载游戏列表…"
            : SelectedCount == 0
                ? "未勾选任何游戏，将同步设置页中启用的游戏。"
                : $"本次将同步勾选的 {SelectedCount} 个游戏。";

    private int SelectedCount => GameToggles.Count(t => t.IsChecked);

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
                OnPropertyChanged(nameof(GamesText));
                OnPropertyChanged(nameof(ProgCountText));
                OnPropertyChanged(nameof(PercentText));
                OnPropertyChanged(nameof(SyncedText));
                OnPropertyChanged(nameof(SkippedText));
                OnPropertyChanged(nameof(FailedText));
                OnPropertyChanged(nameof(ProgMessage));
                OnPropertyChanged(nameof(FinishedText));
                OnPropertyChanged(nameof(HasResult));

                if (value is not null)
                {
                    FailedItems.Clear();
                    foreach (var f in value.FailedItems) FailedItems.Add(f);
                    OnPropertyChanged(nameof(HasFailures));

                    // 记录本次运行的起点，用于计算已用时长与剩余时间。
                    if (value.Running && _startedAt is null)
                    {
                        _startedAt = ParseTime(value.StartedAt) ?? DateTimeOffset.Now;
                    }
                    if (!value.Running) _startedAt = null;
                }
                else
                {
                    _startedAt = null;
                }
            }
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public string ActionError
    {
        get => _actionError;
        set
        {
            if (SetProperty(ref _actionError, value)) OnPropertyChanged(nameof(HasActionError));
        }
    }

    public bool HasActionError => !string.IsNullOrEmpty(_actionError);
    public bool HasMessage => !string.IsNullOrEmpty(_message);

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
            Ui.Run(() =>
            {
                Prog = prog;
                UpdateTiming();
            });
        }
        catch
        {
            // 忽略轮询错误。
        }
    }

    /// <summary>依据已用时长与完成比例估算剩余时间。</summary>
    private void UpdateTiming()
    {
        var prog = Prog;
        if (prog is null || !prog.Running || _startedAt is null)
        {
            if (prog is { Running: false })
            {
                ElapsedText = "";
                EtaText = "";
            }
            return;
        }

        var elapsed = DateTimeOffset.Now - _startedAt.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        ElapsedText = $"已用 {FormatClock(elapsed)}";

        if (prog.Done <= 0 || prog.Total <= 0)
        {
            EtaText = "正在估算剩余时间…";
            return;
        }

        var remaining = TimeSpan.FromSeconds(
            elapsed.TotalSeconds / prog.Done * Math.Max(0, prog.Total - prog.Done));
        EtaText = remaining.TotalSeconds < 1 ? "即将完成" : $"预计剩余 {FormatClock(remaining)}";
    }

    private static string FormatClock(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";

    private static DateTimeOffset? ParseTime(string? iso) =>
        DateTimeOffset.TryParse(iso, out var dto) ? dto : null;

    /// <summary>加载游戏列表（勾选状态默认与设置页的启用游戏一致）。</summary>
    public async Task LoadConfigAsync()
    {
        Loading = true;
        try
        {
            var json = await PyBridge.Instance.CallJsonAsync("get_config");
            var cfg = Json.Deserialize<ConfigModel>(json) ?? new ConfigModel();
            Ui.Run(() =>
            {
                GameToggles.Clear();
                foreach (var g in cfg.SupportedGames)
                {
                    var toggle = new GameToggleViewModel
                    {
                        Id = g.Id,
                        Name = g.Name,
                        IsChecked = cfg.EnabledGames.Contains(g.Id),
                    };
                    toggle.OnChanged = _ => OnPropertyChanged(nameof(EnabledGamesHint));
                    GameToggles.Add(toggle);
                }
                OnPropertyChanged(nameof(EnabledGamesHint));
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
        Message = "";
        Busy = true;
        try
        {
            var args = new Dictionary<string, object?>
            {
                // 未勾选时交给后端按设置页的启用游戏处理。
                ["games"] = SelectedGames.Count > 0 ? SelectedGames.ToList() : null,
                ["force"] = Force,
            };
            if (int.TryParse(MaxPhotos, out var mp) && mp > 0) args["max_photos"] = mp;

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
