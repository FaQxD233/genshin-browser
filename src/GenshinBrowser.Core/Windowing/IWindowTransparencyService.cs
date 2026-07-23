namespace GenshinBrowser.Windowing;

public interface IWindowTransparencyService
{
    double Opacity { get; }

    bool IsLayered { get; }

    void Apply(double opacity);
}
