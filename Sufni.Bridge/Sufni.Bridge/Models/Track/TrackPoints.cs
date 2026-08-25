using MessagePack;

namespace Sufni.Bridge.Models;

[MessagePackObject(keyAsPropertyName: true)]
public class TrackPoints
{
    public long[] TimeMs { get; set; } = [];
    public double[] Lat { get; set; } = [];
    public double[] Lon { get; set; } = [];
    public double[] Ele { get; set; } = [];
}
