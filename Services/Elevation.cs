using System.Windows;
using System.Windows.Media.Effects;

namespace GenshinBrowser.Services;

/// <summary>
/// 按钮投影（附加属性）：ModernButton 模板在底色下面垫一层同形状的投影层，Effect 取自这里，默认 null 不画。
/// 样式里按状态设置（分段选中块、键帽），不必为加投影复制一份按钮模板。
/// </summary>
public static class Elevation
{
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.RegisterAttached(
        "Shadow", typeof(Effect), typeof(Elevation), new PropertyMetadata(null));

    public static Effect? GetShadow(DependencyObject d) => (Effect?)d.GetValue(ShadowProperty);

    public static void SetShadow(DependencyObject d, Effect? value) => d.SetValue(ShadowProperty, value);
}
