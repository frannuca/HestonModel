using System;
using System.Linq;

namespace HestonVolCalibrator.Calibration
{
    // Custom Nelder-Mead simplex in log/atanh-transformed parameter space.
    // Supports multi-start (Restarts) and per-iteration callbacks reporting the running-best.
    // Standard NM coefficients: alpha=1 (reflection), gamma=2 (expansion),
    // rho=0.5 (contraction), sigma=0.5 (shrink).
    public sealed class NelderMeadOptimizer : IOptimizer
    {
        public int Restarts { get; init; } = 5;

        // Initial simplex edge length in transformed space (per coordinate).
        public double[] InitialPerturbation { get; init; } = { 0.5, 0.5, 0.5, 0.3, 0.5 };

        // Random restart scale (fraction of perturbation) in transformed space.
        public double[] RestartJitter { get; init; } = { 0.8, 0.8, 0.8, 0.5, 0.8 };

        public OptimizationResult Minimize(
            IObjective obj,
            double[] x0,
            OptimizerOptions opts,
            Action<int, double, double[]>? iterCallback = null)
        {
            // Operate in transformed (unconstrained) space.
            var t = new TransformedObjective(obj);
            var q0 = ParamTransforms.Encode(x0);

            int globalIter = 0;
            double bestF = double.PositiveInfinity;
            double[] bestQ = (double[])q0.Clone();

            void Report(double f, double[] q)
            {
                globalIter++;
                if (f < bestF)
                {
                    bestF = f;
                    bestQ = (double[])q.Clone();
                }
                iterCallback?.Invoke(globalIter, bestF, ParamTransforms.Decode(bestQ));
            }

            // First start from x0.
            var (qa, fa, ita) = RunSimplex(t, q0, opts, Report);
            if (fa < bestF) { bestF = fa; bestQ = qa; }

            // Random restarts.
            var rng = new Random(opts.Seed ?? 42);
            for (int s = 1; s < Math.Max(1, Restarts); s++)
            {
                var qs = (double[])q0.Clone();
                for (int i = 0; i < qs.Length; i++)
                    qs[i] += (rng.NextDouble() * 2.0 - 1.0) * RestartJitter[i];

                var (qc, fc, _) = RunSimplex(t, qs, opts, Report);
                if (fc < bestF) { bestF = fc; bestQ = qc; }
            }

            return new OptimizationResult(
                X: ParamTransforms.Decode(bestQ),
                FinalValue: bestF,
                Iterations: globalIter,
                Converged: true,
                Method: "NelderMead");
        }

        private (double[] qBest, double fBest, int iters) RunSimplex(
            IObjective tObj,
            double[] q0,
            OptimizerOptions opts,
            Action<double, double[]> report)
        {
            const double alpha = 1.0, gamma = 2.0, rhoC = 0.5, sigma = 0.5;
            int n = q0.Length;
            var simplex = new double[n + 1][];
            var fvals = new double[n + 1];
            simplex[0] = (double[])q0.Clone();
            fvals[0] = tObj.Evaluate(simplex[0]);
            for (int i = 0; i < n; i++)
            {
                var v = (double[])q0.Clone();
                v[i] += InitialPerturbation[i];
                simplex[i + 1] = v;
                fvals[i + 1] = tObj.Evaluate(v);
            }

            int iter = 0;
            for (; iter < opts.MaxIterations; iter++)
            {
                // Sort by function value ascending.
                var order = Enumerable.Range(0, n + 1).OrderBy(i => fvals[i]).ToArray();
                var newSimplex = order.Select(i => simplex[i]).ToArray();
                var newFvals = order.Select(i => fvals[i]).ToArray();
                simplex = newSimplex; fvals = newFvals;

                report(fvals[0], simplex[0]);

                // Convergence: spread of f-values.
                double fSpread = fvals[n] - fvals[0];
                if (fSpread < opts.Tolerance) break;

                // Centroid of all but worst.
                var xBar = new double[n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) xBar[j] += simplex[i][j];
                for (int j = 0; j < n; j++) xBar[j] /= n;

                // Reflection.
                var xr = new double[n];
                for (int j = 0; j < n; j++) xr[j] = xBar[j] + alpha * (xBar[j] - simplex[n][j]);
                double fr = tObj.Evaluate(xr);

                if (fr < fvals[0])
                {
                    // Expansion.
                    var xe = new double[n];
                    for (int j = 0; j < n; j++) xe[j] = xBar[j] + gamma * (xr[j] - xBar[j]);
                    double fe = tObj.Evaluate(xe);
                    if (fe < fr) { simplex[n] = xe; fvals[n] = fe; }
                    else         { simplex[n] = xr; fvals[n] = fr; }
                    continue;
                }

                if (fr < fvals[n - 1])
                {
                    simplex[n] = xr; fvals[n] = fr;
                    continue;
                }

                // Contraction.
                var xc = new double[n];
                if (fr < fvals[n])
                {
                    for (int j = 0; j < n; j++) xc[j] = xBar[j] + rhoC * (xr[j] - xBar[j]);
                }
                else
                {
                    for (int j = 0; j < n; j++) xc[j] = xBar[j] + rhoC * (simplex[n][j] - xBar[j]);
                }
                double fc = tObj.Evaluate(xc);
                if (fc < Math.Min(fr, fvals[n]))
                {
                    simplex[n] = xc; fvals[n] = fc;
                    continue;
                }

                // Shrink toward best vertex.
                for (int i = 1; i <= n; i++)
                {
                    for (int j = 0; j < n; j++)
                        simplex[i][j] = simplex[0][j] + sigma * (simplex[i][j] - simplex[0][j]);
                    fvals[i] = tObj.Evaluate(simplex[i]);
                }
            }

            int bestIdx = 0;
            for (int i = 1; i <= n; i++) if (fvals[i] < fvals[bestIdx]) bestIdx = i;
            return (simplex[bestIdx], fvals[bestIdx], iter);
        }
    }
}
