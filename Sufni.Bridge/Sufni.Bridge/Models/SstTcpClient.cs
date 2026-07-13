using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Sufni.Bridge.Models;

public static class SstTcpClient
{
    // A file transfer whose "file received" acknowledgement (0x05) has not been
    // sent yet. The DAQ moves the file to its "uploaded" folder only upon that
    // acknowledgement, so keeping it pending until the session is safely in the
    // database means a killed app leaves the file on the DAQ, re-importable.
    public sealed class PendingFile : IDisposable
    {
        public byte[] Data { get; }
        private Socket? socket;

        internal PendingFile(byte[] data, Socket socket)
        {
            Data = data;
            this.socket = socket;
        }

        public async Task AcknowledgeAsync()
        {
            if (socket is null) return;
            await socket.SendAsync(new byte[] { 0x05, 0x00, 0x00, 0x00 }, SocketFlags.None);
            socket.Dispose();
            socket = null;
        }

        // Close without acknowledging: the DAQ treats the dropped connection as
        // a failed transfer and keeps the file in place.
        public void Dispose()
        {
            socket?.Dispose();
            socket = null;
        }
    }

    public static async Task<byte[]> GetFile(IPEndPoint ipEndPoint, int fileId)
    {
        using var pending = await GetFileDeferred(ipEndPoint, fileId);
        await pending.AcknowledgeAsync();
        return pending.Data;
    }

    public static async Task<PendingFile> GetFileDeferred(IPEndPoint ipEndPoint, int fileId)
    {
        Socket client = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            return new PendingFile(await ReceiveFile(client, ipEndPoint, fileId), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReceiveFile(Socket client, IPEndPoint ipEndPoint, int fileId)
    {
        await client.ConnectAsync(ipEndPoint);

        // Get identifier as little-endian byte array
        var id = BitConverter.GetBytes(fileId);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(id);
        }

        // Request file
        await client.SendAsync(new byte[]
        {
            0x03, 0x00, 0x00, 0x00,    // 3: file request command
            id[0], id[1], id[2], id[3] // file id
        }, SocketFlags.None);

        // Receive size
        var sizeBuffer = new byte[8];
        await client.ReceiveAsync(sizeBuffer);
        var size = BitConverter.ToInt32(sizeBuffer.AsSpan()[..4]); // We won't be able to process file larger than
                                                                   // int max anyway, so this is OK.

        // Send header OK signal
        await client.SendAsync(new byte[] { 0x04, 0x00, 0x00, 0x00 });

        // Receive data
        var buffer = new byte[size];
        var totalRead = 0;
        do
        {
            var read = client.Receive(buffer, totalRead, size - totalRead, SocketFlags.None);
            if (read == 0)
            {
                throw new Exception("Server closed connection while receiveing data.");
            }
            totalRead += read;
        } while (totalRead != size);

        return buffer;
    }

    public static async Task SendFinish(IPEndPoint ipEndPoint)
    {
        using Socket client = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(ipEndPoint);
        // STATUS_FINISHED = 6: server tears down listening socket and returns to IDLE.
        await client.SendAsync(new byte[] { 0x06, 0x00, 0x00, 0x00 }, SocketFlags.None);

        // The DAQ's recv callback only flags STATUS_FINISHED; its main loop picks
        // the flag up on a ~1 ms poll. Closing our socket immediately races that
        // poll: the FIN triggers the DAQ's connection-closed path, which overwrites
        // the flag with an error status and the server never shuts down. Wait for
        // the DAQ to close the connection first (bounded, in case of old firmware).
        var buf = new byte[1];
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await client.ReceiveAsync(buf, SocketFlags.None, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for the server-side close; give up gracefully.
        }
    }

    public static async Task SendTime(IPEndPoint ipEndPoint, long epochUtc)
    {
        using Socket client = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(ipEndPoint);

        var epoch = BitConverter.GetBytes(epochUtc);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(epoch);
        }

        // Request time sync: [0x07 00 00 00][int64 epoch]
        var payload = new byte[12];
        payload[0] = 0x07;
        Buffer.BlockCopy(epoch, 0, payload, 4, 8);
        await client.SendAsync(payload, SocketFlags.None);

        // Receive STATUS_TIME_SYNCED (11) ack
        var statusBuffer = new byte[4];
        await client.ReceiveAsync(statusBuffer);
        var status = BitConverter.ToInt32(statusBuffer.AsSpan()[..4]);
        Debug.Assert(status == 11);

        // Send file received signal. Server will close connection after receiving this.
        await client.SendAsync(new byte[] { 0x05, 0x00, 0x00, 0x00 });
    }

    public static async Task TrashFile(IPEndPoint ipEndPoint, int fileId)
    {
        using Socket client = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(ipEndPoint);

        // Get identifier as little-endian byte array
        var id = BitConverter.GetBytes(-fileId);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(id);
        }

        // Request file with negative ID
        await client.SendAsync(new byte[]
        {
            0x03, 0x00, 0x00, 0x00,    // 3: file request command
            id[0], id[1], id[2], id[3] // negative file id
        }, SocketFlags.None);

        // Wait for server to acknowledge file deletion
        var statusBuffer = new byte[4];
        await client.ReceiveAsync(statusBuffer);
        var status = BitConverter.ToInt32(statusBuffer.AsSpan()[..4]);
        Debug.Assert(status == 10);

        // Send file received signal. Server will close connection after receiving this.
        await client.SendAsync(new byte[] { 0x05, 0x00, 0x00, 0x00 });
    }
}