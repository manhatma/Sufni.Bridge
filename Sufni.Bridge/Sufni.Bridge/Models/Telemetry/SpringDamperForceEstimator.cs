using System;
using System.Diagnostics;
using MathNet.Numerics;
using Sufni.Bridge.Services;

namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// V1 force estimator: derives wheel force purely from dyno-measured spring and damper
/// curves. Damper contribution is dropped silently when no damper component is assigned —
/// the spring force alone is still informative for the wheel-load metrics.
///
/// Available when:
///   - Setup has a SpringComponentId for the requested axis,
///   - that ID resolves to a loaded SpringLibrary,
///   - Session.{Front|Rear}SpringRate parses to a numeric value.
/// </summary>
public class SpringDamperForceEstimator : IForceEstimator
{
    private readonly DynoCurveLoader loader;

    public SpringDamperForceEstimator(DynoCurveLoader loader)
    {
        this.loader = loader;
    }

    public string Id => "spring-damper-csv";

    public bool IsAvailable(Models.Setup setup, Models.Session session, SuspensionType axis)
    {
        var (springId, _) = ComponentIds(setup, axis);
        if (string.IsNullOrEmpty(springId)) return false;
        if (loader.FindSpringLibrary(springId) is null) return false;

        var springRate = axis == SuspensionType.Front ? session.FrontSpringRate : session.RearSpringRate;
        if (!SpringRateParser.TryParse(springRate, out _, out var unit)) return false;

        // V1 only handles air-pressure inputs; reject coil entries up front.
        // An empty unit (legacy bare number) defaults to psi, so it stays available.
        return string.IsNullOrEmpty(unit) || SpringRateParser.IsAirUnit(unit);
    }

    public ForceData? Estimate(SuspensionType axis, TelemetryData telemetry,
                               Models.Setup setup, Models.Session session)
    {
        var (springId, damperId) = ComponentIds(setup, axis);
        if (string.IsNullOrEmpty(springId))
        {
            Debug.WriteLine($"[Force/{axis}] no spring component assigned");
            return null;
        }
        var spring = loader.FindSpringLibrary(springId);
        if (spring is null)
        {
            Debug.WriteLine($"[Force/{axis}] spring library '{springId}' not found in loader");
            return null;
        }
        var damper = loader.FindDamperLibrary(damperId);

        var sus = axis == SuspensionType.Front ? telemetry.Front : telemetry.Rear;
        if (!sus.Present || sus.Travel == null || sus.Velocity == null || sus.Travel.Length < 2)
        {
            Debug.WriteLine($"[Force/{axis}] suspension data missing (present={sus.Present}, n={sus.Travel?.Length ?? 0})");
            return null;
        }

        var (springRateRaw, volSpc, hsc, lsc, hsr, lsr) = SessionParams(session, axis);
        if (!SpringRateParser.TryParse(springRateRaw, out var sValue, out var sUnit))
        {
            Debug.WriteLine($"[Force/{axis}] spring rate '{springRateRaw}' not parsable");
            return null;
        }

        // Bare-number legacy entries return an empty unit. Treat as the default air unit (psi).
        if (string.IsNullOrEmpty(sUnit)) sUnit = SpringRateParser.UnitPsi;

        // V1 supports air pressure inputs only (psi/bar). Coil-rate inputs (N/mm, lbs/in)
        // would need a different evaluation path against a coil-spring CSV; bail out.
        if (!SpringRateParser.IsAirUnit(sUnit))
        {
            Debug.WriteLine($"[Force/{axis}] coil rate ('{sUnit}') not supported by V1 estimator");
            return null;
        }

        var pressurePsi = SpringRateParser.ToPsi(sValue, sUnit);
        Debug.WriteLine($"[Force/{axis}] computing: spring={springId}, p={pressurePsi:0.0} psi, vol={volSpc ?? 0:0.00} cc, samples={sus.Travel.Length}");
        var volumeCcm = volSpc ?? 0.0;
        var clicks = new DamperClicks(
            (int?)hsc, (int?)lsc, (int?)hsr, (int?)lsr);

        // Pre-compute the leverage-ratio derivative once. The polynomial maps
        // shock travel → wheel travel; its derivative gives LR = dWheel/dShock.
        var lrPoly = Derivative(telemetry.Linkage.Polynomial);
        var sinHa = Math.Sin(telemetry.Linkage.HeadAngle * Math.PI / 180.0);

        int n = sus.Travel.Length;
        var forces = new double[n];
        for (int i = 0; i < n; i++)
        {
            var wheelTravel = sus.Travel[i];
            var wheelVel = sus.Velocity[i];

            double shockTravel, shockVel, dShockDWheel;
            if (axis == SuspensionType.Front)
            {
                // Linear projection: shock-axis is a fixed fraction of vertical wheel travel.
                shockTravel = wheelTravel * sinHa;
                shockVel = wheelVel * sinHa;
                dShockDWheel = sinHa;
            }
            else
            {
                // Rear linkage: invert the polynomial for shock travel, evaluate LR there.
                shockTravel = telemetry.Linkage.WheelToDamperTravel(wheelTravel);
                var lr = lrPoly.Evaluate(shockTravel);
                if (lr <= 0) lr = 1.0;
                shockVel = wheelVel / lr;
                dShockDWheel = 1.0 / lr;
            }

            var fSpring = spring.EvaluateForce(shockTravel, pressurePsi, volumeCcm);
            var fDamper = damper?.EvaluateForce(shockVel, clicks) ?? 0.0;
            var fShock = fSpring + fDamper;
            forces[i] = fShock * dShockDWheel;
        }

        var staticForce = Median(forces);

        // Personalised flat-floor reference: the wheel load this setup would produce if the
        // bike sat statically at the centre of its sag target band. Independent of the
        // trail profile — purely a function of spring + linkage + sag target.
        // Sag-target midpoints match BalancePageViewModel: front 25.5 %, rear 30.5 %.
        double targetSagFraction = axis == SuspensionType.Front ? 0.255 : 0.305;
        var maxWheelTravel = axis == SuspensionType.Front
            ? telemetry.Linkage.MaxFrontTravel
            : telemetry.Linkage.MaxRearTravel;
        double flatRef = ComputeStaticForce(axis, targetSagFraction * maxWheelTravel,
            spring, pressurePsi, volumeCcm, telemetry.Linkage, lrPoly, sinHa);

        return new ForceData
        {
            WheelForce = forces,
            StaticForce = staticForce,
            StaticForceFlat = flatRef,
        };
    }

    /// <summary>
    /// Spring-only wheel force at a fixed wheel travel — used for the flat-floor reference.
    /// Velocity is taken as zero, so the damper contribution drops out and the result is
    /// the pure quasi-static Spring force at the requested travel.
    /// </summary>
    private static double ComputeStaticForce(SuspensionType axis, double wheelTravelMm,
        SpringLibrary spring, double pressurePsi, double volumeCcm,
        Linkage linkage, Polynomial lrPoly, double sinHa)
    {
        double shockTravel, dShockDWheel;
        if (axis == SuspensionType.Front)
        {
            shockTravel = wheelTravelMm * sinHa;
            dShockDWheel = sinHa;
        }
        else
        {
            shockTravel = linkage.WheelToDamperTravel(wheelTravelMm);
            var lr = lrPoly.Evaluate(shockTravel);
            if (lr <= 0) lr = 1.0;
            dShockDWheel = 1.0 / lr;
        }
        var fSpring = spring.EvaluateForce(shockTravel, pressurePsi, volumeCcm);
        return fSpring * dShockDWheel;
    }

    private static (string? Spring, string? Damper) ComponentIds(Models.Setup s, SuspensionType axis) =>
        axis == SuspensionType.Front
            ? (s.FrontSpringComponentId, s.FrontDamperComponentId)
            : (s.RearSpringComponentId, s.RearDamperComponentId);

    private static (string?, double?, uint?, uint?, uint?, uint?) SessionParams(Models.Session s, SuspensionType axis) =>
        axis == SuspensionType.Front
            ? (s.FrontSpringRate, s.FrontVolSpc,
               s.FrontHighSpeedCompression, s.FrontLowSpeedCompression,
               s.FrontHighSpeedRebound, s.FrontLowSpeedRebound)
            : (s.RearSpringRate, s.RearVolSpc,
               s.RearHighSpeedCompression, s.RearLowSpeedCompression,
               s.RearHighSpeedRebound, s.RearLowSpeedRebound);

    /// <summary>Analytic derivative of a polynomial: c[i]·x^i → c[i]·i·x^(i-1).</summary>
    private static Polynomial Derivative(Polynomial p)
    {
        var c = p.Coefficients;
        if (c.Length <= 1) return new Polynomial(0.0);
        var d = new double[c.Length - 1];
        for (int i = 1; i < c.Length; i++) d[i - 1] = c[i] * i;
        return new Polynomial(d);
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy.Length % 2 == 1
            ? copy[copy.Length / 2]
            : 0.5 * (copy[copy.Length / 2 - 1] + copy[copy.Length / 2]);
    }
}
