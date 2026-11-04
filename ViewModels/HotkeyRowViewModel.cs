using GenshinBrowser.Models;

namespace GenshinBrowser.ViewModels;

/// <summary>按键绑定面板的一行：键帽文字（录制中显示已按住的修饰键加省略号）、录制中高亮、点击开始录制。</summary>
public sealed class HotkeyRowViewModel : ViewModelBase
{
    private readonly ControlWindowViewModel _owner;

    internal HotkeyRowViewModel(ControlWindowViewModel owner, HotkeySlot slot)
    {
        _owner = owner;
        Slot = slot;
        RecordCommand = new RelayCommand(() => owner.StartRecording(slot));
    }

    public HotkeySlot Slot { get; }

    public RelayCommand RecordCommand { get; }

    /// <summary>正在录这一组（键帽经 Tag 高亮）。</summary>
    public bool IsRecording => _owner.RecordingSlot == Slot;

    public string KeyText => _owner.GetHotkeyText(Slot);

    /// <summary>录制状态或热键变化后刷新绑定。</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(KeyText));
    }
}
