using System;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace HestonVolCalibrator.Calibration
{
    // Box-constrained BFGS (Math.NET BfgsBMinimizer) in RAW parameter space.
    // The whole point of L-BFGS-B is to respect user-specified bounds literally,
    // so we do NOT apply the log/atanh transform here.
    //
    // Numerical gradient by central differences with reflective stencil at the box edges.
    public sealed class BfgsBOptimizer : IOptimizer
    {
        public double FiniteDiffEps { get; init; } = 1e-5;
        public double GradientTolerance { get; init; } = 1e-6;
        public double ParameterTolerance { get; init; } = 1e-8;
        public double FunctionProgressTolerance { get; init; } = 1e-10;

        public OptimizationResult Minimize(
            IObjective obj,
            double[] x0,
            OptimizerOptions opts,
            Action<int, double, double[]>? iterCallback = null)
        {
            int n = x0.Length;
            var lower = (double[])obj.Lower.Clone();
            var upper = (double[])obj.Upper.Clone();

            // Clamp starting point strictly inside the box.
            var xStart = new double[n];
            for (int i = 0; i < n; i++)
            {
                double lo = lower[i], hi = upper[i];
                double pad = Math.Max(FiniteDiffEps * 10.0, 1e-8);
                xStart[i] = Math.Min(Math.Max(x0[i], lo + pad), hi - pad);
            }

            // Outer-iteration counter: incremented exactly once per gradient evaluation.
            // (See BfgsOptimizer for the rationale.)
            int outerIter = 0;
            int fEvals = 0;
            double bestF = double.PositiveInfinity;
            double[] bestX = (double[])xStart.Clone();
            bool capHit = false;
            // Hard ceiling on F() evals so a pathological line search can't run forever
            // even if Math.NET's internal cap is masked. 50 evals/iter is generous: ~10 for
            // central-difference gradient + line-search probes.
            int fEvalCap = Math.Max(50, opts.MaxIterations * 50);

            // Stop signal: once outerIter hits MaxIterations (or fEvals hits its ceiling) we
            // throw out of the inner functions. Math.NET propagates the exception and our
            // outer catch returns the best-so-far. This is more deterministic than nudging
            // Math.NET with a zero gradient.
            Vector<double> Grad(Vector<double> x)
            {
                if (outerIter >= opts.MaxIterations)
                {
                    capHit = true;
                    throw new IterationCapException();
                }
                outerIter++;

                var g = Vector<double>.Build.Dense(n);
                var xa = x.ToArray();
                double fAtX = obj.Evaluate(xa);
                if (fAtX < bestF)
                {
                    bestF = fAtX;
                    bestX = (double[])xa.Clone();
                }
                for (int i = 0; i < n; i++)
                {
                    double orig = xa[i];
                    double hp = FiniteDiffEps;
                    double hm = FiniteDiffEps;
                    // Keep stencil inside the box.
                    if (orig + hp > upper[i]) hp = Math.Max(upper[i] - orig, 1e-12);
                    if (orig - hm < lower[i]) hm = Math.Max(orig - lower[i], 1e-12);
                    xa[i] = orig + hp;
                    double fp = obj.Evaluate(xa);
                    xa[i] = orig - hm;
                    double fm = obj.Evaluate(xa);
                    xa[i] = orig;
                    g[i] = (fp - fm) / (hp + hm);
                }
                // Report once per outer iteration (NOT once per function eval).
                iterCallback?.Invoke(outerIter, bestF, (double[])bestX.Clone());
                return g;
            }

            double F(Vector<double> x)
            {
                if (++fEvals > fEvalCap)
                {
                    capHit = true;
                    throw new IterationCapException();
                }
                double f = obj.Evaluate(x.ToArray());
                if (f < bestF)
                {
                    bestF = f;
                    bestX = x.ToArray();
                }
                return f;
            }

            var ofg = ObjectiveFunction.Gradient(F, Grad);

            // Use opts.Tolerance as the gradient tolerance.
            double gTol = opts.Tolerance > 0 ? opts.Tolerance : GradientTolerance;

            var bfgsb = new BfgsBMinimizer(
                gradientTolerance: gTol,
                parameterTolerance: ParameterTolerance,
                functionProgressTolerance: FunctionProgressTolerance,
                maximumIterations: opts.MaxIterations);

            try
            {
                var res = bfgsb.FindMinimum(
                    ofg,
                    Vector<double>.Build.DenseOfArray(lower),
                    Vector<double>.Build.DenseOfArray(upper),
                    Vector<double>.Build.DenseOfArray(xStart));
                var x = res.MinimizingPoint.ToArray();
                bool converged = res.ReasonForExit != ExitCondition.ExceedIterations && !capHit;
                return new OptimizationResult(x, res.FunctionInfoAtMinimum.Value, outerIter, converged, "BfgsB");
            }
            catch (IterationCapException)
            {
                return new OptimizationResult(bestX, bestF, outerIter, false, "BfgsB (capped)");
            }
            catch
            {
                return new OptimizationResult(bestX, bestF, outerIter, false, "BfgsB");
            }
        }
    }
}
