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
    /// Heston European option pricer using a numerically centred Lewis/Gatheral integral.
    ///
    /// Main numerical change versus the previous Lewis implementation:
    /// the previous code integrated
    ///     exp(-i u log(K)) * phi_logS(u - i/2)
    /// where both terms oscillate very rapidly because log(K) and log(S) are around 8-10 for
    /// equity index levels. Mathematically these phases cancel, but numerically the cancellation
    /// is poor and creates strike-by-strike oscillations.
    ///
    /// This version factors out the forward F = S exp((r-q)T) and integrates only in log-moneyness:
    ///     k = log(K / F)
    ///     C = df * [ F - sqrt(F K) / pi * Integral_0^inf
    ///           Re( exp(-i u k) * phi_Y(u - i/2) / (u^2 + 1/4) ) du ]
    /// where Y = log(S_T / F). This removes the large artificial phase and makes the smile much smoother.
    ///
    /// For very short maturities, the integral is also extended adaptively because the Fourier tail
    /// decays slowly as T -> 0.
    /// </summary>
    public static class HestonPricer
    {
        private const double MinVariance = 1e-12;
        private const double MinVolOfVol = 1e-10;

        private const int GaussOrder = 48;
        private const double TargetSegmentWidth = 0.5;  // Increased from 0.25 to use fewer segments
        private const int MaxSegments = 2_000;  // Reduced from 12,000
        private const double MinIntegrationUpper = 50.0;  // Reduced from 250.0
        private const double MaxIntegrationUpper = 500.0;  // Reduced from 3_000.0

        // Cache Gauss-Legendre nodes and weights
        private static double[]? _cachedNodes;
        private static double[]? _cachedWeights;

        private static void GetCachedGaussLegendreNodesAndWeights(out double[] nodes, out double[] weights)
        {
            if (_cachedNodes != null && _cachedWeights != null)
            {
                nodes = _cachedNodes;
                weights = _cachedWeights;
                return;
            }

            GetGaussLegendreNodesAndWeights(GaussOrder, out nodes, out weights);
            _cachedNodes = nodes;
            _cachedWeights = weights;
        }

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
            double forward = spot * Math.Exp((rate - dividendYield) * maturity);
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

            double integral = IntegrateCenteredLewis(p, strike, forward, maturity);
            double call = df * (forward - Math.Sqrt(forward * strike) * integral / Math.PI);

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

        private static double IntegrateCenteredLewis(
            HestonModelParams p,
            double strike,
            double forward,
            double maturity)
        {
            GetCachedGaussLegendreNodesAndWeights(out double[] nodes, out double[] weights);

            double logMoneyness = Math.Log(strike / forward);
            double upper = ChooseIntegrationUpper(p, maturity);
            
            // Adaptive segment count based on integration upper bound
            int segments = (int)Math.Ceiling(upper / TargetSegmentWidth);
            segments = Math.Max(8, Math.Min(segments, MaxSegments));

            double segmentWidth = upper / segments;

            // Kahan summation reduces accumulation noise over many short segments.
            double sum = 0.0;
            double compensation = 0.0;

            for (int s = 0; s < segments; s++)
            {
                double a = s * segmentWidth;
                double b = a + segmentWidth;
                double mid = 0.5 * (a + b);
                double half = 0.5 * (b - a);

                double segmentSum = 0.0;

                for (int n = 0; n < GaussOrder; n++)
                {
                    double u = mid + half * nodes[n];
                    
                    // Inline characteristic function evaluation to avoid Complex allocations
                    double integrand = EvaluateIntegrand(p, maturity, u, logMoneyness);
                    
                    if (!double.IsNaN(integrand) && !double.IsInfinity(integrand))
                        segmentSum += weights[n] * integrand;
                }

                double contribution = half * segmentSum;
                double y = contribution - compensation;
                double t = sum + y;
                compensation = (t - sum) - y;
                sum = t;
            }

            return sum;
        }

        /// <summary>
        /// Inline integrand evaluation using real arithmetic where possible to avoid Complex overhead.
        /// </summary>
        private static double EvaluateIntegrand(
            HestonModelParams p,
            double maturity,
            double u,
            double logMoneyness)
        {
            double kappa = p.Kappa;
            double theta = p.Theta;
            double sigma = p.Sigma;
            double rho = p.Rho;
            double v0 = p.V0;

            // Compute characteristic function components
            double sigmaSq = sigma * sigma;
            double rhoSigma = rho * sigma;
            
            // alpha = kappa - rho*sigma*i*u
            // We work with u - i/2, so iu = i*u, and -i*u*0.5 = 0.5
            double alphaReal = kappa;
            double alphaImag = -rhoSigma * u;

            // d = sqrt(alpha^2 + sigma^2*(u^2 + i*u))
            // = sqrt((alphaReal^2 - alphaImag^2 + sigma^2*(-u^2 + u)) + i*(2*alphaReal*alphaImag + sigma^2*u))
            double d2Real = alphaReal * alphaReal - alphaImag * alphaImag + sigmaSq * (-u * u + u);
            double d2Imag = 2.0 * alphaReal * alphaImag + sigmaSq * u;
            
            // sqrt of complex number
            double d2Mag = Math.Sqrt(d2Real * d2Real + d2Imag * d2Imag);
            double d2Ang = Math.Atan2(d2Imag, d2Real);
            
            double dMag = Math.Sqrt(d2Mag);
            double dAng = d2Ang * 0.5;
            
            double dReal = dMag * Math.Cos(dAng);
            double dImag = dMag * Math.Sin(dAng);

            // Ensure correct branch
            if (dReal < 0.0 || (Math.Abs(dReal) < 1e-14 && dImag < 0.0))
            {
                dReal = -dReal;
                dImag = -dImag;
            }

            // g = (alpha - d) / (alpha + d)
            double gNum_r = alphaReal - dReal;
            double gNum_i = alphaImag - dImag;
            double gDen_r = alphaReal + dReal;
            double gDen_i = alphaImag + dImag;
            
            double gDen_mag2 = gDen_r * gDen_r + gDen_i * gDen_i;
            double g_r = (gNum_r * gDen_r + gNum_i * gDen_i) / gDen_mag2;
            double g_i = (gNum_i * gDen_r - gNum_r * gDen_i) / gDen_mag2;

            // exp(-d * maturity)
            double exp_d_t_mag = Math.Exp(-dReal * maturity);
            double exp_d_t_r = exp_d_t_mag * Math.Cos(-dImag * maturity);
            double exp_d_t_i = exp_d_t_mag * Math.Sin(-dImag * maturity);

            // oneMinusGExp = 1 - g * exp(-d*t)
            double gExp_r = g_r * exp_d_t_r - g_i * exp_d_t_i;
            double gExp_i = g_r * exp_d_t_i + g_i * exp_d_t_r;
            
            double oneMinusGExp_r = 1.0 - gExp_r;
            double oneMinusGExp_i = -gExp_i;

            // oneMinusG = 1 - g
            double oneMinusG_r = 1.0 - g_r;
            double oneMinusG_i = -g_i;

            // log(oneMinusGExp / oneMinusG)
            double ratio_r = (oneMinusGExp_r * oneMinusG_r + oneMinusGExp_i * oneMinusG_i) / (oneMinusG_r * oneMinusG_r + oneMinusG_i * oneMinusG_i);
            double ratio_i = (oneMinusGExp_i * oneMinusG_r - oneMinusGExp_r * oneMinusG_i) / (oneMinusG_r * oneMinusG_r + oneMinusG_i * oneMinusG_i);
            
            double logRatio_r = 0.5 * Math.Log(ratio_r * ratio_r + ratio_i * ratio_i);
            double logRatio_i = Math.Atan2(ratio_i, ratio_r);

            // c = (kappa*theta/sigma^2) * ((alpha - d)*maturity - 2*log(...))
            double c_r = (kappa * theta / sigmaSq) * ((alphaReal - dReal) * maturity - 2.0 * logRatio_r);
            double c_i = (kappa * theta / sigmaSq) * ((alphaImag - dImag) * maturity - 2.0 * logRatio_i);

            // dCoef = (alpha - d) / sigma^2 * (1 - exp(-d*t)) / oneMinusGExp
            double oneMinusExp_r = 1.0 - exp_d_t_r;
            double oneMinusExp_i = -exp_d_t_i;
            
            double dCoefNum_r = (alphaReal - dReal) * oneMinusExp_r - (alphaImag - dImag) * oneMinusExp_i;
            double dCoefNum_i = (alphaReal - dReal) * oneMinusExp_i + (alphaImag - dImag) * oneMinusExp_r;
            
            double dCoefDen_mag2 = oneMinusGExp_r * oneMinusGExp_r + oneMinusGExp_i * oneMinusGExp_i;
            double dCoef_r = (dCoefNum_r * oneMinusGExp_r + dCoefNum_i * oneMinusGExp_i) / (sigmaSq * dCoefDen_mag2);
            double dCoef_i = (dCoefNum_i * oneMinusGExp_r - dCoefNum_r * oneMinusGExp_i) / (sigmaSq * dCoefDen_mag2);

            // dCoef * v0
            double dCoefV0_r = dCoef_r * v0;
            double dCoefV0_i = dCoef_i * v0;

            // exp(c + dCoef*v0)
            double exp_arg_r = c_r + dCoefV0_r;
            double exp_arg_i = c_i + dCoefV0_i;
            
            double exp_mag = Math.Exp(exp_arg_r);
            double cf_r = exp_mag * Math.Cos(exp_arg_i);
            double cf_i = exp_mag * Math.Sin(exp_arg_i);

            // phase = exp(-i*u*logMoneyness)
            double phase_r = Math.Cos(-u * logMoneyness);
            double phase_i = Math.Sin(-u * logMoneyness);

            // value = phase * cf / (u^2 + 0.25)
            double denom = u * u + 0.25;
            
            // (phase_r + i*phase_i) * (cf_r + i*cf_i)
            double prod_r = phase_r * cf_r - phase_i * cf_i;
            
            // Real part of value
            return prod_r / denom;
        }

        private static double ChooseIntegrationUpper(HestonModelParams p, double maturity)
        {
            // As T -> 0 the payoff transform is harder to integrate and the tail decays more slowly.
            // Use total std-dev as the scale. The constants are intentionally conservative for calibration.
            double representativeVariance = Math.Max(Math.Max(p.V0, p.Theta), MinVariance);
            double totalStdDev = Math.Sqrt(Math.Max(representativeVariance * maturity, 1e-12));
            double upper = 45.0 / totalStdDev;
            return Clamp(upper, MinIntegrationUpper, MaxIntegrationUpper);
        }

        /// <summary>
        /// Characteristic function of Y = log(S_T / F_0T), where F_0T = S_0 exp((r-q)T).
        /// The deterministic forward phase has been removed to avoid large artificial oscillations.
        /// </summary>
        private static Complex CenteredLogReturnCharFunc(HestonModelParams p, double maturity, Complex u)
        {
            double kappa = p.Kappa;
            double theta = p.Theta;
            double sigma = p.Sigma;
            double rho = p.Rho;
            double v0 = p.V0;

            Complex iu = Complex.ImaginaryOne * u;
            Complex alpha = kappa - rho * sigma * iu;

            Complex d = Complex.Sqrt(alpha * alpha + sigma * sigma * (u * u + iu));
            if (d.Real < 0.0 || (Math.Abs(d.Real) < 1e-14 && d.Imaginary < 0.0))
                d = -d;

            Complex g = (alpha - d) / (alpha + d);
            Complex expNegDt = Complex.Exp(-d * maturity);
            Complex oneMinusGExp = 1.0 - g * expNegDt;
            Complex oneMinusG = 1.0 - g;

            Complex logTerm = Complex.Log(oneMinusGExp / oneMinusG);

            Complex c = kappa * theta / (sigma * sigma)
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
