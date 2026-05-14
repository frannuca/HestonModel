using System;

namespace HestonVolCalibrator.Calibration
{
    // Generic objective function for optimisation.
    // Parameter order across the codebase: [kappa, theta, sigma, rho, v0].
    public interface IObjective
    {
        // Evaluate the objective at point x. Implementations must be deterministic.
        double Evaluate(double[] x);

        // Box bounds in the same parameter order as Evaluate.
        // Lower[i] <= x[i] <= Upper[i].  May be (-inf, +inf) for unconstrained dims.
        double[] Lower { get; }
        double[] Upper { get; }
    }
}
