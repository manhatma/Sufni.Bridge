using System;
using System.Collections.Generic;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Models;

public enum TrackOverlayMetric
{
    GpsSpeed,
    FrontTravel,
    RearTravel,
    FrontCompression,
    RearCompression,
    Impact,
    Pitch
}

public static class TrackOverlayMetricInfo
{
    public static string DisplayName(TrackOverlayMetric metric) => metric switch
    {
        TrackOverlayMetric.GpsSpeed => "GPS Speed",
        TrackOverlayMetric.FrontTravel => "Front Travel",
        TrackOverlayMetric.RearTravel => "Rear Travel",
        TrackOverlayMetric.FrontCompression => "Front Compression",
        TrackOverlayMetric.RearCompression => "Rear Compression",
        TrackOverlayMetric.Impact => "Impact",
        TrackOverlayMetric.Pitch => "Pitch",
        _ => metric.ToString()
    };

    public static string Unit(TrackOverlayMetric metric) => metric switch
    {
        TrackOverlayMetric.GpsSpeed => "km/h",
        TrackOverlayMetric.FrontTravel => "%",
        TrackOverlayMetric.RearTravel => "%",
        TrackOverlayMetric.FrontCompression => "mm/s",
        TrackOverlayMetric.RearCompression => "mm/s",
        TrackOverlayMetric.Impact => "mm/s",
        TrackOverlayMetric.Pitch => "°",
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
        var list = new List<TrackOverlayMetric>(7);
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
        TrackOverlayMetric.FrontCompression
            => data?.Front.Present == true && data.Front.Velocity is { Length: > 0 },
        TrackOverlayMetric.RearCompression
            => data?.Rear.Present == true && data.Rear.Velocity is { Length: > 0 },
        // Impact is the harder of the two wheels, so it only means something when both recorded.
        // A single-wheel session already has that wheel's own compression overlay.
        TrackOverlayMetric.Impact
            => data?.Front.Present == true && data.Rear.Present
               && data.Front.Velocity is { Length: > 0 } && data.Rear.Velocity is { Length: > 0 },
        TrackOverlayMetric.Pitch
            => data?.Front.Present == true && data.Rear.Present
               && data.Front.Travel is { Length: > 0 } && data.Rear.Travel is { Length: > 0 }
               && data.Linkage?.Wheelbase > 0,
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

        double[]? pitch = metric == TrackOverlayMetric.Pitch ? data?.CalculatePitchDegrees() : null;

        for (var s = 0; s < track.Segments.Count; s++)
        {
            var segment = track.Segments[s];
            var n = Math.Max(0, segment.X.Length - 1);
            var values = new double[n];
            for (var i = 0; i < n; i++)
            {
                var v = SamplePair(segment, i, data, metric, pitch);
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
        else if (metric is TrackOverlayMetric.Pitch)
        {
            var mag = Math.Max(Math.Abs(min), Math.Abs(max));
            min = -mag;
            max = mag;
        }
        else if (IsCompression(metric))
        {
            // Anchor the scale at 0 so a smooth section always reads as the cold end of the
            // colormap; the top stays the session's hardest hit.
            min = 0;
            if (max < 0) max = 0;
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
        TrackSegment segment, int i, TelemetryData? data, TrackOverlayMetric metric, double[]? pitch)
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
            TrackOverlayMetric.FrontTravel => TravelPercent(data.Front.Travel, i0, i1, data.Linkage.MaxFrontTravel),
            TrackOverlayMetric.RearTravel => TravelPercent(data.Rear.Travel, i0, i1, data.Linkage.MaxRearTravel),
            TrackOverlayMetric.FrontCompression => MaxCompression(data.Front.Velocity, i0, i1),
            TrackOverlayMetric.RearCompression => MaxCompression(data.Rear.Velocity, i0, i1),
            TrackOverlayMetric.Impact => MaxImpact(data, i0, i1),
            TrackOverlayMetric.Pitch => MeanPitch(pitch, i0, i1),
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

    private static bool IsCompression(TrackOverlayMetric metric) =>
        metric is TrackOverlayMetric.FrontCompression
            or TrackOverlayMetric.RearCompression
            or TrackOverlayMetric.Impact;

    // Peak compression velocity over the edge's sample range. Rebound (negative velocity) is
    // ignored on purpose: a hard trail event is a fast compression, and averaging in the rebound
    // that follows blurs exactly the peak we want to see. The peak rather than a mean, because a
    // single big hit inside a one-second edge is the event — a mean would wash it out.
    // A range that only rebounds is a real 0, not missing data; NaN is reserved for a range with
    // no usable samples at all.
    private static double MaxCompression(double[]? values, int i0, int i1)
    {
        if (values is null || values.Length == 0)
            return double.NaN;
        i0 = Math.Clamp(i0, 0, values.Length - 1);
        i1 = Math.Clamp(i1, i0 + 1, values.Length);
        var peak = 0.0;
        var seen = false;
        for (var i = i0; i < i1; i++)
        {
            var v = values[i];
            if (!double.IsFinite(v)) continue;
            seen = true;
            if (v > peak) peak = v;
        }

        return seen ? peak : double.NaN;
    }

    private static double MaxImpact(TelemetryData data, int i0, int i1)
    {
        var front = MaxCompression(data.Front.Velocity, i0, i1);
        var rear = MaxCompression(data.Rear.Velocity, i0, i1);
        if (!double.IsFinite(front)) return rear;
        if (!double.IsFinite(rear)) return front;
        return Math.Max(front, rear);
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

    private static double TravelPercent(double[]? values, int i0, int i1, double maxTravel)
    {
        if (maxTravel <= 0) return double.NaN;
        var mean = MeanRange(values, i0, i1, abs: true);
        return double.IsFinite(mean) ? mean / maxTravel * 100.0 : double.NaN;
    }

    private static double MeanPitch(double[]? pitch, int i0, int i1)
    {
        if (pitch is null || pitch.Length == 0) return double.NaN;
        i0 = Math.Clamp(i0, 0, pitch.Length - 1);
        i1 = Math.Clamp(i1, i0 + 1, pitch.Length);
        double sum = 0; var n = 0;
        for (var i = i0; i < i1; i++) { var v = pitch[i]; if (!double.IsFinite(v)) continue; sum += v; n++; }
        return n == 0 ? double.NaN : sum / n;
    }
}
