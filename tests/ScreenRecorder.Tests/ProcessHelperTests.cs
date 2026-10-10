using System.Diagnostics;
using ScreenRecorder.Core.Services;
using Xunit;

namespace ScreenRecorder.Tests;

public class ProcessHelperTests
{
    [Fact]
    public void IsProcessRunning_ReturnsTrueForCurrentProcess()
    {
        using var current = Process.GetCurrentProcess();
        var running = ProcessHelper.IsProcessRunning(current.ProcessName);
        Assert.True(running);
    }

    [Fact]
    public void IsProcessRunning_ReturnsFalseForNonExistentProcess()
    {
        var running = ProcessHelper.IsProcessRunning("NonExistentProcessName_A9B8C7D6");
        Assert.False(running);
    }

    [Fact]
    public void GetProcessCount_ReturnsPositiveForCurrentProcess()
    {
        using var current = Process.GetCurrentProcess();
        var count = ProcessHelper.GetProcessCount(current.ProcessName);
        Assert.True(count >= 1);
    }

    [Fact]
    public void GetProcessCount_ReturnsZeroForNonExistentProcess()
    {
        var count = ProcessHelper.GetProcessCount("NonExistentProcessName_A9B8C7D6");
        Assert.Equal(0, count);
    }
}
