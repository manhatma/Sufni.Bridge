using System;
using System.Text.Json.Serialization;
using SQLite;

namespace Sufni.Bridge.Models;

[Table("setup")]
public class Setup : Synchronizable
{
    // Just to satisfy sql-net-pcl's parameterless constructor requirement
    // Uninitialized non-nullable property warnings are suppressed with null! initializer.
    public Setup()
    {
    }

    public Setup(Guid id, string name, Guid linkageId, Guid? frontCalibrationId, Guid? rearCalibrationId)
    {
        Id = id;
        Name = name;
        LinkageId = linkageId;
        FrontCalibrationId = frontCalibrationId;
        RearCalibrationId = rearCalibrationId;
    }

    [JsonPropertyName("id")]
    [PrimaryKey]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    [Column("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("linkage_id")]
    [Column("linkage_id")]
    public Guid LinkageId { get; set; }

    [JsonPropertyName("front_calibration_id")]
    [Column("front_calibration_id")]
    public Guid? FrontCalibrationId { get; set; }

    [JsonPropertyName("rear_calibration_id")]
    [Column("rear_calibration_id")]
    public Guid? RearCalibrationId { get; set; }

    [JsonPropertyName("discipline")]
    [Column("discipline")]
    public Discipline Discipline { get; set; } = Discipline.Enduro;

    // Component IDs reference entries in the dyno-curve asset bundle (Assets/DynoCurves).
    // null = no component assigned → wheel-load metrics stay disabled for this setup.
    [JsonPropertyName("front_spring_component"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("front_spring_component")]
    public string? FrontSpringComponentId { get; set; }

    [JsonPropertyName("front_damper_component"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("front_damper_component")]
    public string? FrontDamperComponentId { get; set; }

    [JsonPropertyName("rear_spring_component"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("rear_spring_component")]
    public string? RearSpringComponentId { get; set; }

    [JsonPropertyName("rear_damper_component"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("rear_damper_component")]
    public string? RearDamperComponentId { get; set; }

    // Mass and IMU hooks for the planned IMU-fused force estimator (V2).
    // All optional; not used by the V1 spring/damper force estimator.
    [JsonPropertyName("front_unsprung_mass_kg"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("front_unsprung_mass_kg")]
    public double? FrontUnsprungMassKg { get; set; }

    [JsonPropertyName("rear_unsprung_mass_kg"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("rear_unsprung_mass_kg")]
    public double? RearUnsprungMassEffectiveKg { get; set; }

    [JsonPropertyName("rear_linkage_effectiveness"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("rear_linkage_effectiveness")]
    public double? RearLinkageEffectivenessFactor { get; set; }

    [JsonPropertyName("total_sprung_mass_kg"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("total_sprung_mass_kg")]
    public double? TotalSprungMassKg { get; set; }

    [JsonPropertyName("imu_config"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Column("imu_config")]
    public string? ImuConfigJson { get; set; }
}