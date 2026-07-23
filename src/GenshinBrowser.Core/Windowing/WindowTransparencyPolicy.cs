namespace GenshinBrowser.Windowing;

public readonly record struct WindowTransparencyDecision(double Opacity, byte Alpha, bool UseLayeredWindow);

public static class WindowTransparencyPolicy
{
    public static WindowTransparencyDecision Create(double opacity)
    {
        double clamped = double.IsFinite(opacity) ? Math.Clamp(opacity, 0.1, 1.0) : 1.0;
        bool useLayered = clamped < 1.0;
        byte alpha = useLayered
            ? (byte)Math.Clamp((int)Math.Round(clamped * byte.MaxValue), 1, byte.MaxValue - 1)
            : byte.MaxValue;
        return new WindowTransparencyDecision(clamped, alpha, useLayered);
    }
}
