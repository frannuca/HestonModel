namespace HestonVolCalibrator.SABR
{
    // Hagan, Kumar, Lesniewski, Woodward (2002) SABR model parameters.
    // Alpha > 0: instantaneous vol level
    // Beta in [0,1]: CEV exponent — typically fixed at 0.5 for rates, 0 = normal backbone, 1 = lognormal
    // Rho in (-1,1): spot-vol instantaneous correlation — drives skew direction
    // Nu > 0: vol-of-vol — drives smile curvature
    public record SabrParams(double Alpha, double Beta, double Rho, double Nu)
    {
        public override string ToString() =>
            $"α={Alpha:F6}, β={Beta:F4}, ρ={Rho:F4}, ν={Nu:F4}";
    }

    // Result of calibrating SABR to a single vol smile slice.
    public record SabrCalibrationResult(
        SabrParams Params,
        double Shift,       // displacement applied to forward + strikes (0 = unshifted)
        double FinalRmse,
        bool Converged,
        int Iterations,
        double[] ModelVols, // SABR-implied vols at calibration strikes
        double[] MarketVols);

    // Vol backbone convention used for quoting and pricing.
    public enum VolConvention
    {
        Lognormal, // Black76 lognormal vol — requires F > 0 and K > 0 (or use Shifted)
        Normal,    // Bachelier / normal vol — valid for negative or near-zero rates
        Shifted    // Shifted lognormal SABR — apply displacement before standard formula
    }
}
