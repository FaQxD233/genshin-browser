namespace GenshinBrowser.Models;

public sealed class FavoriteEntry
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTime SavedAt { get; set; }
}
