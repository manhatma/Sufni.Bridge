using System;

namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// Raw-domain signal conditioning applied BEFORE smoothing and differentiation. Defects must be
/// repaired here: once the Whittaker-Henderson smoother has run, a one-sample defect is spread
/// over ~10 samples and no longer looks isolated, so no velocity-domain test can find it again.
/// </summary>
public static class SignalConditioning
{
    /// <summary>
    /// Replaces NaN samples in place by linear interpolation between the nearest valid
    /// neighbours. Leading gaps take the first valid value, trailing gaps the last one.
    /// Holding the last value instead (zero-order hold) turns every gap into a plateau followed
    /// by a step, which the differentiator reads as a velocity spike roughly twice as high as
    /// the real motion across the gap. Returns false if the signal has no valid sample at all.
    /// </summary>
    public static bool FillGapsLinear(double[] x)
    {
        var lastValid = -1;
        for (var i = 0; i < x.Length; i++)
        {
            if (double.IsNaN(x[i])) continue;

            if (lastValid < 0)
            {
                for (var j = 0; j < i; j++) x[j] = x[i];
            }
            else if (i - lastValid > 1)
            {
                var from = x[lastValid];
                var span = i - lastValid;
                for (var j = lastValid + 1; j < i; j++)
                    x[j] = from + (x[i] - from) * (j - lastValid) / span;
            }

            lastValid = i;
        }

        if (lastValid < 0) return false;
        for (var j = lastValid + 1; j < x.Length; j++) x[j] = x[lastValid];
        return true;
    }

    /// <summary>
    /// Returns a copy of <paramref name="x"/> in which isolated one-sample outliers are replaced
    /// by the mean of their neighbours. A sample counts as an outlier only if it jumps away from
    /// BOTH neighbours in the same direction by more than <paramref name="threshold"/> while the
    /// two neighbours agree with each other within that threshold. Real suspension motion cannot
    /// leave and return to the same position within one sample period by that amount; a sharp
    /// real peak also has neighbours that agree, but its steps stay far below the threshold
    /// (see <see cref="Parameters.SpikeRepairThresholdLsb"/>). The input is never modified, and
    /// the input itself is returned when nothing is repaired.
    /// </summary>
    public static double[] RepairIsolatedSpikes(double[] x, double threshold)
    {
        if (x.Length < 3 || !(threshold > 0)) return x;

        double[]? repaired = null;
        for (var i = 1; i < x.Length - 1; i++)
        {
            var toPrev = x[i] - x[i - 1];
            var toNext = x[i] - x[i + 1];
            if (Math.Sign(toPrev) != Math.Sign(toNext)) continue;
            if (Math.Min(Math.Abs(toPrev), Math.Abs(toNext)) <= threshold) continue;
            if (Math.Abs(x[i + 1] - x[i - 1]) >= threshold) continue;

            repaired ??= (double[])x.Clone();
            repaired[i] = 0.5 * (x[i - 1] + x[i + 1]);
        }

        return repaired ?? x;
    }
}
