using System;

namespace HestonVolCalibrator.Calibration
{
    public record OptimizerOptions(
        int MaxIterations = 2000,
        double Tolerance = 1e-8,
        int? Seed = null);

    public record OptimizationResult(
        double[] X,
        double FinalValue,
        int Iterations,
        bool Converged,
        string Method);

    // Pluggable optimisation back-end.
    // The iteration callback fires after each accepted iteration with (iter, fValue, currentX).
    // currentX is a defensive copy and may be retained by the consumer.
    public interface IOptimizer
    {
        OptimizationResult Minimize(
            IObjective obj,
            double[] x0,
            OptimizerOptions opts,
            Action<int, double, double[]>? iterCallback = null);
    }
}
