using System.IO;

namespace GenshinBrowser.Services;

/// <summary>
/// 更改数据目录时搬运数据：把旧目录下的全部文件（设置、收藏、浏览记录、下载记录、日志、浏览器数据）复制到新目录，
/// 同名覆盖，旧目录原样保留。符号链接 / 联接点不跟进，避免复制到链接指向的别处。
/// 在启动阶段、浏览器内核拉起之前调用，此时没有文件被占用。
/// </summary>
public static class DataDirectoryMigrator
{
    public static void CopyAll(string source, string target, IProgress<double>? progress = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var files = new DirectoryInfo(source).EnumerateFiles("*", options).ToList();
        var totalBytes = Math.Max(1, files.Sum(file => file.Length));
        long copiedBytes = 0;

        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            file.CopyTo(destination, overwrite: true);
            copiedBytes += file.Length;
            progress?.Report((double)copiedBytes / totalBytes);
        }

        progress?.Report(1);
    }
}
