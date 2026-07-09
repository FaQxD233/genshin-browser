using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace GenshinBrowser.Services;

/// <summary>
/// 为 <see cref="TextBox"/> 提供占位符（水印）文本的附加属性。
/// 通过 <see cref="AdornerLayer"/> 渲染，文本非空或获得焦点时自动隐藏，
/// 取代手工维护 GotFocus/LostFocus 的脆弱方案。
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder),
        new PropertyMetadata(string.Empty, OnPlaceholderChanged));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.RegisterAttached(
        "Foreground", typeof(Brush), typeof(Placeholder),
        new PropertyMetadata(null, OnForegroundChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    public static Brush GetForeground(DependencyObject d) => (Brush)d.GetValue(ForegroundProperty);

    public static void SetForeground(DependencyObject d, Brush value) => d.SetValue(ForegroundProperty, value);

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb)
        {
            return;
        }

        Hook(tb);
        Refresh(tb);
    }

    private static void OnForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb)
        {
            return;
        }

        Hook(tb);
        Refresh(tb);
    }

    private static void Hook(TextBox tb)
    {
        tb.Loaded -= OnLoaded;
        tb.Loaded += OnLoaded;
        tb.GotFocus -= OnFocusChanged;
        tb.GotFocus += OnFocusChanged;
        tb.LostFocus -= OnFocusChanged;
        tb.LostFocus += OnFocusChanged;
        tb.TextChanged -= OnTextChanged;
        tb.TextChanged += OnTextChanged;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Refresh((TextBox)sender);

    private static void OnFocusChanged(object sender, RoutedEventArgs e) => Refresh((TextBox)sender);

    private static void OnTextChanged(object sender, TextChangedEventArgs e) => Refresh((TextBox)sender);

    private static void Refresh(TextBox tb)
    {
        var layer = AdornerLayer.GetAdornerLayer(tb);
        if (layer is null)
        {
            // 控件尚未加载完成，Loaded 会再次触发刷新
            return;
        }

        var existing = layer.GetAdorners(tb)?.OfType<PlaceholderAdorner>().FirstOrDefault();
        var text = GetText(tb) ?? string.Empty;
        var shouldShow = !tb.IsFocused && string.IsNullOrEmpty(tb.Text) && !string.IsNullOrEmpty(text);

        if (shouldShow)
        {
            if (existing is null)
            {
                layer.Add(new PlaceholderAdorner(tb));
            }
            else
            {
                existing.InvalidateVisual();
            }
        }
        else if (existing is not null)
        {
            layer.Remove(existing);
        }
    }

    private sealed class PlaceholderAdorner : Adorner
    {
        private readonly TextBox _tb;

        public PlaceholderAdorner(TextBox tb) : base(tb)
        {
            _tb = tb;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var text = GetText(_tb);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var brush = GetForeground(_tb) ?? TextTertiaryBrush;
            var padding = _tb.Padding;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(_tb.FontFamily, _tb.FontStyle, _tb.FontWeight, _tb.FontStretch),
                _tb.FontSize,
                brush,
                dpi);

            // 与 TextBox 内文本对齐：左上各加 1px 偏移以贴近原生水印观感
            dc.DrawText(formatted, new Point(padding.Left + 1, padding.Top + 1));
        }
    }

    private static readonly Brush TextTertiaryBrush = CreateFrozen(Color.FromRgb(0x6E, 0x76, 0x81));

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
