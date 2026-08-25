using System;
using System.Collections.Generic;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Models;

public enum TrackOverlayMetric
{
    GpsSpeed,
    FrontTravel,
    RearTravel,
    FrontVelocity,
    RearVelocity,
    Balance
}

public static class TrackOverlayMetricInfo
{
    public static string DisplayName(TrackOverlayMetric metric) => metric switch
    {
        TrackOverlayMetric.GpsSpeed => "GPS Speed",
        TrackOverlayMetric.FrontTravel => "Front Travel",
        TrackOverlayMetric.RearTravel => "Rear Travel",
        TrackOverlayMetric.FrontVelocity => "Front Velocity",
        TrackOverlayMetric.RearVelocity => "Rear Velocity",
        TrackOverlayMetric.Balance => "Balance",
        _ => metric.ToString()
    };

    public static string Unit(TrackOverlayMetric metric) => metric switch
    {
        TrackOverlayMetric.GpsSpeed => "km/h",
        TrackOverlayMetric.FrontTravel => "mm",
        TrackOverlayMetric.RearTravel => "mm",
        TrackOverlayMetric.FrontVelocity => "mm/s",
        TrackOverlayMetric.RearVelocity => "mm/s",
        TrackOverlayMetric.Balance => "mm",
        _ => ""
    };
}

public sealed class TrackOverlayMetricOption(TrackOverlayMetric metric)
{
    public TrackOverlayMetric Metric { get; } = metric;
    public string DisplayName { get; } = TrackOverlayMetricInfo.DisplayName(metric);
}

/// <summary>
/// Per-edge overlay values for a session track. <see cref="SegmentPairValues"/>[s][i]
/// is the metric for the polyline edge from point i to i+1 of segment s.
/// Min/Max cover the whole session, not the zoom window.
/// </summary>
public sealed class TrackOverlay
{
    public required TrackOverlayMetric Metric { get; init; }
    public required string Label { get; init; }
    public required string Unit { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required IReadOnlyList<double[]> SegmentPairValues { get; init; }
}

public static class TrackOverlaySampler
{
    private const double EarthRadiusMeters = 6371000.0;
    private const double DegToRad = Math.PI / 180.0;

    public static IReadOnlyList<TrackOverlayMetric> AvailableMetrics(TelemetryData? data)
    {
        var list = new List<TrackOverlayMetric>(6);
        foreach (var metric in Enum.GetValues<TrackOverlayMetric>())
        {
            if (IsAvailable(metric, data))
                list.Add(metric);
        }

        return list;
    }

    public static bool IsAvailable(TrackOverlayMetric metric, TelemetryData? data) => metric switch
    {
        TrackOverlayMetric.GpsSpeed => true,
        TrackOverlayMetric.FrontTravel
            => data?.Front.Present == true && data.Front.Travel is { Length: > 0 },
        TrackOverlayMetric.RearTravel
            => data?.Rear.Present == true && data.Rear.Travel is { Length: > 0 },
        TrackOverlayMetric.FrontVelocity
            => data?.Front.Present == true && data.Front.Velocity is { Length: > 0 },
        TrackOverlayMetric.RearVelocity
            => data?.Rear.Present == true && data.Rear.Velocity is { Length: > 0 },
        TrackOverlayMetric.Balance
            => data?.Front.Present == true && data.Rear.Present
               && data.Front.Travel is { Length: > 0 } && data.Rear.Travel is { Length: > 0 },
        _ => false
    };

    public static TrackOverlay? Build(SessionTrack track, TelemetryData? data, TrackOverlayMetric metric)
    {
        if (track.IsEmpty)
            return null;
        if (!IsAvailable(metric, data))
            return null;

        var pairValues = new double[track.Segments.Count][];
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;

        for (var s = 0; s < track.Segments.Count; s++)
        {
            var segment = track.Segments[s];
            var n = Math.Max(0, segment.X.Length - 1);
            var values = new double[n];
            for (var i = 0; i < n; i++)
            {
                var v = SamplePair(segment, i, data, metric);
                values[i] = v;
                if (!double.IsFinite(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            pairValues[s] = values;
        }

        if (double.IsPositiveInfinity(min))
        {
            min = 0;
            max = 0;
        }
        else if (metric is TrackOverlayMetric.FrontVelocity
                 or TrackOverlayMetric.RearVelocity
                 or TrackOverlayMetric.Balance)
        {
            var mag = Math.Max(Math.Abs(min), Math.Abs(max));
            min = -mag;
            max = mag;
        }

        return new TrackOverlay
        {
            Metric = metric,
            Label = TrackOverlayMetricInfo.DisplayName(metric),
            Unit = TrackOverlayMetricInfo.Unit(metric),
            Min = min,
            Max = max,
            SegmentPairValues = pairValues
        };
    }

    private static double SamplePair(
        TrackSegment segment, int i, TelemetryData? data, TrackOverlayMetric metric)
    {
        if (metric == TrackOverlayMetric.GpsSpeed)
            return GpsSpeedKmh(segment, i);

        if (data is null || data.SampleRate <= 0)
            return double.NaN;

        var t0 = segment.TimeSeconds[i];
        var t1 = segment.TimeSeconds[i + 1];
        var i0 = (int)Math.Floor(t0 * data.SampleRate);
        var i1 = (int)Math.Ceiling(t1 * data.SampleRate);
        if (i1 <= i0) i1 = i0 + 1;

        return metric switch
        {
            TrackOverlayMetric.FrontTravel => MeanRange(data.Front.Travel, i0, i1, abs: true),
            TrackOverlayMetric.RearTravel => MeanRange(data.Rear.Travel, i0, i1, abs: true),
            TrackOverlayMetric.FrontVelocity => MeanRange(data.Front.Velocity, i0, i1, abs: false),
            TrackOverlayMetric.RearVelocity => MeanRange(data.Rear.Velocity, i0, i1, abs: false),
            TrackOverlayMetric.Balance => MeanDiff(data.Front.Travel, data.Rear.Travel, i0, i1),
            _ => double.NaN
        };
    }

    private static double GpsSpeedKmh(TrackSegment segment, int i)
    {
        if (segment.Lat.Length <= i + 1 || segment.Lon.Length <= i + 1)
            return double.NaN;
        var dt = segment.TimeSeconds[i + 1] - segment.TimeSeconds[i];
        if (dt <= 0)
            return double.NaN;
        var meters = HaversineMeters(
            segment.Lat[i], segment.Lon[i],
            segment.Lat[i + 1], segment.Lon[i + 1]);
        if (!double.IsFinite(meters))
            return double.NaN;
        return meters / dt * 3.6;
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = lat1 * DegToRad;
        var phi2 = lat2 * DegToRad;
        var dPhi = (lat2 - lat1) * DegToRad;
        var dLam = (lon2 - lon1) * DegToRad;
        var sinDPhi = Math.Sin(dPhi / 2.0);
        var sinDLam = Math.Sin(dLam / 2.0);
        var a = sinDPhi * sinDPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinDLam * sinDLam;
        return 2.0 * EarthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
    }

    private static double MeanRange(double[]? values, int i0, int i1, bool abs)
    {
        if (values is null || values.Length == 0)
            return double.NaN;
        i0 = Math.Clamp(i0, 0, values.Length - 1);
        i1 = Math.Clamp(i1, i0 + 1, values.Length);
        double sum = 0;
        var n = 0;
        for (var i = i0; i < i1; i++)
        {
            var v = values[i];
            if (!double.IsFinite(v)) continue;
            sum += abs ? Math.Abs(v) : v;
            n++;
        }

        return n == 0 ? double.NaN : sum / n;
    }

    private static double MeanDiff(double[]? front, double[]? rear, int i0, int i1)
    {
        if (front is null || rear is null || front.Length == 0 || rear.Length == 0)
            return double.NaN;
        var length = Math.Min(front.Length, rear.Length);
        i0 = Math.Clamp(i0, 0, length - 1);
        i1 = Math.Clamp(i1, i0 + 1, length);
        double sum = 0;
        var n = 0;
        for (var i = i0; i < i1; i++)
        {
            var a = front[i];
            var b = rear[i];
            if (!double.IsFinite(a) || !double.IsFinite(b)) continue;
            sum += a - b;
            n++;
        }

        return n == 0 ? double.NaN : sum / n;
    }
}
