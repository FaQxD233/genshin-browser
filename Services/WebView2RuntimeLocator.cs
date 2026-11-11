using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GenshinBrowser.Services;

/// <summary>
/// 完整版发布包内置的 WebView2 固定版本运行时：exe 同目录下的 <see cref="FolderName"/>。
/// 有就只用它（不检测、不安装系统的 Evergreen 运行时）；标准版和开发构建没有，照旧用系统的。
/// </summary>
internal static class WebView2RuntimeLocator
{
    internal const string FolderName = "WebView2Runtime";
    private const string BrowserExecutable = "msedgewebview2.exe";

    /// <summary>
    /// 官方文档：固定版本 120 起，未打包的应用在 Windows 10 上要给运行时目录授权这两个 SID 读取和执行（继承到子项），
    /// 否则渲染进程（跑在 AppContainer 里）起不来。Windows 11 不需要，授权也无害。
    /// </summary>
    private static readonly SecurityIdentifier[] AppContainerSids =
    [
        new("S-1-15-2-1"), // ALL APPLICATION PACKAGES
        new("S-1-15-2-2"), // ALL RESTRICTED APPLICATION PACKAGES
    ];

    /// <summary>内置运行时目录（里面有 msedgewebview2.exe）；没有返回 null。</summary>
    internal static string? FindBundledRuntime(string baseDirectory)
    {
        var folder = Path.Combine(baseDirectory, FolderName);
        return File.Exists(Path.Combine(folder, BrowserExecutable)) ? folder : null;
    }

    /// <summary>
    /// 确保 AppContainer 能读取并执行运行时目录：已有（含从上级目录继承）就不动。返回是否改了权限。
    /// 非 NTFS 等不支持权限的文件系统会抛异常，由调用方记日志。
    /// </summary>
    internal static bool EnsureAppContainerAccess(string folder)
    {
        var directory = new DirectoryInfo(folder);
        var security = directory.GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var missing = AppContainerSids.Where(sid => !HasInheritableReadAndExecute(rules, sid)).ToList();
        foreach (var sid in missing)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        if (missing.Count > 0)
        {
            directory.SetAccessControl(security);
        }

        return missing.Count > 0;
    }

    private static bool HasInheritableReadAndExecute(AuthorizationRuleCollection rules, SecurityIdentifier sid) =>
        rules.OfType<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            sid.Equals(rule.IdentityReference) &&
            (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
            rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit) &&
            rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit));
}
