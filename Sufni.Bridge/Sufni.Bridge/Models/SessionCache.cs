using System;
using SQLite;

namespace Sufni.Bridge.Models;

/// <summary>
/// Cached render artifacts of a session. The SVG plot columns dominate the row size
/// (~14 MB of text per session), so they are stored gzip-compressed as BLOBs: each
/// [Column] "*Stored" property holds the bytes sqlite-net reads/writes, and its [Ignore]
/// string twin exposes the plot text, packing on set and lazily unpacking (memoized) on
/// get. Rows written before compression hold plain UTF-8 text; CompressedText falls back
/// on the missing gzip magic, so both row generations stay readable. The *Stored
/// properties are only written by sqlite-net during row materialization (before any text
/// access) — setting one later would not invalidate an already-decoded text value.
/// summary_json/balance_metrics_json stay plain TEXT: they are small, and the latter is
/// read as a string by the SessionCacheMeta staleness probe.
/// </summary>
[Table("session_cache")]
public class SessionCache
{
    [Column("session_id"), PrimaryKey] public Guid SessionId { get; set; }

    [Column("travel_comparison_histogram")] public byte[]? TravelComparisonHistogramStored { get; set; }
    [Ignore] public string? TravelComparisonHistogram
    {
        get => _travelComparisonHistogram ??= CompressedText.Unpack(TravelComparisonHistogramStored);
        set { _travelComparisonHistogram = value; TravelComparisonHistogramStored = CompressedText.Pack(value); }
    }
    private string? _travelComparisonHistogram;

    [Column("front_rear_travel_scatter")] public byte[]? FrontRearTravelScatterStored { get; set; }
    [Ignore] public string? FrontRearTravelScatter
    {
        get => _frontRearTravelScatter ??= CompressedText.Unpack(FrontRearTravelScatterStored);
        set { _frontRearTravelScatter = value; FrontRearTravelScatterStored = CompressedText.Pack(value); }
    }
    private string? _frontRearTravelScatter;

    [Column("front_travel_histogram")] public byte[]? FrontTravelHistogramStored { get; set; }
    [Ignore] public string? FrontTravelHistogram
    {
        get => _frontTravelHistogram ??= CompressedText.Unpack(FrontTravelHistogramStored);
        set { _frontTravelHistogram = value; FrontTravelHistogramStored = CompressedText.Pack(value); }
    }
    private string? _frontTravelHistogram;

    [Column("rear_travel_histogram")] public byte[]? RearTravelHistogramStored { get; set; }
    [Ignore] public string? RearTravelHistogram
    {
        get => _rearTravelHistogram ??= CompressedText.Unpack(RearTravelHistogramStored);
        set { _rearTravelHistogram = value; RearTravelHistogramStored = CompressedText.Pack(value); }
    }
    private string? _rearTravelHistogram;

    [Column("front_velocity_histogram")] public byte[]? FrontVelocityHistogramStored { get; set; }
    [Ignore] public string? FrontVelocityHistogram
    {
        get => _frontVelocityHistogram ??= CompressedText.Unpack(FrontVelocityHistogramStored);
        set { _frontVelocityHistogram = value; FrontVelocityHistogramStored = CompressedText.Pack(value); }
    }
    private string? _frontVelocityHistogram;

    [Column("front_low_speed_velocity_histogram")] public byte[]? FrontLowSpeedVelocityHistogramStored { get; set; }
    [Ignore] public string? FrontLowSpeedVelocityHistogram
    {
        get => _frontLowSpeedVelocityHistogram ??= CompressedText.Unpack(FrontLowSpeedVelocityHistogramStored);
        set { _frontLowSpeedVelocityHistogram = value; FrontLowSpeedVelocityHistogramStored = CompressedText.Pack(value); }
    }
    private string? _frontLowSpeedVelocityHistogram;

    [Column("rear_velocity_histogram")] public byte[]? RearVelocityHistogramStored { get; set; }
    [Ignore] public string? RearVelocityHistogram
    {
        get => _rearVelocityHistogram ??= CompressedText.Unpack(RearVelocityHistogramStored);
        set { _rearVelocityHistogram = value; RearVelocityHistogramStored = CompressedText.Pack(value); }
    }
    private string? _rearVelocityHistogram;

    [Column("rear_damper_velocity_histogram")] public byte[]? RearDamperVelocityHistogramStored { get; set; }
    [Ignore] public string? RearDamperVelocityHistogram
    {
        get => _rearDamperVelocityHistogram ??= CompressedText.Unpack(RearDamperVelocityHistogramStored);
        set { _rearDamperVelocityHistogram = value; RearDamperVelocityHistogramStored = CompressedText.Pack(value); }
    }
    private string? _rearDamperVelocityHistogram;

    [Column("rear_low_speed_velocity_histogram")] public byte[]? RearLowSpeedVelocityHistogramStored { get; set; }
    [Ignore] public string? RearLowSpeedVelocityHistogram
    {
        get => _rearLowSpeedVelocityHistogram ??= CompressedText.Unpack(RearLowSpeedVelocityHistogramStored);
        set { _rearLowSpeedVelocityHistogram = value; RearLowSpeedVelocityHistogramStored = CompressedText.Pack(value); }
    }
    private string? _rearLowSpeedVelocityHistogram;

    [Column("combined_balance")] public byte[]? CombinedBalanceStored { get; set; }
    [Ignore] public string? CombinedBalance
    {
        get => _combinedBalance ??= CompressedText.Unpack(CombinedBalanceStored);
        set { _combinedBalance = value; CombinedBalanceStored = CompressedText.Pack(value); }
    }
    private string? _combinedBalance;

    [Column("compression_balance")] public byte[]? CompressionBalanceStored { get; set; }
    [Ignore] public string? CompressionBalance
    {
        get => _compressionBalance ??= CompressedText.Unpack(CompressionBalanceStored);
        set { _compressionBalance = value; CompressionBalanceStored = CompressedText.Pack(value); }
    }
    private string? _compressionBalance;

    [Column("rebound_balance")] public byte[]? ReboundBalanceStored { get; set; }
    [Ignore] public string? ReboundBalance
    {
        get => _reboundBalance ??= CompressedText.Unpack(ReboundBalanceStored);
        set { _reboundBalance = value; ReboundBalanceStored = CompressedText.Pack(value); }
    }
    private string? _reboundBalance;

    [Column("front_hsc_percentage")] public double? FrontHscPercentage { get; set; }
    [Column("rear_hsc_percentage")] public double? RearHscPercentage { get; set; }
    [Column("front_lsc_percentage")] public double? FrontLscPercentage { get; set; }
    [Column("rear_lsc_percentage")] public double? RearLscPercentage { get; set; }
    [Column("front_lsr_percentage")] public double? FrontLsrPercentage { get; set; }
    [Column("rear_lsr_percentage")] public double? RearLsrPercentage { get; set; }
    [Column("front_hsr_percentage")] public double? FrontHsrPercentage { get; set; }
    [Column("rear_hsr_percentage")] public double? RearHsrPercentage { get; set; }

    [Column("velocity_distribution_comparison")] public byte[]? VelocityDistributionComparisonStored { get; set; }
    [Ignore] public string? VelocityDistributionComparison
    {
        get => _velocityDistributionComparison ??= CompressedText.Unpack(VelocityDistributionComparisonStored);
        set { _velocityDistributionComparison = value; VelocityDistributionComparisonStored = CompressedText.Pack(value); }
    }
    private string? _velocityDistributionComparison;

    [Column("position_velocity_comparison")] public byte[]? PositionVelocityComparisonStored { get; set; }
    [Ignore] public string? PositionVelocityComparison
    {
        get => _positionVelocityComparison ??= CompressedText.Unpack(PositionVelocityComparisonStored);
        set { _positionVelocityComparison = value; PositionVelocityComparisonStored = CompressedText.Pack(value); }
    }
    private string? _positionVelocityComparison;

    [Column("front_position_velocity")] public byte[]? FrontPositionVelocityStored { get; set; }
    [Ignore] public string? FrontPositionVelocity
    {
        get => _frontPositionVelocity ??= CompressedText.Unpack(FrontPositionVelocityStored);
        set { _frontPositionVelocity = value; FrontPositionVelocityStored = CompressedText.Pack(value); }
    }
    private string? _frontPositionVelocity;

    [Column("rear_position_velocity")] public byte[]? RearPositionVelocityStored { get; set; }
    [Ignore] public string? RearPositionVelocity
    {
        get => _rearPositionVelocity ??= CompressedText.Unpack(RearPositionVelocityStored);
        set { _rearPositionVelocity = value; RearPositionVelocityStored = CompressedText.Pack(value); }
    }
    private string? _rearPositionVelocity;

    [Column("summary_json")] public string? SummaryJson { get; set; }
    [Column("plot_version")] public int PlotVersion { get; set; }
    [Column("crop_start_sample")] public int? CropStartSample { get; set; }
    [Column("crop_end_sample")] public int? CropEndSample { get; set; }

    [Column("travel_time_history")] public byte[]? TravelTimeHistoryStored { get; set; }
    [Ignore] public string? TravelTimeHistory
    {
        get => _travelTimeHistory ??= CompressedText.Unpack(TravelTimeHistoryStored);
        set { _travelTimeHistory = value; TravelTimeHistoryStored = CompressedText.Pack(value); }
    }
    private string? _travelTimeHistory;

    [Column("front_travel_time_cropped")] public byte[]? FrontTravelTimeCroppedStored { get; set; }
    [Ignore] public string? FrontTravelTimeCropped
    {
        get => _frontTravelTimeCropped ??= CompressedText.Unpack(FrontTravelTimeCroppedStored);
        set { _frontTravelTimeCropped = value; FrontTravelTimeCroppedStored = CompressedText.Pack(value); }
    }
    private string? _frontTravelTimeCropped;

    [Column("rear_travel_time_cropped")] public byte[]? RearTravelTimeCroppedStored { get; set; }
    [Ignore] public string? RearTravelTimeCropped
    {
        get => _rearTravelTimeCropped ??= CompressedText.Unpack(RearTravelTimeCroppedStored);
        set { _rearTravelTimeCropped = value; RearTravelTimeCroppedStored = CompressedText.Pack(value); }
    }
    private string? _rearTravelTimeCropped;

    [Column("front_velocity_time_cropped")] public byte[]? FrontVelocityTimeCroppedStored { get; set; }
    [Ignore] public string? FrontVelocityTimeCropped
    {
        get => _frontVelocityTimeCropped ??= CompressedText.Unpack(FrontVelocityTimeCroppedStored);
        set { _frontVelocityTimeCropped = value; FrontVelocityTimeCroppedStored = CompressedText.Pack(value); }
    }
    private string? _frontVelocityTimeCropped;

    [Column("rear_velocity_time_cropped")] public byte[]? RearVelocityTimeCroppedStored { get; set; }
    [Ignore] public string? RearVelocityTimeCropped
    {
        get => _rearVelocityTimeCropped ??= CompressedText.Unpack(RearVelocityTimeCroppedStored);
        set { _rearVelocityTimeCropped = value; RearVelocityTimeCroppedStored = CompressedText.Pack(value); }
    }
    private string? _rearVelocityTimeCropped;

    [Column("front_acceleration_time_cropped")] public byte[]? FrontAccelerationTimeCroppedStored { get; set; }
    [Ignore] public string? FrontAccelerationTimeCropped
    {
        get => _frontAccelerationTimeCropped ??= CompressedText.Unpack(FrontAccelerationTimeCroppedStored);
        set { _frontAccelerationTimeCropped = value; FrontAccelerationTimeCroppedStored = CompressedText.Pack(value); }
    }
    private string? _frontAccelerationTimeCropped;

    [Column("rear_acceleration_time_cropped")] public byte[]? RearAccelerationTimeCroppedStored { get; set; }
    [Ignore] public string? RearAccelerationTimeCropped
    {
        get => _rearAccelerationTimeCropped ??= CompressedText.Unpack(RearAccelerationTimeCroppedStored);
        set { _rearAccelerationTimeCropped = value; RearAccelerationTimeCroppedStored = CompressedText.Pack(value); }
    }
    private string? _rearAccelerationTimeCropped;

    [Column("combined_travel_fft")] public byte[]? CombinedTravelFftStored { get; set; }
    [Ignore] public string? CombinedTravelFft
    {
        get => _combinedTravelFft ??= CompressedText.Unpack(CombinedTravelFftStored);
        set { _combinedTravelFft = value; CombinedTravelFftStored = CompressedText.Pack(value); }
    }
    private string? _combinedTravelFft;

    [Column("combined_travel_fft_high")] public byte[]? CombinedTravelFftHighStored { get; set; }
    [Ignore] public string? CombinedTravelFftHigh
    {
        get => _combinedTravelFftHigh ??= CompressedText.Unpack(CombinedTravelFftHighStored);
        set { _combinedTravelFftHigh = value; CombinedTravelFftHighStored = CompressedText.Pack(value); }
    }
    private string? _combinedTravelFftHigh;

    [Column("combined_velocity_fft")] public byte[]? CombinedVelocityFftStored { get; set; }
    [Ignore] public string? CombinedVelocityFft
    {
        get => _combinedVelocityFft ??= CompressedText.Unpack(CombinedVelocityFftStored);
        set { _combinedVelocityFft = value; CombinedVelocityFftStored = CompressedText.Pack(value); }
    }
    private string? _combinedVelocityFft;

    [Column("pitch_balance")] public byte[]? PitchBalanceStored { get; set; }
    [Ignore] public string? PitchBalance
    {
        get => _pitchBalance ??= CompressedText.Unpack(PitchBalanceStored);
        set { _pitchBalance = value; PitchBalanceStored = CompressedText.Pack(value); }
    }
    private string? _pitchBalance;

    [Column("pitch_coherence")] public byte[]? PitchCoherenceStored { get; set; }
    [Ignore] public string? PitchCoherence
    {
        get => _pitchCoherence ??= CompressedText.Unpack(PitchCoherenceStored);
        set { _pitchCoherence = value; PitchCoherenceStored = CompressedText.Pack(value); }
    }
    private string? _pitchCoherence;

    [Column("gout_scatter")] public byte[]? GoutScatterStored { get; set; }
    [Ignore] public string? GoutScatter
    {
        get => _goutScatter ??= CompressedText.Unpack(GoutScatterStored);
        set { _goutScatter = value; GoutScatterStored = CompressedText.Pack(value); }
    }
    private string? _goutScatter;

    [Column("cumulative_travel")] public byte[]? CumulativeTravelStored { get; set; }
    [Ignore] public string? CumulativeTravel
    {
        get => _cumulativeTravel ??= CompressedText.Unpack(CumulativeTravelStored);
        set { _cumulativeTravel = value; CumulativeTravelStored = CompressedText.Pack(value); }
    }
    private string? _cumulativeTravel;

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
