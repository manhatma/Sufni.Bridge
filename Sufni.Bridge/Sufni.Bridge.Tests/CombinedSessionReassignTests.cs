using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MessagePack;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Services;
using Xunit;

namespace Sufni.Bridge.Tests;

/// <summary>
/// A combined session's stored travel contains the synthetic transition ramps between its
/// sources. Changing its geometry must rebuild it from the (reprocessed) sources, exactly like
/// CombineSessions does, instead of running ReprocessVelocity over the ramps.
/// </summary>
public class CombinedSessionReassignTests : IDisposable
{
    private const int SampleRate = 1000;
    private const double MaxRearStroke = 55.0;

    private readonly string directory;
    private readonly SqLiteDatabaseService db;

    public CombinedSessionReassignTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "sufni-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        db = new SqLiteDatabaseService(Path.Combine(directory, "sst.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, true); }
        catch { /* the pooled SQLite connection may still hold the file */ }
    }

    private static Linkage MakeLinkage(string name, double lrStart, double lrEnd)
    {
        var rows = Enumerable.Range(0, 16).Select(i =>
        {
            var wheel = i * 10.0;
            var lr = lrStart + (lrEnd - lrStart) * i / 15.0;
            return FormattableString.Invariant($"{wheel},{lr}");
        });
        return new Linkage(Guid.NewGuid(), name, 64.0, null, MaxRearStroke, 1250.0, string.Join("\n", rows));
    }

    // Rear-only recording: shock travel in mm, encoded as a 12-bit linear sensor reading.
    private static ushort[] RearSamples(double seconds, double sag, double phase)
    {
        var n = (int)(seconds * SampleRate);
        var samples = new ushort[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            var shock = sag
                        + 9.0 * Math.Sin(2 * Math.PI * 1.3 * t + phase)
                        + 3.0 * Math.Sin(2 * Math.PI * 6.1 * t + 2 * phase)
                        + 1.5 * Math.Sin(2 * Math.PI * 17.0 * t);
            samples[i] = (ushort)Math.Round(Math.Clamp(shock, 0, MaxRearStroke) * 4096 / MaxRearStroke);
        }
        return samples;
    }

    private async Task<(Guid a, Guid b, Guid combined, Guid oldSetup, Guid newSetup, Linkage newLinkage)> SeedAsync()
    {
        var oldLinkage = MakeLinkage("old", 3.0, 2.5);
        var newLinkage = MakeLinkage("new", 2.8, 2.1);
        await db.PutLinkageAsync(oldLinkage);
        await db.PutLinkageAsync(newLinkage);

        var method = await db.GetCalibrationMethodAsync(CalibrationMethod.LinearId);
        var calibration = new Calibration(Guid.NewGuid(), "rear", CalibrationMethod.LinearId,
            new Dictionary<string, double> { ["min_measurement"] = 0, ["max_measurement"] = 4096 });
        await db.PutCalibrationAsync(calibration);

        var oldSetup = new Setup(Guid.NewGuid(), "old", oldLinkage.Id, null, calibration.Id);
        var newSetup = new Setup(Guid.NewGuid(), "new", newLinkage.Id, null, calibration.Id);
        await db.PutSetupAsync(oldSetup);
        await db.PutSetupAsync(newSetup);

        var importLinkage = await db.GetLinkageAsync(oldLinkage.Id);
        calibration.Prepare(method!, MaxRearStroke, importLinkage!.MaxRearTravel);

        const int t0 = 1_780_000_000;
        var parts = new List<TelemetryData>();
        var ids = new List<Guid>();
        // The first source ends high and the second starts low, so the ramp between them is long.
        foreach (var (name, timestamp, sag, phase) in new[] { ("a", t0, 22.0, Math.PI / 2), ("b", t0 + 600, 8.0, -Math.PI / 2) })
        {
            var td = new TelemetryData(name, 3, SampleRate, timestamp, null, calibration, importLinkage);
            var psst = td.ProcessRecording([], RearSamples(8, sag, phase));
            var session = new Session(Guid.NewGuid(), name, "", oldSetup.Id, timestamp) { ProcessedData = psst };
            await db.PutSessionAsync(session);
            parts.Add(td);
            ids.Add(session.Id);
        }

        var combined = TelemetryData.CombineSessions(parts, "a + b");
        var combinedSession = new Session(Guid.NewGuid(), "a + b", "", oldSetup.Id, t0)
        {
            ProcessedData = MessagePackSerializer.Serialize(combined)
        };
        await db.PutSessionAsync(combinedSession);
        await db.PutCombinedSourcesAsync(combinedSession.Id, ids);

        return (ids[0], ids[1], combinedSession.Id, oldSetup.Id, newSetup.Id, newLinkage);
    }

    private async Task AssertRebuiltFromSourcesAsync(Guid a, Guid b, Guid combined, Linkage newLinkage)
    {
        var actual = await db.GetSessionPsstAsync(combined);
        var expected = TelemetryData.CombineSessions(
            [(await db.GetSessionPsstAsync(a))!, (await db.GetSessionPsstAsync(b))!], "a + b");

        Assert.NotNull(actual);
        Assert.Equal(newLinkage.GeometrySignature, actual!.Linkage.GeometrySignature);
        Assert.Equal(expected.Rear.Travel, actual.Rear.Travel);
        Assert.Equal(expected.Rear.Strokes.Compressions.Length, actual.Rear.Strokes.Compressions.Length);
        Assert.Equal(expected.Rear.Strokes.Rebounds.Length, actual.Rear.Strokes.Rebounds.Length);
    }

    [Fact]
    public async Task ReassignSetupInSessions_RebuildsCombinedSessionFromSources()
    {
        var (a, b, combined, oldSetup, newSetup, newLinkage) = await SeedAsync();

        var count = await db.ReassignSetupInSessionsAsync(oldSetup, newSetup);

        Assert.Equal(3, count);
        await AssertRebuiltFromSourcesAsync(a, b, combined, newLinkage);
    }

    [Fact]
    public async Task ReassignSessionSetup_SourcesFirst_RebuildsCombinedSessionFromSources()
    {
        var (a, b, combined, _, newSetup, newLinkage) = await SeedAsync();

        // Same order as SessionViewModel.HandleSetupReassign: sources, then the combined session.
        foreach (var id in new[] { a, b, combined })
            await db.ReassignSessionSetupAsync(id, newSetup);

        await AssertRebuiltFromSourcesAsync(a, b, combined, newLinkage);
    }
}
