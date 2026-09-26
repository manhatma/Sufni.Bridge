using System.Globalization;
using System.Text;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.Tests;

public class GpxParserTests
{
    private static TrackPoints Parse(string xml, out string? name)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return GpxParser.Parse(stream, out name);
    }

    private static long UnixMs(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime()
            .ToUnixTimeMilliseconds();

    [Fact]
    public void Parse_Gpx11WithNamespace_ReadsLatLonTimeEle_SortsByTime_IgnoresExtensions()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" creator="test"
                 xmlns="http://www.topografix.com/GPX/1/1"
                 xmlns:gpxtpx="http://www.garmin.com/xmlschemas/TrackPointExtension/v1">
              <trk>
                <name>Alps Loop</name>
                <trkseg>
                  <trkpt lat="47.2" lon="11.3">
                    <ele>900.5</ele>
                    <time>2024-06-01T10:00:02Z</time>
                    <extensions>
                      <gpxtpx:TrackPointExtension>
                        <gpxtpx:hr>150</gpxtpx:hr>
                      </gpxtpx:TrackPointExtension>
                    </extensions>
                  </trkpt>
                  <trkpt lat="47.0" lon="11.0">
                    <ele>800</ele>
                    <time>2024-06-01T10:00:00Z</time>
                  </trkpt>
                  <trkpt lat="47.1" lon="11.1">
                    <time>2024-06-01T10:00:01Z</time>
                  </trkpt>
                </trkseg>
              </trk>
            </gpx>
            """;

        var points = Parse(xml, out var name);

        Assert.Equal("Alps Loop", name);
        Assert.Equal(3, points.TimeMs.Length);
        Assert.Equal(UnixMs("2024-06-01T10:00:00Z"), points.TimeMs[0]);
        Assert.Equal(UnixMs("2024-06-01T10:00:01Z"), points.TimeMs[1]);
        Assert.Equal(UnixMs("2024-06-01T10:00:02Z"), points.TimeMs[2]);
        Assert.Equal([47.0, 47.1, 47.2], points.Lat);
        Assert.Equal([11.0, 11.1, 11.3], points.Lon);
        Assert.Equal([800.0, 0.0, 900.5], points.Ele);
        Assert.True(points.TimeMs[0] < points.TimeMs[1]);
        Assert.True(points.TimeMs[1] < points.TimeMs[2]);
    }

    [Fact]
    public void Parse_Gpx10OtherNamespace_MatchesByLocalName()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.0" creator="test" xmlns="http://www.topografix.com/GPX/1/0">
              <trk>
                <name>Legacy Track</name>
                <trkseg>
                  <trkpt lat="10.5" lon="20.25">
                    <ele>12.5</ele>
                    <time>2020-01-01T00:00:00Z</time>
                  </trkpt>
                  <trkpt lat="10.6" lon="20.35">
                    <ele>13</ele>
                    <time>2020-01-01T00:00:05Z</time>
                  </trkpt>
                </trkseg>
              </trk>
            </gpx>
            """;

        var points = Parse(xml, out var name);

        Assert.Equal("Legacy Track", name);
        Assert.Equal(2, points.Lat.Length);
        Assert.Equal(10.5, points.Lat[0]);
        Assert.Equal(20.25, points.Lon[0]);
        Assert.Equal(12.5, points.Ele[0]);
        Assert.Equal(10.6, points.Lat[1]);
        Assert.Equal(20.35, points.Lon[1]);
        Assert.Equal(UnixMs("2020-01-01T00:00:00Z"), points.TimeMs[0]);
        Assert.Equal(UnixMs("2020-01-01T00:00:05Z"), points.TimeMs[1]);
    }

    [Fact]
    public void Parse_SkipsPointsWithoutTime()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk>
                <trkseg>
                  <trkpt lat="1" lon="2">
                    <ele>10</ele>
                    <time>2024-01-01T00:00:00Z</time>
                  </trkpt>
                  <trkpt lat="9" lon="9">
                    <ele>99</ele>
                  </trkpt>
                  <trkpt lat="3" lon="4">
                    <ele>20</ele>
                    <time>2024-01-01T00:00:01Z</time>
                  </trkpt>
                </trkseg>
              </trk>
            </gpx>
            """;

        var points = Parse(xml, out _);

        Assert.Equal(2, points.TimeMs.Length);
        Assert.Equal([1.0, 3.0], points.Lat);
        Assert.Equal([2.0, 4.0], points.Lon);
        Assert.Equal([10.0, 20.0], points.Ele);
    }

    [Fact]
    public void Parse_FewerThanTwoTimedPoints_ThrowsInvalidDataException()
    {
        const string onePoint = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk>
                <trkseg>
                  <trkpt lat="1" lon="2">
                    <time>2024-01-01T00:00:00Z</time>
                  </trkpt>
                  <trkpt lat="3" lon="4" />
                </trkseg>
              </trk>
            </gpx>
            """;

        var ex = Assert.Throws<InvalidDataException>(() => Parse(onePoint, out _));
        Assert.Equal("GPX-Datei enthält keine Zeitstempel", ex.Message);
    }

    [Fact]
    public void Parse_RoutePoints_UsedWhenNoTrack()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <rte>
                <name>Summit Route</name>
                <rtept lat="46.5" lon="10.1">
                  <ele>2100</ele>
                  <time>2024-07-01T08:00:00Z</time>
                </rtept>
                <rtept lat="46.6" lon="10.2">
                  <ele>2200</ele>
                  <time>2024-07-01T08:00:30Z</time>
                </rtept>
              </rte>
            </gpx>
            """;

        var points = Parse(xml, out var name);

        Assert.Equal("Summit Route", name);
        Assert.Equal(2, points.Lat.Length);
        Assert.Equal(46.5, points.Lat[0]);
        Assert.Equal(10.1, points.Lon[0]);
        Assert.Equal(2100.0, points.Ele[0]);
        Assert.Equal(46.6, points.Lat[1]);
        Assert.Equal(10.2, points.Lon[1]);
        Assert.Equal(UnixMs("2024-07-01T08:00:00Z"), points.TimeMs[0]);
        Assert.Equal(UnixMs("2024-07-01T08:00:30Z"), points.TimeMs[1]);
    }

    [Fact]
    public void Parse_TrackName_ReturnedFromTrkName()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk>
                <name>  Named Track  </name>
                <trkseg>
                  <trkpt lat="0" lon="0">
                    <time>2024-01-01T00:00:00Z</time>
                  </trkpt>
                  <trkpt lat="0.001" lon="0">
                    <time>2024-01-01T00:00:01Z</time>
                  </trkpt>
                </trkseg>
              </trk>
            </gpx>
            """;

        Parse(xml, out var name);

        Assert.Equal("Named Track", name);
    }
}
