using HestonVolCalibrator.SABR;

namespace HestonVolCalibrator.IRProducts
{
    // Prices European swaptions using a SABR vol surface.
    //
    // Black76 payer:    A · Black76_Call(F_s, K, σ, T)
    // Black76 receiver: A · Black76_Put(F_s, K, σ, T)
    //
    // Bachelier payer:    A · Bachelier_Call(F_s, K, σ_N, T)
    // Bachelier receiver: A · Bachelier_Put(F_s, K, σ_N, T)
    //
    // The annuity A is passed as the "discount factor" argument to Black76/Bachelier.
    // This is the standard model-independent definition of the swaption measure.
    public static class SwaptionPricer
    {
        // Price a swaption using SABR parameters.
        public static double Price(
            SwaptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardSwapRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;
            return PriceWithVol(spec, vol, convention);
        }

        // Price using an already-known implied vol.
        public static double PriceWithVol(
            SwaptionSpec spec,
            double vol,
            VolConvention convention = VolConvention.Lognormal)
        {
            if (convention == VolConvention.Normal)
            {
                return spec.IsPayer
                    ? BachelierModel.CallPrice(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity)
                    : BachelierModel.PutPrice(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity);
            }

            return spec.IsPayer
                ? Black76.CallPrice(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity)
                : Black76.PutPrice(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity);
        }

        // Vega: sensitivity of price to a 1-unit move in implied vol.
        public static double Vega(
            SwaptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardSwapRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;
            return convention == VolConvention.Normal
                ? BachelierModel.Vega(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity)
                : Black76.Vega(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity);
        }

        // Forward delta: sensitivity to forward swap rate.
        public static double Delta(
            SwaptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardSwapRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;

            double callDelta = convention == VolConvention.Normal
                ? BachelierModel.CallDelta(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity)
                : Black76.CallDelta(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity);

            return spec.IsPayer ? callDelta : callDelta - spec.Annuity;
        }

        // Gamma: second derivative of price to forward swap rate.
        public static double Gamma(
            SwaptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardSwapRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;
            return convention == VolConvention.Normal
                ? BachelierModel.Gamma(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity)
                : Black76.Gamma(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity);
        }

        // Vanna: ∂²V/(∂F ∂σ) — sensitivity of delta to vol and of vega to forward.
        public static double Vanna(
            SwaptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardSwapRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;
            return convention == VolConvention.Normal
                ? BachelierModel.Vanna(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity)
                : Black76.Vanna(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity);
        }

        // Volga: ∂²V/∂σ² — sensitivity of vega to vol.
        public static double Volga(
            SwaptionSpec spec,
            SabrParams sabr,
            double shift = 0.0,
            VolConvention convention = VolConvention.Lognormal)
        {
            double vol = GetSabrVol(sabr, spec.ForwardSwapRate, spec.Strike, spec.OptionExpiry, shift, convention);
            if (double.IsNaN(vol)) return double.NaN;
            return convention == VolConvention.Normal
                ? BachelierModel.Volga(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity)
                : Black76.Volga(spec.ForwardSwapRate, spec.Strike, vol, spec.OptionExpiry, spec.Annuity);
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
