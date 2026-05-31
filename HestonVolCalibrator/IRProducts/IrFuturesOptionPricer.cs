using HestonVolCalibrator.SABR;

namespace HestonVolCalibrator.IRProducts
{
    // Prices options on short-rate futures (Eurodollar, CME SOFR 3M, ICE STIRs, etc.).
    //
    // Exchange-traded futures options are margined daily — there is no discounting
    // (df = 1). The payoff per contract at expiry in rate space:
    //   Call on rate: NotionalPerContract · PeriodFraction · max(L - K_rate, 0)
    //   Put on rate:  NotionalPerContract · PeriodFraction · max(K_rate - L, 0)
    //
    // Default convention is Normal (Bachelier) because:
    //  - CME and ICE typically quote vol in normal (bp) terms.
    //  - Rate levels are low enough that lognormal expansion can become unstable.
    //
    // Lognormal (Black76) is also supported for markets that quote that way.
    public static class IrFuturesOptionPricer
    {
        // Price using SABR parameters.
        public static double Price(
            IrFuturesOptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Normal)
        {
            double vol = GetSabrVol(sabr, spec.FuturesRate, spec.StrikeRate, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;
            return PriceWithVol(spec, vol, convention);
        }

        // Price using a known vol.
        public static double PriceWithVol(
            IrFuturesOptionSpec spec,
            double vol,
            VolConvention convention = VolConvention.Normal)
        {
            const double df = 1.0; // futures are marked to market daily — no discounting
            double unitPrice;

            if (convention == VolConvention.Normal)
            {
                unitPrice = spec.IsCap
                    ? BachelierModel.CallPrice(spec.FuturesRate, spec.StrikeRate, vol, spec.OptionExpiry, df)
                    : BachelierModel.PutPrice(spec.FuturesRate, spec.StrikeRate, vol, spec.OptionExpiry, df);
            }
            else
            {
                unitPrice = spec.IsCap
                    ? Black76.CallPrice(spec.FuturesRate, spec.StrikeRate, vol, spec.OptionExpiry, df)
                    : Black76.PutPrice(spec.FuturesRate, spec.StrikeRate, vol, spec.OptionExpiry, df);
            }

            return unitPrice * spec.PeriodFraction * spec.NotionalPerContract;
        }

        // Vega: change in price per unit change in vol.
        public static double Vega(
            IrFuturesOptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Normal)
        {
            double vol = GetSabrVol(sabr, spec.FuturesRate, spec.StrikeRate, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;

            const double df = 1.0;
            double rawVega = convention == VolConvention.Normal
                ? BachelierModel.Vega(spec.FuturesRate, spec.StrikeRate, vol, spec.OptionExpiry, df)
                : Black76.Vega(spec.FuturesRate, spec.StrikeRate, vol, spec.OptionExpiry, df);

            return rawVega * spec.PeriodFraction * spec.NotionalPerContract;
        }

        // ── Internal ─────────────────────────────────────────────────────────────

        private static double GetSabrVol(
            SabrParams p, double forward, double strike, double expiry,
            double shift, VolConvention convention)
        {
            double f = forward + shift;
            double k = strike + shift;
            return convention == VolConvention.Normal
                ? SabrPricer.NormalImpliedVol(p, f, k, expiry)
                : SabrPricer.ImpliedVol(p, f, k, expiry);
        }
    }
}
