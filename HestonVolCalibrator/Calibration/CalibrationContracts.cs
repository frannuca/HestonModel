using System;
using System.Collections.Generic;

namespace HestonVolCalibrator.Calibration
{
    public enum GlobalMethod { None, NelderMead, Genetic }
    public enum GradientMethod { None, Bfgs, BfgsB }

    public record ConvergencePoint(int Iter, double Rmse, string Stage);

    public record ParamBounds(double Lower, double Upper);

    public record HestonParams(double Kappa, double Theta, double Sigma, double Rho, double V0);

    public record CalibrationRequest
    {
        public GlobalMethod GlobalMethod { get; init; } = GlobalMethod.NelderMead;
        public GradientMethod GradientMethod { get; init; } = GradientMethod.None;
        public bool Chain { get; init; } = true;
        public int NelderMeadRestarts { get; init; } = 5;
        public int GlobalMaxIterations { get; init; } = 2000;
        public int GradientMaxIterations { get; init; } = 500;

        public ParamBounds Kappa { get; init; } = new(0.01, 20.0);
        public ParamBounds Theta { get; init; } = new(1e-4, 1.0);
        public ParamBounds Sigma { get; init; } = new(0.01, 5.0);
        public ParamBounds Rho   { get; init; } = new(-0.99, 0.99);
        public ParamBounds V0    { get; init; } = new(1e-4, 1.0);

        public double[]? InitialGuess { get; init; }

        public double Spot { get; init; }
        public double RiskFreeRate { get; init; } = 0.04;
        public double DividendYield { get; init; } = 0.0;

        public int? Seed { get; init; }
    }

    public record CalibrationResult
    {
        public HestonParams HestonParams { get; init; } = new(0, 0, 0, 0, 0);
        public double FinalRmse { get; init; }
        public List<ConvergencePoint> History { get; init; } = new();
        public double[][] MarketIv { get; init; } = Array.Empty<double[]>();
        public double[][] HestonIv { get; init; } = Array.Empty<double[]>();
        public double[] Expiries { get; init; } = Array.Empty<double>();
        public double[] Strikes { get; init; } = Array.Empty<double>();
        public int TotalIterations { get; init; }
        public double ElapsedMs { get; init; }
    }
}
