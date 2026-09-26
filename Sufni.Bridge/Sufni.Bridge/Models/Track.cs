using System;
using SQLite;

namespace Sufni.Bridge.Models;

[Table("track")]
public class Track
{
    [PrimaryKey, Column("id")]
    public Guid Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("imported")]
    public int Imported { get; set; }

    [Column("start_time")]
    public long StartTimeMs { get; set; }

    [Column("end_time")]
    public long EndTimeMs { get; set; }

    [Column("points")]
    public byte[] Points { get; set; } = [];

    [Column("time_offset_ms")]
    public long TimeOffsetMs { get; set; }
}
