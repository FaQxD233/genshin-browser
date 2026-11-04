namespace GenshinBrowser.Models;

/// <summary>
/// 五组内置热键。声明顺序即冲突消解的优先级：前位保留、后位让位。
/// 成员名也是文案键的一部分（Toast.Record{槽位}、Toast.{槽位}Set），改名要同步中英文案。
/// </summary>
public enum HotkeySlot
{
    ToggleMode,
    Playback,
    Hide,
    SeekBackward,
    SeekForward,
}

public static class HotkeySlots
{
    /// <summary>全部槽位，按优先级排列。</summary>
    public static readonly IReadOnlyList<HotkeySlot> All = Enum.GetValues<HotkeySlot>();
}
