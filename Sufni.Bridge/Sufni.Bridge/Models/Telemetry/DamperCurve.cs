using System;
using System.Collections.Generic;
using System.Linq;

namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// Click setting tuple for a damper. Unset values (null) match any click on that axis,
/// allowing partial-factorial dyno measurements (e.g. only LSC sweeps).
/// </summary>
public readonly record struct DamperClicks(int? Hsc, int? Lsc, int? Hsr, int? Lsr)
{
    public int ManhattanDistance(DamperClicks other)
    {
        int d = 0;
        if (Hsc.HasValue && other.Hsc.HasValue) d += Math.Abs(Hsc.Value - other.Hsc.Value);
        if (Lsc.HasValue && other.Lsc.HasValue) d += Math.Abs(Lsc.Value - other.Lsc.Value);
        if (Hsr.HasValue && other.Hsr.HasValue) d += Math.Abs(Hsr.Value - other.Hsr.Value);
        if (Lsr.HasValue && other.Lsr.HasValue) d += Math.Abs(Lsr.Value - other.Lsr.Value);
        return d;
    }
}

/// <summary>
/// Single dyno-measured damper force/velocity curve at a fixed click configuration.
/// Velocity and Force are signed (negative = rebound, positive = compression).
/// </summary>
public class DamperCurve
{
    public double[] Velocity { get; }
    public double[] Force { get; }
    public DamperClicks Clicks { get; }

    public DamperCurve(double[] velocity, double[] force, DamperClicks clicks)
    {
        if (velocity.Length != force.Length)
            throw new ArgumentException("Velocity and Force arrays must have equal length.");
        if (velocity.Length < 2)
            throw new ArgumentException("Curve needs at least two samples.");

        Velocity = velocity;
        Force = force;
        Clicks = clicks;
    }

    /// <summary>
    /// Linear interpolation in velocity. Outside the measured range the force is
    /// linearly extrapolated using the slope of the two outermost samples — damper
    /// force-velocity curves are roughly monotonic and locally linear at high |v|,
    /// so extrapolating is far closer to reality than clamping to the last measured
    /// point (which would drastically under-report damping during landings, hits,
    /// fast rebounds).
    /// </summary>
    public double InterpolateForce(double velocity)
    {
        if (velocity <= Velocity[0])
        {
            // Linear extrapolation from the first two samples.
            var slope = (Force[1] - Force[0]) / (Velocity[1] - Velocity[0]);
            return Force[0] + slope * (velocity - Velocity[0]);
        }
        if (velocity >= Velocity[^1])
        {
            var slope = (Force[^1] - Force[^2]) / (Velocity[^1] - Velocity[^2]);
            return Force[^1] + slope * (velocity - Velocity[^1]);
        }

        int lo = 0;
        int hi = Velocity.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (Velocity[mid] <= velocity) lo = mid; else hi = mid;
        }

        double v0 = Velocity[lo], v1 = Velocity[hi];
        double f0 = Force[lo], f1 = Force[hi];
        double w = (velocity - v0) / (v1 - v0);
        return f0 + w * (f1 - f0);
    }
}

public class ClickRange
{
    public int Min { get; init; }
    public int Max { get; init; }
}

/// <summary>
/// Library of dyno curves for one damper component. Picks the nearest measured
/// click configuration (Manhattan distance over set click axes) for a given
/// session config, then linearly interpolates in velocity. Full 4D interpolation
/// across click axes is not attempted because typical dyno data sets are not
/// full-factorial — picking the nearest configuration is the realistic choice.
/// </summary>
public class DamperLibrary
{
    public string Id { get; }
    public string Manufacturer { get; }
    public string Model { get; }
    public int Year { get; }
    public string DefaultAxle { get; }
    public ClickRange? HscRange { get; }
    public ClickRange? LscRange { get; }
    public ClickRange? HsrRange { get; }
    public ClickRange? LsrRange { get; }
    public IReadOnlyList<DamperCurve> Curves { get; }

    public DamperLibrary(
        string id, string manufacturer, string model, int year,
        string defaultAxle,
        ClickRange? hsc, ClickRange? lsc, ClickRange? hsr, ClickRange? lsr,
        IReadOnlyList<DamperCurve> curves)
    {
        if (curves.Count == 0)
            throw new ArgumentException("DamperLibrary needs at least one curve.", nameof(curves));

        Id = id;
        Manufacturer = manufacturer;
        Model = model;
        Year = year;
        DefaultAxle = defaultAxle;
        HscRange = hsc;
        LscRange = lsc;
        HsrRange = hsr;
        LsrRange = lsr;
        Curves = curves;
    }

    public string DisplayName => $"{Manufacturer} {Model} {Year}";

    /// <summary>
    /// Evaluate damper force at arbitrary velocity for the given click configuration.
    /// Uses inverse-distance-weighted (IDW) interpolation across all measured curves with
    /// weight w_i = 1/d_i² where d_i is the Manhattan click distance. So:
    ///   - Exact click match (d=0) → that curve is returned (short-circuit, avoids div/0).
    ///   - Single curve in the library → that curve is always used (trivial case).
    ///   - Multiple curves → near curves dominate, far ones contribute proportionally less,
    ///     producing a smooth force surface as the user tunes clicks instead of stepwise
    ///     jumps between nearest-neighbour curves.
    /// Power-2 weighting is the standard IDW choice — strong enough to localise around the
    /// nearest curve but smooth enough that 1-click changes translate to gentle force shifts.
    /// </summary>
    public double EvaluateForce(double velocity, DamperClicks clicks)
    {
        if (Curves.Count == 1) return Curves[0].InterpolateForce(velocity);

        double sumWeighted = 0, sumWeights = 0;
        foreach (var c in Curves)
        {
            int d = c.Clicks.ManhattanDistance(clicks);
            if (d == 0) return c.InterpolateForce(velocity);
            double w = 1.0 / (d * d);
            sumWeighted += w * c.InterpolateForce(velocity);
            sumWeights  += w;
        }
        return sumWeights > 0 ? sumWeighted / sumWeights : Curves[0].InterpolateForce(velocity);
    }
}
