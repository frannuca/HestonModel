using System;
using HestonVolCalibrator.Implementations;

namespace HestonVolCalibrator.IRProducts
{
    // Bachelier (normal) model for IR instruments in low / negative rate environments.
    //
    // Underlying follows: dF = σ_N dW  (arithmetic Brownian motion)
    //
    // Call: df · [(F-K)·N(d) + σ_N·√T·φ(d)]
    // Put:  df · [(K-F)·N(-d) + σ_N·√T·φ(d)]        (note: φ(d) = φ(-d))
    //   d = (F - K) / (σ_N · √T)
    //
    // ATM (F = K): Call = Put = df · σ_N · √T / √(2π)
    //
    // Vols σ_N are often quoted in basis points (divide by 10000 before passing here).
    public static class BachelierModel
    {
        private const double SqrtTwoPi = 2.5066282746310002;

        // Call (cap / payer) price.
        public static double CallPrice(
            double forward, double strike, double normalVol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0) return Math.Max(forward - strike, 0.0) * discountFactor;
            if (normalVol <= 0.0) return Math.Max(forward - strike, 0.0) * discountFactor;

            double sqrtT = Math.Sqrt(expiry);
            double sigSqrtT = normalVol * sqrtT;
            double d = (forward - strike) / sigSqrtT;
            return discountFactor * ((forward - strike) * BlackScholes.NormalCdf(d) + sigSqrtT * BlackScholes.NormalPdf(d));
        }

        // Put (floor / receiver) price.
        public static double PutPrice(
            double forward, double strike, double normalVol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0) return Math.Max(strike - forward, 0.0) * discountFactor;
            if (normalVol <= 0.0) return Math.Max(strike - forward, 0.0) * discountFactor;

            double sqrtT = Math.Sqrt(expiry);
            double sigSqrtT = normalVol * sqrtT;
            double d = (forward - strike) / sigSqrtT;
            return discountFactor * ((strike - forward) * BlackScholes.NormalCdf(-d) + sigSqrtT * BlackScholes.NormalPdf(d));
        }

        // Bachelier vega: df · φ(d) · √T.
        public static double Vega(
            double forward, double strike, double normalVol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || normalVol <= 0.0) return 0.0;
            double sqrtT = Math.Sqrt(expiry);
            double d = (forward - strike) / (normalVol * sqrtT);
            return discountFactor * BlackScholes.NormalPdf(d) * sqrtT;
        }

        // Call delta: df · N(d).
        public static double CallDelta(
            double forward, double strike, double normalVol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || normalVol <= 0.0)
                return (forward > strike ? 1.0 : 0.0) * discountFactor;
            double d = (forward - strike) / (normalVol * Math.Sqrt(expiry));
            return discountFactor * BlackScholes.NormalCdf(d);
        }

        // Gamma: df · φ(d) / (σ_N · √T).
        public static double Gamma(
            double forward, double strike, double normalVol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || normalVol <= 0.0) return 0.0;
            double sigSqrtT = normalVol * Math.Sqrt(expiry);
            double d = (forward - strike) / sigSqrtT;
            return discountFactor * BlackScholes.NormalPdf(d) / sigSqrtT;
        }

        // Vanna: ∂²V/(∂F ∂σ_N) = −df · φ(d) · d / σ_N.
        // Equals both ∂Delta/∂σ_N and ∂Vega/∂F.
        public static double Vanna(
            double forward, double strike, double normalVol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || normalVol <= 0.0) return 0.0;
            double sqrtT = Math.Sqrt(expiry);
            double d = (forward - strike) / (normalVol * sqrtT);
            return -discountFactor * BlackScholes.NormalPdf(d) * d / normalVol;
        }

        // Volga (Vomma): ∂²V/∂σ_N² = df · φ(d) · √T · d² / σ_N = Vega · d² / σ_N.
        public static double Volga(
            double forward, double strike, double normalVol, double expiry, double discountFactor)
        {
            if (expiry <= 0.0 || normalVol <= 0.0) return 0.0;
            double sqrtT = Math.Sqrt(expiry);
            double d = (forward - strike) / (normalVol * sqrtT);
            return discountFactor * BlackScholes.NormalPdf(d) * sqrtT * d * d / normalVol;
        }

        // Solve for normal vol from a call price (Newton-Raphson).
        public static double ImpliedVol(
            double callPrice, double forward, double strike, double expiry, double discountFactor)
        {
            if (expiry <= 0.0) return double.NaN;
            double intrinsic = Math.Max(forward - strike, 0.0) * discountFactor;
            if (callPrice <= intrinsic + 1e-14) return double.NaN;

            double sqrtT = Math.Sqrt(expiry);
            double vol = callPrice / discountFactor * SqrtTwoPi / sqrtT;
            vol = Math.Max(1e-8, vol);

            for (int i = 0; i < 100; i++)
            {
                double model = CallPrice(forward, strike, vol, expiry, discountFactor);
                double diff = model - callPrice;
                if (Math.Abs(diff) < 1e-14) return vol;
                double vega = Vega(forward, strike, vol, expiry, discountFactor);
                if (vega < 1e-16) break;
                vol -= diff / vega;
                if (vol <= 0.0) vol = 1e-8;
            }
            return vol;
        }

        // Convert lognormal vol σ_LN to normal vol σ_N (Hagan & Woodward approximation).
        // Valid for σ_LN · √T << 1 and K ≈ F.
        public static double LognormalToNormal(double sigmaLN, double forward, double strike)
        {
            double fk = Math.Sqrt(forward * strike);
            if (fk <= 0.0) return sigmaLN;   // fall back — undefined for non-positive
            if (Math.Abs(forward - strike) < 1e-10 * Math.Max(Math.Abs(forward), 1e-14))
                return sigmaLN * forward;     // ATM: σ_N ≈ σ_LN · F
            return sigmaLN * (forward - strike) / Math.Log(forward / strike);
        }
    }
}
