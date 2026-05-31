using System;

namespace HestonVolCalibrator.SABR
{
    // Hagan et al. (2002) SABR closed-form approximations for implied vol.
    //
    // Lognormal (Black76) backbone: valid when F > 0 and K > 0.
    //   For near-zero or negative rates use ShiftedImpliedVol (positive displacement)
    //   or NormalImpliedVol (Bachelier backbone, β = 0).
    //
    // Key formulas:
    //   σ_B(K,F,T) = α/[(FK)^{(1-β)/2} · D(K,F)] · z/χ(z) · (1 + C·T)
    //   D = 1 + (1-β)²/24 · ln²(F/K) + (1-β)⁴/1920 · ln⁴(F/K)
    //   z = ν/α · (FK)^{(1-β)/2} · ln(F/K)
    //   χ(z) = ln[(√(1-2ρz+z²) + z - ρ)/(1-ρ)]
    //   C = (1-β)²α²/[24(FK)^{1-β}] + ρβνα/[4(FK)^{(1-β)/2}] + (2-3ρ²)ν²/24
    public static class SabrPricer
    {
        private const double AtmThreshold = 1e-7;

        // ── Lognormal (Black76) implied vol ──────────────────────────────────────

        // Main Hagan 2002 lognormal approximation. Returns NaN for ill-conditioned inputs.
        public static double ImpliedVol(SabrParams p, double forward, double strike, double expiry)
        {
            if (!Validate(p, expiry) || forward <= 0.0 || strike <= 0.0)
                return double.NaN;

            double alpha = p.Alpha, beta = p.Beta, rho = p.Rho, nu = p.Nu;
            double omBeta = 1.0 - beta;

            if (Math.Abs(forward - strike) / Math.Max(forward, 1e-14) < AtmThreshold)
                return AtmLognormalVol(alpha, beta, rho, nu, forward, expiry);

            double logFK = Math.Log(forward / strike);
            double ln2 = logFK * logFK;
            double fkMid = Math.Pow(forward * strike, 0.5 * omBeta); // (FK)^{(1-β)/2}

            // Denominator series
            double denom = 1.0
                + omBeta * omBeta / 24.0 * ln2
                + omBeta * omBeta * omBeta * omBeta / 1920.0 * ln2 * ln2;

            // z and z/χ(z)
            double z = (nu > 0.0 && alpha > 1e-14) ? nu / alpha * fkMid * logFK : 0.0;
            double zc = ZOverChi(z, rho);
            if (double.IsNaN(zc)) return double.NaN;

            // Expiry correction term
            double fkMid2 = fkMid * fkMid; // (FK)^{1-β}
            double c = ExpiryCorrection(alpha, beta, rho, nu, fkMid, fkMid2);

            return alpha / (fkMid * denom) * zc * (1.0 + c * expiry);
        }

        // Shifted lognormal SABR: apply displacement s so that F+s > 0 and K+s > 0.
        public static double ShiftedImpliedVol(SabrParams p, double shift, double forward, double strike, double expiry)
            => ImpliedVol(p, forward + shift, strike + shift, expiry);

        // ATM vol convenience — avoids cancellation errors near K = F.
        public static double AtmImpliedVol(SabrParams p, double forward, double expiry)
        {
            if (!Validate(p, expiry) || forward <= 0.0) return double.NaN;
            return AtmLognormalVol(p.Alpha, p.Beta, p.Rho, p.Nu, forward, expiry);
        }

        // ── Normal (Bachelier) implied vol ────────────────────────────────────────

        // For β = 0 uses the exact Hagan β = 0 normal approximation.
        // For other β, converts lognormal vol via (F-K)/ln(F/K) ≈ F at-the-money.
        public static double NormalImpliedVol(SabrParams p, double forward, double strike, double expiry)
        {
            if (!Validate(p, expiry)) return double.NaN;

            if (Math.Abs(p.Beta) < 1e-10)
                return Beta0NormalVol(p, forward, strike, expiry);

            if (forward <= 0.0 || strike <= 0.0) return double.NaN;

            double sigma = ImpliedVol(p, forward, strike, expiry);
            if (double.IsNaN(sigma)) return double.NaN;

            double relDiff = Math.Abs(forward - strike) / Math.Max(forward, 1e-14);
            return relDiff < AtmThreshold
                ? sigma * forward
                : sigma * (forward - strike) / Math.Log(forward / strike);
        }

        // ATM normal (Bachelier) vol.
        public static double AtmNormalImpliedVol(SabrParams p, double forward, double expiry)
        {
            if (!Validate(p, expiry)) return double.NaN;
            if (Math.Abs(p.Beta) < 1e-10)
                return p.Alpha * (1.0 + (2.0 - 3.0 * p.Rho * p.Rho) * p.Nu * p.Nu / 24.0 * expiry);
            if (forward <= 0.0) return double.NaN;
            double sigma = AtmLognormalVol(p.Alpha, p.Beta, p.Rho, p.Nu, forward, expiry);
            return double.IsNaN(sigma) ? double.NaN : sigma * forward;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        // ATM formula: numerically stable when K → F (avoids ln(F/K) → 0/0).
        private static double AtmLognormalVol(
            double alpha, double beta, double rho, double nu, double fwd, double T)
        {
            double omBeta = 1.0 - beta;
            double fBeta = Math.Pow(fwd, omBeta);   // F^{1-β}
            double fBeta2 = fBeta * fBeta;           // F^{2(1-β)}
            double c = alpha * alpha * omBeta * omBeta / (24.0 * fBeta2)
                       + rho * beta * nu * alpha / (4.0 * fBeta)
                       + (2.0 - 3.0 * rho * rho) * nu * nu / 24.0;
            return alpha / fBeta * (1.0 + c * T);
        }

        // Returns z/χ(z); limit at z = 0 is 1.
        // χ(z) = ln[(√(1-2ρz+z²) + z - ρ)/(1-ρ)]
        private static double ZOverChi(double z, double rho)
        {
            if (Math.Abs(z) < 1e-10) return 1.0;

            double disc = 1.0 - 2.0 * rho * z + z * z;
            if (disc < 0.0) return double.NaN;
            double sq = Math.Sqrt(disc);
            double num = sq + z - rho;
            double den = 1.0 - rho;
            if (num <= 0.0 || den <= 0.0) return double.NaN;
            double chi = Math.Log(num / den);
            if (Math.Abs(chi) < 1e-14) return double.NaN;
            return z / chi;
        }

        // C = (1-β)²α²/[24(FK)^{1-β}] + ρβνα/[4(FK)^{(1-β)/2}] + (2-3ρ²)ν²/24
        private static double ExpiryCorrection(
            double alpha, double beta, double rho, double nu,
            double fkMid, double fkMid2)
        {
            double omBeta = 1.0 - beta;
            return alpha * alpha * omBeta * omBeta / (24.0 * fkMid2)
                   + rho * beta * nu * alpha / (4.0 * fkMid)
                   + (2.0 - 3.0 * rho * rho) * nu * nu / 24.0;
        }

        // β = 0 normal SABR: σ_N = α · z/χ(z) · [1 + (2-3ρ²)ν²/24 · T]
        // z = ν/α · (F - K)   (linear in rate difference, not log-moneyness)
        private static double Beta0NormalVol(SabrParams p, double forward, double strike, double expiry)
        {
            double alpha = p.Alpha, rho = p.Rho, nu = p.Nu;
            double tol = AtmThreshold * Math.Max(Math.Abs(forward) + Math.Abs(strike), 1e-14);

            if (Math.Abs(forward - strike) < tol)
                return alpha * (1.0 + (2.0 - 3.0 * rho * rho) * nu * nu / 24.0 * expiry);

            double z = (nu > 0.0 && alpha > 1e-14) ? nu / alpha * (forward - strike) : 0.0;
            double zc = ZOverChi(z, rho);
            if (double.IsNaN(zc)) return double.NaN;
            return alpha * zc * (1.0 + (2.0 - 3.0 * rho * rho) * nu * nu / 24.0 * expiry);
        }

        private static bool Validate(SabrParams p, double expiry) =>
            p != null
            && p.Alpha > 0.0
            && p.Nu >= 0.0
            && p.Beta >= 0.0 && p.Beta <= 1.0
            && p.Rho > -1.0 && p.Rho < 1.0
            && expiry > 0.0;
    }
}
