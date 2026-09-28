using System;
using System.Linq;
using Sufni.Bridge.Models.Telemetry;
using Xunit;

namespace Sufni.Bridge.Tests;

public class SignalConditioningTests
{
    [Fact]
    public void FillGapsLinear_InterpolatesAcrossInfinity()
    {
        // A calibration expression that divides by zero yields ±Infinity, not NaN.
        var x = new[] { 0.0, double.PositiveInfinity, 2.0, double.NegativeInfinity, double.NaN, 5.0 };

        Assert.True(SignalConditioning.FillGapsLinear(x));
        Assert.Equal(new[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0 }, x);
    }

    [Fact]
    public void FillGapsLinear_OnlyNonFiniteSamples_ReturnsFalse()
    {
        var x = new[] { double.PositiveInfinity, double.NaN };

        Assert.False(SignalConditioning.FillGapsLinear(x));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Smooth_SignalShorterThanOrder_PassesThrough(int length)
    {
        var smoother = new WhittakerHendersonSmoother(Parameters.WhOrder, Parameters.WhLambdaFor(1000));
        var data = Enumerable.Range(0, length).Select(i => (double)i * i).ToArray();

        smoother.EnsurePrepared(length);
        var smoothed = smoother.Smooth(data);

        Assert.Equal(data, smoothed);
        Assert.NotSame(data, smoothed);
    }

    [Fact]
    public void Smooth_ShortSignalAfterLongOne_StillSmoothsLongOnes()
    {
        var smoother = new WhittakerHendersonSmoother(Parameters.WhOrder, Parameters.WhLambdaFor(1000));
        var linear = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();

        smoother.Smooth([1.0, 2.0]);
        var smoothed = smoother.Smooth(linear);

        // A WH smoother of order >= 2 reproduces a straight line exactly.
        for (var i = 0; i < linear.Length; i++)
            Assert.Equal(linear[i], smoothed[i], 1e-6);
    }
}
