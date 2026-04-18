using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Time-history line chart of front and rear acceleration (in g) for the cropped session slice.
/// Acceleration is the numerical derivative of the stored velocity array, converted from mm/s² to g.
/// </summary>
public class AccelerationTimeCroppedPlot(Plot plot) : TelemetryPlot(plot)
{
    private const double GravityMmPerS2 = 9806.65;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Acceleration over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Acceleration (g)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;

        double maxDuration = 0;
        double aMax = double.NegativeInfinity;
        double aMin = double.PositiveInfinity;
        double? frontRms = null;
        double? rearRms = null;

        static double Rms(double[] a)
        {
            if (a.Length == 0) return 0;
            double sum = 0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * a[i];
            return Math.Sqrt(sum / a.Length);
        }

        double[] ToAcceleration(double[] v)
        {
            var n = v.Length;
            var a = new double[n];
            if (n < 2) return a;
            a[0] = (v[1] - v[0]) * sampleRate / GravityMmPerS2;
            for (int i = 1; i < n - 1; i++)
                a[i] = (v[i + 1] - v[i - 1]) * sampleRate / 2.0 / GravityMmPerS2;
            a[n - 1] = (v[n - 1] - v[n - 2]) * sampleRate / GravityMmPerS2;
            return a;
        }

        void Track(double[] a)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] > aMax) aMax = a[i];
                if (a[i] < aMin) aMin = a[i];
            }
        }

        if (telemetryData.Front.Present && telemetryData.Front.Velocity is { Length: > 0 })
        {
            var accFront = ToAcceleration(telemetryData.Front.Velocity);
            var sig = Plot.Add.Signal(accFront, period);
            sig.Color = FrontColor;
            sig.LineWidth = 1;
            maxDuration = Math.Max(maxDuration, accFront.Length * period);
            Track(accFront);
            frontRms = Rms(accFront);
        }

        if (telemetryData.Rear.Present && telemetryData.Rear.Velocity is { Length: > 0 })
        {
            var accRear = ToAcceleration(telemetryData.Rear.Velocity);
            var sig = Plot.Add.Signal(accRear, period);
            sig.Color = RearColor;
            sig.LineWidth = 1;
            maxDuration = Math.Max(maxDuration, accRear.Length * period);
            Track(accRear);
            rearRms = Rms(accRear);
        }

        if (double.IsInfinity(aMax)) { aMax = 1; aMin = -1; }
        var span = Math.Max(aMax - aMin, 1e-9);
        var top    = aMax + span * 0.05;
        var bottom = aMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (maxDuration > 0)
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Legend — upper-left corner
        var range = top - bottom;
        var frontLegend = Plot.Add.Text("Front", 0, top);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperLeft;
        frontLegend.LabelOffsetX = 6;
        frontLegend.LabelOffsetY = 6;

        var rearLegend = Plot.Add.Text("Rear", 0, top - range * 0.08);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperLeft;
        rearLegend.LabelOffsetX = 6;
        rearLegend.LabelOffsetY = 6;

        // RMS readout — upper-right corner, same stacking/formatting style as the legend
        var rightX = maxDuration > 0 ? maxDuration : 1.0;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (frontRms is not null)
        {
            var rmsFront = Plot.Add.Text(string.Format(culture, "rms = {0:0.0}", frontRms.Value), rightX, top);
            rmsFront.LabelFontColor = FrontColor;
            rmsFront.LabelFontSize = 12;
            rmsFront.LabelAlignment = Alignment.UpperRight;
            rmsFront.LabelOffsetX = -6;
            rmsFront.LabelOffsetY = 6;
        }
        if (rearRms is not null)
        {
            var rearY = frontRms is not null ? top - range * 0.08 : top;
            var rmsRear = Plot.Add.Text(string.Format(culture, "rms = {0:0.0}", rearRms.Value), rightX, rearY);
            rmsRear.LabelFontColor = RearColor;
            rmsRear.LabelFontSize = 12;
            rmsRear.LabelAlignment = Alignment.UpperRight;
            rmsRear.LabelOffsetX = -6;
            rmsRear.LabelOffsetY = 6;
        }
    }
}
