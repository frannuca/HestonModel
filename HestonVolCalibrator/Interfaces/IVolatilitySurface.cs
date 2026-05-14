using System;
using System.Collections.Generic;

namespace HestonVolCalibrator.Interfaces
{
    public interface IVolatilitySurface
    {
        // Get implied volatility given forward/spot, strike and time to maturity (in years)
        double GetVolByStrike(double spot, double strike, double maturity);

        // Get implied volatility given forward/spot, option delta (0..1) and maturity
        double GetVolByDelta(double spot, double delta, double maturity);

        // Optional: query if surface contains data for maturity
        bool HasMaturity(double maturity);

        // Optional: enumerate the discrete grid of expiries (years to maturity) backing the
        // surface. Default empty for implementers that have no concept of a grid.
        IReadOnlyList<double> Expiries => Array.Empty<double>();

        // Optional: enumerate the discrete grid of strikes backing the surface.
        IReadOnlyList<double> Strikes => Array.Empty<double>();
    }
}

