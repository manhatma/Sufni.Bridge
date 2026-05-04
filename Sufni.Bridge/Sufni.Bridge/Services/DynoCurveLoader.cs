using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Services;

/// <summary>
/// Loads dyno-measured spring and damper curves from the Avalonia asset bundle at startup.
/// Looks under avares://Sufni.Bridge/Assets/DynoCurves/Springs/{component-id}/ and
/// .../Dampers/{component-id}/. Each component directory is expected to contain a
/// meta.json describing the component plus one CSV file per measured configuration:
///   - Spring: "{pressure}psi_{volume}cc.csv" with header "ShockTravel_mm,Force_N"
///     (decimal point in volume encoded as 'p', e.g. "75psi_0p2cc.csv")
///   - Damper: filename encodes click setting (any subset of hsc/lsc/hsr/lsr,
///     e.g. "hsc8_lsc6_hsr4_lsr5.csv" or "lsc6.csv") with header
///     "Velocity_mmps,Force_N".
/// </summary>
public class DynoCurveLoader
{
    private const string AssetRoot = "avares://Sufni.Bridge/Assets/DynoCurves";

    public IReadOnlyList<SpringLibrary> SpringLibraries { get; private set; } = [];
    public IReadOnlyList<DamperLibrary> DamperLibraries { get; private set; } = [];

    public void Load()
    {
        SpringLibraries = LoadSpringLibraries();
        DamperLibraries = LoadDamperLibraries();
    }

    public SpringLibrary? FindSpringLibrary(string? id) =>
        id is null ? null : SpringLibraries.FirstOrDefault(l => l.Id == id);

    public DamperLibrary? FindDamperLibrary(string? id) =>
        id is null ? null : DamperLibraries.FirstOrDefault(l => l.Id == id);

    private static List<SpringLibrary> LoadSpringLibraries()
    {
        var libraries = new List<SpringLibrary>();
        foreach (var componentDir in EnumerateComponentDirectories($"{AssetRoot}/Springs"))
        {
            try
            {
                var metaOpt = LoadMeta(componentDir);
                if (metaOpt is null) continue;
                var meta = metaOpt.Value;

                var curves = new List<SpringCurve>();
                foreach (var csvUri in EnumerateCsvFiles(componentDir))
                {
                    var (pressure, volume) = ParseSpringFilename(csvUri);
                    if (pressure is null) continue;
                    var (travel, force) = ReadCsv(csvUri, "ShockTravel_mm", "Force_N");
                    if (travel.Length < 2) continue;
                    curves.Add(new SpringCurve(travel, force, pressure.Value, volume!.Value));
                }
                if (curves.Count == 0) continue;

                var pressureRange = (
                    curves.Min(c => c.PressurePsi),
                    curves.Max(c => c.PressurePsi));
                var volumeRange = (
                    curves.Min(c => c.VolumeCcm),
                    curves.Max(c => c.VolumeCcm));
                var pressureRangeFromMeta = ReadDoubleRange(meta, "pressureRange") ?? pressureRange;
                var volumeRangeFromMeta = ReadDoubleRange(meta, "volumespacerRange") ?? volumeRange;

                libraries.Add(new SpringLibrary(
                    id: GetComponentId(componentDir),
                    manufacturer: meta.GetProperty("manufacturer").GetString() ?? "",
                    model: meta.GetProperty("model").GetString() ?? "",
                    year: meta.TryGetProperty("year", out var y) ? y.GetInt32() : 0,
                    type: ParseSpringType(meta),
                    defaultAxle: meta.TryGetProperty("axle", out var a) ? a.GetString() ?? "" : "",
                    pressureRange: pressureRangeFromMeta,
                    volumeRange: volumeRangeFromMeta,
                    volumespacerHint: meta.TryGetProperty("volumespacerHint", out var h) ? h.GetString() ?? "" : "",
                    curves: curves));
            }
            catch
            {
                // Skip libraries that fail to parse — better than crashing the app at startup.
            }
        }
        return libraries;
    }

    private static List<DamperLibrary> LoadDamperLibraries()
    {
        var libraries = new List<DamperLibrary>();
        foreach (var componentDir in EnumerateComponentDirectories($"{AssetRoot}/Dampers"))
        {
            try
            {
                var metaOpt = LoadMeta(componentDir);
                if (metaOpt is null) continue;
                var meta = metaOpt.Value;

                var curves = new List<DamperCurve>();
                foreach (var csvUri in EnumerateCsvFiles(componentDir))
                {
                    var clicks = ParseDamperFilename(csvUri);
                    var (vel, force) = ReadCsv(csvUri, "Velocity_mmps", "Force_N");
                    if (vel.Length < 2) continue;
                    curves.Add(new DamperCurve(vel, force, clicks));
                }
                if (curves.Count == 0) continue;

                libraries.Add(new DamperLibrary(
                    id: GetComponentId(componentDir),
                    manufacturer: meta.GetProperty("manufacturer").GetString() ?? "",
                    model: meta.GetProperty("model").GetString() ?? "",
                    year: meta.TryGetProperty("year", out var y) ? y.GetInt32() : 0,
                    defaultAxle: meta.TryGetProperty("axle", out var a) ? a.GetString() ?? "" : "",
                    hsc: ReadClickRange(meta, "hscRange"),
                    lsc: ReadClickRange(meta, "lscRange"),
                    hsr: ReadClickRange(meta, "hsrRange"),
                    lsr: ReadClickRange(meta, "lsrRange"),
                    curves: curves));
            }
            catch
            {
                // Skip libraries that fail to parse.
            }
        }
        return libraries;
    }

    private static SpringType ParseSpringType(JsonElement meta)
    {
        if (!meta.TryGetProperty("springType", out var t) && !meta.TryGetProperty("type", out t))
            return SpringType.Air;
        var s = t.GetString()?.ToLowerInvariant();
        return s == "coil" ? SpringType.Coil : SpringType.Air;
    }

    private static (double Min, double Max)? ReadDoubleRange(JsonElement meta, string name)
    {
        if (!meta.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() != 2)
            return null;
        return (arr[0].GetDouble(), arr[1].GetDouble());
    }

    private static ClickRange? ReadClickRange(JsonElement meta, string name)
    {
        if (!meta.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() != 2)
            return null;
        return new ClickRange { Min = arr[0].GetInt32(), Max = arr[1].GetInt32() };
    }

    private static IEnumerable<string> EnumerateComponentDirectories(string rootUri)
    {
        IEnumerable<Uri> assets;
        try
        {
            assets = AssetLoader.GetAssets(new Uri(rootUri + "/"), null);
        }
        catch
        {
            yield break;
        }

        var componentRoots = new HashSet<string>();
        foreach (var uri in assets)
        {
            var s = uri.ToString();
            if (!s.StartsWith(rootUri + "/", StringComparison.Ordinal)) continue;
            var rel = s[(rootUri.Length + 1)..];
            var slash = rel.IndexOf('/');
            if (slash < 0) continue;
            var component = rel[..slash];
            componentRoots.Add($"{rootUri}/{component}");
        }
        foreach (var c in componentRoots) yield return c;
    }

    private static IEnumerable<string> EnumerateCsvFiles(string componentDirUri)
    {
        IEnumerable<Uri> assets;
        try
        {
            assets = AssetLoader.GetAssets(new Uri(componentDirUri + "/"), null);
        }
        catch
        {
            yield break;
        }

        foreach (var uri in assets)
        {
            var s = uri.ToString();
            if (!s.StartsWith(componentDirUri + "/", StringComparison.Ordinal)) continue;
            var rel = s[(componentDirUri.Length + 1)..];
            if (rel.Contains('/')) continue;
            if (!rel.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
            yield return s;
        }
    }

    private static JsonElement? LoadMeta(string componentDirUri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"{componentDirUri}/meta.json"));
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static (double[] X, double[] Y) ReadCsv(string uri, string xColumn, string yColumn)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .ToList();
        if (lines.Count == 0) return (Array.Empty<double>(), Array.Empty<double>());

        // Detect header: if the first line's first token is not a valid double, treat as header.
        bool hasHeader = false;
        var firstTokens = lines[0].Split([',', ';', '\t']);
        if (firstTokens.Length >= 1 &&
            !double.TryParse(firstTokens[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            hasHeader = true;
        }

        int xIdx = 0, yIdx = 1;
        int dataStart = 0;
        if (hasHeader)
        {
            for (int i = 0; i < firstTokens.Length; i++)
            {
                var name = firstTokens[i].Trim();
                if (string.Equals(name, xColumn, StringComparison.OrdinalIgnoreCase)) xIdx = i;
                else if (string.Equals(name, yColumn, StringComparison.OrdinalIgnoreCase)) yIdx = i;
            }
            dataStart = 1;
        }

        var xs = new List<double>();
        var ys = new List<double>();
        for (int i = dataStart; i < lines.Count; i++)
        {
            var tokens = lines[i].Split([',', ';', '\t']);
            if (tokens.Length <= Math.Max(xIdx, yIdx)) continue;
            if (!double.TryParse(tokens[xIdx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!double.TryParse(tokens[yIdx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            xs.Add(x);
            ys.Add(y);
        }
        return (xs.ToArray(), ys.ToArray());
    }

    private static string GetComponentId(string componentDirUri)
    {
        var idx = componentDirUri.LastIndexOf('/');
        return idx < 0 ? componentDirUri : componentDirUri[(idx + 1)..];
    }

    private static readonly Regex SpringFilenameRegex = new(
        @"^(?<p>[\d.]+)psi_(?<v>[\dp.]+)cc(?:\.csv)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (double? PressurePsi, double? VolumeCcm) ParseSpringFilename(string uri)
    {
        var name = Path.GetFileName(uri.AsSpan('/').ToString()).Split('?')[0];
        // Avalonia URIs use forward slashes; fall back to last segment.
        var lastSlash = uri.LastIndexOf('/');
        if (lastSlash >= 0) name = uri[(lastSlash + 1)..];

        var m = SpringFilenameRegex.Match(name);
        if (!m.Success) return (null, null);

        if (!double.TryParse(m.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return (null, null);

        var vRaw = m.Groups["v"].Value.Replace('p', '.');
        if (!double.TryParse(vRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (null, null);

        return (p, v);
    }

    private static readonly Regex DamperClickRegex = new(
        @"(?<axis>hsc|lsc|hsr|lsr)(?<n>-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static DamperClicks ParseDamperFilename(string uri)
    {
        var lastSlash = uri.LastIndexOf('/');
        var name = lastSlash >= 0 ? uri[(lastSlash + 1)..] : uri;

        int? hsc = null, lsc = null, hsr = null, lsr = null;
        foreach (Match m in DamperClickRegex.Matches(name))
        {
            if (!int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                continue;
            switch (m.Groups["axis"].Value.ToLowerInvariant())
            {
                case "hsc": hsc = n; break;
                case "lsc": lsc = n; break;
                case "hsr": hsr = n; break;
                case "lsr": lsr = n; break;
            }
        }
        return new DamperClicks(hsc, lsc, hsr, lsr);
    }
}
