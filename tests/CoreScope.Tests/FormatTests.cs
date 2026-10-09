using CoreScope.Core;
using Xunit;

using System;

namespace CoreScope.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(16L * 1024 * 1024 * 1024, "16 GB")]
    public void Bytes_UsesBinaryUnits(double input, string expected) =>
        Assert.Equal(expected, Format.Bytes(input));

    [Theory]
    [InlineData(800, "800 MHz")]
    [InlineData(2400, "2.40 GHz")]
    public void Mhz_SwitchesToGhzAtOneThousand(double input, string expected) =>
        Assert.Equal(expected, Format.Mhz(input));

    [Theory]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU", "Intel Core i5-8250U CPU")]
    [InlineData("AMD  RadeonT   Graphics", "AMD Radeon Graphics")]
    public void CleanName_StripsTrademarkNoise(string input, string expected) =>
        Assert.Equal(expected, Format.CleanName(input));

    [Fact]
    public void Duration_PicksTheLargestUsefulUnits()
    {
        Assert.Equal("5m", Format.Duration(TimeSpan.FromMinutes(5)));
        Assert.Equal("2h 3m", Format.Duration(new TimeSpan(2, 3, 0)));
        Assert.Equal("24d 1h 2m", Format.Duration(new TimeSpan(24, 1, 2, 0)));
    }

    [Fact]
    public void YesNo_HandlesUnknown() => Assert.Equal("Unknown", Format.YesNo(null));
}
