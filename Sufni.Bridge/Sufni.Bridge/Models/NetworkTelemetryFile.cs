using System;
using System.Net;
using System.Threading.Tasks;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Models;

public class NetworkTelemetryFile : ITelemetryFile
{
    public string Name { get; set; }
    public string FileName { get; }
    public string SourceIdentifier { get; }
    public bool? ShouldBeImported { get; set; }
    public bool Imported { get; set; }
    public string Description { get; set; }
    public DateTime StartTime { get; init; }
    public string Duration { get; init; }

    private readonly IPEndPoint ipEndPoint;

    // Transfer whose "file received" ack is deferred until the session is in the
    // database (OnImported). The DAQ only moves the file to "uploaded" on that
    // ack, so a crash/kill mid-import leaves the file re-importable on the DAQ.
    private SstTcpClient.PendingFile? pendingAck;

    public async Task<(TelemetryData Data, byte[] Psst)> GeneratePsstAsync(Linkage linkage, Calibration? frontCal, Calibration? rearCal)
    {
        var idString = FileName[..5].TrimStart('0');
        var idInt = int.Parse(idString);
        pendingAck?.Dispose();
        pendingAck = await SstTcpClient.GetFileDeferred(ipEndPoint, idInt);
        var rawTelemetryData = new RawTelemetryData(pendingAck.Data);
        var telemetryData = new TelemetryData(FileName,
            rawTelemetryData.Version, rawTelemetryData.SampleRate, rawTelemetryData.Timestamp,
            frontCal, rearCal, linkage);
        var psst = telemetryData.ProcessRecording(rawTelemetryData.Front, rawTelemetryData.Rear);
        return (telemetryData, psst);
    }

    public async Task OnImported()
    {
        if (pendingAck is not null)
        {
            await pendingAck.AcknowledgeAsync();
            pendingAck = null;
        }
        Imported = true;
    }

    public Task OnImportFailed()
    {
        // Drop the connection without acknowledging — the DAQ keeps the file.
        pendingAck?.Dispose();
        pendingAck = null;
        return Task.CompletedTask;
    }

    public async Task OnTrashed()
    {
        var idString = FileName[..5].TrimStart('0');
        var idInt = int.Parse(idString);
        await SstTcpClient.TrashFile(ipEndPoint, idInt);
    }

    public NetworkTelemetryFile(IPEndPoint source, string? boardId, ushort sampleRate, string name, ulong size, ulong timestamp)
    {
        var count = (size - 16 /* sizeof(header) */) / 4 /* sizeof(record) */;
        var duration = TimeSpan.FromSeconds((double)count / sampleRate);
        ShouldBeImported = duration.TotalSeconds >= 5 ? true : null;
        StartTime = DateTimeOffset.FromUnixTimeSeconds((int)timestamp).LocalDateTime;
        Duration = duration.ToString(@"hh\:mm\:ss");
        Name = name;
        FileName = name;
        Description = $"Imported from {name}";
        SourceIdentifier = $"{boardId ?? ""}:{timestamp}";
        ipEndPoint = source;
    }
}