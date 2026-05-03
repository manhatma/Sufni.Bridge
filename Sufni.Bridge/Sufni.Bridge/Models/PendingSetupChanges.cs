using System;
using SQLite;

namespace Sufni.Bridge.Models;

[Table("pending_setup_changes")]
public class PendingSetupChanges
{
    [PrimaryKey]
    [Column("setup_id")]
    public Guid SetupId { get; set; }

    [Column("front_springrate")] public string? FrontSpringRate { get; set; }
    [Column("front_volspc")] public double? FrontVolSpc { get; set; }
    [Column("front_hsc")] public uint? FrontHighSpeedCompression { get; set; }
    [Column("front_lsc")] public uint? FrontLowSpeedCompression { get; set; }
    [Column("front_lsr")] public uint? FrontLowSpeedRebound { get; set; }
    [Column("front_hsr")] public uint? FrontHighSpeedRebound { get; set; }
    [Column("front_tire_pressure")] public double? FrontTirePressure { get; set; }

    [Column("rear_springrate")] public string? RearSpringRate { get; set; }
    [Column("rear_volspc")] public double? RearVolSpc { get; set; }
    [Column("rear_hsc")] public uint? RearHighSpeedCompression { get; set; }
    [Column("rear_lsc")] public uint? RearLowSpeedCompression { get; set; }
    [Column("rear_lsr")] public uint? RearLowSpeedRebound { get; set; }
    [Column("rear_hsr")] public uint? RearHighSpeedRebound { get; set; }
    [Column("rear_tire_pressure")] public double? RearTirePressure { get; set; }

    [Column("updated")] public int Updated { get; set; }

    public bool IsEmpty =>
        FrontSpringRate is null && FrontVolSpc is null &&
        FrontHighSpeedCompression is null && FrontLowSpeedCompression is null &&
        FrontLowSpeedRebound is null && FrontHighSpeedRebound is null &&
        FrontTirePressure is null &&
        RearSpringRate is null && RearVolSpc is null &&
        RearHighSpeedCompression is null && RearLowSpeedCompression is null &&
        RearLowSpeedRebound is null && RearHighSpeedRebound is null &&
        RearTirePressure is null;
}
