using System.Linq;
using CoreScope.Core.Hardware;
using Xunit;

using System;

namespace CoreScope.Tests;

public class InsightEngineTests
{
    private static SystemSpec SpecWithVolume(long total, long free)
    {
        var smbios = SmBios.Parse(new byte[] { 0, 3, 6, 0, 6, 0, 0, 0, 127, 4, 0, 0, 0, 0 })
                     ?? throw new InvalidOperationException("SMBIOS parse");
        var spec = new SystemSpec { SmBios = smbios };
        spec.Volumes.Add(new VolumeSpec { Name = "C:", TotalBytes = total, FreeBytes = free });
        return spec;
    }

    [Fact]
    public void NearlyFullDrive_IsCritical()
    {
        var insights = new InsightEngine().Evaluate(SpecWithVolume(2_000_000_000_000, 50_000_000_000));
        Assert.Contains(insights, i => i.Severity == Severity.Critical && i.Category == "Storage");
    }

    [Fact]
    public void DriveWithPlentyOfSpace_RaisesNoStorageWarning()
    {
        var insights = new InsightEngine().Evaluate(SpecWithVolume(2_000_000_000_000, 1_000_000_000_000));
        Assert.DoesNotContain(insights, i => i.Category == "Storage" && i.Severity <= Severity.Warning);
    }

    [Fact]
    public void NoSensors_DoesNotThrow()
    {
        var engine = new InsightEngine();
        engine.Observe(Array.Empty<SensorReading>());
        Assert.NotNull(engine.Evaluate(SpecWithVolume(0, 0)));
    }
}
