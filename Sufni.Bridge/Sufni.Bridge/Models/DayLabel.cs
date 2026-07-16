using SQLite;

namespace Sufni.Bridge.Models;

/// <summary>
/// A user-defined text label attached to a calendar day, shown alongside the date in the
/// session list's day-group header (e.g. "12.07.2026 · Finale Ligure"). Keyed by the ISO
/// date string rather than a synthetic id, since there is at most one label per day.
/// </summary>
[Table("day_label")]
public class DayLabel
{
    // ISO "yyyy-MM-dd" — sortable and unambiguous across locales.
    [PrimaryKey, Column("date")] public string Date { get; set; } = "";

    [Column("label")] public string Label { get; set; } = "";
}
