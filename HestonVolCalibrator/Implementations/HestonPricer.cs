using System;
using System.Numerics;

namespace HestonVolCalibrator.Implementations
{
    public static class HestonPricer
    {
        // ... rest of the file content ...
        
        private static double ComputeProb(
            HestonModelParams p,
            double spot,
            double strike,
            double maturity,
            double rate,
            double dividendYield,
            int probabilityIndex)
        {
            // ... rest of the method content ...
            
            for  (int k = 0; k <= n; k++)
            {
                double phi = PhiMin + k * h;
                Complex cf = CharFunc(p, spot, maturity, rate, dividendYield, probabilityIndex, phi);
                
                // ... rest of the loop content ...
            
           <｜begin▁of▁sentence｜>}

            double probability = 0.5 + (h / 3.0) * sum / Math.PI;

            // Small numerical violations of [0,1] can cause severe IV spikes.
            return Clamp(probability, 0.0, 1.0);
         }
        
        // ... rest of the file content ...
    }
}
