using ScreenRecorder.Core.Services;
using Xunit;

namespace ScreenRecorder.Tests;

public class ConversionServiceTests
{
    [Fact]
    public void SelectEncoderArgs_PrefersQsvThenNvencThenAmfThenX264()
    {
        var sample = " V....D h264_qsv   H.264 ...\n V....D h264_nvenc ...\n V....D h264_amf ...\n V....D libx264 ...";
        Assert.Contains("h264_qsv", ConversionService.SelectEncoderArgs(sample));

        var nvenc = " V....D h264_nvenc ...\n V....D libx264 ...";
        Assert.Contains("h264_nvenc", ConversionService.SelectEncoderArgs(nvenc));

        var amf = " V....D h264_amf ...\n V....D libx264 ...";
        Assert.Contains("h264_amf", ConversionService.SelectEncoderArgs(amf));

        var none = " V....D libx264 ...";
        Assert.Contains("libx264", ConversionService.SelectEncoderArgs(none));
    }

    [Fact]
    public void CandidateEncoderArgs_OrdersHardwareFirstAndX264Last()
    {
        var text = " V....D h264_qsv ...\n V....D h264_nvenc ...\n V....D h264_amf ...\n V....D libx264 ...";
        var candidates = ConversionService.CandidateEncoderArgs(text);

        Assert.Equal(4, candidates.Count);
        Assert.Contains("h264_qsv", candidates[0]);
        Assert.Contains("h264_nvenc", candidates[1]);
        Assert.Contains("h264_amf", candidates[2]);
        Assert.Contains("libx264", candidates[^1]);
    }

    [Fact]
    public void CandidateEncoderArgs_AlwaysEndsWithLibx264()
    {
        var candidates = ConversionService.CandidateEncoderArgs("nothing here");
        Assert.Single(candidates);
        Assert.Contains("libx264", candidates[0]);
    }

    [Fact]
    public void BuildConversionArgs_ForcesMp4MuxerAndFaststart()
    {
        var args = ConversionService.BuildConversionArgs(
            @"C:\tmp\frag.mkv", @"C:\tmp\frag.tmp.mp4", "-c:v libx264 -preset ultrafast", remuxOnly: false);

        Assert.Contains("-f mp4", args);
        Assert.Contains("-movflags +faststart", args);
        Assert.Contains("-i \"C:\\tmp\\frag.mkv\"", args);
        Assert.Contains("C:\\tmp\\frag.tmp.mp4", args);
        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-c:a aac", args);
    }

    [Fact]
    public void BuildConversionArgs_RemuxUsesStreamCopy()
    {
        var args = ConversionService.BuildConversionArgs("in.mkv", "out.mp4", "ignored", remuxOnly: true);
        Assert.Contains("-c:v copy", args);
        Assert.DoesNotContain("ignored", args);
    }
}
