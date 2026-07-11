using System.IO;
using System.Text.Json;

namespace GenshinBrowser.Services;

internal static class JsonFileWriter
{
    /// <summary>
    /// 共享的 JSON 序列化选项，避免每个服务各持一份实例。
    /// JsonSerializerOptions 用于序列化时是线程安全的。
    /// </summary>
    public static readonly JsonSerializerOptions SharedOptions = new() { WriteIndented = true };

    public static async Task WriteAtomicAsync<T>(string path, T value, JsonSerializerOptions options)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            directory ?? string.Empty,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, options).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
