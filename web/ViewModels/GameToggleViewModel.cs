namespace ForzaGallerySync.ViewModels;

/// <summary>游戏多选 Toggle 按钮的 ViewModel（IsChecked 双向绑定 + 变更回调）。</summary>
public sealed class GameToggleViewModel : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value)) OnChanged?.Invoke(this);
        }
    }

    /// <summary>选中状态变化回调（由所属 ViewModel 设置）。</summary>
    public Action<GameToggleViewModel>? OnChanged { get; set; }
}
