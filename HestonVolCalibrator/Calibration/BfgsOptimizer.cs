using System;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace HestonVolCalibrator.Calibration
{
    // Unconstrained BFGS in the log/atanh-transformed parameter space (R^5).
    // Numerical gradient by central differences in the transformed space.
    //
    // The transform implicitly bounds parameters to (0,inf) and rho to (-1,1). If the caller
    // supplied tighter literal bounds via IObjective.Lower/Upper, we honour them by clamping
    // the decoded raw-space point before evaluation and before reporting iterates. This makes
    // BFGS behaviour consistent with BfgsB (which uses literal bounds natively).
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
            var lower = obj.Lower;
            var upper = obj.Upper;
            var t = new TransformedObjective(new BoxClampedObjective(obj, lower, upper));
            // Clamp x0 into the user box before encoding, so the starting point isn't outside the bounds.
            var x0Clamped = ClampToBox(x0, lower, upper);
            var q0 = ParamTransforms.Encode(x0Clamped);

            // Outer-iteration counter: incremented exactly once per gradient evaluation.
            // In Math.NET BFGS, each accepted outer step needs one gradient at the new point,
            // so this is the natural "outer iteration" counter and matches the callback semantics
            // of our other optimizers (NelderMead, GA).
            int outerIter = 0;
            int fEvals = 0;
            double bestF = double.PositiveInfinity;
            double[] bestQ = (double[])q0.Clone();
            bool capHit = false;
            int fEvalCap = Math.Max(50, opts.MaxIterations * 50);

            // Throwing out of the inner functions once the cap is hit is more deterministic than
            // returning a zero gradient — Math.NET's line search can otherwise iterate forever
            // with a zero search direction when the tight convergence tolerances aren't satisfied.
            Vector<double> Grad(Vector<double> q)
            {
                int n = q.Count;
                if (outerIter >= opts.MaxIterations)
                {
                    capHit = true;
                    throw new IterationCapException();
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
                iterCallback?.Invoke(outerIter, bestF, ClampToBox(ParamTransforms.Decode(bestQ), lower, upper));
                return g;
            }

            double F(Vector<double> q)
            {
                if (++fEvals > fEvalCap)
                {
                    capHit = true;
                    throw new IterationCapException();
                }
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
                var x = ClampToBox(ParamTransforms.Decode(res.MinimizingPoint.ToArray()), lower, upper);
                bool converged = res.ReasonForExit != ExitCondition.ExceedIterations && !capHit;
                return new OptimizationResult(x, res.FunctionInfoAtMinimum.Value, outerIter, converged, "Bfgs");
            }
            catch (IterationCapException)
            {
                return new OptimizationResult(ClampToBox(ParamTransforms.Decode(bestQ), lower, upper), bestF, outerIter, false, "Bfgs (capped)");
            }
            catch
            {
                return new OptimizationResult(ClampToBox(ParamTransforms.Decode(bestQ), lower, upper), bestF, outerIter, false, "Bfgs");
            }
        }

        private static double[] ClampToBox(double[] x, double[] lower, double[] upper)
        {
            var y = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
                y[i] = Math.Min(Math.Max(x[i], lower[i]), upper[i]);
            return y;
        }

        // Projects the unconstrained iterate back into the user-supplied box before evaluating
        // the underlying objective. Without this wrapper, the log/atanh transform only enforces
        // (0,inf)/(-1,1) — tighter bounds from CalibrationRequest are silently ignored.
        private sealed class BoxClampedObjective : IObjective
        {
            private readonly IObjective _inner;
            public double[] Lower { get; }
            public double[] Upper { get; }

            public BoxClampedObjective(IObjective inner, double[] lower, double[] upper)
            {
                _inner = inner;
                Lower = lower;
                Upper = upper;
            }

            public double Evaluate(double[] x)
            {
                var y = new double[x.Length];
                for (int i = 0; i < x.Length; i++)
                    y[i] = Math.Min(Math.Max(x[i], Lower[i]), Upper[i]);
                return _inner.Evaluate(y);
            }
        }
    }
}
