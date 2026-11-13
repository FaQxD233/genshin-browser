using System.Windows.Shell;
using GenshinBrowser.Models;
using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class AppCommandsTests
{
    [Theory]
    [InlineData("toggle-mode", AppCommand.ToggleMode)]
    [InlineData("toggle-touch", AppCommand.ToggleTouchMode)]
    [InlineData("toggle-hide", AppCommand.ToggleHide)]
    [InlineData("toggle-playback", AppCommand.TogglePlayback)]
    [InlineData("Toggle-Mode", AppCommand.ToggleMode)]
    public void TryParse_KnownName_ReturnsCommand(string name, AppCommand expected)
    {
        Assert.True(AppCommands.TryParse(["--command", name], out var command));
        Assert.Equal(expected, command);
    }

    [Fact]
    public void TryParse_FindsCommandAfterOtherArguments()
    {
        Assert.True(AppCommands.TryParse([App.RestartArgument, "--Command", "toggle-hide"], out var command));
        Assert.Equal(AppCommand.ToggleHide, command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--command")]
    [InlineData("--command unknown")]
    [InlineData("--command unknown toggle-mode")]
    [InlineData("toggle-mode")]
    [InlineData("toggle-mode --command")]
    [InlineData(App.RestartArgument)]
    public void TryParse_MissingOrUnknown_ReturnsFalse(string commandLine)
    {
        Assert.False(AppCommands.TryParse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries), out _));
    }

    [Fact]
    public void Message_IsRegisteredWindowMessage()
    {
        // RegisterWindowMessage 的编号在 0xC000–0xFFFF；两个进程按同一名称注册拿到同一个编号
        Assert.InRange(AppCommands.Message, 0xC000u, 0xFFFFu);
    }

    [Theory]
    [InlineData(1, AppCommand.ToggleMode)]
    [InlineData(2, AppCommand.ToggleTouchMode)]
    [InlineData(3, AppCommand.ToggleHide)]
    [InlineData(4, AppCommand.TogglePlayback)]
    public void TryRead_CommandMessage_ReturnsCommand(int wParam, AppCommand expected)
    {
        Assert.True(AppCommands.TryRead((int)AppCommands.Message, new IntPtr(wParam), out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void TryRead_UnknownCommand_ReturnsFalse(int wParam)
    {
        Assert.False(AppCommands.TryRead((int)AppCommands.Message, new IntPtr(wParam), out _));
    }

    [Theory]
    [InlineData(0x0000)] // WM_NULL
    [InlineData(0x0201)] // WM_LBUTTONDOWN
    public void TryRead_OtherMessage_ReturnsFalse(int msg)
    {
        Assert.False(AppCommands.TryRead(msg, new IntPtr(1), out _));
    }

    [Fact]
    public void CreateJumpList_HasFourTasksThatParseBackInMenuOrder()
    {
        const string exe = @"C:\GenshinBrowser\GenshinBrowser.exe";
        var jumpList = AppCommands.CreateJumpList(exe);

        Assert.False(jumpList.ShowFrequentCategory);
        Assert.False(jumpList.ShowRecentCategory);
        var tasks = jumpList.JumpItems.Cast<JumpTask>().ToList();
        Assert.All(tasks, task =>
        {
            Assert.Equal(exe, task.ApplicationPath);
            Assert.Equal(@"C:\GenshinBrowser", task.WorkingDirectory);
            Assert.Null(task.CustomCategory);
            Assert.False(string.IsNullOrWhiteSpace(task.Title));
        });

        var commands = tasks.Select(task =>
        {
            Assert.True(AppCommands.TryParse(task.Arguments.Split(' '), out var command));
            return command;
        });
        Assert.Equal(
            [AppCommand.ToggleMode, AppCommand.ToggleTouchMode, AppCommand.ToggleHide, AppCommand.TogglePlayback],
            commands);
    }
}
