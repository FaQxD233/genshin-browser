using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GenshinBrowser.Services;

/// <summary>
/// 读不出来的数据文件。<see cref="PreservedPath"/> 是原文件改名后的位置；为 null 表示改名也失败（多半被其它程序占用），
/// 本次运行不再写这个文件，免得覆盖还在的数据。
/// </summary>
public sealed record DataFileProblem(string FileName, string? PreservedPath);

/// <summary>
/// 设置、收藏、浏览记录、下载记录共用的读盘。读不出来时不能当成空文件继续：之后第一次保存就会把原文件整个覆盖掉。
/// </summary>
internal static class DataFile
{
    private const int ReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 读取并反序列化。文件不存在返回 null，<paramref name="problem"/> 也为 null。
    /// 读盘 IO 异常（被杀毒、同步软件短暂占用等）重试几次；内容损坏、超过大小上限或一直读不出来时，
    /// 把原文件改名为 {名}.broken-{时间}{扩展名} 留在原处，返回 null 并给出 <paramref name="problem"/>。
    /// </summary>
    public static T? Read<T>(string path, int maxBytes, JsonSerializerOptions? options, string logContext, out DataFileProblem? problem)
        where T : class
    {
        problem = null;
        for (var attempt = 1; ; attempt++)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(JsonFileWriter.ReadAllTextBounded(path, maxBytes), options);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReadAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                FileLogger.LogException(ex, logContext);
                // 重试期间文件被删掉了：按不存在处理
                problem = File.Exists(path) ? Preserve(path) : null;
                return null;
            }
        }
    }

    /// <summary><see cref="Read{T}"/> 放到线程池上执行（启动时异步加载用）。</summary>
    public static Task<(T? Value, DataFileProblem? Problem)> ReadAsync<T>(string path, int maxBytes, JsonSerializerOptions? options, string logContext)
        where T : class =>
        Task.Run(() =>
        {
            var value = Read<T>(path, maxBytes, options, logContext, out var problem);
            return (value, problem);
        });

    private static DataFileProblem Preserve(string path)
    {
        var fileName = Path.GetFileName(path);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var preservedPath = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}.broken-{stamp}{Path.GetExtension(path)}");
        try
        {
            File.Move(path, preservedPath);
            return new DataFileProblem(fileName, preservedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, $"Preserve unreadable {fileName}");
            return new DataFileProblem(fileName, null);
        }
    }
}
