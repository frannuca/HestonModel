using System;

namespace HestonVolCalibrator.Implementations
{
    // Black-Scholes utility functions for option pricing and delta calculations
    public static class BlackScholes
    {
        private const double Sqrt2Pi = 2.506628274829;

        // Cumulative normal distribution
        public static double NormalCdf(double x)
        {
            return 0.5 * (1.0 + Erf(x / System.Math.Sqrt(2.0)));
        }

        // Probability density function of standard normal
        public static double NormalPdf(double x)
        {
            return System.Math.Exp(-x * x / 2.0) / Sqrt2Pi;
        }

        // Error function approximation (for normal CDF)
        private static double Erf(double x)
        {
            // Abramowitz and Stegun approximation
            double a1 = 0.254829592;
            double a2 = -0.284496736;
            double a3 = 1.421413741;
            double a4 = -1.453152027;
            double a5 = 1.061405429;
            double p = 0.3275911;

            int sign = x < 0 ? -1 : 1;
            x = System.Math.Abs(x);

            double t = 1.0 / (1.0 + p * x);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * System.Math.Exp(-x * x);

            return sign * y;
        }

        // Call option price using Black-Scholes formula
        public static double CallPrice(double spot, double strike, double vol, double maturity, double rate = 0.0)
        {
            if (maturity <= 0) return System.Math.Max(spot - strike, 0);
            if (vol <= 0) return System.Math.Max(spot - System.Math.Exp(-rate * maturity) * strike, 0);

            double d1 = (System.Math.Log(spot / strike) + (rate + 0.5 * vol * vol) * maturity) / (vol * System.Math.Sqrt(maturity));
            double d2 = d1 - vol * System.Math.Sqrt(maturity);

            return spot * NormalCdf(d1) - strike * System.Math.Exp(-rate * maturity) * NormalCdf(d2);
        }

        // Put option price using Black-Scholes formula
        public static double PutPrice(double spot, double strike, double vol, double maturity, double rate = 0.0)
        {
            if (maturity <= 0) return System.Math.Max(strike - spot, 0);
            if (vol <= 0) return System.Math.Max(System.Math.Exp(-rate * maturity) * strike - spot, 0);

            double d1 = (System.Math.Log(spot / strike) + (rate + 0.5 * vol * vol) * maturity) / (vol * System.Math.Sqrt(maturity));
            double d2 = d1 - vol * System.Math.Sqrt(maturity);

            return strike * System.Math.Exp(-rate * maturity) * NormalCdf(-d2) - spot * NormalCdf(-d1);
        }

        // Call delta with continuous dividend yield q. Without q, delta is on the wrong forward
        // when pricing an underlying that pays carry (e.g. SPX index ~1.3% q). The Heston pricer
        // already takes q, so the BS-equivalent delta we attach to surface cells must too.
        public static double CallDelta(double spot, double strike, double vol, double maturity, double rate = 0.0, double dividendYield = 0.0)
        {
            if (maturity <= 0)
                return spot > strike ? 1.0 : 0.0;
            if (vol <= 0)
                return spot > strike ? 1.0 : 0.0;

            double sqrtT = System.Math.Sqrt(maturity);
            double d1 = (System.Math.Log(spot / strike) + (rate - dividendYield + 0.5 * vol * vol) * maturity) / (vol * sqrtT);
            return System.Math.Exp(-dividendYield * maturity) * NormalCdf(d1);
        }

        // Put delta — standard put-call parity for delta: Δ_put = Δ_call - e^{-qT}.
        public static double PutDelta(double spot, double strike, double vol, double maturity, double rate = 0.0, double dividendYield = 0.0)
        {
            return CallDelta(spot, strike, vol, maturity, rate, dividendYield) - System.Math.Exp(-dividendYield * maturity);
        }

        // Convert delta to strike (inverse of delta function)
        // Solves for strike K such that CallDelta(spot, K, vol, T) = delta
        public static double DeltaToStrike(double spot, double delta, double vol, double maturity, double rate = 0.0)
        {
            if (delta <= 0 || delta >= 1)
                throw new System.ArgumentException("Delta must be in (0, 1)");

            // Use Newton-Raphson to solve for strike
            // We want: CallDelta(spot, K, vol, T) = delta
            // Equivalently: N(d1) = delta, so d1 = N_inv(delta)

            double d1Target = NormalInverse(delta);
            // d1 = (ln(S/K) + (r + 0.5*vol^2)*T) / (vol*sqrt(T))
            // Solve for K:
            // K = S * exp(-vol*sqrt(T)*d1_target - (r + 0.5*vol^2)*T)

            double sqrtT = System.Math.Sqrt(maturity);
            double exponent = -vol * sqrtT * d1Target - (rate + 0.5 * vol * vol) * maturity;
            return spot * System.Math.Exp(exponent);
        }

        // Inverse of standard normal CDF (quantile function)
        private static double NormalInverse(double p)
        {
            if (p <= 0 || p >= 1)
                throw new System.ArgumentException("p must be in (0, 1)");

            // Wichura algorithm for high precision
            if (p < 0.02425)
                return RationalApprox(System.Math.Sqrt(-2.0 * System.Math.Log(p)), true);
            if (p > 0.97575)
                return RationalApprox(System.Math.Sqrt(-2.0 * System.Math.Log(1 - p)), false);
            return RationalApprox(p - 0.5, false);
        }

        private static double RationalApprox(double x, bool lower)
        {
            // Coefficients for rational approximation
            double[] a = { -3.969683028665376e+01, 2.221222899801429e+02, -2.821152023902548e+02, 1.340426573934869e+02, -3.387622301428440e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+00 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };

            double num = ((((a[0] * x + a[1]) * x + a[2]) * x + a[3]) * x + a[4]);
            double den = ((((b[0] * x + b[1]) * x + b[2]) * x + b[3]) * x + b[4]);

            if (lower)
                return -num / den;

            num = ((((c[0] * x + c[1]) * x + c[2]) * x + c[3]) * x + c[4]);
            den = (((d[0] * x + d[1]) * x + d[2]) * x + d[3]);
            return num / den;
        }
    }
}
