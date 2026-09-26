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
    public int FrontDropouts { get; private set; }
    public int RearDropouts { get; private set; }

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

        Front = hasFront ? new ushort[count] : [];
        Rear = hasRear ? new ushort[count] : [];
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderSize + i * RecordSize;
            if (hasFront)
                Front[i] = U16(sstData, offset);
            if (hasRear)
                Rear[i] = U16(sstData, offset + 2);
        }

        FrontDropouts = FillDropouts(Front);
        RearDropouts = FillDropouts(Rear);
    }

    /// <summary>
    /// Replaces dropout records (0xffff) by linear interpolation between the neighbouring valid
    /// samples; a trailing run holds the last valid value. Holding the last value across every
    /// gap would create a plateau followed by a step, which differentiation turns into a
    /// velocity spike about twice as high as the real motion across the gap. The first record
    /// of a present channel is valid by definition (it decides channel presence).
    /// Returns the number of replaced samples.
    /// </summary>
    private static int FillDropouts(ushort[] samples)
    {
        var dropouts = 0;
        var lastValid = -1;
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] == 0xffff)
            {
                dropouts++;
                continue;
            }

            if (lastValid >= 0 && i - lastValid > 1)
            {
                double from = samples[lastValid];
                double to = samples[i];
                var span = i - lastValid;
                for (var j = lastValid + 1; j < i; j++)
                    samples[j] = (ushort)Math.Round(from + (to - from) * (j - lastValid) / span);
            }

            lastValid = i;
        }

        if (lastValid >= 0)
        {
            for (var j = lastValid + 1; j < samples.Length; j++)
                samples[j] = samples[lastValid];
        }

        return dropouts;
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
