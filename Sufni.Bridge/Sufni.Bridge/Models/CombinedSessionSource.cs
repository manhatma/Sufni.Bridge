using System;
using SQLite;

namespace Sufni.Bridge.Models;

[Table("combined_session")]
public class CombinedSessionSource
{
    [Column("combined_id")]
    public Guid CombinedId { get; set; }

    [Column("source_id")]
    public Guid SourceId { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }
}
