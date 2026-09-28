using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Sufni.Bridge.Models;
using Xunit;

namespace Sufni.Bridge.Tests;

public class SstTcpClientTests
{
    private static async Task ReadExactly(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
    }

    [Fact]
    public async Task GetFile_HeaderAndPayloadInSeveralSegments_ReturnsWholeFile()
    {
        var payload = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endPoint = (IPEndPoint)listener.LocalEndpoint;

        // Fake DAQ: splits the 8-byte size header and the payload, with pauses, so a single
        // receive on the client side gets only part of a message.
        var daq = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadExactly(stream, 8); // file request

            var header = new byte[8];
            BitConverter.GetBytes(payload.Length).CopyTo(header, 0);
            await stream.WriteAsync(header.AsMemory(0, 3));
            await stream.FlushAsync();
            await Task.Delay(100);
            await stream.WriteAsync(header.AsMemory(3, 5));

            await ReadExactly(stream, 4); // header OK
            for (var offset = 0; offset < payload.Length; offset += 30_000)
            {
                await stream.WriteAsync(payload.AsMemory(offset, Math.Min(30_000, payload.Length - offset)));
                await stream.FlushAsync();
                await Task.Delay(20);
            }

            await ReadExactly(stream, 4); // file received
        });

        var data = await SstTcpClient.GetFile(endPoint, 1);
        await daq;

        Assert.Equal(payload, data);
    }
}
