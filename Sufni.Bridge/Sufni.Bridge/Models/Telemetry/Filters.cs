using System;
using System.Collections.Generic;

namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// Replaces ADC-quantisation plateaus (consecutive identical samples that occur when
/// the underlying motion is slower than 1 LSB per sample) with a linear ramp between
/// the LSB transitions that bracket the plateau. This eliminates the "v ≈ 0 horizontal
/// segments" that the central-difference derivative produces from staircase position
/// data without affecting fast motion (no plateaus → input passes through unchanged).
///
/// A plateau is only interpolated when the closing transition step |x[t1] − x[t0]| is
/// below maxStepMm. Real quantisation steps are ≤ 1–2 LSB (~3–15 µm depending on sensor),
/// so the default 0.1 mm threshold accepts them with comfortable margin while rejecting
/// large jumps such as airtime→landing transitions (multi-mm) — those plateaus stay
/// untouched and airtime detection / phantom-velocity issues are avoided regardless of
/// plateau length.
/// </summary>
public static class PlateauInterpolator
{
    public static double[] Interpolate(double[] x, double maxStepMm = 0.1)
    {
        var n = x.Length;
        if (n < 2) return (double[])x.Clone();

        var y = (double[])x.Clone();

        // Indices where x[i] differs from x[i-1]; bracketed by sentinels at 0 and n.
        var transitions = new List<int> { 0 };
        for (var i = 1; i < n; i++)
        {
            if (x[i] != x[i - 1])
                transitions.Add(i);
        }
        transitions.Add(n);

        for (var k = 0; k < transitions.Count - 1; k++)
        {
            var t0 = transitions[k];
            var t1 = transitions[k + 1];
            if (t1 >= n) break;             // last segment has no closing transition
            var plateauLen = t1 - t0;
            if (plateauLen <= 1) continue;  // single sample, nothing to interpolate

            var v0 = x[t0];
            var v1 = x[t1];
            if (Math.Abs(v1 - v0) > maxStepMm) continue;  // large jump → not quantisation

            var span = (double)plateauLen;
            for (var j = t0 + 1; j < t1; j++)
            {
                var frac = (j - t0) / span;
                y[j] = v0 + frac * (v1 - v0);
            }
        }

        return y;
    }
}

public class WhittakerHendersonSmoother
{
    private static readonly double[][] DiffCoeffs =
    [
        [-1, 1],
        [1, -2, 1],
        [-1, 3, -3, 1],
        [1, -4, 6, -4, 1],
        [-1, 5, -10, 10, -5, 1],
    ];

    private readonly int order;
    private readonly double lambda;
    private double[][]? matrix;
    private int matrixLength;

    public WhittakerHendersonSmoother(int order, double lambda)
    {
        if (order < 1 || order > DiffCoeffs.Length)
            throw new ArgumentException($"Order must be between 1 and {DiffCoeffs.Length}");

        this.order = order;
        this.lambda = lambda;
    }

    public double[] Smooth(double[] data)
    {
        if (matrix == null || matrixLength != data.Length)
        {
            matrix = BuildCholeskyMatrix(data.Length);
            matrixLength = data.Length;
        }

        return Solve(matrix, data);
    }

    private double[][] BuildCholeskyMatrix(int size)
    {
        var b = MakeDPrimeD(size);
        TimesLambdaPlusIdent(b);
        Cholesky(b);
        return b;
    }

    private double[][] MakeDPrimeD(int size)
    {
        var coeffs = DiffCoeffs[order - 1];
        var b = new double[order + 1][];
        for (var d = 0; d <= order; d++)
        {
            b[d] = new double[size - d];
        }

        for (var d = 0; d <= order; d++)
        {
            var band = b[d];
            var bandLen = band.Length;

            for (var i = 0; i < (bandLen + 1) / 2; i++)
            {
                var jLower = Math.Max(0, i - bandLen + coeffs.Length - d);
                var jUpper = Math.Min(i + 1, coeffs.Length - d);
                var sum = 0.0;

                for (var j = jLower; j < jUpper; j++)
                {
                    sum += coeffs[j] * coeffs[j + d];
                }

                band[i] = sum;
                if (i != bandLen - 1 - i)
                {
                    band[bandLen - 1 - i] = sum;
                }
            }
        }

        return b;
    }

    private void TimesLambdaPlusIdent(double[][] b)
    {
        for (var i = 0; i < b[0].Length; i++)
        {
            b[0][i] = 1.0 + b[0][i] * lambda;
        }

        for (var d = 1; d < b.Length; d++)
        {
            for (var i = 0; i < b[d].Length; i++)
            {
                b[d][i] *= lambda;
            }
        }
    }

    private static void Cholesky(double[][] b)
    {
        var n = b[0].Length;
        var dmax = b.Length - 1;

        for (var i = 0; i < n; i++)
        {
            var jStart = Math.Max(0, i - dmax);
            for (var j = jStart; j <= i; j++)
            {
                var kLower = Math.Max(Math.Max(0, i - dmax), j - dmax);
                var sum = 0.0;

                for (var k = kLower; k < j; k++)
                {
                    sum += b[i - k][k] * b[j - k][k];
                }

                if (i == j)
                {
                    b[0][i] = Math.Sqrt(b[0][i] - sum);
                }
                else
                {
                    b[i - j][j] = (b[i - j][j] - sum) / b[0][j];
                }
            }
        }
    }

    private static double[] Solve(double[][] b, double[] vec)
    {
        var n = vec.Length;
        var dmax = b.Length - 1;
        var result = new double[n];

        // Forward substitution: L * y = vec
        for (var i = 0; i < n; i++)
        {
            var sum = 0.0;
            var jLower = Math.Max(0, i - dmax);
            for (var j = jLower; j < i; j++)
            {
                sum += b[i - j][j] * result[j];
            }
            result[i] = (vec[i] - sum) / b[0][i];
        }

        // Backward substitution: L' * x = y
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = 0.0;
            var jUpper = Math.Min(n, i + dmax + 1);
            for (var j = i + 1; j < jUpper; j++)
            {
                sum += b[j - i][i] * result[j];
            }
            result[i] = (result[i] - sum) / b[0][i];
        }

        return result;
    }
}
