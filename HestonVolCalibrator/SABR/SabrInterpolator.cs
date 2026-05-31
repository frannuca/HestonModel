using System;
using System.Linq;

namespace HestonVolCalibrator.SABR
{
    // Interpolates a SABR smile to an arbitrary expiry using total-variance interpolation.
    //
    // For each strike K and each calibrated slice i:
    //   V_i(K) = σ_SABR(K; params_i, T_i)² × T_i   (total variance)
    //
    // V is then linearly interpolated (or flat-extrapolated) in T, and the
    // vol at the target expiry is recovered as σ = √(V(T*) / T*).
    //
    // The forward at the target expiry is also linearly interpolated from the
    // bounding slices.
    public static class SabrInterpolator
    {
        public record Slice(
            double Expiry,
            double Forward,
            SabrParams Params,
            double Shift = 0.0);

        public record SmileResult(
            double TargetForward,
            double[] Strikes,
            double[] Vols);

        // Build a smile at targetExpiry over the supplied strike grid.
        public static SmileResult InterpolateSmile(
            Slice[] slices,
            double targetExpiry,
            double strikeMin,
            double strikeMax,
            int nPoints,
            VolConvention convention)
        {
            if (slices is not { Length: >= 2 })
                throw new ArgumentException("At least two calibrated slices are required.");
            if (nPoints < 2)
                throw new ArgumentOutOfRangeException(nameof(nPoints));
            if (strikeMax <= strikeMin)
                throw new ArgumentException("strikeMax must exceed strikeMin.");

            var ordered = slices.OrderBy(s => s.Expiry).ToArray();
            double t = targetExpiry;

            // Locate bounding indices.
            int lo = 0;
            for (int i = 0; i < ordered.Length - 1; i++)
            {
                if (ordered[i].Expiry <= t) lo = i;
                else break;
            }
            int hi = Math.Min(lo + 1, ordered.Length - 1);

            double tLo = ordered[lo].Expiry, tHi = ordered[hi].Expiry;
            double w = tHi > tLo
                ? Math.Clamp((t - tLo) / (tHi - tLo), 0.0, 1.0)
                : 0.0;

            double targetForward = ordered[lo].Forward * (1.0 - w) + ordered[hi].Forward * w;

            // Build strike grid.
            double[] strikes = new double[nPoints];
            for (int i = 0; i < nPoints; i++)
                strikes[i] = strikeMin + (strikeMax - strikeMin) * i / (nPoints - 1);

            double[] vols = new double[nPoints];
            for (int i = 0; i < nPoints; i++)
            {
                double k = strikes[i];

                double VolAt(Slice s)
                {
                    double f = s.Forward + s.Shift;
                    double ks = k + s.Shift;
                    double v = convention == VolConvention.Normal
                        ? SabrPricer.NormalImpliedVol(s.Params, f, ks, s.Expiry)
                        : SabrPricer.ImpliedVol(s.Params, f, ks, s.Expiry);
                    return double.IsNaN(v) || v <= 0 ? 0.0 : v;
                }

                double vLo = VolAt(ordered[lo]);
                double vHi = VolAt(ordered[hi]);

                double varT = vLo * vLo * tLo * (1.0 - w) + vHi * vHi * tHi * w;
                vols[i] = varT > 0 ? Math.Sqrt(varT / t) : 0.0;
            }

            return new SmileResult(targetForward, strikes, vols);
        }
    }
}
