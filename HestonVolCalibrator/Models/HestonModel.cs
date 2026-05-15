using System;
using System.Numerics;

namespace HestonVolCalibrator.Implementations
{
    public sealed class HestonModelParams
    {
        public double Kappa { get; set; }
        public double Theta { get; set; }
        public double Sigma { get; set; }
        public double Rho { get; set; }
        public double V0 { get; set; }

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
    /// Heston European option pricer using the Lewis/Gatheral single-integral representation.
    ///
    /// This version avoids the more fragile P1/P2 probability inversion, which is a common source
    /// of strike-by-strike oscillations in calibrated implied-volatility smiles.
    ///
    /// Formula:
    /// C = S exp(-qT) - sqrt(K) exp(-rT) / pi * integral_0^inf
    ///     Re[ exp(-iu log K) * phi_X(u - i/2) / (u^2 + 1/4) ] du
    /// where phi_X is the Heston characteristic function of log(S_T).
    ///
    /// Numerical method:
    /// - Composite Gauss-Legendre integration over [0, IntegrationUpper].
    /// - Stable Heston characteristic function with Re(d) >= 0.
    /// - No reciprocal branch switching and no probability clamping.
    /// - Final option price is only clamped to no-arbitrage bounds before IV inversion.
    /// </summary>
    public static class HestonPricer
    {
        private const double MinVariance = 1e-12;
        private const double MinVolOfVol = 1e-10;

        // Conservative defaults. Increase SegmentCount for very short maturities or extreme params.
        private const int GaussOrder = 32;
        private const int SegmentCount = 96;
        private const double IntegrationUpper = 200.0;

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

            if (Math.Abs(p.Sigma) < MinVolOfVol)
            {
                double effectiveVariance = IntegratedDeterministicVariance(p, maturity) / maturity;
                double vol = Math.Sqrt(Math.Max(effectiveVariance, MinVariance));
                return BlackScholesCall(spot, strike, maturity, rate, dividendYield, vol);
            }

            double integral = IntegrateLewis(p, spot, strike, maturity, rate, dividendYield);
            double call = spot * dfq - Math.Sqrt(strike) * df * integral / Math.PI;

            return Clamp(call, intrinsic, upper);
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

        private static double IntegrateLewis(
            HestonModelParams p,
            double spot,
            double strike,
            double maturity,
            double rate,
            double dividendYield)
        {
            GetGaussLegendreNodesAndWeights(GaussOrder, out double[] nodes, out double[] weights);

            double logK = Math.Log(strike);
            double segmentWidth = IntegrationUpper / SegmentCount;
            double sum = 0.0;

            for (int s = 0; s < SegmentCount; s++)
            {
                double a = s * segmentWidth;
                double b = a + segmentWidth;
                double mid = 0.5 * (a + b);
                double half = 0.5 * (b - a);

                double segmentSum = 0.0;

                for (int n = 0; n < GaussOrder; n++)
                {
                    double u = mid + half * nodes[n];

                    Complex shiftedU = new Complex(u, -0.5); // u - i/2
                    Complex cf = LogSpotCharFunc(p, spot, maturity, rate, dividendYield, shiftedU);
                    Complex oscillation = Complex.Exp(-Complex.ImaginaryOne * u * logK);
                    Complex value = oscillation * cf / (u * u + 0.25);

                    double integrand = value.Real;
                    if (!double.IsNaN(integrand) && !double.IsInfinity(integrand))
                        segmentSum += weights[n] * integrand;
                }

                sum += half * segmentSum;
            }

            if (Math.Abs(sum) < 1e-6)
            {
                var msg = "";
            }
            return sum;
        }

        /// <summary>
        /// Characteristic function of log(S_T) under the Heston model.
        /// Accepts complex u because Lewis evaluates phi(u - i/2).
        /// </summary>
        private static Complex LogSpotCharFunc(
            HestonModelParams p,
            double spot,
            double maturity,
            double rate,
            double dividendYield,
            Complex u)
        {
            double kappa = p.Kappa;
            double theta = p.Theta;
            double sigma = p.Sigma;
            double rho = p.Rho;
            double v0 = p.V0;

            Complex iu = Complex.ImaginaryOne * u;
            Complex alpha = kappa - rho * sigma * iu;

            // d = sqrt((kappa - rho sigma i u)^2 + sigma^2 (u^2 + i u))
            Complex d = Complex.Sqrt(alpha * alpha + sigma * sigma * (u * u + iu));
            if (d.Real < 0.0)
                d = -d;

            Complex g = (alpha - d) / (alpha + d);
            Complex expNegDt = Complex.Exp(-d * maturity);

            Complex oneMinusG = 1.0 - g;
            Complex oneMinusGExp = 1.0 - g * expNegDt;

            Complex logTerm = Complex.Log(oneMinusGExp / oneMinusG);

            Complex c = iu * (Math.Log(spot) + (rate - dividendYield) * maturity)
                      + kappa * theta / (sigma * sigma)
                      * ((alpha - d) * maturity - 2.0 * logTerm);

            Complex dCoef = (alpha - d) / (sigma * sigma)
                          * (1.0 - expNegDt) / oneMinusGExp;

            return Complex.Exp(c + dCoef * v0);
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
            double df = Math.Exp(-rate * maturity);
            double dfq = Math.Exp(-dividendYield * maturity);
            double intrinsic = Math.Max(spot * dfq - strike * df, 0.0);
            double timeValue = Math.Max(price - intrinsic, 1e-12);
            return Math.Sqrt(2.0 * Math.PI / maturity) * timeValue / Math.Max(spot * dfq, 1e-12);
        }

        private static double IntegratedDeterministicVariance(HestonModelParams p, double maturity)
        {
            if (Math.Abs(p.Kappa) < 1e-12)
                return Math.Max(p.V0, MinVariance) * maturity;

            return p.Theta * maturity + (p.V0 - p.Theta) * (1.0 - Math.Exp(-p.Kappa * maturity)) / p.Kappa;
        }

        private static void ValidateInputs(HestonModelParams p, double spot, double strike, double maturity)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (spot <= 0.0) throw new ArgumentOutOfRangeException(nameof(spot), "Spot must be positive.");
            if (strike <= 0.0) throw new ArgumentOutOfRangeException(nameof(strike), "Strike must be positive.");
            if (maturity < 0.0) throw new ArgumentOutOfRangeException(nameof(maturity), "Maturity cannot be negative.");
            if (p.Kappa <= 0.0) throw new ArgumentOutOfRangeException(nameof(p.Kappa), "Kappa must be positive.");
            if (p.Theta <= 0.0) throw new ArgumentOutOfRangeException(nameof(p.Theta), "Theta must be positive.");
            if (p.Sigma < 0.0) throw new ArgumentOutOfRangeException(nameof(p.Sigma), "Sigma cannot be negative.");
            if (p.Rho <= -1.0 || p.Rho >= 1.0) throw new ArgumentOutOfRangeException(nameof(p.Rho), "Rho must be strictly between -1 and 1.");
            if (p.V0 <= 0.0) throw new ArgumentOutOfRangeException(nameof(p.V0), "V0 must be positive.");
        }

        private static double NormalPdf(double x) =>
            Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);

        private static double NormalCdf(double x)
        {
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

        private static void GetGaussLegendreNodesAndWeights(int order, out double[] nodes, out double[] weights)
        {
            nodes = new double[order];
            weights = new double[order];

            const double eps = 1e-15;
            int m = (order + 1) / 2;

            for (int i = 0; i < m; i++)
            {
                double z = Math.Cos(Math.PI * (i + 0.75) / (order + 0.5));
                double z1;
                double p1 = 0.0;
                double p2 = 0.0;
                double pp = 0.0;

                do
                {
                    p1 = 1.0;
                    p2 = 0.0;
                    for (int j = 1; j <= order; j++)
                    {
                        double p3 = p2;
                        p2 = p1;
                        p1 = ((2.0 * j - 1.0) * z * p2 - (j - 1.0) * p3) / j;
                    }

                    pp = order * (z * p1 - p2) / (z * z - 1.0);
                    z1 = z;
                    z = z1 - p1 / pp;
                }
                while (Math.Abs(z - z1) > eps);

                nodes[i] = -z;
                nodes[order - 1 - i] = z;
                weights[i] = 2.0 / ((1.0 - z * z) * pp * pp);
                weights[order - 1 - i] = weights[i];
            }
        }
    }
}
