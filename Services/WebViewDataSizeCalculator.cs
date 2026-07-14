using System.IO;

namespace GenshinBrowser.Services;

internal static class WebViewDataSizeCalculator
{
    private static readonly HashSet<string> ReclaimableDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
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

                    if (ReclaimableDirectoryNames.Contains(child.Name))
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
