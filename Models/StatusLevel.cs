namespace GenshinBrowser.Models;

/// <summary>
/// 状态消息级别。控制窗按级别决定是否 Toast，避免用中文文案做逻辑判断。
/// </summary>
public enum StatusLevel
{
    /// <summary>普通信息，通常仅同步到状态，不强制 Toast。</summary>
    Info = 0,

    /// <summary>成功或已完成类反馈，可 Toast。</summary>
    Success = 1,

    /// <summary>警告，可 Toast。</summary>
    Warning = 2,

    /// <summary>失败或错误，应 Toast。</summary>
    Error = 3,
}
