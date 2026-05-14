using System;

namespace HestonVolCalibrator.Calibration
{
    // Heston parameter transforms used by NelderMead / BFGS (unconstrained):
    //   kappa, theta, sigma, v0 > 0  -->  log()
    //   rho in (-1, 1)               -->  atanh()
    // Parameter order: [kappa, theta, sigma, rho, v0].
    internal static class ParamTransforms
    {
        public static double[] Encode(double[] raw)
        {
            return new[]
            {
                Math.Log(raw[0]),
                Math.Log(raw[1]),
                Math.Log(raw[2]),
                Math.Atanh(Math.Clamp(raw[3], -0.999, 0.999)),
                Math.Log(raw[4])
            };
        }

        public static double[] Decode(double[] q)
        {
            return new[]
            {
                Math.Exp(q[0]),
                Math.Exp(q[1]),
                Math.Exp(q[2]),
                Math.Tanh(q[3]),
                Math.Exp(q[4])
            };
        }
    }

    // Objective wrapper that exposes a raw-space IObjective as an unconstrained function in
    // transformed space. Bounds are not enforced here (the transform implicitly bounds the
    // raw parameters to their natural domain).
    internal sealed class TransformedObjective : IObjective
    {
        private readonly IObjective _raw;

        public TransformedObjective(IObjective raw) { _raw = raw; }

        public double Evaluate(double[] q) => _raw.Evaluate(ParamTransforms.Decode(q));

        // Transformed space is unbounded.
        public double[] Lower { get; } = Enumerable("neg");
        public double[] Upper { get; } = Enumerable("pos");

        private static double[] Enumerable(string sign)
        {
            var v = sign == "neg" ? double.NegativeInfinity : double.PositiveInfinity;
            return new[] { v, v, v, v, v };
        }
    }
}
