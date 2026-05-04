using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class SuspensionSettings : ObservableObject
{
    // The persisted spring-rate string (e.g. "75.0 psi"). Kept as the single source of
    // truth so existing DB persistence and pending-change capture continue to work.
    // SpringRateValue + SpringRateUnit are bound to the UI and stay in sync via the
    // partial OnXxxChanged hooks below.
    [ObservableProperty] private string? springRate;

    [ObservableProperty] private double? springRateValue;
    [ObservableProperty] private string springRateUnit = SpringRateParser.UnitPsi;

    public IReadOnlyList<string> SpringRateUnitOptions { get; } = SpringRateParser.AllUnits;

    [RelayCommand]
    private void CycleSpringRateUnit()
    {
        var options = SpringRateUnitOptions;
        var idx = -1;
        for (var i = 0; i < options.Count; i++)
            if (options[i] == SpringRateUnit) { idx = i; break; }
        SpringRateUnit = options[(idx < 0 ? 0 : idx + 1) % options.Count];
    }

    private bool springRateUpdating;

    partial void OnSpringRateChanged(string? value)
    {
        if (springRateUpdating) return;
        if (SpringRateParser.TryParse(value, out var v, out var u))
        {
            springRateUpdating = true;
            try
            {
                SpringRateValue = v;
                // Empty unit = legacy bare-number entry; keep the current unit selection.
                if (!string.IsNullOrEmpty(u))
                    SpringRateUnit = u;
            }
            finally { springRateUpdating = false; }
        }
        else if (string.IsNullOrWhiteSpace(value))
        {
            springRateUpdating = true;
            try { SpringRateValue = null; }
            finally { springRateUpdating = false; }
        }
    }

    partial void OnSpringRateValueChanged(double? value) => UpdateSpringRateString();
    partial void OnSpringRateUnitChanged(string value) => UpdateSpringRateString();

    private void UpdateSpringRateString()
    {
        if (springRateUpdating) return;
        springRateUpdating = true;
        try
        {
            SpringRate = SpringRateParser.Format(SpringRateValue, SpringRateUnit);
        }
        finally { springRateUpdating = false; }
    }

    [ObservableProperty] private double? volSpc;

    partial void OnVolSpcChanged(double? value)
    {
        if (value.HasValue)
        {
            var rounded = Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
            if (rounded != value.Value)
                VolSpc = rounded;
        }
    }
    [ObservableProperty] private uint? highSpeedCompression;
    [ObservableProperty] private uint? lowSpeedCompression;
    [ObservableProperty] private uint? lowSpeedRebound;
    [ObservableProperty] private uint? highSpeedRebound;
    [ObservableProperty] private double? tirePressure;

    [ObservableProperty] private bool springRateEditing;
    [ObservableProperty] private bool volSpcEditing;
    [ObservableProperty] private bool highSpeedCompressionEditing;
    [ObservableProperty] private bool lowSpeedCompressionEditing;
    [ObservableProperty] private bool lowSpeedReboundEditing;
    [ObservableProperty] private bool highSpeedReboundEditing;
    [ObservableProperty] private bool tirePressureEditing;

    private string? springRateSnapshot;
    private double? volSpcSnapshot;
    private uint? hscSnapshot;
    private uint? lscSnapshot;
    private uint? lsrSnapshot;
    private uint? hsrSnapshot;
    private double? tirePressureSnapshot;

    public event EventHandler<(string FieldKey, object? Value)>? CommitRequested;

    // After capturing a pending change the field reverts to the snapshot — the
    // pending value is for the NEXT session, not the current one.
    partial void OnSpringRateEditingChanged(bool value)
    {
        if (value) { springRateSnapshot = SpringRate; return; }
        if (SpringRate == springRateSnapshot) return;
        CommitRequested?.Invoke(this, ("SpringRate", SpringRate));
        SpringRate = springRateSnapshot;
    }
    partial void OnVolSpcEditingChanged(bool value)
    {
        if (value) { volSpcSnapshot = VolSpc; return; }
        if (VolSpc == volSpcSnapshot) return;
        CommitRequested?.Invoke(this, ("VolSpc", VolSpc));
        VolSpc = volSpcSnapshot;
    }
    partial void OnHighSpeedCompressionEditingChanged(bool value)
    {
        if (value) { hscSnapshot = HighSpeedCompression; return; }
        if (HighSpeedCompression == hscSnapshot) return;
        CommitRequested?.Invoke(this, ("HSC", HighSpeedCompression));
        HighSpeedCompression = hscSnapshot;
    }
    partial void OnLowSpeedCompressionEditingChanged(bool value)
    {
        if (value) { lscSnapshot = LowSpeedCompression; return; }
        if (LowSpeedCompression == lscSnapshot) return;
        CommitRequested?.Invoke(this, ("LSC", LowSpeedCompression));
        LowSpeedCompression = lscSnapshot;
    }
    partial void OnLowSpeedReboundEditingChanged(bool value)
    {
        if (value) { lsrSnapshot = LowSpeedRebound; return; }
        if (LowSpeedRebound == lsrSnapshot) return;
        CommitRequested?.Invoke(this, ("LSR", LowSpeedRebound));
        LowSpeedRebound = lsrSnapshot;
    }
    partial void OnHighSpeedReboundEditingChanged(bool value)
    {
        if (value) { hsrSnapshot = HighSpeedRebound; return; }
        if (HighSpeedRebound == hsrSnapshot) return;
        CommitRequested?.Invoke(this, ("HSR", HighSpeedRebound));
        HighSpeedRebound = hsrSnapshot;
    }
    partial void OnTirePressureEditingChanged(bool value)
    {
        if (value) { tirePressureSnapshot = TirePressure; return; }
        if (TirePressure == tirePressureSnapshot) return;
        CommitRequested?.Invoke(this, ("TirePressure", TirePressure));
        TirePressure = tirePressureSnapshot;
    }
}

public partial class PendingChangeEntry : ObservableObject
{
    public string FieldKey { get; }
    public string Label { get; }
    public string ValueDisplay { get; }
    public object? Value { get; }
    public IRelayCommand RemoveCommand { get; }

    public PendingChangeEntry(string fieldKey, string label, string valueDisplay, object? value, Action<PendingChangeEntry> onRemove)
    {
        FieldKey = fieldKey;
        Label = label;
        ValueDisplay = valueDisplay;
        Value = value;
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }
}

public partial class NotesPageViewModel : PageViewModelBase
{
    [ObservableProperty] private string? description;

    public SuspensionSettings ForkSettings { get; } = new();
    public SuspensionSettings ShockSettings { get; } = new();
    public ObservableCollection<PendingChangeEntry> PendingChanges { get; } = new();

    /// <summary>
    /// Set by SessionViewModel — invoked after every user-driven edit to PendingChanges
    /// (capture or remove) so the row in pending_setup_changes is written immediately,
    /// not only when the session is saved.
    /// </summary>
    public Func<Task>? PersistPendingAsync { get; set; }

    public NotesPageViewModel() : base("Notes")
    {
        ForkSettings.CommitRequested += (_, e) => CapturePending("Front", e.FieldKey, e.Value);
        ShockSettings.CommitRequested += (_, e) => CapturePending("Rear", e.FieldKey, e.Value);
        PendingChanges.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingChanges));
    }

    public bool HasPendingChanges => PendingChanges.Count > 0;

    private void CapturePending(string side, string fieldKey, object? value)
    {
        var key = side + fieldKey;
        var existing = PendingChanges.FirstOrDefault(e => e.FieldKey == key);
        if (existing != null) PendingChanges.Remove(existing);

        if (value is null) return;
        if (value is string s && string.IsNullOrEmpty(s)) return;

        PendingChanges.Add(new PendingChangeEntry(
            key,
            side + " " + FieldDisplayName(fieldKey),
            FormatValue(fieldKey, value),
            value,
            RemoveEntry));

        _ = PersistPendingAsync?.Invoke();
    }

    private void RemoveEntry(PendingChangeEntry entry)
    {
        PendingChanges.Remove(entry);
        _ = PersistPendingAsync?.Invoke();
    }

    private static string FieldDisplayName(string key) => key switch
    {
        "SpringRate" => "Spring",
        "VolSpc" => "VolSpc",
        "TirePressure" => "Tire",
        _ => key
    };

    private static string FormatValue(string fieldKey, object value) => fieldKey switch
    {
        "VolSpc" or "TirePressure" => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", value),
        _ => value.ToString() ?? ""
    };

    public PendingSetupChanges BuildPending(Guid setupId)
    {
        var p = new PendingSetupChanges { SetupId = setupId };
        foreach (var entry in PendingChanges)
        {
            switch (entry.FieldKey)
            {
                case "FrontSpringRate": p.FrontSpringRate = entry.Value as string; break;
                case "FrontVolSpc": p.FrontVolSpc = entry.Value as double?; break;
                case "FrontHSC": p.FrontHighSpeedCompression = entry.Value as uint?; break;
                case "FrontLSC": p.FrontLowSpeedCompression = entry.Value as uint?; break;
                case "FrontLSR": p.FrontLowSpeedRebound = entry.Value as uint?; break;
                case "FrontHSR": p.FrontHighSpeedRebound = entry.Value as uint?; break;
                case "FrontTirePressure": p.FrontTirePressure = entry.Value as double?; break;
                case "RearSpringRate": p.RearSpringRate = entry.Value as string; break;
                case "RearVolSpc": p.RearVolSpc = entry.Value as double?; break;
                case "RearHSC": p.RearHighSpeedCompression = entry.Value as uint?; break;
                case "RearLSC": p.RearLowSpeedCompression = entry.Value as uint?; break;
                case "RearLSR": p.RearLowSpeedRebound = entry.Value as uint?; break;
                case "RearHSR": p.RearHighSpeedRebound = entry.Value as uint?; break;
                case "RearTirePressure": p.RearTirePressure = entry.Value as double?; break;
            }
        }
        return p;
    }

    public void LoadPending(PendingSetupChanges? p)
    {
        PendingChanges.Clear();
        if (p is null) return;

        void Add(string key, string label, string display, object? value) =>
            PendingChanges.Add(new PendingChangeEntry(key, label, display, value, RemoveEntry));

        if (p.FrontSpringRate is not null) Add("FrontSpringRate", "Front Spring", p.FrontSpringRate, p.FrontSpringRate);
        if (p.FrontVolSpc is not null) Add("FrontVolSpc", "Front VolSpc", FormatValue("VolSpc", p.FrontVolSpc), p.FrontVolSpc);
        if (p.FrontHighSpeedCompression is not null) Add("FrontHSC", "Front HSC", p.FrontHighSpeedCompression.ToString()!, p.FrontHighSpeedCompression);
        if (p.FrontLowSpeedCompression is not null) Add("FrontLSC", "Front LSC", p.FrontLowSpeedCompression.ToString()!, p.FrontLowSpeedCompression);
        if (p.FrontLowSpeedRebound is not null) Add("FrontLSR", "Front LSR", p.FrontLowSpeedRebound.ToString()!, p.FrontLowSpeedRebound);
        if (p.FrontHighSpeedRebound is not null) Add("FrontHSR", "Front HSR", p.FrontHighSpeedRebound.ToString()!, p.FrontHighSpeedRebound);
        if (p.FrontTirePressure is not null) Add("FrontTirePressure", "Front Tire", FormatValue("TirePressure", p.FrontTirePressure), p.FrontTirePressure);
        if (p.RearSpringRate is not null) Add("RearSpringRate", "Rear Spring", p.RearSpringRate, p.RearSpringRate);
        if (p.RearVolSpc is not null) Add("RearVolSpc", "Rear VolSpc", FormatValue("VolSpc", p.RearVolSpc), p.RearVolSpc);
        if (p.RearHighSpeedCompression is not null) Add("RearHSC", "Rear HSC", p.RearHighSpeedCompression.ToString()!, p.RearHighSpeedCompression);
        if (p.RearLowSpeedCompression is not null) Add("RearLSC", "Rear LSC", p.RearLowSpeedCompression.ToString()!, p.RearLowSpeedCompression);
        if (p.RearLowSpeedRebound is not null) Add("RearLSR", "Rear LSR", p.RearLowSpeedRebound.ToString()!, p.RearLowSpeedRebound);
        if (p.RearHighSpeedRebound is not null) Add("RearHSR", "Rear HSR", p.RearHighSpeedRebound.ToString()!, p.RearHighSpeedRebound);
        if (p.RearTirePressure is not null) Add("RearTirePressure", "Rear Tire", FormatValue("TirePressure", p.RearTirePressure), p.RearTirePressure);
    }

    public bool IsDirty(Session session)
    {
        return
            Description != session.Description ||
            (!(ForkSettings.SpringRate is null && session.FrontSpringRate is null) && ForkSettings.SpringRate != session.FrontSpringRate) ||
            (!(ForkSettings.HighSpeedCompression is null && session.FrontHighSpeedCompression is null) && ForkSettings.HighSpeedCompression != session.FrontHighSpeedCompression) ||
            (!(ForkSettings.LowSpeedCompression is null && session.FrontLowSpeedCompression is null) && ForkSettings.LowSpeedCompression != session.FrontLowSpeedCompression) ||
            (!(ForkSettings.LowSpeedRebound is null && session.FrontLowSpeedRebound is null) && ForkSettings.LowSpeedRebound != session.FrontLowSpeedRebound) ||
            (!(ForkSettings.HighSpeedRebound is null && session.FrontHighSpeedRebound is null) && ForkSettings.HighSpeedRebound != session.FrontHighSpeedRebound) ||
            (ForkSettings.VolSpc ?? 0) != (session.FrontVolSpc ?? 0) ||
            (!(ShockSettings.SpringRate is null && session.RearSpringRate is null) && ShockSettings.SpringRate != session.RearSpringRate) ||
            (!(ShockSettings.HighSpeedCompression is null && session.RearHighSpeedCompression is null) && ShockSettings.HighSpeedCompression != session.RearHighSpeedCompression) ||
            (!(ShockSettings.LowSpeedCompression is null && session.RearLowSpeedCompression is null) && ShockSettings.LowSpeedCompression != session.RearLowSpeedCompression) ||
            (!(ShockSettings.LowSpeedRebound is null && session.RearLowSpeedRebound is null) && ShockSettings.LowSpeedRebound != session.RearLowSpeedRebound) ||
            (!(ShockSettings.HighSpeedRebound is null && session.RearHighSpeedRebound is null) && ShockSettings.HighSpeedRebound != session.RearHighSpeedRebound) ||
            (ShockSettings.VolSpc ?? 0) != (session.RearVolSpc ?? 0) ||
            (ForkSettings.TirePressure ?? 0) != (session.FrontTirePressure ?? 0) ||
            (ShockSettings.TirePressure ?? 0) != (session.RearTirePressure ?? 0);
    }
}
