using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.Calibration
{
    // Calibrates Heston parameters to a vega-weighted IV surface.
    // Pluggable optimisation back-ends: global (NelderMead, Genetic) and gradient (Bfgs, BfgsB),
    // optionally chained.
    //
    // Parameter order across this codebase: [kappa, theta, sigma, rho, v0].
    public class HestonCalibrator
    {
        private readonly IMarketData _market;

        public HestonCalibrator(IMarketData market) => _market = market;

        // ───────────────────────── New entry point ─────────────────────────
        public CalibrationResult Calibrate(
            CalibrationRequest req,
            IVolatilitySurface surface,
            Action<ConvergencePoint>? onProgress = null)
        {
            var sw = Stopwatch.StartNew();
            var history = new List<ConvergencePoint>();

            // Sample every (maturity, strike) cell — one global fit over the full surface.
            var expiries = surface.Expiries.OrderBy(t => t).ToArray();
            var strikes  = surface.Strikes.OrderBy(k => k).ToArray();
            var pts = new List<(double, double)>(expiries.Length * strikes.Length);
            foreach (var t in expiries)
                foreach (var k in strikes)
                    pts.Add((t, k));

            double[] lower = { req.Kappa.Lower, req.Theta.Lower, req.Sigma.Lower, req.Rho.Lower, req.V0.Lower };
            double[] upper = { req.Kappa.Upper, req.Theta.Upper, req.Sigma.Upper, req.Rho.Upper, req.V0.Upper };

            var objective = new VegaWeightedObjective(
                surface, req.Spot, req.RiskFreeRate, req.DividendYield, pts, lower, upper);

            double[] x0 = req.InitialGuess ?? DefaultInitialGuess(surface, req.Spot);
            for (int i = 0; i < x0.Length; i++)
                x0[i] = Math.Min(Math.Max(x0[i], lower[i] + 1e-8), upper[i] - 1e-8);

            int totalIters = 0;
            double[] currentBest = (double[])x0.Clone();
            double currentBestF = objective.Evaluate(currentBest);
            var stages = new List<StageResult>();

            // ── Global stage ──
            if (req.GlobalMethod != GlobalMethod.None)
            {
                IOptimizer global = req.GlobalMethod switch
                {
                    GlobalMethod.NelderMead => new NelderMeadOptimizer { Restarts = req.NelderMeadRestarts },
                    GlobalMethod.Genetic    => new GeneticOptimizer { GenerationsOverride = req.GlobalMaxIterations },
                    _ => throw new InvalidOperationException()
                };

                var gOpts = new OptimizerOptions(req.GlobalMaxIterations, 1e-8, req.Seed);

                void GlobalCb(int it, double f, double[] x)
                {
                    var pt = new ConvergencePoint(it, f, "global");
                    history.Add(pt);
                    onProgress?.Invoke(pt);
                }

                var gres = global.Minimize(objective, x0, gOpts, GlobalCb);
                totalIters += gres.Iterations;
                stages.Add(new StageResult("global", gres.Method, gres.Converged, gres.Iterations, gres.FinalValue));
                if (gres.FinalValue < currentBestF)
                {
                    currentBest = gres.X;
                    currentBestF = gres.FinalValue;
                }
            }

            // ── Gradient stage ──
            if (req.GradientMethod != GradientMethod.None)
            {
                IOptimizer grad = req.GradientMethod switch
                {
                    GradientMethod.Bfgs  => new BfgsOptimizer(),
                    GradientMethod.BfgsB => new BfgsBOptimizer(),
                    _ => throw new InvalidOperationException()
                };

                double[] startX = req.Chain ? currentBest : x0;
                var grOpts = new OptimizerOptions(req.GradientMaxIterations, 1e-8, req.Seed);
                int globalBase = history.Count;

                void GradCb(int it, double f, double[] x)
                {
                    var pt = new ConvergencePoint(globalBase + it, f, "gradient");
                    history.Add(pt);
                    onProgress?.Invoke(pt);
                }

                var rres = grad.Minimize(objective, startX, grOpts, GradCb);
                totalIters += rres.Iterations;
                stages.Add(new StageResult("gradient", rres.Method, rres.Converged, rres.Iterations, rres.FinalValue));
                if (rres.FinalValue < currentBestF)
                {
                    currentBest = rres.X;
                    currentBestF = rres.FinalValue;
                }
            }

            bool allConverged = stages.Count > 0 && stages.TrueForAll(s => s.Converged);

            var fitted = new HestonParams(currentBest[0], currentBest[1], currentBest[2], currentBest[3], currentBest[4]);
            var fittedModel = new HestonModelParams(fitted.Kappa, fitted.Theta, fitted.Sigma, fitted.Rho, fitted.V0);

            // ── Build market & Heston IV matrices ──
            var marketIv = new double[expiries.Length][];
            var hestonIv = new double[expiries.Length][];
            for (int i = 0; i < expiries.Length; i++)
            {
                marketIv[i] = new double[strikes.Length];
                hestonIv[i] = new double[strikes.Length];
                double t = expiries[i];
                for (int j = 0; j < strikes.Length; j++)
                {
                    double k = strikes[j];
                    try { marketIv[i][j] = surface.GetVolByStrike(req.Spot, k, t); } catch { marketIv[i][j] = double.NaN; }
                    try { hestonIv[i][j] = HestonPricer.ImpliedVol(fittedModel, req.Spot, k, t, req.RiskFreeRate, req.DividendYield); }
                    catch { hestonIv[i][j] = double.NaN; }
                }
            }

            sw.Stop();
            return new CalibrationResult
            {
                HestonParams = fitted,
                FinalRmse = currentBestF,
                History = history,
                MarketIv = marketIv,
                HestonIv = hestonIv,
                Expiries = expiries,
                Strikes = strikes,
                TotalIterations = totalIters,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Converged = allConverged,
                Stages = stages
            };
        }

        private static double[] DefaultInitialGuess(IVolatilitySurface surface, double spot)
        {
            // ATM-ish vol via shortest-expiry near-spot strike.
            double atmVar = 0.04;
            try
            {
                var ts = surface.Expiries;
                var ks = surface.Strikes;
                if (ts.Count > 0 && ks.Count > 0)
                {
                    double t = ts[0];
                    double k = ks.OrderBy(x => Math.Abs(x - spot)).First();
                    double v = surface.GetVolByStrike(spot, k, t);
                    if (v > 0.001 && v < 5.0) atmVar = v * v;
                }
            }
            catch { /* fall through */ }

            return new[] { 1.5, atmVar, 0.5, -0.6, atmVar };
        }

        // ───────────────────────── Legacy entry point ─────────────────────────
        // Preserves the signature used by Runner/Program.cs and DataLoader/Program.cs.
        // Routes through the new pipeline with NM defaults.
        public HestonModelParams Calibrate(
            double spot,
            IEnumerable<(double maturity, double strike)> samples,
            HestonModelParams initial,
            double rate = 0.0,
            double dividendYield = 0.0,
            int maxIterations = 2000,
            int numStarts = 5,
            int randomSeed = 42)
        {
            var pts = samples.ToList();

            // Use the legacy custom-grid path directly to avoid relying on surface.Expiries/Strikes
            // (the legacy callers pass a curated sample list).
            double[] lower = { 1e-4, 1e-6, 1e-4, -0.999, 1e-6 };
            double[] upper = { 100.0, 5.0, 10.0, 0.999, 5.0 };

            var obj = new VegaWeightedObjective(
                new IndirectSurface(_market),
                spot, rate, dividendYield, pts, lower, upper);

            var nm = new NelderMeadOptimizer { Restarts = numStarts };
            var x0 = new[] { initial.Kappa, initial.Theta, initial.Sigma, initial.Rho, initial.V0 };
            var res = nm.Minimize(obj, x0, new OptimizerOptions(maxIterations, 1e-8, randomSeed));

            return new HestonModelParams(res.X[0], res.X[1], res.X[2], res.X[3], res.X[4]);
        }

        // Adapter so legacy path can feed an IMarketData-backed surface into VegaWeightedObjective,
        // which expects an IVolatilitySurface.
        private sealed class IndirectSurface : IVolatilitySurface
        {
            private readonly IMarketData _md;
            public IndirectSurface(IMarketData md) { _md = md; }
            public double GetVolByStrike(double spot, double strike, double maturity) =>
                _md.GetVolByStrike(spot, strike, maturity);
            public double GetVolByDelta(double spot, double delta, double maturity) =>
                _md.GetVolByDelta(spot, delta, maturity);
            public bool HasMaturity(double maturity) => true;
        }

    }
}
