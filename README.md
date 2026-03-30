Fake commit
Sufni.Bridge
============

Sufni.Bridge\* is a work-in-progress cross-platform (.NET core / Avalonia) application to process
recorded sessions directly from a [Sufni Suspension Telemetry](https://github.com/sghctoma/sst)
DAQ either via the DAQ's server, or its mass storage device (MSC) mode. As of
now, the application has a limited functionality compared to the
[web-based dashboard](https://github.com/sghctoma/sst/wiki/03-Dashboard), but
it does not require an internet connection.

## Session Analysis

| ![Sessions list](pics/sessions.png) | ![Session summary](pics/summary.png) | ![Spring — travel](pics/spring.png) | ![Damper — velocity](pics/damper.png) |
|---|---|---|---|

| ![Balance](pics/balance.png) | ![Position vs velocity](pics/position_velocity.png) | ![Setup & notes](pics/notes.png) | ![Import](pics/import.png) |
|---|---|---|---|

Each recorded session provides:

- **Summary** — position and velocity statistics for front and rear suspension (avg, 95th percentile, max, bottom-outs, HSR/LSR/LSC/HSC ratios)
- **Spring** — travel histogram comparison (front vs rear) and front-vs-rear travel scatter plot
- **Damper** — velocity distribution histogram and per-wheel velocity breakdown
- **Balance** — compression/rebound balance plots with linear fit
- **Position vs Velocity** — front and rear suspension position-velocity plots and combined comparison
- **Notes** — setup fields (spring, volume spacers, clicker positions) and free-text notes

## Compare Sessions

Select two sessions from the list to compare them side by side:

| ![Travel histograms](pics/compare_travel.png) | ![Balance comparison](pics/compare_balance.png) | ![Summary table](pics/compare_summary.png) |
|---|---|---|

The compare view includes travel histograms, front-vs-rear travel scatter, balance plots,
rebound/compression balance, low-speed velocity histograms, and full summary tables for
both front and rear wheel.

## Linkages & Calibrations

| ![Linkage](pics/linkage.png) | ![Calibration](pics/calibration.png) | | |
|---|---|---|---|

Leverage ratio curves and stroke calibrations can be defined per bike and are
applied automatically when analysing sessions.

## Limitations

- No interactive graphs, GPS map, video
- No internet connection required

\* *Pronounced SHOOF-nee dot bridge. Sufni means tool shed in Hungarian, but
also used as an adjective denoting something as DIY, garage hack, etc.*
