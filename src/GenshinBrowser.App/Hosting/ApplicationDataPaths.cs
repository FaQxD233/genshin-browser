using System.IO;

namespace GenshinBrowser.Hosting;

internal sealed class ApplicationDataPaths
{
    public ApplicationDataPaths()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GenshinBrowser");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WebViewProfile);
    }

    public string Root { get; }

    public string Settings => Path.Combine(Root, "settings.json");

    public string History => Path.Combine(Root, "history.json");

    public string Favorites => Path.Combine(Root, "favorites.json");

    public string Downloads => Path.Combine(Root, "downloads.json");

    public string Logs => Path.Combine(Root, "logs");

    public string WebViewProfile => Path.Combine(Root, "WebViewProfile");
}
