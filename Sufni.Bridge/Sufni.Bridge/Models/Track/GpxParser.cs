using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Sufni.Bridge.Models;

public static class GpxParser
{
    public static TrackPoints Parse(Stream stream, out string? trackName)
    {
        var document = XDocument.Load(stream);
        var root = document.Root ?? throw new InvalidDataException("GPX-Datei enthält keine Zeitstempel");

        trackName = FirstLocalValue(root, "trk", "name")
                    ?? FirstLocalValue(root, "rte", "name");

        var points = CollectPoints(root, "trkpt");
        if (points.Count == 0)
            points = CollectPoints(root, "rtept");

        if (points.Count < 2)
            throw new InvalidDataException("GPX-Datei enthält keine Zeitstempel");

        points.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

        var timeMs = new long[points.Count];
        var lat = new double[points.Count];
        var lon = new double[points.Count];
        var ele = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            timeMs[i] = points[i].TimeMs;
            lat[i] = points[i].Lat;
            lon[i] = points[i].Lon;
            ele[i] = points[i].Ele;
        }

        return new TrackPoints
        {
            TimeMs = timeMs,
            Lat = lat,
            Lon = lon,
            Ele = ele
        };
    }

    private static List<GpxPoint> CollectPoints(XElement root, string pointLocalName)
    {
        var points = new List<GpxPoint>();
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == pointLocalName))
        {
            if (!TryParsePoint(element, out var point))
                continue;
            points.Add(point);
        }

        return points;
    }

    private static bool TryParsePoint(XElement element, out GpxPoint point)
    {
        point = default;
        var latAttr = element.Attribute("lat")?.Value;
        var lonAttr = element.Attribute("lon")?.Value;
        if (latAttr is null || lonAttr is null)
            return false;
        if (!double.TryParse(latAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            return false;
        if (!double.TryParse(lonAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return false;

        var timeText = element.Elements().FirstOrDefault(e => e.Name.LocalName == "time")?.Value;
        if (string.IsNullOrWhiteSpace(timeText))
            return false;

        DateTimeOffset time;
        try
        {
            time = DateTimeOffset.Parse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch (FormatException)
        {
            return false;
        }

        var eleText = element.Elements().FirstOrDefault(e => e.Name.LocalName == "ele")?.Value;
        double ele = 0;
        if (!string.IsNullOrWhiteSpace(eleText))
            double.TryParse(eleText, NumberStyles.Float, CultureInfo.InvariantCulture, out ele);

        point = new GpxPoint(time.ToUniversalTime().ToUnixTimeMilliseconds(), lat, lon, ele);
        return true;
    }

    private static string? FirstLocalValue(XElement root, string parentLocalName, string childLocalName)
    {
        foreach (var parent in root.Descendants().Where(e => e.Name.LocalName == parentLocalName))
        {
            var child = parent.Elements().FirstOrDefault(e => e.Name.LocalName == childLocalName);
            if (!string.IsNullOrWhiteSpace(child?.Value))
                return child.Value.Trim();
        }

        return null;
    }

    private readonly struct GpxPoint(long timeMs, double lat, double lon, double ele)
    {
        public long TimeMs { get; } = timeMs;
        public double Lat { get; } = lat;
        public double Lon { get; } = lon;
        public double Ele { get; } = ele;
    }
}
