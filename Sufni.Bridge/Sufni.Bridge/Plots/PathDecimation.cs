using System;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Grid-based decimation for phase-portrait polylines (travel vs. velocity).
///
/// WHY: PositionVelocityPlot/PositionVelocityComparisonPlot plot every stroke sample —
/// up to ~1M points on long sessions. ScottPlot emits one SVG path vertex per point, so the
/// cached session SVGs reached 28.1 MB (comparison) / 14.2/14.0 MB (fork/damper), roughly
/// 68 MB per session_cache row, all re-parsed by SvgSource.LoadFromSvg every time a session
/// is opened. The rendered plot is only a few hundred pixels across, so almost every point
/// lands on the same on-screen pixel as its neighbour and contributes nothing visible.
///
/// GUARANTEE: this only drops points whose (quantized) grid cell is identical to the last
/// KEPT point's cell within the same stroke segment — i.e. points that would land on the same
/// pixel at (and well beyond) the actual render resolution. Segment boundaries (NaN separators)
/// and each segment's first/last finite point are always preserved, so stroke topology and the
/// data extents (and therefore axis limits computed from the full arrays) are unaffected. The
/// output is visually equivalent to the input at any of this app's render sizes.
///
/// Inputs are the memoized PositionVelocityData arrays shared by reference across multiple
/// plots/consumers — this method must never mutate xs/ys, only return new arrays.
/// </summary>
internal static class PathDecimation
{
    // Quantization grid: comfortably above both the cached render (393x350ish logical px)
    // and larger PDF-export renders, so dedup is imperceptible at any size we actually draw.
    private const int GridColumns = 1600;
    private const int GridRows = 1200;

    // Output cap. Phase-portrait velocity noise defeats consecutive-cell dedup (nearly every
    // sample lands in a new cell), so the windowed-extremes pass below does the real reduction:
    // it collapses each stride window to at most 6 ordered points (first, x/y extremes, last),
    // which keeps the drawn envelope — and therefore the global extents any axis autoscaling
    // derives from the plotted data — exact, while dense loop regions stay visually saturated.
    private const int MaxKeptPoints = 48_000;
    private const int PointsPerWindow = 6;

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

        // Single pass: finite min/max of both axes.
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
        // Guard zero range (e.g. constant travel) — collapse that axis to one cell.
        var scaleX = rangeX > 0 ? GridColumns / rangeX : 0.0;
        var scaleY = rangeY > 0 ? GridRows / rangeY : 0.0;

        var keptXs = new double[length];
        var keptYs = new double[length];
        var kept = 0;

        var lastCellX = int.MinValue;
        var lastCellY = int.MinValue;
        var segmentStart = 0; // index into keptXs/keptYs of the current segment's first kept point.
        var haveSegmentPoint = false;

        void CloseSegment(int lastFiniteSourceIndex)
        {
            if (!haveSegmentPoint) return;
            // Always keep the segment's final finite point, even if it shares a cell with the
            // last kept point (guarantees exact extents/endpoint fidelity per stroke).
            if (kept == segmentStart || keptXs[kept - 1] != xs[lastFiniteSourceIndex] || keptYs[kept - 1] != ys[lastFiniteSourceIndex])
            {
                keptXs[kept] = xs[lastFiniteSourceIndex];
                keptYs[kept] = ys[lastFiniteSourceIndex];
                kept++;
            }
            haveSegmentPoint = false;
        }

        var lastFiniteIndex = -1;
        for (var i = 0; i < length; i++)
        {
            var x = xs[i];
            var y = ys[i];
            if (double.IsNaN(x) || double.IsNaN(y))
            {
                CloseSegment(lastFiniteIndex);
                lastFiniteIndex = -1;
                // Emit exactly one NaN pair between segments, matching the input separator.
                if (kept > 0 && !(double.IsNaN(keptXs[kept - 1]) && double.IsNaN(keptYs[kept - 1])))
                {
                    keptXs[kept] = double.NaN;
                    keptYs[kept] = double.NaN;
                    kept++;
                }
                lastCellX = int.MinValue;
                lastCellY = int.MinValue;
                segmentStart = kept;
                continue;
            }

            var cellX = rangeX > 0 ? (int)((x - minX) * scaleX) : 0;
            var cellY = rangeY > 0 ? (int)((y - minY) * scaleY) : 0;

            if (!haveSegmentPoint)
            {
                // Always keep the segment's first point.
                keptXs[kept] = x;
                keptYs[kept] = y;
                kept++;
                haveSegmentPoint = true;
                lastCellX = cellX;
                lastCellY = cellY;
            }
            else if (cellX != lastCellX || cellY != lastCellY)
            {
                keptXs[kept] = x;
                keptYs[kept] = y;
                kept++;
                lastCellX = cellX;
                lastCellY = cellY;
            }

            lastFiniteIndex = i;
        }
        CloseSegment(lastFiniteIndex);

        // Nothing dropped — return the original arrays untouched.
        if (kept == length)
            return (xs, ys);

        if (kept > MaxKeptPoints)
            return StridePerSegment(keptXs, keptYs, kept);

        Array.Resize(ref keptXs, kept);
        Array.Resize(ref keptYs, kept);
        return (keptXs, keptYs);
    }

    /// <summary>
    /// Backstop for pathological inputs where grid dedup alone still leaves too many points
    /// (e.g. noise that keeps toggling between two adjacent cells). Walks each segment in
    /// stride-sized windows and keeps every window's X/Y extremes (in original order) plus the
    /// segment's first/last point and the NaN separators — so the drawn envelope, and therefore
    /// any axis limits derived from the plotted data, never shrinks.
    /// </summary>
    private static (double[] Xs, double[] Ys) StridePerSegment(double[] xs, double[] ys, int length)
    {
        // Window size such that windowCount * PointsPerWindow stays within the cap.
        var stride = (int)Math.Ceiling((double)PointsPerWindow * length / MaxKeptPoints);
        if (stride < 1) stride = 1;

        // Each input index is picked by at most one window and separators map 1:1,
        // so `length` is a safe upper bound; the arrays are resized down at the end.
        var outXs = new double[length];
        var outYs = new double[length];
        var count = 0;

        void Keep(int index)
        {
            if (count > 0 && outXs[count - 1] == xs[index] && outYs[count - 1] == ys[index]) return;
            outXs[count] = xs[index];
            outYs[count] = ys[index];
            count++;
        }

        var segStart = 0;
        for (var i = 0; i <= length; i++)
        {
            var isSeparator = i == length || (double.IsNaN(xs[i]) && double.IsNaN(ys[i]));
            if (!isSeparator) continue;

            var lastIndex = i - 1;
            for (var w = segStart; w <= lastIndex; w += stride)
            {
                var wEnd = Math.Min(w + stride - 1, lastIndex);
                // Window extremes, emitted in original point order (indices sorted below).
                int minXi = w, maxXi = w, minYi = w, maxYi = w;
                for (var j = w + 1; j <= wEnd; j++)
                {
                    if (xs[j] < xs[minXi]) minXi = j;
                    if (xs[j] > xs[maxXi]) maxXi = j;
                    if (ys[j] < ys[minYi]) minYi = j;
                    if (ys[j] > ys[maxYi]) maxYi = j;
                }
                Span<int> picks = [w, minXi, maxXi, minYi, maxYi, wEnd];
                picks.Sort();
                foreach (var p in picks) Keep(p);
            }

            if (i < length)
            {
                outXs[count] = double.NaN;
                outYs[count] = double.NaN;
                count++;
            }
            segStart = i + 1;
        }

        Array.Resize(ref outXs, count);
        Array.Resize(ref outYs, count);
        return (outXs, outYs);
    }
}
