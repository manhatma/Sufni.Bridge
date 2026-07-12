using System;

namespace Sufni.Bridge.Models.Telemetry;

public static class Parameters
{
    // (s) minimum duration to consider stroke an idle period
    public const double IdlingDurationThreshold = 0.10;

    // (s) minimum duration to consider stroke an airtime
    public const double AirtimeDurationThreshold = 0.20;

    // (s) maximum duration. A bike leaning, hanging or being carried has both elements extended
    // and dead still, and is set down hard enough to look like a landing — indistinguishable
    // from flight in a two-channel travel recording except by how long it lasts. Ballistics
    // bounds the real thing: 1.2 s of hang time needs a 1.8 m vertical launch. In the reference
    // corpus (307 sessions, 2859 airtimes) the longest hand-verified airtime is 1.05 s and this
    // bound rejects exactly one interval, a 1.45 s rest period.
    public const double AirtimeDurationMax = 1.2;

    // (mm/s) minimum velocity after stroke to consider it an airtime
    public const double AirtimeVelocityThreshold = 500;

    // f&r airtime candidates must overlap at least this fraction of the SHORTER one's duration
    public const double AirtimeOverlapThreshold = 0.5;

    // How far above its own top-out (as a fraction of max travel) an element may sit and still
    // count as resting there. Used both for the per-sample settled check (RestsAtTopOut) and
    // for the candidate gate on a stroke's mean travel in Strokes.Categorize. Generous, because
    // stiction leaves an element resting anywhere within ~5-10 mm of top-out (measured: the
    // same shock rests at 6.1 mm on one jump and 11.3 mm on the next; a fork whose session-wide
    // top-out is 0.0 mm stuck at 7.8 mm during a jump). The two gates deliberately share this
    // value: when the candidate gate was tighter (2.5%), jumps whose element stuck mid-band
    // passed the settled check but never became candidates in the first place.
    public const double AirtimeSettledTravelRatio = 0.08;

    // Fraction of the stroke each end must spend CONTIGUOUSLY at rest at its top-out. A run
    // rather than an average or a tail sample, because both ends of the bike are busy at the
    // stroke's edges and neither is at rest there: the far end is still unloading when the near
    // end tops out (a shock needs ~0.3 s to extend its last centimetre), and on a rear-wheel-
    // first landing it touches down again while the near end is still airborne. Only in between
    // is the bike unambiguously flying.
    public const double AirtimeSettleFraction = 0.25;

    // (mm/s) speed below which a suspension element counts as being at rest. This, not travel,
    // is what separates a wheel hanging in the air from one working the ground: an airborne
    // element only creeps towards top-out (measured: 2-50 mm/s), a grounded one is driven by the
    // terrain (measured: 400-1200 mm/s on a manual whose fork was pinned at top-out and whose
    // travel therefore looked airborne).
    public const double AirtimeQuiescentVelocity = 150;

    // (mm) how far ABOVE THE TOP-OUT POSITION a stroke's mean travel may sit and still be
    // an airtime. Measured against the mean, not the maximum: an airborne stroke begins
    // while the element is still extending, so its maximum includes that approach ramp
    // (up to 14 mm on the reference bike's shock, whose leverage ratio and low spring
    // force near top-out make the last centimetre of extension slow).
    public const double AirtimeTravelThreshold = 3;

    // (mm/s) drift rate tolerated inside an airborne stroke. Being airborne is a *rate*
    // property, not a displacement one: an unloaded element keeps creeping towards top-out
    // for a few hundred ms (measured: 13 mm/s mean over 0.77 s on the reference bike's
    // shock). Judging a 0.8 s hover by the same fixed 0.5 mm budget as a 0.1 s one demands
    // a drift below 0.6 mm/s — far under the measurement noise floor, let alone real
    // suspension behaviour. The budget is StrokeLengthThreshold + this * duration.
    public const double AirtimeCreepRate = 15.0;

    // Top-out estimation. A suspension element does not necessarily read 0 mm when fully
    // extended: calibration offsets, coil preload, top-out bumpers and the shock→wheel
    // polynomial all shift the fully-extended position (measured: 0.3 mm fork / 6.3 mm shock
    // on one bike, 4.2 mm / 5.0 mm on another). Absolute travel is therefore a poor airtime
    // criterion — the reference must be the element's OWN extended position, estimated as a
    // low quantile of the recorded travel. Every ride tops out repeatedly, so the low tail
    // of the travel distribution clusters around that position.
    public const double TopOutQuantile = 0.005;

    // The quantile above degenerates into "sag" if a session never tops out (e.g. a short
    // recording of seated pedalling). Capping it at this fraction of max travel keeps a
    // never-extended session from opening the airtime gate at its sag point.
    public const double TopOutMaxRatio = 0.06;

    // (mm) minimum length to consider stroke a compression/rebound
    public const double StrokeLengthThreshold = 0.5;

    // factor for top-out concatenation with respect to StrokeLengthThreshold
    public const double StrokeLengthThresholdFac = 30;

    // (mm/s) step between velocity histogram bins
    public const double VelocityHistStep = 100.0;

    // (mm/s) step between fine-grained velocity histogram bins
    public const double VelocityHistStepFine = 10.0;

    // (mm/s) step between rear shock/damper-domain velocity histogram bins. Finer than the
    // wheel-domain step because shaft velocities span a much smaller range.
    public const double DamperVelocityHistStep = 25.0;

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

    // Sample rate the λ constants above were calibrated at (ADS1115 continuous mode, 860 SPS).
    public const double WhReferenceSampleRate = 860.0;

    // The WH smoother operates on sample indices, so a fixed λ fixes the cutoff in f/fs and the
    // Hz cutoff would scale linearly with the device sample rate (which is read from the SST file
    // header and not pinned to 860). Keeping f_c constant in Hz requires λ ∝ fs^(2p), from
    // f_c/f_s ≈ (1/2π)·λ^(−1/2p). At exactly 860 SPS these return the raw constants.
    public static double WhLambdaFor(double sampleRate) =>
        sampleRate > 0 ? WhLambda * Math.Pow(sampleRate / WhReferenceSampleRate, 2.0 * WhOrder) : WhLambda;

    public static double WhAccelLambdaFor(double sampleRate) =>
        sampleRate > 0 ? WhAccelLambda * Math.Pow(sampleRate / WhReferenceSampleRate, 2.0 * WhAccelOrder) : WhAccelLambda;
}