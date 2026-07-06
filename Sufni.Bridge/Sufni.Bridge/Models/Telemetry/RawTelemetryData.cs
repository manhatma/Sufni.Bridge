using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Sufni.Bridge.Models.Telemetry;

public class RawTelemetryData
{
    public byte[] Magic { get; }
    public byte Version { get; }
    public ushort SampleRate { get; }
    public int Timestamp { get; }
    public ushort[] Front { get; }
    public ushort[] Rear { get; }

    // 16-byte header: magic (3) + version (1) + sample rate (u16) + padding (u16) + timestamp (i64),
    // followed by 4-byte records of fork/shock angle (u16 each). All values little-endian, matching
    // the BinaryReader-based parser this replaces.
    private const int HeaderSize = 16;
    private const int RecordSize = 4;

    private static ushort U16(byte[] b, int offset) => (ushort)(b[offset] | (b[offset + 1] << 8));

    public RawTelemetryData(byte[] sstData)
    {
        if (sstData.Length < HeaderSize)
        {
            throw new EndOfStreamException();
        }

        Magic = [sstData[0], sstData[1], sstData[2]];
        Version = sstData[3];
        SampleRate = U16(sstData, 4);
        // bytes 6..8: padding
        Timestamp = (int)BinaryPrimitives.ReadInt64LittleEndian(sstData.AsSpan(8, 8));

        if (Encoding.ASCII.GetString(Magic) != "SST")
        {
            throw new Exception("Data is not SST format");
        }

        var count = (sstData.Length - HeaderSize) / RecordSize;
        if (count == 0)
        {
            throw new Exception("SST data contains no records");
        }

        // Channel presence is decided from the first record only (0xffff = channel absent).
        var firstFork = U16(sstData, HeaderSize);
        var firstShock = U16(sstData, HeaderSize + 2);
        var hasFront = firstFork != 0xffff;
        var hasRear = firstShock != 0xffff;

        // Error-baseline detection: starting at record 1, skip samples at or below the first
        // record's value; the FIRST sample above it decides — a jump larger than 0x0050 marks a
        // constant acquisition error to subtract, anything else means no error. Either way the
        // scan stops there.
        ushort frontError = 0, rearError = 0;
        for (var i = 1; i < count; i++)
        {
            var v = U16(sstData, HeaderSize + i * RecordSize);
            if (v <= firstFork) continue;
            if (v - firstFork > 0x0050)
            {
                frontError = v;
            }

            break;
        }

        for (var i = 1; i < count; i++)
        {
            var v = U16(sstData, HeaderSize + i * RecordSize + 2);
            if (v <= firstShock) continue;
            if (v - firstShock > 0x0050)
            {
                rearError = v;
            }

            break;
        }

        // Single pass straight into the target arrays (unchecked ushort wraparound on the
        // error subtraction, exactly like the previous parser).
        Front = hasFront ? new ushort[count] : [];
        Rear = hasRear ? new ushort[count] : [];
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderSize + i * RecordSize;
            if (hasFront)
            {
                Front[i] = (ushort)(U16(sstData, offset) - frontError);
            }

            if (hasRear)
            {
                Rear[i] = (ushort)(U16(sstData, offset + 2) - rearError);
            }
        }
    }

    // Reads the remaining stream content into memory and parses it there — functionally
    // identical to the previous stream-based parser (callers always pass fresh streams).
    public RawTelemetryData(Stream stream) : this(ToByteArray(stream))
    {
    }

    private static byte[] ToByteArray(Stream stream)
    {
        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Max(0, stream.Length - stream.Position) : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
