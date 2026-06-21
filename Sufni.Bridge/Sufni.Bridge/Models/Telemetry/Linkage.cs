using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using CsvHelper;
using CsvHelper.Configuration;
using MathNet.Numerics;
using MathNet.Numerics.LinearRegression;
using MessagePack;
using SQLite;

namespace Sufni.Bridge.Models.Telemetry;

[Table("linkage")]
[MessagePackObject(keyAsPropertyName: true)]
public class Linkage : Synchronizable
{
    private LeverageRatioData? leverageRatioData;
    private double? maxFrontTravel;
    private double? maxRearTravel;
    private double[]? shockWheelCoeffs;

#pragma warning disable IDE0052 // Remove unread private members
                                // leverageRatio is used by the MessagePack deserializer
    private double[][]? leverageRatio;
#pragma warning restore IDE0052 // Remove unread private members

    // Just to satisfy sql-net-pcl's parameterless constructor requirement
    // Uninitialized non-nullable property warnings are suppressed with null! initializer.
    public Linkage() { }

    public Linkage(Guid id, string name, double headAngle, double? maxFrontStroke, double? maxRearStroke, double? wheelbase, string? rawData)
    {
        Id = id;
        Name = name;
        HeadAngle = headAngle;
        MaxFrontStroke = maxFrontStroke;
        MaxRearStroke = maxRearStroke;
        Wheelbase = wheelbase;
        RawData = rawData;
    }

    [JsonPropertyName("id")]
    [PrimaryKey]
    [Column("id")]
    [IgnoreMember]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    [Column("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("head_angle")]
    [Column("head_angle")]
    public double HeadAngle { get; set; }

    [JsonPropertyName("front_stroke")]
    [Column("front_stroke")]
    public double? MaxFrontStroke { get; set; }

    [JsonPropertyName("rear_stroke")]
    [Column("rear_stroke")]
    public double? MaxRearStroke { get; set; }

    [JsonPropertyName("wheelbase")]
    [Column("wheelbase")]
    public double? Wheelbase { get; set; }

    [JsonPropertyName("data")]
    [Column("raw_lr_data")]
    [IgnoreMember]
    public string? RawData { get; set; }

    [Ignore]
    [JsonIgnore]
    [IgnoreMember]
    public double MaxFrontTravel
    {
        get
        {
            maxFrontTravel ??= Math.Sin(HeadAngle * Math.PI / 180.0) * MaxFrontStroke ?? 0;
            return maxFrontTravel.Value;
        }
        set => maxFrontTravel = value;
    }

    [Ignore]
    [JsonIgnore]
    [IgnoreMember]
    public double MaxRearTravel
    {
        get
        {
            maxRearTravel ??= Polynomial.Evaluate(MaxRearStroke ?? 0);
            return maxRearTravel.Value;
        }
        set => maxRearTravel = value;
    }

    [Ignore]
    [JsonIgnore]
    [IgnoreMember]
    public double[] ShockWheelCoeffs
    {
        get
        {
            if (shockWheelCoeffs is null)
            {
                var shock = LeverageRatioData?.ShockTravel;
                var wheel = LeverageRatioData?.WheelTravel;
                if (shock is null || wheel is null || shock.Count < 4)
                {
                    // Not enough points for a cubic fit — return a no-op polynomial.
                    shockWheelCoeffs = [0, 0, 0, 0];
                }
                else
                {
                    // Intercept-free cubic least-squares fit (basis x, x², x³) so the
                    // shock→wheel curve passes exactly through (0,0). Zeroing the
                    // constant term of an *unconstrained* fit would instead shift the
                    // entire curve by c₀, biasing every rear-travel sample across the
                    // full stroke (the signal would never return to 0 during airtime).
                    var predictors = new double[shock.Count][];
                    for (var i = 0; i < shock.Count; i++)
                    {
                        var x = shock[i];
                        predictors[i] = [x, x * x, x * x * x];
                    }
                    var c = MultipleRegression.QR(predictors, wheel.ToArray(), intercept: false);
                    shockWheelCoeffs = [0, c[0], c[1], c[2]];
                }
            }
            return shockWheelCoeffs;
        }
        set => shockWheelCoeffs = value;
    }

    [Ignore][JsonIgnore][IgnoreMember] public Polynomial Polynomial => new(ShockWheelCoeffs);

    /// <summary>
    /// Converts wheel travel (mm) to damper/shock travel (mm) by numerically inverting
    /// the shock→wheel polynomial via binary search.
    /// </summary>
    public double WheelToDamperTravel(double wheelTravel)
    {
        var maxShock = MaxRearStroke ?? 0;
        if (maxShock <= 0) return 0;

        var lo = 0.0;
        var hi = maxShock;
        for (var i = 0; i < 50; i++)
        {
            var mid = (lo + hi) / 2.0;
            if (Polynomial.Evaluate(mid) < wheelTravel)
                lo = mid;
            else
                hi = mid;
        }

        return Math.Clamp((lo + hi) / 2.0, 0, maxShock);
    }

    [Ignore]
    [JsonIgnore]
    public double[][]? LeverageRatio
    {
        get => LeverageRatioData?.ToArray();
        set => leverageRatio = value;
    }

    [Ignore]
    [JsonIgnore]
    [IgnoreMember]
    public LeverageRatioData? LeverageRatioData
    {
        get
        {
            if (leverageRatioData is null && RawData is not null)
            {
                // Linkage.data does not have a header, it's just "wt,lr" pairs per line,
                // so we add the header so that LeverageFromCsv can process it.
                var csv = $"Wheel_T,Leverage_R\n{RawData}";
                leverageRatioData = new LeverageRatioData(new StringReader(csv));
            }
            else if (leverageRatioData is null && leverageRatio is not null)
            {
                // After MessagePack roundtrip RawData is gone (IgnoreMember), but the
                // leverageRatio field carries the [wheel, lr] pairs.
                leverageRatioData = new LeverageRatioData(leverageRatio);
            }

            return leverageRatioData;
        }
    }

}

public class LeverageRatioData
{
    public List<double> WheelTravel { get; init; }
    public List<double> LeverageRatio { get; init; }
    public List<double> ShockTravel { get; init; }

    private void ProcessWheelLeverageRatio(CsvReader reader)
    {
        var shock = 0.0;
        double? prevWheel = null;
        double? prevLeverage = null;
        while (reader.Read())
        {
            var wheel = reader.GetField<double>("Wheel_T");
            var leverage = reader.GetField<double>("Leverage_R");

            if (prevWheel.HasValue && prevLeverage is > 0)
            {
                shock += (wheel - prevWheel.Value) / prevLeverage.Value;
            }

            WheelTravel.Add(wheel);
            LeverageRatio.Add(leverage);
            ShockTravel.Add(shock);

            prevWheel = wheel;
            prevLeverage = leverage;
        }
    }

    private void ProcessWheelTravelShockTravel(CsvReader reader)
    {
        var idx = 0;

        while (reader.Read())
        {
            var shock = reader.GetField<double>("Shock_T");
            var wheel = reader.GetField<double>("Wheel_T");
            double lr = 0;

            if (idx > 0)
            {
                var sdiff = shock - ShockTravel[idx - 1];
                var wdiff = wheel - WheelTravel[idx - 1];
                lr = wdiff / sdiff;
                LeverageRatio[idx - 1] = lr;
            }

            ShockTravel.Add(shock);
            WheelTravel.Add(wheel);
            LeverageRatio.Add(lr);

            idx++;
        }
    }

    public LeverageRatioData(TextReader reader)
    {
        WheelTravel = [];
        LeverageRatio = [];
        ShockTravel = [];

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            AllowComments = true,
            Comment = '#'
        };

        using var csvReader = new CsvReader(reader, config);

        if (!csvReader.Read() || !csvReader.ReadHeader() || !csvReader.HeaderRecord!.Contains("Wheel_T"))
        {
            throw new Exception("Failed processing leverage data.");
        }

        if (csvReader.HeaderRecord!.Contains("Leverage_R"))
        {
            ProcessWheelLeverageRatio(csvReader);
        }

        if (csvReader.HeaderRecord!.Contains("Shock_T"))
        {
            ProcessWheelTravelShockTravel(csvReader);
        }
    }

    public LeverageRatioData(double[][] data)
    {
        WheelTravel = new List<double>(data.Length);
        LeverageRatio = new List<double>(data.Length);
        ShockTravel = new List<double>(data.Length);

        var shock = 0.0;
        double? prevWheel = null;
        double? prevLeverage = null;
        foreach (var d in data)
        {
            var wheel = d[0];
            var leverage = d[1];

            if (prevWheel.HasValue && prevLeverage is > 0)
            {
                shock += (wheel - prevWheel.Value) / prevLeverage.Value;
            }

            WheelTravel.Add(wheel);
            LeverageRatio.Add(leverage);
            ShockTravel.Add(shock);

            prevWheel = wheel;
            prevLeverage = leverage;
        }
    }

    public double[][] ToArray()
    {
        var data = new double[WheelTravel.Count][];
        for (var i = 0; i < WheelTravel.Count; i++)
        {
            data[i] = [WheelTravel[i], LeverageRatio[i]];
        }

        return data;
    }

    public override string ToString()
    {
        return string.Join("\n", WheelTravel.Zip(LeverageRatio, (wt, lr) =>
            string.Create(CultureInfo.InvariantCulture, $"{wt},{lr}")));
    }

    public override int GetHashCode()
    {
        return LeverageRatio.GetHashCode();
    }

    public override bool Equals(object? obj)
    {
        if (obj is LeverageRatioData other)
        {
            return WheelTravel.SequenceEqual(other.WheelTravel) &&
                   LeverageRatio.SequenceEqual(other.LeverageRatio);
        }

        return false;
    }
}