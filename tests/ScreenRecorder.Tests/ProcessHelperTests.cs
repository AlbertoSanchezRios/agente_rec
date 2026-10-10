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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsProcessRunning_ReturnsFalseForNullOrWhitespace(string? input)
    {
        Assert.False(ProcessHelper.IsProcessRunning(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetProcessCount_ReturnsZeroForNullOrWhitespace(string? input)
    {
        Assert.Equal(0, ProcessHelper.GetProcessCount(input));
    }
}
