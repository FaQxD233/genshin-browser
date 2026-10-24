using System.Windows;
using System.Windows.Threading;
using GenshinBrowser.Services;

namespace GenshinBrowser.Windows;

/// <summary>启动时搬运数据的进度窗（见 <see cref="AppPaths.RunPendingMove"/>）。</summary>
public partial class MigrationWindow : Window
{
    private MigrationWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeTitleBarService.Apply(
            this,
            string.Equals(ThemeService.Current, ThemeService.Dark, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>显示进度窗，在后台复制，复制结束前在这里等待（消息循环照常运行）；复制中的异常原样抛出。</summary>
    public static void Run(string source, string target)
    {
        var window = new MigrationWindow();
        var progress = new Progress<double>(value => window.CopyProgress.Value = value);
        var frame = new DispatcherFrame();
        var copy = Task.Run(() => DataDirectoryMigrator.CopyAll(source, target, progress));
        _ = copy.ContinueWith(
            _ => window.Dispatcher.BeginInvoke(() => frame.Continue = false),
            TaskScheduler.Default);
        window.Show();
        Dispatcher.PushFrame(frame);
        window.Close();
        copy.GetAwaiter().GetResult();
    }
}
