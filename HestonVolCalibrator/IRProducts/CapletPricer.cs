using HestonVolCalibrator.SABR;

namespace HestonVolCalibrator.IRProducts
{
    // Prices caplets and floorlets using a SABR vol surface.
    // Black76 backbone: df · δ · Black76_Call/Put(F, K, σ_SABR, T)
    // Bachelier backbone: df · δ · Bachelier_Call/Put(F, K, σ_N_SABR, T)
    public static class CapletPricer
    {
        // Price a caplet/floorlet using SABR parameters.
        // Returns price per unit of notional × period (multiply by notional separately).
        public static double Price(
            CapletSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;
            return PriceWithVol(spec, vol, convention);
        }

        // Price using an already-known implied vol (e.g. direct market quote).
        public static double PriceWithVol(
            CapletSpec spec,
            double vol,
            VolConvention convention = VolConvention.Lognormal)
        {
            double pv;
            if (convention == VolConvention.Normal)
            {
                pv = spec.IsCap
                    ? BachelierModel.CallPrice(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor)
                    : BachelierModel.PutPrice(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor);
            }
            else
            {
                pv = spec.IsCap
                    ? Black76.CallPrice(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor)
                    : Black76.PutPrice(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor);
            }
            return pv * spec.PeriodLength;
        }

        // DV01 per basis-point of vol (vega).
        public static double Vega(
            CapletSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;

            double rawVega = convention == VolConvention.Normal
                ? BachelierModel.Vega(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor)
                : Black76.Vega(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor);

            return rawVega * spec.PeriodLength;
        }

        // Forward delta (sensitivity to forward rate move).
        public static double Delta(
            CapletSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;

            double rawDelta = convention == VolConvention.Normal
                ? BachelierModel.CallDelta(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor)
                : Black76.CallDelta(spec.ForwardRate, spec.Strike, vol, spec.OptionExpiry, spec.DiscountFactor);

            return (spec.IsCap ? rawDelta : rawDelta - spec.DiscountFactor) * spec.PeriodLength;
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
