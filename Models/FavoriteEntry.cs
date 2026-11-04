namespace GenshinBrowser.Models;

public sealed class FavoriteEntry : IUrlEntry
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTime SavedAt { get; set; }

    DateTime IUrlEntry.Timestamp
    {
        get => SavedAt;
        set => SavedAt = value;
    }
}
