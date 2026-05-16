using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using HestonVolCalibrator.DataLoader;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.Web;

public sealed class CachedSurface
{
    public required double Spot { get; init; }
    public required string Ticker { get; init; }
    public required IVolatilitySurface Surface { get; init; }
    public required string Source { get; init; }
    public required double[] Expiries { get; init; }
    public required double[] Strikes { get; init; }
    public required double[][] Iv { get; init; }
    public required double?[][] CallPrice { get; init; }
    public required double?[][] PutPrice { get; init; }
    public required double RiskFreeRate { get; init; }
    public required double DividendYield { get; init; }
    public CleanStatsDto? CleanStats { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CleanStatsDto(
    int Input,
    int Kept,
    int DroppedBidAsk,
    int DroppedBounds,
    int DroppedConvexity,
    int DroppedIvOutlier);

public sealed class SurfaceService
{
    // Public so the rest of the API (calibration, heston-surface, frontend payloads) can align
    // its rate/dividend assumptions with the values used to build the IV surface.
    public const double RiskFreeRate = 0.045;
    public const double DividendYield = 0.013;

    private readonly ConcurrentDictionary<string, CachedSurface> _cache = new();

    public async Task<CachedSurface> FetchAsync(
        string ticker,
        int maxExpiries,
        bool forceSynthetic = false,
        bool clean = true,
        QuoteCleaner.CleanOptions? cleanOptions = null,
        double? minMaturity = null,
        double? maxMaturity = null,
        double? minMoneyness = null,
        double? maxMoneyness = null)
    {
        double tLo = minMaturity ?? 0.0;
        double tHi = maxMaturity ?? double.PositiveInfinity;
        double mLo = minMoneyness ?? 0.0;
        double mHi = maxMoneyness ?? double.PositiveInfinity;

        double spot;
        System.Collections.Generic.List<OptionQuote> quotes;
        string source;

        if (forceSynthetic)
        {
            (spot, quotes) = SyntheticSurface.BuildSpx();
            source = "synthetic";
        }
        else
        {
            try
            {
                using var loader = new YahooOptionsLoader();
                (spot, quotes) = await loader.LoadSpxAsync(maxExpiries, tLo, tHi);
                if (spot <= 0 || quotes.Count == 0)
                    throw new Exception("Empty Yahoo response.");
                source = "yahoo";
            }
            catch
            {
                (spot, quotes) = SyntheticSurface.BuildSpx();
                source = "synthetic";
            }
        }

        // Apply maturity & moneyness windows uniformly (covers synthetic path and double-guards Yahoo).
        if (spot > 0 && (tLo > 0 || !double.IsPositiveInfinity(tHi) || mLo > 0 || !double.IsPositiveInfinity(mHi)))
        {
            quotes = quotes.Where(q =>
            {
                if (q.Maturity < tLo || q.Maturity > tHi) return false;
                double m = q.Strike / spot;
                return m >= mLo && m <= mHi;
            }).ToList();
        }

        CleanStatsDto? cleanDto = null;
        if (clean)
        {
            var (kept, stats) = QuoteCleaner.Clean(
                quotes, spot, RiskFreeRate, DividendYield,
                cleanOptions ?? new QuoteCleaner.CleanOptions());
            quotes = kept;
            cleanDto = new CleanStatsDto(
                stats.Input, stats.Kept,
                stats.DroppedBidAsk, stats.DroppedBounds,
                stats.DroppedConvexity, stats.DroppedIvOutlier);
        }

        var (surface, _) = SurfaceBuilder.Build(quotes, spot, RiskFreeRate, DividendYield);

        var expiries = surface.Expiries.OrderBy(t => t).ToArray();
        var strikes = surface.Strikes.OrderBy(k => k).ToArray();
        var iv = new double[expiries.Length][];
        for (int i = 0; i < expiries.Length; i++)
        {
            iv[i] = new double[strikes.Length];
            for (int j = 0; j < strikes.Length; j++)
            {
                try { iv[i][j] = surface.GetVolByStrike(spot, strikes[j], expiries[i]); }
                catch { iv[i][j] = double.NaN; }
            }
        }

        var (callPrice, putPrice) = BuildPriceGrids(quotes, expiries, strikes);

        var cached = new CachedSurface
        {
            Spot = spot,
            Ticker = ticker,
            Surface = surface,
            Source = source,
            Expiries = expiries,
            Strikes = strikes,
            Iv = iv,
            CallPrice = callPrice,
            PutPrice = putPrice,
            RiskFreeRate = RiskFreeRate,
            DividendYield = DividendYield,
            CleanStats = cleanDto
        };
        _cache[ticker] = cached;
        return cached;
    }

    private static (double?[][] callGrid, double?[][] putGrid) BuildPriceGrids(
        System.Collections.Generic.List<OptionQuote> quotes,
        double[] expiries,
        double[] strikes)
    {
        var callGrid = new double?[expiries.Length][];
        var putGrid = new double?[expiries.Length][];
        for (int i = 0; i < expiries.Length; i++)
        {
            callGrid[i] = new double?[strikes.Length];
            putGrid[i] = new double?[strikes.Length];
        }

        foreach (var q in quotes)
        {
            int ti = NearestIndex(expiries, q.Maturity, 1/365.0);
            int ki = NearestIndex(strikes, q.Strike, 1/365.0);
            if (ti < 0 || ki < 0) continue;

            double mid = 0.5 * (q.Bid + q.Ask);
            if (!double.IsFinite(mid) || mid <= 0) continue;

            if (q.IsCall) callGrid[ti][ki] = mid;
            else putGrid[ti][ki] = mid;
        }
        return (callGrid, putGrid);
    }

    private static int NearestIndex(double[] arr, double target, double tol)
    {
        int best = -1;
        double bestDiff = double.PositiveInfinity;
        for (int i = 0; i < arr.Length; i++)
        {
            double d = Math.Abs(arr[i] - target);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        if (best < 0) return -1;
        // Tolerance: strikes are absolute, maturities ~ years. Use relative tol for safety.
        double scale = Math.Max(1.0, Math.Abs(target));
        return bestDiff <= tol * scale ? best : -1;
    }

    public bool TryGet(string ticker, out CachedSurface surface) =>
        _cache.TryGetValue(ticker, out surface!);

    public async Task<CachedSurface> GetOrFetchAsync(string ticker, int maxExpiries)
    {
        if (_cache.TryGetValue(ticker, out var cached)) return cached;
        return await FetchAsync(ticker, maxExpiries);
    }
}
