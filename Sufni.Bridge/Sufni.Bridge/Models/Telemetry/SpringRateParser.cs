using System.Globalization;
using System.Text.RegularExpressions;

namespace Sufni.Bridge.Models.Telemetry;

/// <summary>
/// Parses and formats the free-text <c>Session.FrontSpringRate</c> / <c>RearSpringRate</c>
/// fields. The Notes UI writes a controlled format ("75.0 psi"), but the parser is
/// also tolerant to legacy entries.
///
/// Supported units: psi, bar (air); lbs/in (or lb/in), N/mm (coil).
/// SI conversions: 1 psi = 0.0689476 bar; 1 lb/in = 0.1751268 N/mm.
/// </summary>
public static class SpringRateParser
{
    public const string UnitPsi = "psi";
    public const string UnitBar = "bar";
    public const string UnitLbsPerIn = "lbs/in";
    public const string UnitNPerMm = "N/mm";

    public static readonly string[] AirUnits = [UnitPsi, UnitBar];
    public static readonly string[] CoilUnits = [UnitNPerMm, UnitLbsPerIn];
    public static readonly string[] AllUnits = [UnitPsi, UnitBar, UnitNPerMm, UnitLbsPerIn];

    private static readonly Regex Pattern = new(
        @"^\s*(?<value>[-+]?\d+(?:\.\d+)?)\s*(?<unit>psi|bar|lbs?/in|N/mm)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parse a free-text spring rate. Returns false if the string is null/empty/unparsable.
    /// Bare numbers without a unit are accepted (legacy data); <paramref name="unit"/> is then
    /// returned as an empty string so the caller can keep its current unit selection.
    /// </summary>
    public static bool TryParse(string? raw, out double value, out string unit)
    {
        value = 0;
        unit = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var m = Pattern.Match(raw);
        if (!m.Success) return false;

        if (!double.TryParse(m.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        var rawUnit = m.Groups["unit"].Value;
        unit = string.IsNullOrEmpty(rawUnit) ? "" : NormalizeUnit(rawUnit);
        return true;
    }

    /// <summary>
    /// Format value + unit into the controlled persistence format, e.g. "75.0 psi".
    /// Returns null for null/NaN value.
    /// </summary>
    public static string? Format(double? value, string? unit)
    {
        if (!value.HasValue || double.IsNaN(value.Value)) return null;
        var u = NormalizeUnit(unit ?? UnitPsi);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", value.Value, u);
    }

    /// <summary>Returns true if <paramref name="unit"/> is an air-spring unit (psi/bar).</summary>
    public static bool IsAirUnit(string? unit) =>
        NormalizeUnit(unit ?? "") is UnitPsi or UnitBar;

    /// <summary>Convert any supported air pressure to psi.</summary>
    public static double ToPsi(double value, string unit) =>
        NormalizeUnit(unit) == UnitBar ? value / 0.0689476 : value;

    /// <summary>Convert any supported coil rate to N/mm.</summary>
    public static double ToNewtonsPerMm(double value, string unit) =>
        NormalizeUnit(unit) == UnitLbsPerIn ? value * 0.1751268 : value;

    private static string NormalizeUnit(string raw)
    {
        var u = raw.Trim().ToLowerInvariant();
        return u switch
        {
            "psi" => UnitPsi,
            "bar" => UnitBar,
            "lb/in" or "lbs/in" => UnitLbsPerIn,
            "n/mm" => UnitNPerMm,
            _ => raw,
        };
    }
}
