using System;
using System.Collections.Generic;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.Calibration
{
    // Vega-weighted RMSE of implied volatilities (model vs market) over a (maturity, strike) grid.
    // Parameter order: [kappa, theta, sigma, rho, v0].
    public sealed class VegaWeightedObjective : IObjective
    {
        private readonly IVolatilitySurface _surface;
        private readonly double _spot;
        private readonly double _rate;
        private readonly double _q;
        private readonly IReadOnlyList<(double maturity, double strike)> _pts;

        public double[] Lower { get; }
        public double[] Upper { get; }

        public VegaWeightedObjective(
            IVolatilitySurface surface,
            double spot,
            double rate,
            double dividendYield,
            IReadOnlyList<(double maturity, double strike)> pts,
            double[] lower,
            double[] upper)
        {
            _surface = surface;
            _spot = spot;
            _rate = rate;
            _q = dividendYield;
            _pts = pts;
            Lower = lower;
            Upper = upper;
        }

        public double Evaluate(double[] x)
        {
            // x = [kappa, theta, sigma, rho, v0]
            var p = new HestonModelParams(x[0], x[1], x[2], x[3], x[4]);

            double sum = 0.0, wSum = 0.0;
            foreach (var (m, k) in _pts)
            {
                double marketVol;
                try { marketVol = _surface.GetVolByStrike(_spot, k, m); }
                catch { continue; }

                double modelVol;
                try { modelVol = HestonPricer.ImpliedVol(p, _spot, k, m, _rate, _q); }
                catch { return double.MaxValue; }
                if (double.IsNaN(modelVol) || double.IsInfinity(modelVol)) return double.MaxValue;

                double sqrtT = Math.Sqrt(m);
                double d1 = (Math.Log(_spot / k) + (_rate + 0.5 * marketVol * marketVol) * m)
                            / (marketVol * sqrtT);
                double w = BlackScholes.NormalPdf(d1) * sqrtT;
                w = Math.Max(w, 1e-6);

                double diff = modelVol - marketVol;
                sum  += w * diff * diff;
                wSum += w;
            }
            return wSum < 1e-12 ? double.MaxValue : Math.Sqrt(sum / wSum);
        }
    }
}
