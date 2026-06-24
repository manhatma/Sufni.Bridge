using SQLite;

namespace Sufni.Bridge.Models;

/// <summary>
/// A user override of a balance-metric's green (good) target range, stored per discipline.
/// Only the green range is persisted; the yellow (acceptable) band is derived from it at
/// display time (see BalanceTargetDefaults). Single-cutoff metrics use <see cref="GreenMin"/>
/// only and leave <see cref="GreenMax"/> null.
/// </summary>
[Table("balance_target_override")]
public class BalanceTargetOverride
{
    // sql-net-pcl has no composite-PK support, so we encode (discipline, metric) into a single
    // synthetic key and upsert via InsertOrReplaceAsync. The individual columns are kept for
    // querying by discipline.
    [PrimaryKey, Column("id")] public string Id { get; set; } = "";

    [Column("discipline")] public Discipline Discipline { get; set; }
    [Column("metric_key")] public string MetricKey { get; set; } = "";
    [Column("green_min")] public double? GreenMin { get; set; }
    [Column("green_max")] public double? GreenMax { get; set; }
    [Column("updated")] public int Updated { get; set; }

    public static string MakeId(Discipline discipline, string metricKey) => $"{(int)discipline}:{metricKey}";
}
