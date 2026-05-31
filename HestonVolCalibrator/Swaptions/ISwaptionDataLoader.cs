using System;
using System.Threading;
using System.Threading.Tasks;
using HestonVolCalibrator.SABR;

namespace HestonVolCalibrator.Swaptions
{
    // ── Request ────────────────────────────────────────────────────────────────────
    public record SwaptionLoadRequest
    {
        // Date to fetch data for; null = use most recent available.
        public DateTime? AsOf { get; init; } = null;

        // Option expiries (years) for the vol cube rows.
        public double[] OptionExpiries { get; init; } = [0.25, 0.5, 1.0, 2.0, 5.0, 10.0];

        // Swap tenors (years) for the vol cube columns.
        public double[] SwapTenors { get; init; } = [1.0, 2.0, 5.0, 10.0, 30.0];

        // Strikes expressed as offsets from ATM in bps.
        // A value of 0 means ATMF; -100 means 100bps below forward swap rate; +200 = 200bps above.
        public double[] StrikeOffsetsBps { get; init; } = [-200, -100, -50, 0, 50, 100, 200];

        // Vol convention to use for the output surface and for SABR calibration.
        public VolConvention VolConvention { get; init; } = VolConvention.Normal;

        // Override for the ATM normal vol level (in decimal, e.g. 0.0100 = 100bps).
        // When null the loader estimates it from the rate level.
        public double? AtmNormalVolOverride { get; init; } = null;

        // SABR parameters used to generate the synthetic smile skew.
        // When null the loader uses its own defaults.
        public SabrParams? SyntheticSabrOverride { get; init; } = null;

        // Annualised coupon frequency for swap rate bootstrap (2 = semi-annual, 4 = quarterly).
        public int CouponFrequency { get; init; } = 2;

        // When true, skip all network fetches and return hardcoded typical rates immediately.
        public bool ForceSynthetic { get; init; } = false;
    }

    // ── Output data model ──────────────────────────────────────────────────────────

    // A single point on the bootstrapped yield/discount curve.
    public record DiscountPoint(double MaturityYears, double DiscountFactor, double ZeroRate);

    // A UST/swap par rate quote used as input to the bootstrap.
    public record ParRatePoint(double TenorYears, double ParRate);

    // One cell of the swaption vol cube: (optionExpiry, swapTenor) → vol smile.
    public record SwaptionVolPoint(
        double OptionExpiry,    // years to option exercise
        double SwapTenor,       // tenor of the underlying swap (years)
        double ForwardSwapRate, // par rate of the forward-starting swap
        double Annuity,         // Σ df(t_i) × δ_i
        double[] Strikes,       // absolute rate strikes (converted from bps offset)
        double[] MarketVols,    // implied vol at each strike
        VolConvention Convention,
        bool IsSynthetic,       // true when vol is generated, not exchange-quoted
        SabrParams? SabrFit);   // SABR params if the smile was calibrated, else null

    // Full swaption market snapshot returned by the loader.
    public record SwaptionMarketData(
        DateTime AsOf,
        string Source,
        ParRatePoint[] ParRates,        // raw market swap/treasury rates
        DiscountPoint[] DiscountCurve,  // bootstrapped discount factors
        SwaptionVolPoint[] VolSurface); // [option_expiry × swap_tenor] vol cube cells

    // ── Interface ─────────────────────────────────────────────────────────────────

    // Data loader interface for swaption market data.
    // Concrete implementations can source data from:
    //   - US Treasury yield curve (no key, free)
    //   - FRED API (free API key required)
    //   - Bloomberg / Refinitiv / ICE (commercial feeds)
    //   - Synthetic / test data (deterministic, no network)
    public interface ISwaptionDataLoader
    {
        // Load a swaption market snapshot for the given request.
        // Implementations may use caching internally; callers should check AsOf on the result.
        Task<SwaptionMarketData> LoadAsync(SwaptionLoadRequest request, CancellationToken ct = default);
    }
}
