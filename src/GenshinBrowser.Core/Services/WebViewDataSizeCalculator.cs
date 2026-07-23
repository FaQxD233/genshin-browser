using System.IO;

namespace GenshinBrowser.Services;

public static class WebViewDataSizeCalculator
{
    // Keep the measurement aligned with BrowserSession's DiskCache-only cleanup.
    // Site data (IndexedDB/LocalStorage/Service Worker) may contain login state and
    // must not contribute to a threshold that claims it can be reclaimed.
    private static readonly HashSet<string> DiskCacheDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache",
        "Code Cache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "GPUCache",
        "GPUPersistentCache",
        "GraphiteDawnCache",
        "GrShaderCache",
        "Media Cache",
        "ShaderCache",
    };

    // Do not descend into site-data trees: a nested directory named "Cache" must not
    // make login/session storage look reclaimable or trigger a routine cache purge.
    private static readonly HashSet<string> ProtectedSiteDataDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Service Worker",
        "blob_storage",
        "databases",
        "File System",
        "IndexedDB",
        "Local Storage",
        "Session Storage",
        "WebStorage",
    };

    public static Task<long?> CalculateAsync(string userDataFolder, CancellationToken cancellationToken) =>
        Task.Run(() => Calculate(userDataFolder, cancellationToken), cancellationToken);

    internal static long? Calculate(string userDataFolder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder) || !Directory.Exists(userDataFolder))
        {
            return 0;
        }

        try
        {
            long totalBytes = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(userDataFolder));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var child in directory.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (ProtectedSiteDataDirectoryNames.Contains(child.Name))
                    {
                        continue;
                    }

                    if (DiskCacheDirectoryNames.Contains(child.Name))
                    {
                        totalBytes = checked(totalBytes + MeasureTree(child, cancellationToken));
                    }
                    else
                    {
                        pending.Push(child);
                    }
                }
            }

            return totalBytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OverflowException)
        {
            FileLogger.LogException(ex, "Measure reclaimable WebView2 data");
            return null;
        }
    }

    private static long MeasureTree(DirectoryInfo root, CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    totalBytes = checked(totalBytes + file.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A single locked cache file should not invalidate the whole measurement.
                }
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }

        return totalBytes;
    }
}
