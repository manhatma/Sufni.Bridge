# Sufni.Bridge

**Sufni.Bridge** is a cross-platform telemetry analysis app for mountain bike suspension, built with .NET and Avalonia. It connects to a [Sufni Suspension Telemetry](https://github.com/sghctoma/sst) DAQ device, imports recorded ride sessions, and provides a rich set of per-session analysis views — travel histograms, velocity distributions, compression/rebound balance, and more. All analysis runs fully offline on-device, without any cloud dependency.

Recorded sessions can be non-destructively cropped, merged with combine, or placed side-by-side in compare mode to evaluate how setup changes affect suspension behavior across runs.

> This is a personal fork of [sghctoma/Sufni.Bridge](https://github.com/sghctoma/Sufni.Bridge). All credit for the original design and implementation goes to [@sghctoma](https://github.com/sghctoma).

> \* *Pronounced "SHOOF-nee Bridge". "Sufni" is Hungarian for tool shed and is also used colloquially for DIY/garage-style engineering.*

---

## Session List

The session list is the main entry point of the app. It shows all imported sessions with their name, date, and time. A toolbar at the top provides quick access to **Filter**, **Delete**, **Combine**, and **Compare** actions. A search bar allows instant text-based filtering by session name.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Session-1.png" width="260"/></td>
  </tr>
</table>

---

## Filter

The filter panel lets you narrow down the session list by criteria such as track name and date range. Filtered results update the session list immediately, and the active filter is indicated in the toolbar.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Filter-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Filter-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Filter-3.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Filter-4.png" width="260"/></td>
  </tr>
</table>

---

## Summary

The Summary tab provides a quick overview of the most important metrics for a session: maximum travel, average travel, and bottom-out count for both front and rear suspension. A setup dropdown lets you switch between different bike setups linked to a session.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Summary-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Summary-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Summary_Setup-Dropdown.png" width="260"/></td>
  </tr>
</table>

---

## Spring

The Spring tab shows a travel histogram for both front and rear suspension. The x-axis represents suspension travel as a percentage of total travel, and the y-axis shows the percentage of time spent at each travel value. Markers indicate average travel, the 95th percentile, and maximum travel, giving a clear picture of how the suspension is being used across the ride.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Spring-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Spring-2.png" width="260"/></td>
  </tr>
</table>

---

## Damper

The Damper tab displays velocity histograms for both front and rear suspension. Compression and rebound velocities are visualized separately, split into low-speed and high-speed zones. This helps identify whether the damper is spending most of its time in the high-speed or low-speed range — key information for tuning compression and rebound circuits.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Damper-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Damper-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Damper-3.png" width="260"/></td>
  </tr>
</table>

---

## Balance

The Balance tab shows a scatter chart of compression velocity versus rebound velocity across the full range of suspension travel. Each data point represents a moment in the ride, plotted at its travel percentage with a color indicating whether the suspension was compressing or rebounding. A trend line and MSD (Mean Signed Deviation) metric quantify how well the compression and rebound forces are balanced relative to each other — a zero MSD means perfect balance.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Balance-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Balance-2.png" width="260"/></td>
  </tr>
</table>

---

## Notes

The Notes tab provides a free-form text field for recording setup details alongside a session — spring rate, volume spacer configuration, clicker positions, tire pressure, or any other rider notes.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Notes-1.png" width="260"/></td>
  </tr>
</table>

---

## Misc

The Misc tab shows additional session metadata and derived information, including session-level details and the ability to assign or change the bike setup associated with a session.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Misc-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Misc-2.png" width="260"/></td>
  </tr>
</table>

---

## Crop

The Crop feature lets you non-destructively trim a session to a specific time range. An interactive timeline view shows the full session with drag handles that define the start and end of the crop region. Confirming the crop creates a new derived session in the list containing only the selected time range — the original session is preserved unchanged.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Crop.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Crop-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Crop-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Crop-3.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Crop-4.png" width="260"/></td>
  </tr>
</table>

---

## Combine

The Combine feature merges multiple sessions into a single unified session. This is useful when a ride was recorded in separate segments — for example, after a DAQ restart mid-run. Combined sessions appear in the list with an ∞ symbol, and their constituent child sessions are shown indented below. The combined session aggregates all data from its children and can be analyzed just like any other session.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Combine-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Combine-3.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Combine-4.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Combine-5.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Combine-6.png" width="260"/></td>
  </tr>
</table>

---

## Compare (2 Sessions)

The Compare feature places two sessions side by side for direct analysis. After selecting two sessions from the list, all analysis tabs (Spring, Damper, Balance) display overlaid data for both sessions simultaneously. The Spring tab additionally shows a front-vs-rear travel scatter plot with a linear regression line — the coefficient `a` quantifies the front-to-rear travel ratio. This makes it easy to see how a setup change shifted suspension behavior between two rides.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare-3.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare-4.png" width="260"/></td>
  </tr>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare-5.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare-6.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare-7.png" width="260"/></td>
  </tr>
</table>

---

## Compare (3 Sessions)

Three-session comparison works identically to two-session compare, but overlays data from three sessions simultaneously. Each session is assigned a distinct color (teal, yellow, red/orange) used consistently across all chart types — Spring histograms, Damper velocity distributions, and Balance scatter charts — making it straightforward to track which session each data series belongs to.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare3-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare3-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare3-3.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare3-4.png" width="260"/></td>
  </tr>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare3-5.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare3-6.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Compare3-7.png" width="260"/></td>
  </tr>
</table>

---

## Delete

Sessions can be deleted by swiping left on a session row to reveal the Delete button. A confirmation dialog prevents accidental deletion. Once confirmed, the session is removed from the list permanently.

<table>
  <tr>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Delete-1.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Delete-2.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Delete-3.png" width="260"/></td>
    <td><img src="Sufni.Bridge/Sufni.Bridge.iOS/screenshots/Delete-4.png" width="260"/></td>
  </tr>
</table>

---

## Project Structure

- `Sufni.Bridge/` — main Avalonia application (shared UI and logic)
- `Sufni.Bridge/Sufni.Bridge.iOS/` — iOS host project
- `HapticFeedback/` — platform haptic feedback abstraction
- `SecureStorage/` — platform secure storage abstraction
- `ServiceDiscovery/` — network service discovery for DAQ connectivity

## Build

### Prerequisites

- .NET SDK (version specified in `global.json`)
- Avalonia-compatible environment for your target platform
- For iOS builds: Xcode + iOS workload (`dotnet workload install ios`)

### Restore and build

```bash
dotnet restore Sufni.Bridge.sln
dotnet build Sufni.Bridge.sln
```

### iOS

```bash
dotnet build Sufni.Bridge/Sufni.Bridge.iOS/Sufni.Bridge.iOS.csproj
```
