using System;
using System.Collections.Generic;

namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// A multi-sample loss-of-contact episode. The sample range is inclusive and spans from the
/// first curvature-threshold crossing through the last crossing assigned to the episode.
/// </summary>
public record GlitchBurst(int StartSample, int EndSample, double PeakDeviationLsb);

/// <summary>
/// Derived signal-integrity information for one suspension channel. This is a diagnostic result
/// only: detection never repairs or otherwise modifies the recorded telemetry.
/// </summary>
public record ChannelIntegrity(
    IReadOnlyList<GlitchBurst> Bursts,
    double ThresholdLsb,
    double NoiseScaleLsb,
    int TotalCorruptSamples)
{
    /// <summary>
    /// Returns the duration represented by the inclusive reported burst ranges.
    /// </summary>
    public double TotalCorruptDurationSeconds(int sampleRate) =>
        sampleRate > 0 ? TotalCorruptSamples / (double)sampleRate : 0.0;
}

/// <summary>
/// Detects multi-sample potentiometer contact-loss bursts without changing the source signal.
/// A per-sample step limit cannot distinguish these faults from real hard impacts: genuine
/// suspension motion can have a very large step while its derivative remains smooth. Curvature
/// deviation instead measures how far a sample departs from linear interpolation between its
/// neighbours, exposing the decay-and-snap-back shape produced when the wiper loses contact.
/// </summary>
public static class SignalIntegrity
{
    /// <summary>
    /// Detects multi-sample glitch bursts in raw shock or fork travel.
    /// The threshold adapts to each channel using the median absolute deviation, which is robust
    /// to the very glitches being sought and to the heavy-tailed motion of a healthy channel. An
    /// absolute floor prevents exceptionally quiet channels from acquiring an unrealistically
    /// sensitive threshold. The input array is read only and is never modified.
    /// </summary>
    public static ChannelIntegrity Detect(double[]? shockTravel, double travelPerLsb, int sampleRate)
    {
        if (shockTravel is null || travelPerLsb <= 0 || shockTravel.Length < 5)
            return Empty();

        var sampleCount = shockTravel.Length;
        var raw = new double[sampleCount];
        var deviation = new double[sampleCount];

        for (var i = 0; i < sampleCount; i++)
            raw[i] = shockTravel[i] / travelPerLsb;

        for (var i = 1; i < sampleCount - 1; i++)
            deviation[i] = Math.Abs(raw[i] - (raw[i - 1] + raw[i + 1]) / 2.0);

        var medianDeviation = Median(deviation);
        var absoluteDeviations = new double[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            absoluteDeviations[i] = Math.Abs(deviation[i] - medianDeviation);

        var noiseScale = 1.4826 * Median(absoluteDeviations);
        var threshold = Math.Max(Parameters.GlitchBurstSigma * noiseScale, Parameters.GlitchBurstFloorLsb);
        var mergeGap = (int)(sampleRate * Parameters.GlitchBurstMergeGapSeconds);

        var bursts = new List<GlitchBurst>();
        var firstSeed = -1;
        var lastSeed = -1;
        var seedCount = 0;
        var peakDeviation = 0.0;

        for (var i = 1; i < sampleCount - 1; i++)
        {
            if (deviation[i] <= threshold)
                continue;

            if (firstSeed >= 0 && i - lastSeed > mergeGap)
            {
                AddBurstIfMultiSeed(bursts, firstSeed, lastSeed, seedCount, peakDeviation);
                firstSeed = -1;
                seedCount = 0;
                peakDeviation = 0.0;
            }

            if (firstSeed < 0)
                firstSeed = i;

            lastSeed = i;
            seedCount++;
            peakDeviation = Math.Max(peakDeviation, deviation[i]);
        }

        AddBurstIfMultiSeed(bursts, firstSeed, lastSeed, seedCount, peakDeviation);

        var totalCorruptSamples = 0;
        foreach (var burst in bursts)
            totalCorruptSamples += burst.EndSample - burst.StartSample + 1;

        return new ChannelIntegrity(bursts, threshold, noiseScale, totalCorruptSamples);
    }

    private static ChannelIntegrity Empty() =>
        new(Array.Empty<GlitchBurst>(), 0.0, 0.0, 0);

    private static void AddBurstIfMultiSeed(
        List<GlitchBurst> bursts,
        int firstSeed,
        int lastSeed,
        int seedCount,
        double peakDeviation)
    {
        if (seedCount >= Parameters.GlitchBurstMinSeeds)
            bursts.Add(new GlitchBurst(firstSeed, lastSeed, peakDeviation));
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}
