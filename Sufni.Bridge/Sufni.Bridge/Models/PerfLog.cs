using System;
using System.Diagnostics;

namespace Sufni.Bridge.Models;

/// <summary>
/// Lightweight, DEBUG-only performance instrumentation. Output goes through
/// Debug.WriteLine, and the Log call sites are compiled away entirely in
/// Release builds via [Conditional("DEBUG")]. Observational only — must never
/// affect analysis results or stored data.
/// </summary>
public static class PerfLog
{
    [Conditional("DEBUG")]
    public static void Log(string label, double ms)
    {
        Debug.WriteLine($"[perf] {label}: {ms:F1} ms");
    }

    /// <summary>
    /// Measures wall-clock time of a scope: <c>using var _ = PerfLog.Measure("label");</c>
    /// </summary>
    public static Scope Measure(string label) => new(label);

    public readonly struct Scope : IDisposable
    {
        private readonly string label;
        private readonly long startTimestamp;

        internal Scope(string label)
        {
            this.label = label;
            startTimestamp = Stopwatch.GetTimestamp();
        }

        public void Dispose() => Log(label, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
    }
}
