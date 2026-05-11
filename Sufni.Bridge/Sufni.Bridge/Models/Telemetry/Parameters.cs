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

    // Single-sample velocity spike rejection — deviation-from-linear-interpolation test.
    // The Taylor expansion of a smooth signal f(t) gives
    //   |f[i] − (f[i-1]+f[i+1])/2|  ≈  ½·dt²·|f''(t)|
    // Since the filter operates on the VELOCITY array, f''(t) is d²v/dt² = jerk (rate
    // of change of acceleration), not acceleration itself. The test
    //   |v[i] − (v[i-1]+v[i+1])/2|  >  ½·dt²·SpikeJerkLimit
    // therefore reads "implied instantaneous jerk exceeds the physical bound", and the
    // local velocity gradient drops out of the Taylor expansion so legitimate fast
    // transitions (steep rising/falling edges of real impacts) pass without any heuristic
    // span-tolerance term. The dt² scaling makes the filter sample-rate invariant.
    //
    // Raw-signal verification (SST file 00121, peak event at t=46.47 s) showed real
    // multi-sample compression peaks producing 5-tap-CD wheel velocities of ~14 m/s with
    // a per-sample deviation of ~1440 mm/s around the peak. SpikeJerkLimit = 2·10⁹
    // (floor 1353 mm/s @ 860 SPS) was therefore borderline-clipping real impact peaks.
    // 5·10⁹ mm/s³ (floor 3382 mm/s @ 860 SPS) sits ~2× above the strongest observed real
    // deviation and still catches isolated 1-sample ADC glitches (implied jerks orders
    // of magnitude higher than physical mechanics).
    public const double SpikeJerkLimit = 5.0e9;     // mm/s³ ≈ 510 000 g/s

    // Whittaker-Henderson smoother used as a pre-filter for the acceleration plot.
    // Acceleration is the second derivative of travel; its noise gain ∝ ω². The
    // velocity-tuned WH (order 3, λ=11) leaves enough 30–93 Hz content that, when
    // differentiated a second time, produces unphysical g-peaks (~95 g rear in active
    // riding). Pre-smoothing the velocity with this stronger WH (cutoff ≈29 Hz @ 860 SPS)
    // places the effective bandwidth just below the suspension's mechanical response
    // (~30–40 Hz) — preserves real impact peaks (10–25 Hz fundamental) while suppressing
    // residual differentiation noise.
    public const int WhAccelOrder = 3;
    public const double WhAccelLambda = 10000.0;
}