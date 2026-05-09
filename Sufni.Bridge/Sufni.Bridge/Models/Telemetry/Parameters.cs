namespace Sufni.Bridge.Models.Telemetry;

public static class Parameters
{
    // (s) minimum duration to consider stroke an idle period
    public const double IdlingDurationThreshold = 0.10;

    // (s) minimum duration to consider stroke an airtime
    public const double AirtimeDurationThreshold = 0.20;

    // (mm/s) minimum velocity after stroke to consider it an airtime
    public const double AirtimeVelocityThreshold = 500;

    // f&r airtime candidates must overlap at least this amount to be an airtime
    public const double AirtimeOverlapThreshold = 0.5;

    // stroke f&r mean travel must be below max*this to be an airtime
    public const double AirtimeTravelMeanThresholdRatio = 0.08;

    // (mm) maximum travel to consider stroke an airtime
    public const double AirtimeTravelThreshold = 3;

    // (mm) minimum length to consider stroke a compression/rebound
    public const double StrokeLengthThreshold = 0.5;

    // factor for top-out concatenation with respect to StrokeLengthThreshold
    public const double StrokeLengthThresholdFac = 30;

    // (mm/s) step between velocity histogram bins
    public const double VelocityHistStep = 100.0;

    // (mm/s) step between fine-grained velocity histogram bins
    public const double VelocityHistStepFine = 10.0;

    // (mm) bottom-outs are regions where travel > max_travel - this value
    public const double BottomoutThreshold = 2.5;

    // number of travel histogram bins
    public const int TravelHistBins = 20;

    // Whittaker-Henderson smoother for travel→velocity differentiation.
    // Setup: ADS1115 PGA 4.096V, sensor swing 0–3.3V → 26400 usable codes (log2 = 14.6883).
    // VLP200 fork: 7.58 µm/LSB, sub-LSB threshold 6.5 mm/s.
    // ELPM75 shock: 2.84 µm/LSB on shock travel, 2.4 mm/s sub-LSB threshold (rear pipeline
    // smooths shock travel before the leverage polynomial to keep this finer quantisation).
    // f_c/f_s ≈ (1/2π)·λ^(−1/2p): order 3, λ 11 → −3 dB at ~80 Hz @ 860 SPS, steeper roll-off
    // than the previous (2, 5) at the same cutoff so the central-difference noise gain
    // above f_s/4 is suppressed without sacrificing impulse fidelity on rock/square-edge hits.
    public const int WhOrder = 3;
    public const double WhLambda = 11.0;

    // Single-sample velocity spike rejection. Real shock motion has finite mass and
    // acceleration, so a sample whose magnitude dwarfs both immediate neighbours is
    // non-physical (ADC glitch, broken contact). Multi-sample fast events survive
    // because each member of the burst is supported by at least one same-burst neighbour.
    // Trigger: |v[i]| > Factor * max(|v[i-1]|,|v[i+1]|) + Floor  (mm/s)
    public const double SpikeRejectionFactor = 3.0;
    public const double SpikeRejectionFloor = 2000.0;
}