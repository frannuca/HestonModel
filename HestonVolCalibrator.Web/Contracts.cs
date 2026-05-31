using HestonVolCalibrator.Calibration;
using HestonVolCalibrator.SABR;

namespace HestonVolCalibrator.Web;

public record SurfaceRequest(
    string Ticker = "^SPX",
    int MaxExpiries = 6,
    bool ForceSynthetic = false,
    bool Clean = true,
    double? MinMaturity = null,
    double? MaxMaturity = null,
    double? MinMoneyness = null,
    double? MaxMoneyness = null);

public record SurfaceResponse(
    double Spot,
    string Ticker,
    double[] Expiries,
    double[] Strikes,
    double[][] Iv,
    double?[][] CallPrice,
    double?[][] PutPrice,
    string Source,
    double RiskFreeRate,
    double DividendYield,
    CleanStatsDto? CleanStats);

public record CalibrateApiRequest
{
    public string Ticker { get; init; } = "^SPX";
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
    public double? RiskFreeRate { get; init; }
    public double? DividendYield { get; init; }
    public int? Seed { get; init; }
}

public record HestonSurfaceRequest(
    HestonParams Params,
    double Spot,
    double RiskFreeRate,
    double DividendYield,
    double[] Strikes,
    double[] Maturities);

public record HestonSurfaceResponse(double[][] Iv);

// Pure-Heston delta-axis transformation.
// Pipeline per cell:  price = Heston(p, S, K, T, r, q)
//                     iv    = BlackScholesImpliedVol(price, S, K, T, r, q)
//                     delta = BlackScholesCallDelta(S, K, iv, T, r, q)
// We expose iv alongside delta because the same call computes both — the frontend uses iv
// for the Heston smile curve, delta for the x-axis.
public record HestonDeltaResponse(double[][] Iv, double[][] Delta);

public record HestonSurfaceResponseWithGreeks(
    double[][] Iv,
    double?[][]? Delta = null,
    double?[][]? Gamma = null,
    double?[][]? Vega = null,
    double?[][]? Theta = null,
    double?[][]? Rho = null);

public record SaveSnapshotRequest(string Name, Snapshot Snapshot);

// ── SABR API contracts ────────────────────────────────────────────────────────

// Calibrate SABR to a single vol smile slice.
public record SabrCalibrateRequest
{
    public double Forward { get; init; }
    public double Expiry { get; init; }
    public double[] Strikes { get; init; } = [];
    public double[] MarketVols { get; init; } = [];
    public double Beta { get; init; } = 0.5;
    public bool FixBeta { get; init; } = true;
    public double Shift { get; init; } = 0.0;
    public VolConvention Convention { get; init; } = VolConvention.Lognormal;
    public int MaxIterations { get; init; } = 2000;
    public int Restarts { get; init; } = 5;
    public int? Seed { get; init; }
    // When provided, per-strike Greeks are computed and included in the response.
    public double? Annuity { get; init; }
    public bool IsPayer { get; init; } = true;
}

public record SabrParamsDto(double Alpha, double Beta, double Rho, double Nu);

public record SabrGreeksDto(
    double Strike,
    double Delta,
    double Gamma,
    double Vega,
    double Vanna,
    double Volga);

public record SabrCalibrateResponse(
    SabrParamsDto Params,
    double Shift,
    double FinalRmse,
    bool Converged,
    int Iterations,
    double[] ModelVols,
    double[] MarketVols,
    SabrGreeksDto[]? Greeks = null);

// Compute SABR vol at a list of strikes (without calibrating).
public record SabrVolRequest
{
    public SabrParamsDto Params { get; init; } = new(0.02, 0.5, -0.3, 0.4);
    public double Forward { get; init; }
    public double Expiry { get; init; }
    public double[] Strikes { get; init; } = [];
    public double Shift { get; init; } = 0.0;
    public VolConvention Convention { get; init; } = VolConvention.Lognormal;
}

public record SabrVolResponse(double[] Vols, double AtmVol);

// Calibrate SABR independently to each of multiple slices (e.g. a cap vol surface or swaption cube row).
public record SabrSurfaceCalibrateRequest
{
    public SabrSliceInput[] Slices { get; init; } = [];
    public double Beta { get; init; } = 0.5;
    public bool FixBeta { get; init; } = true;
    public double Shift { get; init; } = 0.0;
    public VolConvention Convention { get; init; } = VolConvention.Lognormal;
    public int MaxIterations { get; init; } = 2000;
    public int Restarts { get; init; } = 5;
    public int? Seed { get; init; }
}

public record SabrSliceInput(
    double Forward,
    double Expiry,
    double[] Strikes,
    double[] MarketVols);

public record SabrSliceResult(
    double Forward,
    double Expiry,
    SabrParamsDto Params,
    double Shift,
    double FinalRmse,
    bool Converged,
    int Iterations,
    double[] ModelVols,
    double[] MarketVols);

public record SabrSurfaceCalibrateResponse(SabrSliceResult[] Slices);

// Interpolate a SABR smile to an arbitrary expiry via total-variance interpolation.
// Slices must cover at least two distinct expiries for the same swap tenor.
public record SabrSliceForInterp(
    double Expiry,
    double Forward,
    SabrParamsDto Params,
    double Shift = 0.0);

public record SabrInterpolateSmileRequest
{
    public SabrSliceForInterp[] Slices { get; init; } = [];
    public double TargetExpiry { get; init; }
    public double StrikeMin { get; init; }
    public double StrikeMax { get; init; }
    public int NPoints { get; init; } = 50;
    public VolConvention Convention { get; init; } = VolConvention.Normal;
}

public record SabrInterpolateSmileResponse(
    double TargetForward,
    double[] Strikes,
    double[] Vols);

// Per-strike Greeks computed on a variance-interpolated SABR smile.
public record SabrGreeksRequest
{
    public SabrSliceForInterp[] Slices { get; init; } = [];
    public double TargetExpiry { get; init; }
    public double StrikeMin { get; init; }
    public double StrikeMax { get; init; }
    public int NPoints { get; init; } = 50;
    public VolConvention Convention { get; init; } = VolConvention.Normal;
    public double Annuity { get; init; } = 1.0;
    public bool IsPayer { get; init; } = true;
}

public record SabrGreeksRowDto(
    double Strike,
    double Vol,
    double Price,
    double Delta,
    double Gamma,
    double Vega,
    double Vanna,
    double Volga);

public record SabrGreeksResponse(double TargetForward, SabrGreeksRowDto[] Greeks);

// ── IR product pricing contracts ──────────────────────────────────────────────

public record CapletPriceRequest
{
    public double ForwardRate { get; init; }
    public double Strike { get; init; }
    public double OptionExpiry { get; init; }
    public double PeriodLength { get; init; } = 0.25;
    public double DiscountFactor { get; init; } = 1.0;
    public bool IsCap { get; init; } = true;
    public SabrParamsDto SabrParams { get; init; } = new(0.02, 0.5, -0.3, 0.4);
    public double Shift { get; init; } = 0.0;
    public VolConvention Convention { get; init; } = VolConvention.Lognormal;
}

public record CapletPriceResponse(double Price, double Vol, double Delta, double Vega);

public record SwaptionPriceRequest
{
    public double ForwardSwapRate { get; init; }
    public double Strike { get; init; }
    public double OptionExpiry { get; init; }
    public double Annuity { get; init; }
    public bool IsPayer { get; init; } = true;
    public SabrParamsDto SabrParams { get; init; } = new(0.02, 0.5, -0.3, 0.4);
    public double Shift { get; init; } = 0.0;
    public VolConvention Convention { get; init; } = VolConvention.Lognormal;
}

public record SwaptionPriceResponse(double Price, double Vol, double Delta, double Vega);

public record IrFuturesPriceRequest
{
    public double FuturesRate { get; init; }
    public double StrikeRate { get; init; }
    public double OptionExpiry { get; init; }
    public double PeriodFraction { get; init; } = 0.25;
    public double NotionalPerContract { get; init; } = 1_000_000.0;
    public bool IsCap { get; init; } = true;
    public SabrParamsDto SabrParams { get; init; } = new(0.01, 0.0, -0.2, 0.8);
    public double Shift { get; init; } = 0.0;
    public VolConvention Convention { get; init; } = VolConvention.Normal;
}

public record IrFuturesPriceResponse(double Price, double Vol, double Vega);

// ── Swaption data loader contracts ────────────────────────────────────────────

public record SwaptionSurfaceRequest
{
    public string? AsOf { get; init; } = null;             // ISO date string or null for today
    public double[] OptionExpiries { get; init; } = [0.25, 0.5, 1.0, 2.0, 5.0, 10.0];
    public double[] SwapTenors { get; init; } = [1.0, 2.0, 5.0, 10.0, 30.0];
    public double[] StrikeOffsetsBps { get; init; } = [-200, -100, -50, 0, 50, 100, 200];
    public double? AtmNormalVolOverrideBps { get; init; } = null; // override in bps
    public int CouponFrequency { get; init; } = 2;
    public bool ForceSynthetic { get; init; } = false;     // skip network fetch, use hardcoded curve
}

public record SwaptionVolPointDto(
    double OptionExpiry,
    double SwapTenor,
    double ForwardSwapRate,
    double Annuity,
    double[] Strikes,
    double[] MarketVols,
    string Convention,
    bool IsSynthetic,
    SabrParamsDto? SabrFit);

public record SwaptionSurfaceResponse(
    string AsOf,
    string Source,
    double[] ParTenors,
    double[] ParRates,
    SwaptionVolPointDto[] VolSurface);
