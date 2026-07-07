using System;
using System.Collections.Generic;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Curve-faithful decimation for phase-portrait polylines (travel vs. velocity).
///
/// WHY: PositionVelocityPlot/PositionVelocityComparisonPlot plot every stroke sample —
/// up to ~1M points on long sessions. ScottPlot emits one SVG path vertex per point, so the
/// cached session SVGs reached 28.1 MB (comparison) / 14.2/14.0 MB (fork/damper), roughly
/// 68 MB per session_cache row, all re-parsed by SvgSource.LoadFromSvg every time a session
/// is opened. The rendered plot is only a few hundred pixels across, so almost every point
/// lands on the same on-screen pixel as its neighbour and contributes nothing visible.
///
/// HISTORY: an earlier version quantized points onto a pixel grid and, as a backstop for dense
/// noise that defeats grid dedup, collapsed each stride window to its first/last/X-Y-extreme
/// points. That backstop caps every window at 6 points regardless of the curve's actual shape,
/// so sparse strokes (few samples spread over a window) lost interior points and rendered as
/// visible straight-line polygons instead of the smooth loops the sensor data actually traces —
/// unacceptable on-device. Rejected in favour of the algorithm below, which has no fixed
/// point-per-window budget: it keeps exactly as many points as the curve's shape demands.
///
/// GUARANTEE: per-stroke-segment Ramer-Douglas-Peucker simplification in a normalized "virtual
/// pixel" space (see constants below). RDP keeps a point iff it deviates from the straight line
/// between its surviving neighbours by more than the tolerance — so every dropped point sat
/// within that tolerance of the drawn line, and every segment's first/last point is always kept
/// (RDP never discards segment endpoints). At the tolerance used here (0.4 virtual px = 0.1
/// logical px at the cached 393x350 render size), no dropped point can ever be visually
/// distinguishable from the line that replaces it, at any render size this app actually draws
/// (including 2x-scale PDF export, where it is still ~0.2 px). Segment boundaries (NaN
/// separators) are always preserved unchanged.
///
/// Inputs are the memoized PositionVelocityData arrays shared by reference across multiple
/// plots/consumers — this method must never mutate xs/ys, only return new arrays.
/// </summary>
internal static class PathDecimation
{
    // Virtual raster the tolerance below is defined against: 4x the cached render size (393x350
    // logical px), comfortably above larger renders (e.g. 786x700 PDF export) too, so the
    // permitted deviation stays sub-pixel at every size this app actually draws.
    private const double VirtualPixelWidth = 1572.0;   // 4 * 393
    private const double VirtualPixelHeight = 1400.0;  // 4 * 350

    // Maximum perpendicular deviation a dropped point may have had from the line that replaces
    // it, in virtual px on the raster above. 0.4 virtual px = 0.1 logical px at the cached
    // render size, ~0.2 px even at a 786x700 (2x) PDF-export render — sub-pixel everywhere.
    private const double Epsilon = 0.4;

    /// <summary>
    /// Decimates a travel/velocity polyline (NaN-separated segments) for plotting. Returns new
    /// arrays; never mutates the inputs. If nothing would be dropped, returns the original
    /// arrays unchanged (no pointless copy).
    /// </summary>
    internal static (double[] Xs, double[] Ys) DecimatePolyline(double[] xs, double[] ys)
    {
        var length = Math.Min(xs.Length, ys.Length);
        if (length == 0)
            return (xs, ys);

        // Single pass: finite min/max of both axes, to normalize onto the virtual raster.
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;
        for (var i = 0; i < length; i++)
        {
            var x = xs[i];
            var y = ys[i];
            if (double.IsNaN(x) || double.IsNaN(y)) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        if (double.IsPositiveInfinity(minX))
            return (xs, ys); // No finite points at all.

        var rangeX = maxX - minX;
        var rangeY = maxY - minY;
        // Guard zero range (e.g. constant travel) — collapse that axis to a single coordinate;
        // RDP deviation is then driven entirely by the other axis, as intended.
        var scaleX = rangeX > 0 ? VirtualPixelWidth / rangeX : 0.0;
        var scaleY = rangeY > 0 ? VirtualPixelHeight / rangeY : 0.0;

        var keep = new bool[length];
        var segmentStart = -1;
        for (var i = 0; i <= length; i++)
        {
            var isSeparator = i == length || double.IsNaN(xs[i]) || double.IsNaN(ys[i]);
            if (isSeparator)
            {
                if (segmentStart >= 0)
                    MarkKeptRdp(xs, ys, segmentStart, i - 1, scaleX, scaleY, keep);
                segmentStart = -1;
            }
            else if (segmentStart < 0)
            {
                segmentStart = i;
            }
        }

        var keptCount = 0;
        for (var i = 0; i < length; i++)
        {
            if (keep[i] || double.IsNaN(xs[i]) || double.IsNaN(ys[i])) keptCount++;
        }

        // Nothing dropped — return the original arrays untouched.
        if (keptCount == length)
            return (xs, ys);

        var outXs = new double[keptCount];
        var outYs = new double[keptCount];
        var o = 0;
        for (var i = 0; i < length; i++)
        {
            if (keep[i] || double.IsNaN(xs[i]) || double.IsNaN(ys[i]))
            {
                outXs[o] = xs[i];
                outYs[o] = ys[i];
                o++;
            }
        }
        return (outXs, outYs);
    }

    /// <summary>
    /// Ramer-Douglas-Peucker over a single NaN-free segment [lo, hi] (inclusive source indices).
    /// Marks <paramref name="keep"/> for every point that survives simplification; lo and hi
    /// (the segment's endpoints) are always kept. Iterative (explicit stack) rather than
    /// recursive so pathologically long, noisy segments can't blow the call stack.
    /// </summary>
    private static void MarkKeptRdp(double[] xs, double[] ys, int lo, int hi,
        double scaleX, double scaleY, bool[] keep)
    {
        keep[lo] = true;
        keep[hi] = true;
        if (hi - lo < 2)
            return; // No interior points to evaluate.

        var stack = new Stack<(int Lo, int Hi)>();
        stack.Push((lo, hi));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;

            var ax = xs[a] * scaleX;
            var ay = ys[a] * scaleY;
            var bx = xs[b] * scaleX;
            var by = ys[b] * scaleY;
            var dx = bx - ax;
            var dy = by - ay;
            var segLenSq = dx * dx + dy * dy;
            var segLen = Math.Sqrt(segLenSq);

            var maxDist = -1.0;
            var maxIndex = -1;
            for (var i = a + 1; i < b; i++)
            {
                var px = xs[i] * scaleX - ax;
                var py = ys[i] * scaleY - ay;
                double dist;
                if (segLen > 0)
                {
                    // Perpendicular distance from point to the chord a-b.
                    var cross = dx * py - dy * px;
                    dist = Math.Abs(cross) / segLen;
                }
                else
                {
                    // Degenerate chord (a and b coincide in virtual-pixel space) — distance is
                    // just the Euclidean distance to that shared point.
                    dist = Math.Sqrt(px * px + py * py);
                }

                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIndex = i;
                }
            }

            if (maxDist > Epsilon)
            {
                keep[maxIndex] = true;
                stack.Push((a, maxIndex));
                stack.Push((maxIndex, b));
            }
        }
    }
}
