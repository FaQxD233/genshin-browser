namespace GenshinBrowser.Models;

public sealed class AppSettings
{
    public string LastUrl { get; set; } = string.Empty;

    public WindowMode WindowMode { get; set; } = WindowMode.Fixed;

    public double WindowLeft { get; set; } = 100;

    public double WindowTop { get; set; } = 100;

    public double WindowWidth { get; set; } = 648;

    public double WindowHeight { get; set; } = 400;

    public double ControlWindowLeft { get; set; } = -1;

    public double ControlWindowTop { get; set; } = -1;

    public double ControlWindowWidth { get; set; } = 640;

    public double ControlWindowHeight { get; set; } = 853;
}
