using System;

namespace Sufni.Bridge.Models.Telemetry;

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
