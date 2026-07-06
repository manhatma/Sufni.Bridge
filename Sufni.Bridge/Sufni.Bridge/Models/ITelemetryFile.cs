using System;
using System.Threading.Tasks;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Models;

public interface ITelemetryFile
{
    public string Name { get; set; }
    public string FileName { get; }
    public string SourceIdentifier { get; }
    public bool? ShouldBeImported { get; set; }
    public bool Imported { get; set; }
    public string Description { get; set; }
    public DateTime StartTime { get; }
    public string Duration { get; }

    // Returns both the processed in-memory TelemetryData and its serialized PSST blob, so
    // callers can reuse the object (duration, cache precomputation) without re-deserializing.
    public Task<(TelemetryData Data, byte[] Psst)> GeneratePsstAsync(Linkage linkage, Calibration? frontCal, Calibration? rearCal);
    public Task OnImported();
    public Task OnTrashed();
}