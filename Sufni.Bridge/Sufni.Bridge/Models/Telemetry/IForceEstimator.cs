namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// Per-axis dynamic wheel-force time series produced by a force estimator.
/// </summary>
public class ForceData
{
    /// <summary>Sample-aligned wheel-force in Newton (signed; ground-pressing forces are positive).</summary>
    public double[] WheelForce { get; init; } = [];
    /// <summary>Static wheel load at sag in Newton. Used as the reference for normalised metrics.</summary>
    public double StaticForce { get; init; }
}

/// <summary>
/// Strategy interface for estimating dynamic wheel forces from a session's telemetry.
/// V1 implementation: <c>SpringDamperForceEstimator</c> derived purely from spring + damper
/// dyno curves. Planned V2 implementation: an IMU-fused estimator that resolves the unsprung
/// mass equation m·a = F_spring + F_damper − F_z to recover ground-reaction force.
/// </summary>
public interface IForceEstimator
{
    /// <summary>Stable identifier (e.g. "spring-damper-csv", "imu-fused").</summary>
    string Id { get; }

    /// <summary>True if the setup + session carry all inputs the estimator needs.</summary>
    bool IsAvailable(Sufni.Bridge.Models.Setup setup, Sufni.Bridge.Models.Session session, SuspensionType axis);

    /// <summary>Computes wheel force for one axis. Returns null if inputs are insufficient.</summary>
    ForceData? Estimate(SuspensionType axis, TelemetryData telemetry,
                        Sufni.Bridge.Models.Setup setup,
                        Sufni.Bridge.Models.Session session);
}
