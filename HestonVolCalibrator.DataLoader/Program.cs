using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.Calibration;
using HestonVolCalibrator.DataLoader;

public static class Program
{
    // ── Market parameters ────────────────────────────────────────────────────────
    // Approximate USD risk-free rate (3m T-bill, adjust as needed).
    const double RiskFreeRate = 0.045;
    // SPX dividend yield (trailing 12m, adjust as needed).
    const double DividendYield = 0.013;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════╗");
        Console.WriteLine("║   SPX Options Loader + Heston Calibration     ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝\n");

        Console.WriteLine($"Risk-free rate : {RiskFreeRate * 100:F2}%");
        Console.WriteLine($"Dividend yield : {DividendYield * 100:F2}%\n");

        // ── Step 1: Fetch SPX options from Yahoo Finance ─────────────────────────────
        Console.WriteLine("Step 1: Fetching SPX options from Yahoo Finance...");
        var loader = new YahooOptionsLoader();

        double spot;
        List<HestonVolCalibrator.DataLoader.OptionQuote> rawQuotes;

        try
        {
            (spot, rawQuotes) = await loader.LoadAsync("^SPX", maxExpiries: 6);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: Could not fetch live data: {ex.Message}");
            Console.WriteLine("Falling back to synthetic SPX-like surface for demonstration.\n");
            (spot, rawQuotes) = BuildSyntheticSurface();
        }

        if (spot <= 0)
        {
            Console.WriteLine("Could not determine SPX spot price. Exiting.");
            return;
        }

        Console.WriteLine($"\nTotal raw quotes fetched: {rawQuotes.Count}");

        // ── Step 2: Build implied vol surface ───────────────────────────────────────
        Console.WriteLine("\nStep 2: Computing implied vols and building surface...");
        var (surface, volPoints) = SurfaceBuilder.Build(rawQuotes, spot, RiskFreeRate, DividendYield);
        Console.WriteLine($"Valid vol points: {volPoints.Count}");

        if (volPoints.Count == 0)
        {
            Console.WriteLine("No valid vol points computed. Cannot calibrate. Exiting.");
            return;
        }

        SurfaceBuilder.Print(volPoints, spot);

        // Export to CSV next to the executable
        string csvPath = Path.Combine(AppContext.BaseDirectory, "spx_vol_surface.csv");
        SurfaceBuilder.ExportCsv(volPoints, csvPath);

        // ── Step 3: Build calibration sample grid ───────────────────────────────────
        Console.WriteLine("\nStep 3: Selecting calibration grid...");
        var samplePoints = new List<(double maturity, double strike)>();
        var maturities = volPoints.Select(p => p.Maturity).Distinct().OrderBy(t => t).ToList();

        foreach (var t in maturities)
        {
            // Pick strikes within 85–115% of spot; take up to 7 per maturity
            var strikes = volPoints
                .Where(p => Math.Abs(p.Maturity - t) < 0.001
                            && p.Strike >= spot * 0.85
                            && p.Strike <= spot * 1.15)
                .Select(p => p.Strike)
                .Distinct()
                .OrderBy(k => k)
                .ToList();

            // Subsample to at most 7 strikes spread across the range
            int step = Math.Max(1, strikes.Count / 7);
            for (int i = 0; i < strikes.Count; i += step)
                samplePoints.Add((t, strikes[i]));
        }

        Console.WriteLine($"Calibration grid: {samplePoints.Count} (maturity, strike) points");

        // ── Step 4: Heston calibration ───────────────────────────────────────────────
        Console.WriteLine("\nStep 4: Running Heston calibration (Nelder-Mead)...");

        var market   = new SurfaceMarketData(surface);
        var calibrator = new HestonCalibrator(market);

        // ATM vol as initial vol estimate
        double atmVol = volPoints
            .Where(p => Math.Abs(p.Strike / spot - 1.0) < 0.05)
            .Select(p => p.ImpliedVol)
            .DefaultIfEmpty(0.18)
            .Average();

        double initVar = atmVol * atmVol;
        var initial = new HestonModelParams(
            kappa: 1.5, theta: initVar, sigma: 0.5, rho: -0.6, v0: initVar);

        Console.WriteLine($"Initial guess: {initial}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var calibrated = calibrator.Calibrate(
            spot, samplePoints, initial,
            rate: RiskFreeRate,
            dividendYield: DividendYield,
            maxIterations: 2000,
            numStarts: 5);
        sw.Stop();

        Console.WriteLine($"Calibrated:    {calibrated}");
        Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F2}s");

        // ── Step 5: Fit quality report ────────────────────────────────────────────────
        Console.WriteLine("\nStep 5: Fit quality report\n");
        Console.WriteLine($"  {"Maturity",8}  {"Strike",9}  {"MktVol%",9}  {"ModelVol%",10}  {"Err(bp)",9}");
        Console.WriteLine("  " + new string('-', 56));

        double rmse = 0; int cnt = 0;
        foreach (var (t, k) in samplePoints)
        {
            double mv = surface.GetVolByStrike(spot, k, t);
            double hv = HestonPricer.ImpliedVol(calibrated, spot, k, t, RiskFreeRate, DividendYield);
            double bps = (hv - mv) * 10000;
            rmse += bps * bps;
            cnt++;
            Console.WriteLine($"  {t,8:F3}  {k,9:F1}  {mv * 100,9:F3}  {hv * 100,10:F3}  {bps,9:F1}");
        }

        if (cnt > 0)
            Console.WriteLine($"\n  RMSE: {Math.Sqrt(rmse / cnt):F1} bp");

        Console.WriteLine("\nDone.");
    }

    // ── Fallback: build a realistic SPX-like synthetic surface ───────────────────
    static (double spot, List<HestonVolCalibrator.DataLoader.OptionQuote> quotes) BuildSyntheticSurface()
    {
        double s = 5300.0;
        // Reference Heston params calibrated to a realistic SPX surface
        var p = new HestonModelParams(kappa: 1.2, theta: 0.04, sigma: 0.35, rho: -0.75, v0: 0.035);
        double rate = 0.045, divYield = 0.013;

        var list = new List<HestonVolCalibrator.DataLoader.OptionQuote>();
        double[] ts = { 0.0833, 0.25, 0.5, 1.0, 1.5, 2.0 };   // ~1m, 3m, 6m, 1y, 18m, 2y
        double[] moneyness = { 0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.15, 1.20 };

        var rng = new Random(42);
        foreach (var t in ts)
            foreach (var m in moneyness)
            {
                double k = Math.Round(s * m / 5.0) * 5.0; // round to nearest $5
                double iv = HestonPricer.ImpliedVol(p, s, k, t, rate, divYield);
                if (iv < 0.01 || double.IsNaN(iv)) continue;

                // Add small bid-ask spread
                double price = HestonPricer.CallPrice(p, s, k, t, rate, divYield);
                double spread = Math.Max(price * 0.01, 0.10);
                double noise = 1.0 + (rng.NextDouble() - 0.5) * 0.002;

                list.Add(new HestonVolCalibrator.DataLoader.OptionQuote(
                    Strike: k, Maturity: t,
                    Bid: price * noise - spread / 2,
                    Ask: price * noise + spread / 2,
                    LastPrice: price * noise,
                    OpenInterest: rng.Next(100, 5000),
                    Volume: rng.Next(10, 500),
                    IsCall: true));
            }

        Console.WriteLine($"  Synthetic surface: {list.Count} quotes, spot={s}");
        return (s, list);
    }
}
