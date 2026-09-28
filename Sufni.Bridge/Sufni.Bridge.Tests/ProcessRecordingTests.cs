using System;
using System.Collections.Generic;
using System.Linq;
using Sufni.Bridge.Models.Telemetry;
using Xunit;

namespace Sufni.Bridge.Tests;

public class ProcessRecordingTests
{
    private const int SampleRate = 1000;
    private const double MaxRearStroke = 55.0;

    private static Linkage MakeLinkage()
    {
        var rows = Enumerable.Range(0, 16).Select(i =>
            FormattableString.Invariant($"{i * 10.0},{3.0 - 0.03 * i}"));
        return new Linkage(Guid.NewGuid(), "test", 64.0, 160.0, MaxRearStroke, 1250.0, string.Join("\n", rows));
    }

    private static Calibration MakeRearCalibration(Linkage linkage)
    {
        var method = new CalibrationMethod(CalibrationMethod.LinearId, "linear", "",
            new CalibrationMethodProperties(
                ["min_measurement", "max_measurement"],
                new Dictionary<string, string> { ["factor"] = "MAX_STROKE / (max_measurement - min_measurement)" },
                "(sample - min_measurement) * factor"));
        var calibration = new Calibration(Guid.NewGuid(), "rear", method.Id,
            new Dictionary<string, double> { ["min_measurement"] = 0, ["max_measurement"] = 4096 });
        calibration.Prepare(method, MaxRearStroke, linkage.MaxRearTravel);
        return calibration;
    }

    private static ushort[] Samples(int n) =>
        Enumerable.Range(0, n)
            .Select(i => (ushort)(1500 + 700 * Math.Sin(2 * Math.PI * 1.3 * i / SampleRate)
                                       + 200 * Math.Sin(2 * Math.PI * 6.1 * i / SampleRate)))
            .ToArray();

    [Fact]
    public void RecordedChannelWithoutCalibration_IsIgnored()
    {
        var linkage = MakeLinkage();
        var td = new TelemetryData("t", 3, SampleRate, 0, null, MakeRearCalibration(linkage), linkage);

        // The DAQ recorded a front channel, but the setup has no front calibration.
        td.ProcessRecording(Samples(8000), Samples(8000));

        Assert.False(td.Front.Present);
        Assert.True(td.Rear.Present);
    }

    [Fact]
    public void NoCalibrationForAnyRecordedChannel_ThrowsClearMessage()
    {
        var linkage = MakeLinkage();
        var td = new TelemetryData("t", 3, SampleRate, 0, null, MakeRearCalibration(linkage), linkage);

        var e = Assert.Throws<Exception>(() => td.ProcessRecording(Samples(8000), []));

        Assert.Contains("no calibration", e.Message);
    }
}
