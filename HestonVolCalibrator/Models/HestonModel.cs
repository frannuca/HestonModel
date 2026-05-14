using System;
using System.Numerics;

namespace HestonVolCalibrator.Implementations
{
    public sealed class HestonModelParams
    {
        public double Kappa { get; set; }    // mean reversion speed
        public double Theta { get; set; }    // long-run variance
        public double Sigma { get; set; }    // vol of vol
        public double Rho { get; set; }      // spot-variance correlation
        public double V0 { get; set; }       // initial variance

        public HestonModelParams(double kappa, double theta, double sigma, double rho, double v0)
        {
            Kappa = kappa;
            Theta = theta;
            Sigma = sigma;
            Rho = rho;
            V0 = v0;
        }

        public override string ToString() =>
            $"Kappa={Kappa:F4}, Theta={Theta:F4}, Sigma={Sigma:F4}, Rho={Rho:F4}, V0={V0:F4}";
    }

    /// <summary>
    /// Heston (1993) European option pricer using characteristic-function inversion.
    ///
    /// Changes versus the original version:
    /// - Keeps Simpson grid spacing bounded when NMax is hit by reducing phiMax instead of silently increasing h.
    /// - Enforces a stable complex square-root branch with Re(d) >= 0.
    /// - Uses the little-Heston-trap representation with a guard if |g| is numerically unsafe.
    /// - Clamps probabilities and option prices to arbitrage bounds before implied-vol inversion.
    /// - Uses robust BS implied-vol inversion with bisection fallback and low-vega protection.
    ///
    /// This is still a semi-analytical Fourier pricer. For production calibration, compare against a
    /// Gauss-Laguerre / Lewis-Gatheral implementation and validate monotonicity/convexity by strike.
    /// </summary>
    public static class HestonPricer
    {
        private const double TargetH = 0.025;
        private const int NMax = 40_000;
        private const double PhiMin = 1e-8;
        private const double PhiMaxFloor = 75.0;
        private const double PhiMaxCeil = 300.0;
        private const double MinVariance = 1e-12;
        private const double MinVolOfVol = 1e-10;

        public static double CallPrice(
            HestonModelParams p,
            double spot,
            double strike,
            double maturity,
            double rate = 0.0,
            double dividendYield = 0.0)
        {
            ValidateInputs(p, spot, strike, maturity);

            double df = Math.Exp(-rate * maturity);
            double dfq = Math.Exp(-dividendYield * maturity);
            double intrinsic = Math.Max(spot * dfq - strike * df, 0.0);
            double upper = spot * dfq;

            if (maturity <= 0.0)
                return intrinsic;

            // Degenerate Heston limit: if vol-of-vol is almost zero, use Black-Scholes with variance
            // close to the deterministic variance level. This avoids divisions by sigma^2.
            if (Math.Abs(p.Sigma) < MinVolOfVol)
            {
                double effectiveVariance = IntegratedDeterministicVariance(p, maturity) / maturity;
                double vol = Math.Sqrt(Math.Max(effectiveVariance, MinVariance));
                return BlackScholesCall(spot, strike, maturity, rate, dividendYield, vol);
            }

            double prob1 = ComputeProb(p, spot, strike, maturity, rate, dividendYield, 1);
            double prob2 = ComputeProb(p, spot, strike, maturity, rate, dividendYield, 2);

            double price = spot * dfq * prob1 - strike * df * prob2;
            return Clamp(price, intrinsic, upper);
        }

        public static double PutPrice(
            HestonModelParams p,
            double spot,
            double strike,
            double maturity,
            double rate = 0.0,
            double dividendYield = 0.0)
        {
            double call = CallPrice(p, spot, strike, maturity, rate, dividendYield);
            double df = Math.Exp(-rate * maturity);
            double dfq = Math.Exp(-dividendYield * maturity);
            return call - spot * dfq + strike * df;
        }

        public static double ImpliedVol(
            HestonModelParams p,
            double spot,
            double strike,
            double maturity,
            double rate = 0.0,
            double dividendYield = 0.0)
        {
            if (maturity <= 0.0)
                return 1e-4;

            double price = CallPrice(p, spot, strike, maturity, rate, dividendYield);
            return BsImpliedVol(price, spot, strike, maturity, rate, dividendYield);
        }

        private static double ComputeProb(
            HestonModelParams p,
            double spot,
            double strike,
            double maturity,
            double rate,
            double dividendYield,
            int probabilityIndex)
        {
            double logK = Math.Log(strike);

            // Avoid huge phiMax. Very large domains make Simpson integration noisy for dense strike grids.
            double effectiveV0 = Math.Max(p.V0, 1e-4);
            double phiMax = 120.0 / Math.Sqrt(Math.Max(maturity, 1e-6) * effectiveV0);
            phiMax = Clamp(phiMax, PhiMaxFloor, PhiMaxCeil);

            int n = (int)Math.Ceiling((phiMax - PhiMin) / TargetH);
            if ((n & 1) == 1)
                n++;

            // Critical fix: if N is capped, also reduce phiMax so the actual h remains close to TargetH.
            // The old implementation kept phiMax huge and silently coarsened h, producing oscillatory smiles.
            if (n > NMax)
            {
                n = NMax;
                if ((n & 1) == 1)
                    n--;
                phiMax = PhiMin + n * TargetH;
            }

            if (n < 2)
                n = 2;

            double h = (phiMax - PhiMin) / n;
            double sum = 0.0;

            for (int k = 0; k <= n; k++)
            {
                double phi = PhiMin + k * h;
                Complex cf = CharFunc(p, spot, maturity, rate, dividendYield, probabilityIndex, phi);

                // Re[exp(-i phi logK) * cf / (i phi)] = Im[exp(-i phi logK) * cf] / phi
                double angle = -phi * logK;
                double rotatedImaginary = Math.Cos(angle) * cf.Imaginary + Math.Sin(angle) * cf.Real;
                double integrand = rotatedImaginary / phi;

                if (double.IsNaN(integrand) || double.IsInfinity(integrand))
                    continue;

                int weight = (k == 0 || k == n) ? 1 : ((k & 1) == 1 ? 4 : 2);
                sum += weight * integrand;
            }

            double probability = 0.5 + (h / 3.0) * sum / Math.PI;

            // Small numerical violations of [0,1] can cause severe IV spikes.
            return Clamp(probability, 0.0, 1.0);
        }

        private static Complex CharFunc(
            HestonModelParams p,
            double spot,
            double maturity,
            double rate,
            double dividendYield,
            int probabilityIndex,
            double phi)
        {
            double kappa = p.Kappa;
            double theta = p.Theta;
            double sigma = p.Sigma;
            double rho = p.Rho;
            double v0 = p.V0;

            double u = probabilityIndex == 1 ? 0.5 : -0.5;
            double b = probabilityIndex == 1 ? kappa - rho * sigma : kappa;

            Complex iPhi = new Complex(0.0, phi);
            Complex beta = b - rho * sigma * iPhi;
            Complex dArg = beta * beta + sigma * sigma * (phi * phi - 2.0 * u * iPhi);
            Complex d = Complex.Sqrt(dArg);

            // Stable square-root branch. This helps prevent discontinuous phase jumps.
            if (d.Real < 0.0)
                d = -d;

            Complex g = (beta - d) / (beta + d);

            // Little trap should generally have |g| < 1. If numerical round-off gives |g| > 1,
            // use the reciprocal representation to avoid explosive exp/log terms.
            bool useReciprocal = Complex.Abs(g) > 1.0;

            Complex c;
            Complex dCoef;

            if (!useReciprocal)
            {
                Complex expNegDt = Complex.Exp(-d * maturity);
                Complex oneMinusGExp = 1.0 - g * expNegDt;
                Complex oneMinusG = 1.0 - g;

                Complex logFactor = Complex.Log(oneMinusGExp / oneMinusG);

                c = (rate - dividendYield) * iPhi * maturity
                    + kappa * theta / (sigma * sigma) * ((beta - d) * maturity - 2.0 * logFactor);

                dCoef = (beta - d) / (sigma * sigma) * (1.0 - expNegDt) / oneMinusGExp;
            }
            else
            {
                // Reciprocal formulation: use G = 1/g and exp(+dT). This is algebraically equivalent
                // but more stable if |g| is numerically outside the unit circle.
                Complex gInv = 1.0 / g;
                Complex expDt = Complex.Exp(d * maturity);
                Complex oneMinusGInvExp = 1.0 - gInv * expDt;
                Complex oneMinusGInv = 1.0 - gInv;

                Complex logFactor = Complex.Log(oneMinusGInvExp / oneMinusGInv);

                c = (rate - dividendYield) * iPhi * maturity
                    + kappa * theta / (sigma * sigma) * ((beta + d) * maturity - 2.0 * logFactor);

                dCoef = (beta + d) / (sigma * sigma) * (1.0 - expDt) / oneMinusGInvExp;
            }

            return Complex.Exp(c + dCoef * v0 + iPhi * Math.Log(spot));
        }

        public static double BsImpliedVol(
            double price,
            double spot,
            double strike,
            double maturity,
            double rate,
            double dividendYield = 0.0)
        {
            const double volLo = 1e-4;
            const double volHi = 5.0;
            const double priceTolerance = 1e-11;
            const double volTolerance = 1e-8;
            const int maxIterations = 100;

            if (maturity <= 0.0)
                return volLo;

            double df = Math.Exp(-rate * maturity);
            double dfq = Math.Exp(-dividendYield * maturity);
            double intrinsic = Math.Max(spot * dfq - strike * df, 0.0);
            double upper = spot * dfq;

            price = Clamp(price, intrinsic, upper);

            if (price <= intrinsic + 1e-10)
                return volLo;

            if (price >= upper - 1e-10)
                return volHi;

            double sqrtT = Math.Sqrt(maturity);
            double forward = spot * dfq / df;
            double logFk = Math.Log(forward / strike);

            double lo = volLo;
            double hi = volHi;
            double vol = InitialVolGuess(price, spot, strike, maturity, rate, dividendYield);
            vol = Clamp(vol, lo, hi);

            for (int iter = 0; iter < maxIterations; iter++)
            {
                double bsCall = BlackScholesCall(spot, strike, maturity, rate, dividendYield, vol);
                double diff = bsCall - price;

                if (Math.Abs(diff) < priceTolerance)
                    return Clamp(vol, volLo, volHi);

                if (diff > 0.0)
                    hi = Math.Min(hi, vol);
                else
                    lo = Math.Max(lo, vol);

                double d1 = (logFk + 0.5 * vol * vol * maturity) / (vol * sqrtT);
                double vega = spot * dfq * NormalPdf(d1) * sqrtT;

                double nextVol;
                if (vega > 1e-10)
                {
                    nextVol = vol - diff / vega;

                    if (double.IsNaN(nextVol) || double.IsInfinity(nextVol) || nextVol <= lo || nextVol >= hi)
                        nextVol = 0.5 * (lo + hi);
                }
                else
                {
                    nextVol = 0.5 * (lo + hi);
                }

                if (Math.Abs(nextVol - vol) < volTolerance)
                    return Clamp(nextVol, volLo, volHi);

                vol = nextVol;
            }

            return Clamp(vol, volLo, volHi);
        }

        private static double BlackScholesCall(
            double spot,
            double strike,
            double maturity,
            double rate,
            double dividendYield,
            double vol)
        {
            if (maturity <= 0.0)
                return Math.Max(spot - strike, 0.0);

            double df = Math.Exp(-rate * maturity);
            double dfq = Math.Exp(-dividendYield * maturity);
            double sqrtT = Math.Sqrt(maturity);
            double sigmaSqrtT = vol * sqrtT;

            if (sigmaSqrtT <= 0.0)
                return Math.Max(spot * dfq - strike * df, 0.0);

            double forwardOverStrike = spot * dfq / (strike * df);
            double d1 = (Math.Log(forwardOverStrike) + 0.5 * vol * vol * maturity) / sigmaSqrtT;
            double d2 = d1 - sigmaSqrtT;

            return spot * dfq * NormalCdf(d1) - strike * df * NormalCdf(d2);
        }

        private static double InitialVolGuess(
            double price,
            double spot,
            double strike,
            double maturity,
            double rate,
            double dividendYield)
        {
            double dfq = Math.Exp(-dividendYield * maturity);
            double intrinsic = Math.Max(spot * dfq - strike * Math.Exp(-rate * maturity), 0.0);
            double timeValue = Math.Max(price - intrinsic, 1e-12);

            // Brenner-Subrahmanyam ATM-style guess. It is imperfect away from ATM but good enough
            // because the solver is bracketed.
            return Math.Sqrt(2.0 * Math.PI / maturity) * timeValue / Math.Max(spot * dfq, 1e-12);
        }

        private static double IntegratedDeterministicVariance(HestonModelParams p, double maturity)
        {
            if (Math.Abs(p.Kappa) < 1e-12)
                return Math.Max(p.V0, MinVariance) * maturity;

            // E[v_t] = theta + (v0 - theta) exp(-kappa t)
            // integral_0^T E[v_t] dt = theta T + (v0 - theta)(1 - exp(-kappa T))/kappa
            return p.Theta * maturity + (p.V0 - p.Theta) * (1.0 - Math.Exp(-p.Kappa * maturity)) / p.Kappa;
        }

        private static void ValidateInputs(HestonModelParams p, double spot, double strike, double maturity)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));
            if (spot <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(spot), "Spot must be positive.");
            if (strike <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(strike), "Strike must be positive.");
            if (maturity < 0.0)
                throw new ArgumentOutOfRangeException(nameof(maturity), "Maturity cannot be negative.");
            if (p.Kappa <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(p.Kappa), "Kappa must be positive.");
            if (p.Theta <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(p.Theta), "Theta must be positive.");
            if (p.Sigma < 0.0)
                throw new ArgumentOutOfRangeException(nameof(p.Sigma), "Sigma cannot be negative.");
            if (p.Rho <= -1.0 || p.Rho >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(p.Rho), "Rho must be strictly between -1 and 1.");
            if (p.V0 <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(p.V0), "V0 must be positive.");
        }

        private static double NormalPdf(double x)
        {
            return Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);
        }

        private static double NormalCdf(double x)
        {
            // Abramowitz-Stegun approximation. Max absolute error around 7.5e-8.
            double sign = x < 0.0 ? -1.0 : 1.0;
            double z = Math.Abs(x) / Math.Sqrt(2.0);
            double t = 1.0 / (1.0 + 0.3275911 * z);
            double erf = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
            return 0.5 * (1.0 + sign * erf);
        }

        private static double Clamp(double value, double min, double max)
        {
#if NET5_0_OR_GREATER
            return Math.Clamp(value, min, max);
#else
            if (value < min) return min;
            if (value > max) return max;
            return value;
#endif
        }
    }
}
