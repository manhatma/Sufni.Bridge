using System;
using System.Collections.Generic;
using System.Linq;

namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// Single dyno-measured spring force curve at a fixed (pressure, volume) configuration.
/// Travel/Force pairs are quasi-static compression measurements.
/// </summary>
public class SpringCurve
{
    public double[] Travel { get; }
    public double[] Force { get; }
    public double PressurePsi { get; }
    public double VolumeCcm { get; }

    public SpringCurve(double[] travel, double[] force, double pressurePsi, double volumeCcm)
    {
        if (travel.Length != force.Length)
            throw new ArgumentException("Travel and Force arrays must have equal length.");
        if (travel.Length < 2)
            throw new ArgumentException("Curve needs at least two samples.");

        Travel = travel;
        Force = force;
        PressurePsi = pressurePsi;
        VolumeCcm = volumeCcm;
    }

    /// <summary>
    /// Linear interpolation in travel. Clamps to edges outside the measured range.
    /// </summary>
    public double InterpolateForce(double travel)
    {
        if (travel <= Travel[0]) return Force[0];
        if (travel >= Travel[^1]) return Force[^1];

        int lo = 0;
        int hi = Travel.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (Travel[mid] <= travel) lo = mid; else hi = mid;
        }

        double t0 = Travel[lo], t1 = Travel[hi];
        double f0 = Force[lo], f1 = Force[hi];
        double w = (travel - t0) / (t1 - t0);
        return f0 + w * (f1 - f0);
    }
}

public enum SpringType { Air, Coil }

/// <summary>
/// Library of dyno curves for one suspension spring component (e.g. "fox-float-38-2023").
/// Holds curves at multiple (pressure, volume) configurations and bilinearly interpolates
/// between them for force evaluation at arbitrary configurations.
/// </summary>
public class SpringLibrary
{
    public string Id { get; }
    public string Manufacturer { get; }
    public string Model { get; }
    public int Year { get; }
    public SpringType Type { get; }
    public string DefaultAxle { get; }
    public (double Min, double Max) PressureRangePsi { get; }
    public (double Min, double Max) VolumeRangeCcm { get; }
    public string VolumespacerHint { get; }
    public IReadOnlyList<SpringCurve> Curves { get; }

    public SpringLibrary(
        string id, string manufacturer, string model, int year, SpringType type,
        string defaultAxle,
        (double, double) pressureRange, (double, double) volumeRange,
        string volumespacerHint,
        IReadOnlyList<SpringCurve> curves)
    {
        if (curves.Count == 0)
            throw new ArgumentException("SpringLibrary needs at least one curve.", nameof(curves));

        Id = id;
        Manufacturer = manufacturer;
        Model = model;
        Year = year;
        Type = type;
        DefaultAxle = defaultAxle;
        PressureRangePsi = pressureRange;
        VolumeRangeCcm = volumeRange;
        VolumespacerHint = volumespacerHint;
        Curves = curves;
    }

    public string DisplayName => $"{Manufacturer} {Model} {Year}";

    /// <summary>
    /// Evaluate spring force at arbitrary (travel, pressure, volume).
    /// Bilinear interpolation in (pressure, volume) selects between neighboring curves;
    /// linear interpolation in travel within the chosen curve. Outside measured range
    /// values are clamped to the nearest available curve (no extrapolation).
    /// </summary>
    public double EvaluateForce(double travel, double pressurePsi, double volumeCcm)
    {
        var pressures = Curves.Select(c => c.PressurePsi).Distinct().OrderBy(p => p).ToArray();
        var volumes = Curves.Select(c => c.VolumeCcm).Distinct().OrderBy(v => v).ToArray();

        var (pLo, pHi, pW) = FindBracket(pressures, pressurePsi);
        var (vLo, vHi, vW) = FindBracket(volumes, volumeCcm);

        double f00 = ForceAt(pLo, vLo, travel);
        double f10 = ForceAt(pHi, vLo, travel);
        double f01 = ForceAt(pLo, vHi, travel);
        double f11 = ForceAt(pHi, vHi, travel);

        double f0 = f00 + pW * (f10 - f00);
        double f1 = f01 + pW * (f11 - f01);
        return f0 + vW * (f1 - f0);
    }

    private static (double Lo, double Hi, double W) FindBracket(double[] values, double x)
    {
        if (x <= values[0]) return (values[0], values[0], 0.0);
        if (x >= values[^1]) return (values[^1], values[^1], 0.0);
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] >= x)
            {
                double lo = values[i - 1], hi = values[i];
                double w = (x - lo) / (hi - lo);
                return (lo, hi, w);
            }
        }
        return (values[^1], values[^1], 0.0);
    }

    private double ForceAt(double pressure, double volume, double travel)
    {
        SpringCurve? best = null;
        double bestDistance = double.MaxValue;
        foreach (var c in Curves)
        {
            double d = Math.Abs(c.PressurePsi - pressure) + Math.Abs(c.VolumeCcm - volume);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        return best!.InterpolateForce(travel);
    }
}
