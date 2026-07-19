using System;
using SQLite;

namespace Sufni.Bridge.Models;

[Table("session_cache")]
public class SessionCache
{
    [Column("session_id"), PrimaryKey] public Guid SessionId { get; set; }
    [Column("travel_comparison_histogram")] public string? TravelComparisonHistogram { get; set; }
    [Column("front_rear_travel_scatter")] public string? FrontRearTravelScatter { get; set; }
    [Column("front_travel_histogram")] public string? FrontTravelHistogram { get; set; }
    [Column("rear_travel_histogram")] public string? RearTravelHistogram { get; set; }
    [Column("front_velocity_histogram")] public string? FrontVelocityHistogram { get; set; }
    [Column("front_low_speed_velocity_histogram")] public string? FrontLowSpeedVelocityHistogram { get; set; }
    [Column("rear_velocity_histogram")] public string? RearVelocityHistogram { get; set; }
    [Column("rear_damper_velocity_histogram")] public string? RearDamperVelocityHistogram { get; set; }
    [Column("rear_low_speed_velocity_histogram")] public string? RearLowSpeedVelocityHistogram { get; set; }
    [Column("combined_balance")] public string? CombinedBalance { get; set; }
    [Column("compression_balance")] public string? CompressionBalance { get; set; }
    [Column("rebound_balance")] public string? ReboundBalance { get; set; }
    [Column("front_hsc_percentage")] public double? FrontHscPercentage { get; set; }
    [Column("rear_hsc_percentage")] public double? RearHscPercentage { get; set; }
    [Column("front_lsc_percentage")] public double? FrontLscPercentage { get; set; }
    [Column("rear_lsc_percentage")] public double? RearLscPercentage { get; set; }
    [Column("front_lsr_percentage")] public double? FrontLsrPercentage { get; set; }
    [Column("rear_lsr_percentage")] public double? RearLsrPercentage { get; set; }
    [Column("front_hsr_percentage")] public double? FrontHsrPercentage { get; set; }
    [Column("rear_hsr_percentage")] public double? RearHsrPercentage { get; set; }
    [Column("velocity_distribution_comparison")] public string? VelocityDistributionComparison { get; set; }
    [Column("position_velocity_comparison")] public string? PositionVelocityComparison { get; set; }
    [Column("front_position_velocity")] public string? FrontPositionVelocity { get; set; }
    [Column("rear_position_velocity")] public string? RearPositionVelocity { get; set; }
    [Column("summary_json")] public string? SummaryJson { get; set; }
    [Column("plot_version")] public int PlotVersion { get; set; }
    [Column("crop_start_sample")] public int? CropStartSample { get; set; }
    [Column("crop_end_sample")] public int? CropEndSample { get; set; }
    [Column("travel_time_history")] public string? TravelTimeHistory { get; set; }
    [Column("front_travel_time_cropped")] public string? FrontTravelTimeCropped { get; set; }
    [Column("rear_travel_time_cropped")] public string? RearTravelTimeCropped { get; set; }
    [Column("front_velocity_time_cropped")] public string? FrontVelocityTimeCropped { get; set; }
    [Column("rear_velocity_time_cropped")] public string? RearVelocityTimeCropped { get; set; }
    [Column("front_acceleration_time_cropped")] public string? FrontAccelerationTimeCropped { get; set; }
    [Column("rear_acceleration_time_cropped")] public string? RearAccelerationTimeCropped { get; set; }
    [Column("combined_travel_fft")] public string? CombinedTravelFft { get; set; }
    [Column("combined_travel_fft_high")] public string? CombinedTravelFftHigh { get; set; }
    [Column("combined_velocity_fft")] public string? CombinedVelocityFft { get; set; }
    [Column("pitch_balance")] public string? PitchBalance { get; set; }
    [Column("pitch_coherence")] public string? PitchCoherence { get; set; }
    [Column("gout_scatter")] public string? GoutScatter { get; set; }
    [Column("cumulative_travel")] public string? CumulativeTravel { get; set; }
    [Column("balance_metrics_json")] public string? BalanceMetricsJson { get; set; }
    // Input signature of the PitchBalance SVG (like plot_version/crop_*): the expected pitch
    // band baked into it. Balance-target overrides are edited per discipline, so LoadCache
    // compares this against the band implied by the CURRENT overrides and treats a mismatch
    // as a cache miss — otherwise the plot would contradict the live μ traffic light.
    [Column("pitch_expected_min_deg")] public double? PitchExpectedMinDeg { get; set; }
    [Column("pitch_expected_max_deg")] public double? PitchExpectedMaxDeg { get; set; }
    // Sample rate and FULL (uncropped) sample count of the session's telemetry, so the crop
    // slider bounds can be initialized without deserializing the multi-MB telemetry blob.
    // Nullable/0 on rows written before these columns existed — callers must fall back.
    [Column("sample_rate")] public int? SampleRate { get; set; }
    [Column("sample_count")] public int? SampleCount { get; set; }
}

/// <summary>
/// Scalar-only projection of a session_cache row. The full row carries ~30 columns of SVG
/// text (often tens of MB); this covers everything needed to decide staleness (plot version,
/// crop bounds, pitch-band signature) and to seed the crop slider, without materializing any
/// SVG. Fetched via IDatabaseService.GetSessionCacheMetaAsync.
/// </summary>
public class SessionCacheMeta
{
    [Column("plot_version")] public int PlotVersion { get; set; }
    [Column("crop_start_sample")] public int? CropStartSample { get; set; }
    [Column("crop_end_sample")] public int? CropEndSample { get; set; }
    [Column("pitch_expected_min_deg")] public double? PitchExpectedMinDeg { get; set; }
    [Column("pitch_expected_max_deg")] public double? PitchExpectedMaxDeg { get; set; }
    [Column("balance_metrics_json")] public string? BalanceMetricsJson { get; set; }
    [Column("sample_rate")] public int? SampleRate { get; set; }
    [Column("sample_count")] public int? SampleCount { get; set; }
    [Column("has_pitch_balance")] public bool HasPitchBalance { get; set; }
}
