using System;
using HestonVolCalibrator.Implementations;

namespace HestonVolCalibrator.IRProducts
{
    // Black (1976) model for forward-based IR instruments (caplets, floorlets, swaptions).
    //
    // Call (cap / payer): df · [F·N(d1) - K·N(d2)]
    // Put  (floor / receiver): df · [K·N(-d2) - F·N(-d1)]
    //   d1 = (ln(F/K) + ½σ²T) / (σ√T),  d2 = d1 - σ√T
    //
    // Prices are per-unit. Multiply by accrual fraction and notional at the call site.
    public static class Black76
    {

        // Call (cap / payer swaption) price.
        public static double CallPrice(
            double forward, double strike, double vol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || forward <= 0.0 || strike <= 0.0)
                return Math.Max(forward - strike, 0.0) * discountFactor;
            if (vol <= 0.0)
                return Math.Max(forward - strike, 0.0) * discountFactor;

            (double d1, double d2) = D1D2(forward, strike, vol, expiry);
            return discountFactor * (forward * Ncdf(d1) - strike * Ncdf(d2));
        }

        // Put (floor / receiver swaption) price.
        public static double PutPrice(
            double forward, double strike, double vol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || forward <= 0.0 || strike <= 0.0)
                return Math.Max(strike - forward, 0.0) * discountFactor;
            if (vol <= 0.0)
                return Math.Max(strike - forward, 0.0) * discountFactor;

            (double d1, double d2) = D1D2(forward, strike, vol, expiry);
            return discountFactor * (strike * Ncdf(-d2) - forward * Ncdf(-d1));
        }

        // Vega: df · F · φ(d1) · √T.
        public static double Vega(
            double forward, double strike, double vol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || vol <= 0.0 || forward <= 0.0 || strike <= 0.0) return 0.0;
            (double d1, _) = D1D2(forward, strike, vol, expiry);
            return discountFactor * forward * Npdf(d1) * Math.Sqrt(expiry);
        }

        // Black76 call delta: df · N(d1).
        public static double CallDelta(
            double forward, double strike, double vol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || vol <= 0.0 || forward <= 0.0 || strike <= 0.0)
                return (forward > strike ? 1.0 : 0.0) * discountFactor;
            (double d1, _) = D1D2(forward, strike, vol, expiry);
            return discountFactor * Ncdf(d1);
        }

        // Gamma: df · φ(d1) / (F · σ · √T).
        public static double Gamma(
            double forward, double strike, double vol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || vol <= 0.0 || forward <= 0.0 || strike <= 0.0) return 0.0;
            (double d1, _) = D1D2(forward, strike, vol, expiry);
            return discountFactor * Npdf(d1) / (forward * vol * Math.Sqrt(expiry));
        }

        // Vanna: ∂²V/(∂F ∂σ) = −df · φ(d1) · d2 / σ.
        // Equals both ∂Delta/∂σ and ∂Vega/∂F.
        public static double Vanna(
            double forward, double strike, double vol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || vol <= 0.0 || forward <= 0.0 || strike <= 0.0) return 0.0;
            (double d1, double d2) = D1D2(forward, strike, vol, expiry);
            return -discountFactor * Npdf(d1) * d2 / vol;
        }

        // Volga (Vomma): ∂²V/∂σ² = Vega · d1·d2 / σ.
        public static double Volga(
            double forward, double strike, double vol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || vol <= 0.0 || forward <= 0.0 || strike <= 0.0) return 0.0;
            (double d1, double d2) = D1D2(forward, strike, vol, expiry);
            return discountFactor * forward * Npdf(d1) * Math.Sqrt(expiry) * d1 * d2 / vol;
        }

        // Solve for lognormal vol from a call price (Newton-Raphson with bisection fallback).
        public static double ImpliedVol(
            double callPrice, double forward, double strike, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || forward <= 0.0 || strike <= 0.0) return double.NaN;
            double intrinsic = Math.Max(forward - strike, 0.0) * discountFactor;
            double upper = forward * discountFactor;
            if (callPrice <= intrinsic + 1e-12 || callPrice >= upper - 1e-12) return double.NaN;

            // Initial guess via Brenner-Subrahmanyam approximation
            double vol = Math.Sqrt(2.0 * Math.PI / expiry) * callPrice / (forward * discountFactor);
            vol = Math.Max(1e-5, Math.Min(vol, 10.0));

            double lo = 1e-5, hi = 10.0;
            for (int i = 0; i < 100; i++)
            {
                double model = CallPrice(forward, strike, vol, expiry, discountFactor);
                double diff = model - callPrice;
                if (Math.Abs(diff) < 1e-12) return vol;
                if (diff > 0.0) hi = Math.Min(hi, vol);
                else            lo = Math.Max(lo, vol);
                double vega = Vega(forward, strike, vol, expiry, discountFactor);
                double next = vega > 1e-14 ? vol - diff / vega : 0.5 * (lo + hi);
                vol = (next <= lo || next >= hi) ? 0.5 * (lo + hi) : next;
                if (Math.Abs(hi - lo) < 1e-10) break;
            }
            return vol;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static (double d1, double d2) D1D2(double f, double k, double vol, double t)
        {
            double sqrtT = Math.Sqrt(t);
            double d1 = (Math.Log(f / k) + 0.5 * vol * vol * t) / (vol * sqrtT);
            return (d1, d1 - vol * sqrtT);
        }

        // Delegate to the project-wide normal CDF / PDF to keep a single implementation.
        internal static double Ncdf(double x) => BlackScholes.NormalCdf(x);
        internal static double Npdf(double x) => BlackScholes.NormalPdf(x);
    }
}
