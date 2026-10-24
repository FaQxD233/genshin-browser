using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class AppPathsTests
{
    private const string Default = @"C:\Data\Default";

    [Fact]
    public void Resolve_PrefersEnvironmentThenRegistryThenDefault()
    {
        Assert.Equal((@"D:\Env", (string?)null), AppPaths.Resolve(@"D:\Env", @"D:\Reg", Default, _ => true));
        Assert.Equal((@"D:\Reg", (string?)null), AppPaths.Resolve(null, @"D:\Reg", Default, _ => true));
        // 空白当作没设置；结尾的分隔符去掉
        Assert.Equal((@"D:\Reg", (string?)null), AppPaths.Resolve("  ", @"D:\Reg\", Default, _ => true));
        Assert.Equal((Default, (string?)null), AppPaths.Resolve(null, null, Default, _ => true));
    }

    [Fact]
    public void Resolve_FallsBackToDefaultAndReportsUnavailableDirectory()
    {
        var (root, unavailable) = AppPaths.Resolve(null, @"X:\Missing", Default, _ => false);

        Assert.Equal(Default, root);
        Assert.Equal(@"X:\Missing", unavailable);
    }

    [Theory]
    [InlineData(@"D:\Data", @"D:\Data", true)]
    [InlineData(@"D:\Data", @"d:\data\", true)]
    [InlineData(@"D:\Data\Sub", @"D:\Data", true)]
    [InlineData(@"D:\Data", @"D:\Data\Sub", true)]
    [InlineData(@"D:\Data", @"D:\DataOther", false)]
    [InlineData(@"D:\Data", @"E:\Data", false)]
    public void AreNested_DetectsSameOrContainedFolders(string first, string second, bool expected) =>
        Assert.Equal(expected, AppPaths.AreNested(first, second));

    [Fact]
    public void IsUsableAndHasAppData_CheckRealFolders()
    {
        using var directory = new TestDirectory();
        var target = Path.Combine(directory.Path, "new-root");

        // 不存在时创建；能写入才算可用
        Assert.True(AppPaths.IsUsable(target));
        Assert.False(AppPaths.HasAppData(target));
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
        Assert.True(AppPaths.HasAppData(target));
        Assert.False(AppPaths.IsUsable(@"relative\path"));
    }

    [Fact]
    public void CopyAll_CopiesNestedFilesKeepsSourceAndReportsProgress()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Path, "old");
        var target = Path.Combine(directory.Path, "new");
        Directory.CreateDirectory(Path.Combine(source, "WebViewProfile", "Default"));
        File.WriteAllText(Path.Combine(source, "settings.json"), "{\"ThemeMode\":\"Light\"}");
        File.WriteAllText(Path.Combine(source, "WebViewProfile", "Default", "Cookies"), "cookie");
        var reported = new List<double>();

        DataDirectoryMigrator.CopyAll(source, target, new SyncProgress(reported.Add));

        Assert.Equal("{\"ThemeMode\":\"Light\"}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("cookie", File.ReadAllText(Path.Combine(target, "WebViewProfile", "Default", "Cookies")));
        Assert.True(File.Exists(Path.Combine(source, "settings.json")));
        Assert.Equal(1, reported[^1]);
    }

    // Progress<T> 的回调走同步上下文 / 线程池，测试里用同步实现
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
