using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Sufni.Bridge.Models;

public class NetworkTelemetryDataStore : ITelemetryDataStore
{
    public string Name { get; }
    public string? BoardId { get; private set; }
    private readonly IPEndPoint ipEndPoint;
    public readonly Task Initialization;

    public async Task<List<ITelemetryFile>> GetFiles()
    {
        var directoryInfo = await SstTcpClient.GetFile(ipEndPoint, 0);
        var recordCount = directoryInfo.Length / 25; // 9 characters + size (long) + timestamp (long)
        using var memoryStream = new MemoryStream(directoryInfo);
        using var reader = new BinaryReader(memoryStream);
        var boardId = reader.ReadBytes(8);
        BoardId = Convert.ToHexString(boardId).ToLower();
        var sampleRate = reader.ReadUInt16();

        var files = new List<ITelemetryFile>();
        for (var i = 0; i < recordCount; i++)
        {
            var name = Encoding.ASCII.GetString(reader.ReadBytes(9));
            var size = reader.ReadUInt64();
            var timestamp = reader.ReadUInt64();

            try
            {
                var f = new NetworkTelemetryFile(ipEndPoint, BoardId, sampleRate, name, size, timestamp);
                files.Add(f);
            }
            catch (Exception)
            {
                // we don't care, the invalid file just simply won't show up in the list
            }
        }

        return files.OrderByDescending(f => f.StartTime).ToList();
    }

    public NetworkTelemetryDataStore(IPAddress address, int port, int protocolVersion = 1)
    {
        ipEndPoint = new IPEndPoint(address, port);
        Name = $"gosst://{ipEndPoint.Address}:{ipEndPoint.Port}";

        // We need this to set BoardId
        Initialization = InitializeAsync(protocolVersion);
    }

    private async Task InitializeAsync(int protocolVersion)
    {
        if (protocolVersion >= 2)
        {
            // Best-effort RTC sync — DAQs without internet rely on the iPhone
            // to deliver wall-clock time. A failure here must not block the
            // file listing.
            try
            {
                await SstTcpClient.SendTime(ipEndPoint, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch (Exception e)
            {
                Debug.WriteLine($"RTC sync to {Name} failed: {e.Message}");
            }
        }

        await GetFiles();
    }
}