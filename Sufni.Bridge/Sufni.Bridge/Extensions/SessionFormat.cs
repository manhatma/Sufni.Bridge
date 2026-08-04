using System.Globalization;

namespace Sufni.Bridge.Extensions;

// Session summary values include units because there is no unit header, while compare table
// values are bare numbers because their units are already shown in the column header.
internal static class SessionFormat
{
    // with-units variants — used by the session summary page, where there is no unit header
    internal static string TravelWithUnits(double value, double maxTravel)
    {
        if (maxTravel <= 0)
        {
            return "-";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{value / maxTravel * 100.0:0.0} % - {value:0.0} mm");
    }

    internal static string VelocityWithUnits(double value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} mm/s");
    }

    internal static string BottomoutsWithUnits(int value) => $"{value} times";

    // bare-number variants — used by the compare table, where the unit is in the column header
    internal static string TravelPercentOnly(double value, double maxTravel) =>
        maxTravel <= 0
            ? "-"
            : string.Create(CultureInfo.InvariantCulture, $"{value / maxTravel * 100.0:0.0}");

    internal static string VelocityPlain(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0.0}");

    internal static string BottomoutsPlain(int value) => $"{value}";

    // The summary page and compare table used to round differently: "0.0" rounds the shortest
    // round-trippable decimal away from zero, while "F1" rounds the exact binary value (for example,
    // 0.15 became "0.2" vs "0.1"). Use "0.0" for both so the views agree.
    internal static string Percent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0.0}");
}
