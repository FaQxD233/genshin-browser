using System.IO;
using System.Text;
using System.Text.Json;

namespace GenshinBrowser.Services;

internal static class JsonFileWriter
{
    /// <summary>
    /// 共享的 JSON 序列化选项（带缩进），用于用户可能直接查看的配置文件（如 settings.json）。
    /// JsonSerializerOptions 用于序列化时是线程安全的。
    /// </summary>
    public static readonly JsonSerializerOptions SharedOptions = new() { WriteIndented = true };

    /// <summary>
    /// 紧凑（无缩进）序列化选项，用于历史/收藏等内部数据文件，减少磁盘占用。
    /// </summary>
    public static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

    public static string ReadAllTextBounded(string path, int maxBytes)
    {
        using var stream = OpenBoundedRead(path, maxBytes, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static async Task<string> ReadAllTextBoundedAsync(string path, int maxBytes)
    {
        await using var stream = OpenBoundedRead(
            path,
            maxBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

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
                try { File.Delete(tempPath); } catch { /* 忽略删除失败 */ }
            }
        }
    }

    /// <summary>
    /// 清理指定目录下残留的 JSON 原子写临时文件（命名模式：{原名}.json.{guid}.tmp）。
    /// 进程崩溃 / 任务被取消可能在原子写流程中留下 .tmp 文件，启动时调用一次回收磁盘。
    /// </summary>
    /// <param name="directory">应用数据根目录。只扫描顶层且只删除 JSON GUID 临时文件，不触碰 WebViewProfile 或其他临时文件。</param>
    public static void PurgeStaleTempFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            int separator = stem.LastIndexOf('.');
            if (separator <= 0)
            {
                continue;
            }

            string originalFileName = stem[..separator];
            string guidSuffix = stem[(separator + 1)..];
            if (!originalFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(guidSuffix, "N", out _))
            {
                continue;
            }

            try { File.Delete(file); }
            catch { /* 单个文件删除失败不影响其他文件 */ }
        }
    }

    private static FileStream OpenBoundedRead(string path, int maxBytes, FileOptions options)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, options);
        if (stream.Length <= maxBytes)
        {
            return stream;
        }

        var actualBytes = stream.Length;
        stream.Dispose();
        throw new InvalidDataException(
            $"JSON file '{Path.GetFileName(path)}' is {actualBytes} bytes; the limit is {maxBytes} bytes.");
    }
}
