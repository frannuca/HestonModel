using System;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace HestonVolCalibrator.Calibration
{
    // Unconstrained BFGS in the log/atanh-transformed parameter space (R^5).
    // Numerical gradient by central differences in the transformed space.
    public sealed class BfgsOptimizer : IOptimizer
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
            var t = new TransformedObjective(obj);
            var q0 = ParamTransforms.Encode(x0);

            // Outer-iteration counter: incremented exactly once per gradient evaluation.
            // In Math.NET BFGS, each accepted outer step needs one gradient at the new point,
            // so this is the natural "outer iteration" counter and matches the callback semantics
            // of our other optimizers (NelderMead, GA).
            int outerIter = 0;
            double bestF = double.PositiveInfinity;
            double[] bestQ = (double[])q0.Clone();
            bool capHit = false;

            // Stop signal: once the cap is hit, return a zero gradient so Math.NET's stopping
            // criteria fire and no further reports are emitted.
            Vector<double> Grad(Vector<double> q)
            {
                int n = q.Count;
                if (outerIter >= opts.MaxIterations)
                {
                    capHit = true;
                    return Vector<double>.Build.Dense(n, 0.0);
                }
                outerIter++;

                var g = Vector<double>.Build.Dense(n);
                var qa = q.ToArray();
                double fAtQ = t.Evaluate(qa);
                if (fAtQ < bestF)
                {
                    bestF = fAtQ;
                    bestQ = (double[])qa.Clone();
                }
                for (int i = 0; i < n; i++)
                {
                    double orig = qa[i];
                    qa[i] = orig + FiniteDiffEps;
                    double fp = t.Evaluate(qa);
                    qa[i] = orig - FiniteDiffEps;
                    double fm = t.Evaluate(qa);
                    qa[i] = orig;
                    g[i] = (fp - fm) / (2.0 * FiniteDiffEps);
                }
                // Report once per outer iteration (NOT once per function eval).
                iterCallback?.Invoke(outerIter, bestF, ParamTransforms.Decode(bestQ));
                return g;
            }

            double F(Vector<double> q)
            {
                double f = t.Evaluate(q.ToArray());
                if (f < bestF)
                {
                    bestF = f;
                    bestQ = q.ToArray();
                }
                return f;
            }

            var ofg = ObjectiveFunction.Gradient(
                function: F,
                gradient: Grad);

            // Use opts.Tolerance as the gradient tolerance (most natural stopping criterion for BFGS).
            double gTol = opts.Tolerance > 0 ? opts.Tolerance : GradientTolerance;

            var bfgs = new BfgsMinimizer(
                gradientTolerance: gTol,
                parameterTolerance: ParameterTolerance,
                functionProgressTolerance: FunctionProgressTolerance,
                maximumIterations: opts.MaxIterations);

            try
            {
                var res = bfgs.FindMinimum(ofg, Vector<double>.Build.DenseOfArray(q0));
                var x = ParamTransforms.Decode(res.MinimizingPoint.ToArray());
                bool converged = res.ReasonForExit != ExitCondition.ExceedIterations && !capHit;
                return new OptimizationResult(x, res.FunctionInfoAtMinimum.Value, outerIter, converged, "Bfgs");
            }
            catch
            {
                return new OptimizationResult(ParamTransforms.Decode(bestQ), bestF, outerIter, false, "Bfgs");
            }
        }
    }
}
