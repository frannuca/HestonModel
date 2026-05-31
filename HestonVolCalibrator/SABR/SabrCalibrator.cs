using System;
using System.Linq;

namespace HestonVolCalibrator.SABR
{
    // Calibrates SABR to a single vol smile slice by minimising unweighted RMSE.
    //
    // Standard (fixBeta = true):  calibrate (α, ρ, ν) with β given — market convention.
    // Free-beta (fixBeta = false): calibrate all four (α, β, ρ, ν) — less stable, use with care.
    //
    // Internally runs multi-start Nelder-Mead in transformed (unconstrained) space:
    //   α → log(α),  β → logit(β),  ρ → atanh(ρ),  ν → log(ν)
    public static class SabrCalibrator
    {
        // ── Input/output ──────────────────────────────────────────────────────────

        public record CalibrateInput(
            double Forward,
            double Expiry,
            double[] Strikes,
            double[] MarketVols,
            double Beta = 0.5,
            bool FixBeta = true,
            double Shift = 0.0,                         // displacement for negative-rate environments
            VolConvention Convention = VolConvention.Lognormal,
            int MaxIterations = 2000,
            int Restarts = 5,
            int? Seed = null);

        // ── Entry point ───────────────────────────────────────────────────────────

        public static SabrCalibrationResult Calibrate(CalibrateInput input)
        {
            if (input.Strikes.Length != input.MarketVols.Length)
                throw new ArgumentException("Strikes and MarketVols must have equal length.");
            if (input.Strikes.Length == 0)
                throw new ArgumentException("At least one market quote is required.");

            double shift = input.Shift;
            double fwd = input.Forward + shift;

            // Filter valid quotes (shifted strike must be positive, vol must be finite and positive)
            var valid = input.Strikes
                .Zip(input.MarketVols, (k, v) => (K: k + shift, V: v))
                .Where(q => q.K > 0 && q.V > 0 && double.IsFinite(q.V))
                .ToArray();

            if (valid.Length == 0)
            {
                var fallback = new SabrParams(0.01, input.Beta, 0.0, 0.3);
                return new SabrCalibrationResult(
                    fallback, shift, double.MaxValue, false, 0,
                    new double[input.Strikes.Length], input.MarketVols);
            }

            double[] ks = valid.Select(q => q.K).ToArray();
            double[] vs = valid.Select(q => q.V).ToArray();

            // ATM vol estimate for initial guess
            double atmVol = vs[0];
            double minDist = Math.Abs(ks[0] - fwd);
            for (int i = 1; i < ks.Length; i++)
            {
                double d = Math.Abs(ks[i] - fwd);
                if (d < minDist) { minDist = d; atmVol = vs[i]; }
            }
            atmVol = Math.Max(atmVol, 1e-4);

            return input.FixBeta
                ? CalibrateFixed(fwd, input.Expiry, ks, vs, input.Beta, shift, input, atmVol)
                : CalibrateFree(fwd, input.Expiry, ks, vs, shift, input, atmVol, input.Beta);
        }

        // ── Fixed-beta: calibrate (α, ρ, ν) ──────────────────────────────────────

        private static SabrCalibrationResult CalibrateFixed(
            double fwd, double expiry, double[] ks, double[] vs,
            double beta, double shift, CalibrateInput input, double atmVol)
        {
            double fBeta = fwd > 0 ? Math.Pow(fwd, 1.0 - beta) : 1.0;
            double alpha0 = Math.Max(atmVol * fBeta, 1e-5);

            // x = [log(α), atanh(ρ), log(ν)]
            double[] x0 = { Math.Log(alpha0), 0.0, Math.Log(0.3) };

            double Obj(double[] x)
            {
                var p = new SabrParams(Math.Exp(x[0]), beta, Math.Tanh(x[1]), Math.Exp(x[2]));
                return Rmse(p, fwd, expiry, ks, vs, input.Convention);
            }

            double[] pert = { 0.4, 0.4, 0.5 };
            double[] jitter = { 0.7, 0.6, 0.8 };
            var (xBest, fBest, iters) = NelderMead(Obj, x0, pert, jitter,
                input.MaxIterations, input.Restarts, input.Seed ?? 42);

            var best = new SabrParams(Math.Exp(xBest[0]), beta, Math.Tanh(xBest[1]), Math.Exp(xBest[2]));
            double[] modelVols = BuildModelVols(best, input.Forward, expiry, input.Strikes, shift, input.Convention);
            return new SabrCalibrationResult(best, shift, fBest, fBest < 5e-4, iters, modelVols, input.MarketVols);
        }

        // ── Free-beta: calibrate (α, β, ρ, ν) ───────────────────────────────────

        private static SabrCalibrationResult CalibrateFree(
            double fwd, double expiry, double[] ks, double[] vs,
            double shift, CalibrateInput input, double atmVol, double betaHint)
        {
            double fBeta = fwd > 0 ? Math.Pow(fwd, 1.0 - betaHint) : 1.0;
            double alpha0 = Math.Max(atmVol * fBeta, 1e-5);

            // x = [log(α), logit(β), atanh(ρ), log(ν)]
            double[] x0 = { Math.Log(alpha0), Logit(betaHint), 0.0, Math.Log(0.3) };

            double Obj(double[] x)
            {
                var p = new SabrParams(Math.Exp(x[0]), Sigmoid(x[1]), Math.Tanh(x[2]), Math.Exp(x[3]));
                return Rmse(p, fwd, expiry, ks, vs, input.Convention);
            }

            double[] pert = { 0.4, 0.3, 0.4, 0.5 };
            double[] jitter = { 0.7, 0.4, 0.6, 0.8 };
            var (xBest, fBest, iters) = NelderMead(Obj, x0, pert, jitter,
                input.MaxIterations, input.Restarts, input.Seed ?? 42);

            var best = new SabrParams(
                Math.Exp(xBest[0]), Sigmoid(xBest[1]), Math.Tanh(xBest[2]), Math.Exp(xBest[3]));
            double[] modelVols = BuildModelVols(best, input.Forward, expiry, input.Strikes, shift, input.Convention);
            return new SabrCalibrationResult(best, shift, fBest, fBest < 5e-4, iters, modelVols, input.MarketVols);
        }

        // ── Objective ────────────────────────────────────────────────────────────

        private static double Rmse(SabrParams p, double fwd, double expiry,
            double[] ks, double[] vs, VolConvention conv)
        {
            double sum = 0.0;
            for (int i = 0; i < ks.Length; i++)
            {
                double v = ModelVol(p, fwd, ks[i], expiry, conv);
                if (double.IsNaN(v) || !double.IsFinite(v)) return double.MaxValue;
                double d = v - vs[i];
                sum += d * d;
            }
            return Math.Sqrt(sum / ks.Length);
        }

        private static double ModelVol(SabrParams p, double fwd, double k, double expiry, VolConvention conv) =>
            conv == VolConvention.Normal
                ? SabrPricer.NormalImpliedVol(p, fwd, k, expiry)
                : SabrPricer.ImpliedVol(p, fwd, k, expiry);

        private static double[] BuildModelVols(SabrParams p, double fwdRaw, double expiry,
            double[] strikes, double shift, VolConvention conv)
        {
            var result = new double[strikes.Length];
            double fwd = fwdRaw + shift;
            for (int i = 0; i < strikes.Length; i++)
            {
                double k = strikes[i] + shift;
                result[i] = (k > 0 && fwd > 0) ? ModelVol(p, fwd, k, expiry, conv) : double.NaN;
            }
            return result;
        }

        // ── Nelder-Mead (multi-start, n-dimensional) ──────────────────────────────

        private static (double[] xBest, double fBest, int iters) NelderMead(
            Func<double[], double> fn,
            double[] x0,
            double[] pert,
            double[] jitter,
            int maxIter,
            int restarts,
            int seed)
        {
            const double refl = 1.0, expn = 2.0, cont = 0.5, shrink = 0.5;
            const double tolF = 1e-10;
            int n = x0.Length;

            double bestF = double.PositiveInfinity;
            double[] bestX = (double[])x0.Clone();
            int totalIter = 0;
            var rng = new Random(seed);

            for (int run = 0; run < Math.Max(1, restarts); run++)
            {
                var start = (double[])x0.Clone();
                if (run > 0)
                    for (int i = 0; i < n; i++)
                        start[i] += (rng.NextDouble() * 2.0 - 1.0) * jitter[i];

                // Build initial simplex
                var sx = new double[n + 1][];
                var fv = new double[n + 1];
                sx[0] = (double[])start.Clone();
                fv[0] = fn(sx[0]);
                for (int i = 0; i < n; i++)
                {
                    sx[i + 1] = (double[])start.Clone();
                    sx[i + 1][i] += pert[i];
                    fv[i + 1] = fn(sx[i + 1]);
                }

                for (int iter = 0; iter < maxIter; iter++)
                {
                    // Sort ascending by function value
                    int[] ord = Enumerable.Range(0, n + 1).OrderBy(i => fv[i]).ToArray();
                    sx = ord.Select(i => sx[i]).ToArray();
                    fv = ord.Select(i => fv[i]).ToArray();
                    totalIter++;

                    if (fv[n] - fv[0] < tolF) break;

                    // Centroid of n best vertices
                    var xBar = new double[n];
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++) xBar[j] += sx[i][j];
                    for (int j = 0; j < n; j++) xBar[j] /= n;

                    // Reflection
                    var xr = new double[n];
                    for (int j = 0; j < n; j++) xr[j] = xBar[j] + refl * (xBar[j] - sx[n][j]);
                    double fr = fn(xr);

                    if (fr < fv[0])
                    {
                        // Expansion
                        var xe = new double[n];
                        for (int j = 0; j < n; j++) xe[j] = xBar[j] + expn * (xr[j] - xBar[j]);
                        double fe = fn(xe);
                        if (fe < fr) { sx[n] = xe; fv[n] = fe; }
                        else         { sx[n] = xr; fv[n] = fr; }
                    }
                    else if (fr < fv[n - 1])
                    {
                        sx[n] = xr; fv[n] = fr;
                    }
                    else
                    {
                        // Contraction
                        bool outside = fr < fv[n];
                        var src = outside ? xr : sx[n];
                        double srcF = outside ? fr : fv[n];
                        var xc = new double[n];
                        for (int j = 0; j < n; j++) xc[j] = xBar[j] + cont * (src[j] - xBar[j]);
                        double fc = fn(xc);
                        if (fc < srcF) { sx[n] = xc; fv[n] = fc; }
                        else
                        {
                            // Shrink
                            for (int i = 1; i <= n; i++)
                            {
                                for (int j = 0; j < n; j++)
                                    sx[i][j] = sx[0][j] + shrink * (sx[i][j] - sx[0][j]);
                                fv[i] = fn(sx[i]);
                            }
                        }
                    }
                }

                if (fv[0] < bestF) { bestF = fv[0]; bestX = (double[])sx[0].Clone(); }
            }

            return (bestX, bestF, totalIter);
        }

        // ── Transform helpers ─────────────────────────────────────────────────────

        private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));
        private static double Logit(double p)
        {
            p = Math.Max(1e-6, Math.Min(1.0 - 1e-6, p));
            return Math.Log(p / (1.0 - p));
        }
    }
}
