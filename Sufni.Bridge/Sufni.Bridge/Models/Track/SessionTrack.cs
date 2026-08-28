using System;
using System.Collections.Generic;
using Sufni.Bridge.Plots;

namespace Sufni.Bridge.Models;

public readonly record struct WallClockSlice(long StartUnixMs, long DurationMs, double SessionStartSeconds);

public sealed class MapBounds
{
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public MapBounds Pad(double fraction)
    {
        var padX = Width * fraction;
        var padY = Height * fraction;
        if (padX <= 0) padX = 1;
        if (padY <= 0) padY = 1;
        return new MapBounds
        {
            MinX = MinX - padX,
            MinY = MinY - padY,
            MaxX = MaxX + padX,
            MaxY = MaxY + padY
        };
    }

    public const double DefaultPad = 0.30;

    /// <summary>
    /// Pads by <paramref name="fraction"/>, then expands the shorter axis symmetrically
    /// so that Width/Height == <paramref name="aspect"/> (surface width/height).
    /// Keeps the satellite image undistorted when the extent is stretched to the surface.
    /// </summary>
    public MapBounds PadAndFit(double fraction, double aspect)
    {
        var padded = Pad(fraction);
        var w = padded.Width;
        var h = padded.Height;
        if (w <= 0 || h <= 0 || aspect <= 0)
            return padded;

        var cx = (padded.MinX + padded.MaxX) / 2.0;
        var cy = (padded.MinY + padded.MaxY) / 2.0;
        var current = w / h;
        if (current < aspect)
        {
            var newW = h * aspect;
            return new MapBounds
            {
                MinX = cx - newW / 2.0, MaxX = cx + newW / 2.0,
                MinY = padded.MinY, MaxY = padded.MaxY
            };
        }

        var newH = w / aspect;
        return new MapBounds
        {
            MinX = padded.MinX, MaxX = padded.MaxX,
            MinY = cy - newH / 2.0, MaxY = cy + newH / 2.0
        };
    }

    /// <summary>Smallest bounds containing both inputs (per-axis min/max).</summary>
    public static MapBounds Union(MapBounds a, MapBounds b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY)
    };

    public static MapBounds FromPoints(IReadOnlyList<TrackSegment> segments)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var segment in segments)
        {
            for (var i = 0; i < segment.X.Length; i++)
            {
                var x = segment.X[i];
                var y = segment.Y[i];
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (double.IsPositiveInfinity(minX))
            return new MapBounds();

        return new MapBounds { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
    }
}

public sealed class ZoomWindow
{
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
}

public sealed class TrackSegment
{
    public double[] X { get; }
    public double[] Y { get; }
    public double[] TimeSeconds { get; }
    public double[] Lat { get; }
    public double[] Lon { get; }

    public TrackSegment(double[] x, double[] y, double[] timeSeconds, double[] lat, double[] lon)
    {
        X = x;
        Y = y;
        TimeSeconds = timeSeconds;
        Lat = lat;
        Lon = lon;
    }
}

public sealed class SessionTrack
{
    public static SessionTrack Empty { get; } = new([]);

    public IReadOnlyList<TrackSegment> Segments { get; }
    public MapBounds Bounds { get; }
    public bool IsEmpty => Segments.Count == 0;

    private SessionTrack(IReadOnlyList<TrackSegment> segments)
    {
        Segments = segments;
        Bounds = MapBounds.FromPoints(segments);
    }

    public static SessionTrack Build(
        TrackPoints points,
        IReadOnlyList<WallClockSlice> slices,
        long trackTimeOffsetMs = 0)
    {
        if (points.TimeMs.Length < 2 || slices.Count == 0)
            return Empty;

        // gpxTimeMs = sstWallClockMs + TimeOffsetMs, so SST slicing sees GPX times
        // shifted back. Copy TimeMs; the imported arrays stay on the GPX clock.
        if (trackTimeOffsetMs != 0)
        {
            var shifted = new long[points.TimeMs.Length];
            for (var i = 0; i < points.TimeMs.Length; i++)
                shifted[i] = points.TimeMs[i] - trackTimeOffsetMs;
            points = new TrackPoints
            {
                TimeMs = shifted,
                Lat = points.Lat,
                Lon = points.Lon,
                Ele = points.Ele
            };
        }

        var segments = new List<TrackSegment>();
        foreach (var slice in slices)
        {
            if (slice.DurationMs <= 0) continue;
            var segment = Slice(points, slice);
            if (segment is not null)
                segments.Add(SubdivideLongEdges(Decimate(segment)));
        }

        return segments.Count == 0 ? Empty : new SessionTrack(segments);
    }

    /// <summary>
    /// Smallest span a focus box may have, in Web Mercator metres. Without it a rider standing
    /// still inside the zoom window collapses the box to a few metres and the map zooms to a
    /// meaningless patch of upscaled pixels.
    /// </summary>
    public const double MinFocusSpanMeters = 60.0;

    /// <summary>
    /// Bounds of the part of the track that falls inside [startSeconds, endSeconds]. Edges are
    /// clipped in the time domain and their endpoints interpolated exactly like
    /// <see cref="Plots.TrackMapRenderer"/> does when it draws the zoomed sub-range, so the box
    /// matches the line the map actually paints. Returns null when no edge overlaps the range.
    /// </summary>
    public MapBounds? BoundsForWindow(double startSeconds, double endSeconds)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        void Include(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) return;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var segment in Segments)
        {
            var edges = Math.Max(0, segment.X.Length - 1);
            for (var i = 0; i < edges; i++)
            {
                var t0 = segment.TimeSeconds[i];
                var t1 = segment.TimeSeconds[i + 1];
                if (t1 <= t0) continue;
                var a = Math.Max(t0, startSeconds);
                var b = Math.Min(t1, endSeconds);
                if (b <= a) continue;

                var dx = segment.X[i + 1] - segment.X[i];
                var dy = segment.Y[i + 1] - segment.Y[i];
                var f0 = (a - t0) / (t1 - t0);
                var f1 = (b - t0) / (t1 - t0);
                Include(segment.X[i] + dx * f0, segment.Y[i] + dy * f0);
                Include(segment.X[i] + dx * f1, segment.Y[i] + dy * f1);
            }
        }

        if (double.IsPositiveInfinity(minX))
            return null;

        var (x0, x1) = AtLeast(minX, maxX, MinFocusSpanMeters);
        var (y0, y1) = AtLeast(minY, maxY, MinFocusSpanMeters);
        return new MapBounds { MinX = x0, MinY = y0, MaxX = x1, MaxY = y1 };

        static (double Lo, double Hi) AtLeast(double lo, double hi, double span)
        {
            if (hi - lo >= span) return (lo, hi);
            var centre = (lo + hi) / 2.0;
            return (centre - span / 2.0, centre + span / 2.0);
        }
    }

    public static SessionTrack FromSession(
        TrackPoints points,
        int startUnixSeconds,
        int durationSeconds,
        long trackTimeOffsetMs = 0)
    {
        return Build(points, [
            new WallClockSlice((long)startUnixSeconds * 1000, (long)durationSeconds * 1000, 0)
        ], trackTimeOffsetMs);
    }

    /// <summary>
    /// Web Mercator in meters. Same formula as sst/dashboard/app/telemetry/map.py:18-26.
    /// Returns easting (x), northing (y).
    /// </summary>
    public static bool TryToWebMercator(double lat, double lon, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (Math.Abs(lon) > 180 || Math.Abs(lat) >= 90)
            return false;

        const double degToRad = 0.017453292519943295;
        var num = lon * degToRad;
        x = 6378137.0 * num;
        var a = lat * degToRad;
        y = 3189068.5 * Math.Log((1.0 + Math.Sin(a)) / (1.0 - Math.Sin(a)));
        return true;
    }

    private static TrackSegment? Slice(TrackPoints points, WallClockSlice slice)
    {
        var times = points.TimeMs;
        var n = times.Length;
        var startMs = slice.StartUnixMs;
        var endMs = slice.StartUnixMs + slice.DurationMs;

        var firstInside = -1;
        var lastInside = -1;
        for (var i = 0; i < n; i++)
        {
            if (times[i] < startMs) continue;
            if (times[i] > endMs) break;
            if (firstInside < 0) firstInside = i;
            lastInside = i;
        }

        var before = firstInside >= 0 ? firstInside - 1 : LastIndexBefore(times, startMs);
        var after = lastInside >= 0 ? lastInside + 1 : FirstIndexAfter(times, endMs);
        if (after < n && times[after] <= endMs) after = lastInside + 1;

        if (firstInside < 0)
        {
            before = LastIndexBefore(times, startMs);
            after = FirstIndexAfter(times, endMs);
            if (before < 0 || after >= n)
                return null;
        }

        var xs = new List<double>();
        var ys = new List<double>();
        var ts = new List<double>();
        var lats = new List<double>();
        var lons = new List<double>();

        void Add(long timeMs, double lat, double lon)
        {
            if (!TryToWebMercator(lat, lon, out var x, out var y))
                return;
            xs.Add(x);
            ys.Add(y);
            ts.Add((timeMs - startMs) / 1000.0 + slice.SessionStartSeconds);
            lats.Add(lat);
            lons.Add(lon);
        }

        if (before >= 0 && (firstInside < 0 || times[firstInside] > startMs))
        {
            var afterIdx = firstInside >= 0 ? firstInside : after;
            if (afterIdx >= 0 && afterIdx < n && times[before] < startMs)
            {
                Interpolate(points, before, afterIdx, startMs, out var lat, out var lon);
                Add(startMs, lat, lon);
            }
        }

        if (firstInside >= 0)
        {
            for (var i = firstInside; i <= lastInside; i++)
                Add(times[i], points.Lat[i], points.Lon[i]);
        }

        var afterEdge = firstInside < 0 ? after : lastInside + 1;
        if (afterEdge < n && (lastInside < 0 || times[lastInside] < endMs))
        {
            var beforeIdx = lastInside >= 0 ? lastInside : before;
            if (beforeIdx >= 0 && times[afterEdge] > endMs)
            {
                Interpolate(points, beforeIdx, afterEdge, endMs, out var lat, out var lon);
                Add(endMs, lat, lon);
            }
        }

        return xs.Count < 2
            ? null
            : new TrackSegment(xs.ToArray(), ys.ToArray(), ts.ToArray(), lats.ToArray(), lons.ToArray());
    }

    private static int LastIndexBefore(long[] times, long t)
    {
        var idx = -1;
        for (var i = 0; i < times.Length; i++)
        {
            if (times[i] >= t) break;
            idx = i;
        }

        return idx;
    }

    private static int FirstIndexAfter(long[] times, long t)
    {
        for (var i = 0; i < times.Length; i++)
        {
            if (times[i] > t) return i;
        }

        return times.Length;
    }

    private static void Interpolate(TrackPoints points, int i0, int i1, long timeMs,
        out double lat, out double lon)
    {
        var t0 = points.TimeMs[i0];
        var t1 = points.TimeMs[i1];
        var span = t1 - t0;
        var frac = span == 0 ? 0 : (double)(timeMs - t0) / span;
        lat = points.Lat[i0] + frac * (points.Lat[i1] - points.Lat[i0]);
        lon = points.Lon[i0] + frac * (points.Lon[i1] - points.Lon[i0]);
    }

    private static TrackSegment Decimate(TrackSegment segment)
    {
        var (xs, ys, ts, lat, lon) = PathDecimation.DecimatePolyline(
            segment.X, segment.Y, segment.TimeSeconds, segment.Lat, segment.Lon);
        return new TrackSegment(xs, ys, ts, lat, lon);
    }

    // The overlay aggregates telemetry per polyline edge, so an edge is also the width of one
    // colour sample. Decimation merges collinear points and can stretch a single edge over
    // several seconds, which then shows the peak of that whole span next to one-second
    // neighbours. Splitting long edges back down to one second restores a uniform window.
    // Position, time and coordinates are interpolated linearly, which is accurate enough over
    // a few seconds. The cap keeps a long GPS dropout from exploding into hundreds of points.
    private static TrackSegment SubdivideLongEdges(TrackSegment segment, double maxSeconds = 1.0)
    {
        var n = segment.TimeSeconds.Length;
        if (n < 2)
            return segment;

        var split = false;
        for (var i = 0; i < n - 1; i++)
        {
            var probe = segment.TimeSeconds[i + 1] - segment.TimeSeconds[i];
            if (double.IsFinite(probe) && probe > maxSeconds && probe > 0)
            {
                split = true;
                break;
            }
        }

        if (!split)
            return segment;

        var xs = new List<double>();
        var ys = new List<double>();
        var ts = new List<double>();
        var lats = new List<double>();
        var lons = new List<double>();

        for (var i = 0; i < n - 1; i++)
        {
            xs.Add(segment.X[i]);
            ys.Add(segment.Y[i]);
            ts.Add(segment.TimeSeconds[i]);
            lats.Add(segment.Lat[i]);
            lons.Add(segment.Lon[i]);

            var dt = segment.TimeSeconds[i + 1] - segment.TimeSeconds[i];
            if (!double.IsFinite(dt) || dt <= maxSeconds || dt <= 0)
                continue;

            var parts = Math.Min(60, (int)Math.Ceiling(dt / maxSeconds));
            for (var p = 1; p < parts; p++)
            {
                var f = p / (double)parts;
                xs.Add(segment.X[i] + f * (segment.X[i + 1] - segment.X[i]));
                ys.Add(segment.Y[i] + f * (segment.Y[i + 1] - segment.Y[i]));
                ts.Add(segment.TimeSeconds[i] + f * dt);
                lats.Add(segment.Lat[i] + f * (segment.Lat[i + 1] - segment.Lat[i]));
                lons.Add(segment.Lon[i] + f * (segment.Lon[i + 1] - segment.Lon[i]));
            }
        }

        xs.Add(segment.X[n - 1]);
        ys.Add(segment.Y[n - 1]);
        ts.Add(segment.TimeSeconds[n - 1]);
        lats.Add(segment.Lat[n - 1]);
        lons.Add(segment.Lon[n - 1]);

        return new TrackSegment(xs.ToArray(), ys.ToArray(), ts.ToArray(), lats.ToArray(), lons.ToArray());
    }
}
