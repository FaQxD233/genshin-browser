using System.Windows;

namespace GenshinBrowser.Models;

/// <summary>
/// 导入配置时一个窗口的大小 / 位置与当前的比较。大小按设置面板显示的整数比较；
/// 文件里的位置不在任何屏幕内时导入只用大小，位置不算不同。
/// </summary>
public sealed record WindowBoundsChange(Size Current, Size Imported, bool PositionDiffers, bool ImportedPositionOffScreen)
{
    public bool SizeDiffers =>
        Math.Round(Current.Width) != Math.Round(Imported.Width) ||
        Math.Round(Current.Height) != Math.Round(Imported.Height);

    public bool HasDifference => SizeDiffers || PositionDiffers;

    /// <param name="importedHasPosition">文件里记录了位置（控制台从没拖动过时没有）。</param>
    /// <param name="importedOnScreen">文件里的位置与任一显示器有交集。</param>
    public static WindowBoundsChange Create(Rect current, Rect imported, bool importedHasPosition, bool importedOnScreen) =>
        new(
            current.Size,
            imported.Size,
            PositionDiffers: importedHasPosition && importedOnScreen &&
                             (Math.Abs(current.X - imported.X) >= 1 || Math.Abs(current.Y - imported.Y) >= 1),
            ImportedPositionOffScreen: importedHasPosition && !importedOnScreen);
}

/// <summary>导入配置时主窗口与控制台的大小 / 位置比较结果；有不同时导入前询问用哪一套。</summary>
public sealed record ImportWindowBoundsDiff(WindowBoundsChange Main, WindowBoundsChange Control)
{
    public bool HasDifference => Main.HasDifference || Control.HasDifference;
}
