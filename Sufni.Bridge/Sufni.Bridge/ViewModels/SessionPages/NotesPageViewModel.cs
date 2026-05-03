using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class SuspensionSettings : ObservableObject
{
    [ObservableProperty] private string? springRate;
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

    partial void OnSpringRateEditingChanged(bool value)
    {
        if (value) springRateSnapshot = SpringRate;
        else if (SpringRate != springRateSnapshot)
            CommitRequested?.Invoke(this, ("SpringRate", SpringRate));
    }
    partial void OnVolSpcEditingChanged(bool value)
    {
        if (value) volSpcSnapshot = VolSpc;
        else if (VolSpc != volSpcSnapshot)
            CommitRequested?.Invoke(this, ("VolSpc", VolSpc));
    }
    partial void OnHighSpeedCompressionEditingChanged(bool value)
    {
        if (value) hscSnapshot = HighSpeedCompression;
        else if (HighSpeedCompression != hscSnapshot)
            CommitRequested?.Invoke(this, ("HSC", HighSpeedCompression));
    }
    partial void OnLowSpeedCompressionEditingChanged(bool value)
    {
        if (value) lscSnapshot = LowSpeedCompression;
        else if (LowSpeedCompression != lscSnapshot)
            CommitRequested?.Invoke(this, ("LSC", LowSpeedCompression));
    }
    partial void OnLowSpeedReboundEditingChanged(bool value)
    {
        if (value) lsrSnapshot = LowSpeedRebound;
        else if (LowSpeedRebound != lsrSnapshot)
            CommitRequested?.Invoke(this, ("LSR", LowSpeedRebound));
    }
    partial void OnHighSpeedReboundEditingChanged(bool value)
    {
        if (value) hsrSnapshot = HighSpeedRebound;
        else if (HighSpeedRebound != hsrSnapshot)
            CommitRequested?.Invoke(this, ("HSR", HighSpeedRebound));
    }
    partial void OnTirePressureEditingChanged(bool value)
    {
        if (value) tirePressureSnapshot = TirePressure;
        else if (TirePressure != tirePressureSnapshot)
            CommitRequested?.Invoke(this, ("TirePressure", TirePressure));
    }
}

public partial class PendingChangeEntry : ObservableObject
{
    public string FieldKey { get; }
    public string Label { get; }
    public string ValueDisplay { get; }
    public IRelayCommand RemoveCommand { get; }

    public PendingChangeEntry(string fieldKey, string label, string valueDisplay, Action<PendingChangeEntry> onRemove)
    {
        FieldKey = fieldKey;
        Label = label;
        ValueDisplay = valueDisplay;
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }
}

public partial class NotesPageViewModel : PageViewModelBase
{
    [ObservableProperty] private string? description;

    public SuspensionSettings ForkSettings { get; } = new();
    public SuspensionSettings ShockSettings { get; } = new();
    public ObservableCollection<PendingChangeEntry> PendingChanges { get; } = new();

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
            RemoveEntry));
    }

    private void RemoveEntry(PendingChangeEntry entry) => PendingChanges.Remove(entry);

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
                case "FrontSpringRate": p.FrontSpringRate = ForkSettings.SpringRate; break;
                case "FrontVolSpc": p.FrontVolSpc = ForkSettings.VolSpc; break;
                case "FrontHSC": p.FrontHighSpeedCompression = ForkSettings.HighSpeedCompression; break;
                case "FrontLSC": p.FrontLowSpeedCompression = ForkSettings.LowSpeedCompression; break;
                case "FrontLSR": p.FrontLowSpeedRebound = ForkSettings.LowSpeedRebound; break;
                case "FrontHSR": p.FrontHighSpeedRebound = ForkSettings.HighSpeedRebound; break;
                case "FrontTirePressure": p.FrontTirePressure = ForkSettings.TirePressure; break;
                case "RearSpringRate": p.RearSpringRate = ShockSettings.SpringRate; break;
                case "RearVolSpc": p.RearVolSpc = ShockSettings.VolSpc; break;
                case "RearHSC": p.RearHighSpeedCompression = ShockSettings.HighSpeedCompression; break;
                case "RearLSC": p.RearLowSpeedCompression = ShockSettings.LowSpeedCompression; break;
                case "RearLSR": p.RearLowSpeedRebound = ShockSettings.LowSpeedRebound; break;
                case "RearHSR": p.RearHighSpeedRebound = ShockSettings.HighSpeedRebound; break;
                case "RearTirePressure": p.RearTirePressure = ShockSettings.TirePressure; break;
            }
        }
        return p;
    }

    public void LoadPending(PendingSetupChanges? p)
    {
        PendingChanges.Clear();
        if (p is null) return;

        void Add(string key, string label, string display) =>
            PendingChanges.Add(new PendingChangeEntry(key, label, display, RemoveEntry));

        if (p.FrontSpringRate is not null) Add("FrontSpringRate", "Front Spring", p.FrontSpringRate);
        if (p.FrontVolSpc is not null) Add("FrontVolSpc", "Front VolSpc", FormatValue("VolSpc", p.FrontVolSpc));
        if (p.FrontHighSpeedCompression is not null) Add("FrontHSC", "Front HSC", p.FrontHighSpeedCompression.ToString()!);
        if (p.FrontLowSpeedCompression is not null) Add("FrontLSC", "Front LSC", p.FrontLowSpeedCompression.ToString()!);
        if (p.FrontLowSpeedRebound is not null) Add("FrontLSR", "Front LSR", p.FrontLowSpeedRebound.ToString()!);
        if (p.FrontHighSpeedRebound is not null) Add("FrontHSR", "Front HSR", p.FrontHighSpeedRebound.ToString()!);
        if (p.FrontTirePressure is not null) Add("FrontTirePressure", "Front Tire", FormatValue("TirePressure", p.FrontTirePressure));
        if (p.RearSpringRate is not null) Add("RearSpringRate", "Rear Spring", p.RearSpringRate);
        if (p.RearVolSpc is not null) Add("RearVolSpc", "Rear VolSpc", FormatValue("VolSpc", p.RearVolSpc));
        if (p.RearHighSpeedCompression is not null) Add("RearHSC", "Rear HSC", p.RearHighSpeedCompression.ToString()!);
        if (p.RearLowSpeedCompression is not null) Add("RearLSC", "Rear LSC", p.RearLowSpeedCompression.ToString()!);
        if (p.RearLowSpeedRebound is not null) Add("RearLSR", "Rear LSR", p.RearLowSpeedRebound.ToString()!);
        if (p.RearHighSpeedRebound is not null) Add("RearHSR", "Rear HSR", p.RearHighSpeedRebound.ToString()!);
        if (p.RearTirePressure is not null) Add("RearTirePressure", "Rear Tire", FormatValue("TirePressure", p.RearTirePressure));
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
