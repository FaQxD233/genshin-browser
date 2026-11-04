namespace GenshinBrowser.Models;

public sealed class HistoryEntry : IUrlEntry
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTime VisitedAt { get; set; }

    DateTime IUrlEntry.Timestamp
    {
        get => VisitedAt;
        set => VisitedAt = value;
    }
}
