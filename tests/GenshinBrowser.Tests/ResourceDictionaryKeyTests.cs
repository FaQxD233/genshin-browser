using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace GenshinBrowser.Tests;

/// <summary>
/// 成对的资源字典（暗 / 亮主题、中 / 英文案）必须定义同一组键：DynamicResource 找不到键时静默落空，
/// 只在一边加 token 或文案不会编译报错，只会在切到另一边时显示成空白、缺投影。
/// </summary>
public sealed class ResourceDictionaryKeyTests
{
    [Theory]
    [InlineData("Resources/Themes/Dark.xaml", "Resources/Themes/Light.xaml")]
    [InlineData("Resources/i18n/Strings.zh-CN.xaml", "Resources/i18n/Strings.en-US.xaml")]
    public void PairedDictionaries_DefineSameKeys(string left, string right)
    {
        var leftKeys = ReadKeys(left);
        var rightKeys = ReadKeys(right);

        Assert.NotEmpty(leftKeys);
        Assert.Empty(leftKeys.Except(rightKeys));
        Assert.Empty(rightKeys.Except(leftKeys));
    }

    private static HashSet<string> ReadKeys(string relativePath)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));
        return document.Root!.Elements()
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    // 按本源文件位置定位仓库根（本文件在 tests/GenshinBrowser.Tests/ 下），读的是源码里的字典
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
