using System;
using System.Collections.Generic;
using System.Linq;

namespace HestonVolCalibrator.Swaptions
{
    // Bootstraps a piecewise-linear zero-rate (continuous compounding) discount curve
    // from par swap / yield rates using exact par-rate stripping.
    //
    // Par rate convention: r_n is the annual coupon rate s.t. a par bond
    // with coupon frequency f pays c = r_n/f per period and is priced at par:
    //   1 = c · Σ_{k=1}^{n*f} df(k/f) + df(n)
    //
    // Between grid maturities the zero rate is linearly interpolated.
    public static class DiscountCurveBootstrap
    {
        // Build a discount curve from par rates.
        // parRates must contain at least one entry and must be sorted by tenor (ascending).
        public static DiscountPoint[] Bootstrap(IEnumerable<ParRatePoint> parRates, int couponFrequency = 2)
        {
            var sorted = parRates.OrderBy(p => p.TenorYears).ToArray();
            if (sorted.Length == 0)
                throw new ArgumentException("At least one par rate is required.");

            double freq = couponFrequency;
            double dt = 1.0 / freq; // coupon period in years

            var dfs = new Dictionary<double, double>();   // maturity → df
            var zeros = new Dictionary<double, double>(); // maturity → zero rate

            // ── Insert df(0) = 1.0 ──
            dfs[0.0] = 1.0;

            foreach (var pt in sorted)
            {
                double tenor = pt.TenorYears;
                double coupon = pt.ParRate / freq;

                // Enumerate all coupon dates in [dt, tenor]
                int numPeriods = (int)Math.Round(tenor * freq);
                if (numPeriods < 1) numPeriods = 1;

                // Sum of discounted coupons for all but the last period
                double sumDf = 0.0;
                for (int k = 1; k < numPeriods; k++)
                {
                    double t = k * dt;
                    sumDf += coupon * Interpolate(dfs, t);
                }

                // Strip final df: 1 = sumDf + (coupon + 1) * df(tenor)
                double dfN = (1.0 - sumDf) / (1.0 + coupon);
                dfN = Math.Max(dfN, 1e-8); // clamp to avoid negative discount factors

                dfs[tenor] = dfN;

                double zeroRate = dfN > 0
                    ? -Math.Log(dfN) / tenor
                    : 0.0;
                zeros[tenor] = zeroRate;
            }

            return dfs
                .Where(kv => kv.Key > 0)
                .OrderBy(kv => kv.Key)
                .Select(kv => new DiscountPoint(kv.Key, kv.Value, zeros.TryGetValue(kv.Key, out var z) ? z : 0.0))
                .ToArray();
        }

        // Compute a discount factor at an arbitrary maturity by interpolating the zero rate.
        public static double DiscountFactor(DiscountPoint[] curve, double maturity)
        {
            if (maturity <= 0.0) return 1.0;
            double zr = InterpolateZeroRate(curve, maturity);
            return Math.Exp(-zr * maturity);
        }

        // Compute the forward swap rate for a swap that starts at t_start and has tenor years,
        // with payments at frequency f per year.
        public static (double forwardSwapRate, double annuity) ForwardSwapRate(
            DiscountPoint[] curve, double optionExpiry, double swapTenor, int couponFrequency = 2)
        {
            double freq = couponFrequency;
            double dt = 1.0 / freq;
            double tEnd = optionExpiry + swapTenor;

            int numPeriods = (int)Math.Round(swapTenor * freq);
            if (numPeriods < 1) numPeriods = 1;

            double annuity = 0.0;
            for (int k = 1; k <= numPeriods; k++)
            {
                double t = optionExpiry + k * dt;
                double df = DiscountFactor(curve, t);
                annuity += df * dt;
            }

            double dfStart = DiscountFactor(curve, optionExpiry);
            double dfEnd   = DiscountFactor(curve, tEnd);

            double fwdSwapRate = annuity > 1e-14
                ? (dfStart - dfEnd) / annuity
                : 0.0;

            return (fwdSwapRate, annuity);
        }

        // ── Helpers ────────────────────────────────────────────────────────────────

        // Log-linear interpolation of df(t) within the known grid.
        // Extrapolates using flat zero rate (not flat df) beyond the last known point,
        // which is critical when bootstrapping long-maturity par bonds.
        private static double Interpolate(Dictionary<double, double> dfs, double t)
        {
            if (dfs.TryGetValue(t, out double exact)) return exact;

            var sorted = dfs.OrderBy(kv => kv.Key).ToArray();

            if (t <= sorted[0].Key) return sorted[0].Value;

            // Flat zero-rate extrapolation beyond the last known point
            if (t >= sorted[^1].Key)
            {
                double lastT = sorted[^1].Key;
                double lastDf = sorted[^1].Value;
                if (lastT <= 0 || lastDf <= 0) return lastDf;
                double lastZ = -Math.Log(Math.Max(lastDf, 1e-15)) / lastT;
                return Math.Exp(-lastZ * t);
            }

            // Log-linear interpolation within the grid (= linear zero rate)
            for (int i = 0; i < sorted.Length - 1; i++)
            {
                if (sorted[i].Key <= t && t <= sorted[i + 1].Key)
                {
                    double t0 = sorted[i].Key, df0 = sorted[i].Value;
                    double t1 = sorted[i + 1].Key, df1 = sorted[i + 1].Value;
                    double alpha = (t - t0) / (t1 - t0);
                    return Math.Exp((1 - alpha) * Math.Log(Math.Max(df0, 1e-15))
                                    + alpha      * Math.Log(Math.Max(df1, 1e-15)));
                }
            }

            return sorted[^1].Value;
        }

        // Zero-rate interpolation from a DiscountPoint[].
        private static double InterpolateZeroRate(DiscountPoint[] curve, double maturity)
        {
            if (curve.Length == 0) return 0.0;
            if (maturity <= curve[0].MaturityYears) return curve[0].ZeroRate;
            if (maturity >= curve[^1].MaturityYears) return curve[^1].ZeroRate;

            for (int i = 0; i < curve.Length - 1; i++)
            {
                if (curve[i].MaturityYears <= maturity && maturity <= curve[i + 1].MaturityYears)
                {
                    double t0 = curve[i].MaturityYears, z0 = curve[i].ZeroRate;
                    double t1 = curve[i + 1].MaturityYears, z1 = curve[i + 1].ZeroRate;
                    double alpha = (maturity - t0) / (t1 - t0);
                    return z0 + alpha * (z1 - z0);
                }
            }

            return curve[^1].ZeroRate;
        }
    }
}
