using HestonVolCalibrator.SABR;

namespace HestonVolCalibrator.IRProducts
{
    // ── Caplet / Floorlet ─────────────────────────────────────────────────────────
    // Pays δ · max(L_i - K, 0) at t_{i+1}  (caplet) or δ · max(K - L_i, 0)  (floorlet).
    // Priced via Black76: df · δ · Black76_Call(F_i, K, σ, T_i)
    public record CapletSpec(
        double ForwardRate,    // F_i: forward rate for the accrual period [t_i, t_{i+1}]
        double Strike,         // K: strike rate
        double OptionExpiry,   // T_i: time to fixing / exercise in years
        double PeriodLength,   // δ: accrual fraction (e.g. 0.25 for 3M ACT/360)
        double DiscountFactor, // df(t_{i+1}): discount factor to payment date
        bool IsCap = true      // true = caplet (call on rate), false = floorlet (put)
    );

    // ── Swaption ──────────────────────────────────────────────────────────────────
    // European option on a fixed-for-floating swap.
    // Payer:   right to pay fixed K — call on swap rate.
    // Receiver: right to receive fixed K — put on swap rate.
    // Price = A · Black76_Call(F_s, K, σ, T)   where A = annuity.
    public record SwaptionSpec(
        double ForwardSwapRate, // F_s: par rate of the underlying forward swap
        double Strike,          // K: exercise strike
        double OptionExpiry,    // T: time to option expiry in years
        double Annuity,         // A = Σ df(t_i) · δ_i over the swap's payment dates
        bool IsPayer = true     // true = payer (call on rate), false = receiver (put)
    );

    // ── IR Futures Option ─────────────────────────────────────────────────────────
    // Options on short-rate futures (CME Eurodollar, CME SOFR 3M, ICE STIRs, etc.).
    // Futures price convention: P = 100·(1 - L), so L = 1 - P/100.
    // Both strike and forward are expressed as rates (not prices) for SABR consistency.
    // Pricing: options on exchange-traded futures carry no discounting (daily P&L settlement).
    // Payoff per contract = NotionalPerContract · PeriodFraction · max(L - K_rate, 0).
    public record IrFuturesOptionSpec(
        double FuturesRate,           // L = 1 - FuturesPrice/100
        double StrikeRate,            // K_rate = 1 - StrikePrice/100
        double OptionExpiry,          // time to last trading day in years
        double PeriodFraction,        // δ: notional multiplier — 0.25 for 3M contracts
        double NotionalPerContract,   // e.g. 1_000_000 for USD Eurodollar
        bool IsCap = true             // true = call on rate (floor on futures price), false = put on rate
    );

    // ── Exchange vol slices (calibration inputs) ──────────────────────────────────

    // Generic single-expiry vol smile as quoted by an exchange or broker.
    // Used as input to SabrCalibrator.Calibrate().
    public record VolSlice(
        double Forward,         // forward rate
        double Expiry,          // time to expiry in years
        double[] Strikes,       // absolute rate strikes
        double[] MarketVols,    // quoted implied vols (lognormal or normal, per Convention)
        VolConvention Convention = VolConvention.Lognormal,
        double Shift = 0.0);    // displacement for shifted-SABR

    // Cap/floor vol slice: one expiry, multiple strikes quoted by an exchange.
    // Represents a single row of the cap/floor vol matrix (e.g. 1Y expiry, ATMF ± offsets).
    public record CapFloorVolSlice(
        double ForwardRate,
        double CapletExpiry,
        double PeriodLength,
        double DiscountFactor,
        double[] Strikes,
        double[] MarketVols,
        VolConvention Convention = VolConvention.Lognormal,
        double Shift = 0.0);

    // Swaption vol cube entry: one (option_expiry, swap_tenor) cell, multiple strikes.
    // The vol cube is formed by assembling SwaptionVolSlice[] across expiries and tenors.
    public record SwaptionVolSlice(
        double ForwardSwapRate,
        double OptionExpiry,
        double SwapTenorYears,  // tenor label only (e.g. 10.0 for 10Y swap)
        double Annuity,
        double[] Strikes,
        double[] MarketVols,
        VolConvention Convention = VolConvention.Lognormal,
        double Shift = 0.0);

    // IR futures vol smile: one delivery month, multiple strikes in rate space.
    // CME publishes these as normal (bp) vols; hence Convention defaults to Normal.
    public record IrFuturesVolSlice(
        double FuturesRate,
        double OptionExpiry,
        double PeriodFraction,
        double NotionalPerContract,
        double[] RateStrikes,
        double[] MarketVols,
        VolConvention Convention = VolConvention.Normal,
        double Shift = 0.0);
}
