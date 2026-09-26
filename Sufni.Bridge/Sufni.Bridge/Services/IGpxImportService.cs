using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Sufni.Bridge.Services;

public sealed class GpxImportResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Guid TrackId { get; init; }
    public string TrackName { get; init; } = "";
    public long StartTimeMs { get; init; }
    public long EndTimeMs { get; init; }
    public IReadOnlyList<string> AssignedSessionNames { get; init; } = [];
    public IReadOnlyList<Guid> AssignedSessionIds { get; init; } = [];
    public long TimeOffsetMs { get; init; }
    public bool TimeOffsetEstimated { get; init; }
}

public interface IGpxImportService
{
    Task<GpxImportResult> ImportAsync(Stream gpx, string fileName);
}
