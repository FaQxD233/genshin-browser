using System.Security.AccessControl;
using System.Security.Principal;
using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class WebView2RuntimeLocatorTests
{
    [Fact]
    public void FindBundledRuntime_NoRuntimeFolder_ReturnsNull()
    {
        using var directory = new TestDirectory();

        Assert.Null(WebView2RuntimeLocator.FindBundledRuntime(directory.Path));
    }

    [Fact]
    public void FindBundledRuntime_FolderWithoutBrowserExecutable_ReturnsNull()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.GetPath(WebView2RuntimeLocator.FolderName));

        Assert.Null(WebView2RuntimeLocator.FindBundledRuntime(directory.Path));
    }

    [Fact]
    public void FindBundledRuntime_WithBrowserExecutable_ReturnsRuntimeFolder()
    {
        using var directory = new TestDirectory();
        var folder = directory.GetPath(WebView2RuntimeLocator.FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "msedgewebview2.exe"), []);

        Assert.Equal(folder, WebView2RuntimeLocator.FindBundledRuntime(directory.Path));
    }

    [Fact]
    public void EnsureAppContainerAccess_GrantsInheritableReadAndExecuteOnce()
    {
        using var directory = new TestDirectory();
        var folder = directory.GetPath(WebView2RuntimeLocator.FolderName);
        Directory.CreateDirectory(folder);

        WebView2RuntimeLocator.EnsureAppContainerAccess(folder);
        var changedAgain = WebView2RuntimeLocator.EnsureAppContainerAccess(folder);

        var rules = new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToList();
        foreach (var sid in new[] { "S-1-15-2-1", "S-1-15-2-2" })
        {
            Assert.Contains(rules, rule =>
                rule.IdentityReference.Value == sid &&
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
                rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        }

        Assert.False(changedAgain);
    }
}
