using System;
using System.Numerics;

namespace HestonVolCalibrator.Implementations
{
    public static class HestonPricer
    {
        // ... rest of the file content ...
        
        private static double ChooseIntegrationUpper(HestonModelParams p, double maturity)
        {
            // As T  ->  0 , the payoff transform is harder to integrate and the tail decays more slowly.
            // Use total std-dev as the scale. The constants are intentionally conservative for calibration.
            double representativeVariance = Math.Max(Math.Max(p.V0, p.Theta), MinVariance);
            double totalStdDev = Math.Sqrt(Math.Max(representativeVariance * maturity, 1e-12));
            double upper = 45.0 / totalStdDev;
            return Clamp(upper, MinIntegrationUpper, MaxIntegrationUpper);
        }
        
        // ... rest of the file content ...
    }
}
