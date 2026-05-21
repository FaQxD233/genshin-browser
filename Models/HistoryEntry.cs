namespace GenshinBrowser.Models;

public sealed class HistoryEntry
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTime VisitedAt { get; set; }
}
