using System;
using System.IO;
using ScreenRecorder.Core.Services;
using Xunit;

namespace ScreenRecorder.Tests;

public class MediaValidatorTests
{
    private static string WriteTemp(byte[] header, int totalSize)
    {
        var path = Path.Combine(Path.GetTempPath(), $"media_{Guid.NewGuid():N}.bin");
        var data = new byte[totalSize];
        Array.Copy(header, data, Math.Min(header.Length, totalSize));
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void IsValidMkv_AcceptsEbmlHeader_RejectsOthers()
    {
        var valid = WriteTemp(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x93 }, 2048);
        var invalid = WriteTemp(new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, 2048);
        var tooSmall = WriteTemp(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, 10);
        try
        {
            Assert.True(MediaValidator.IsValidMkv(valid));
            Assert.False(MediaValidator.IsValidMkv(invalid));
            Assert.False(MediaValidator.IsValidMkv(tooSmall));
        }
        finally
        {
            foreach (var p in new[] { valid, invalid, tooSmall }) File.Delete(p);
        }
    }

    [Fact]
    public void IsValidMp4_AcceptsFtypAtOffset4()
    {
        var valid = WriteTemp(new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }, 2048);
        var invalid = WriteTemp(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, 2048);
        try
        {
            Assert.True(MediaValidator.IsValidMp4(valid));
            Assert.False(MediaValidator.IsValidMp4(invalid));
        }
        finally
        {
            foreach (var p in new[] { valid, invalid }) File.Delete(p);
        }
    }

    [Fact]
    public void IsValidMedia_AcceptsEitherContainer()
    {
        var mkv = WriteTemp(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, 2048);
        var mp4 = WriteTemp(new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, 2048);
        var garbage = WriteTemp(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 2048);
        try
        {
            Assert.True(MediaValidator.IsValidMedia(mkv));
            Assert.True(MediaValidator.IsValidMedia(mp4));
            Assert.False(MediaValidator.IsValidMedia(garbage));
            Assert.False(MediaValidator.IsValidMedia(Path.Combine(Path.GetTempPath(), "no_existe_xyz.mkv")));
        }
        finally
        {
            foreach (var p in new[] { mkv, mp4, garbage }) File.Delete(p);
        }
    }
}
