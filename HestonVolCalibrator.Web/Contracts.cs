using HestonVolCalibrator.Calibration;

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
