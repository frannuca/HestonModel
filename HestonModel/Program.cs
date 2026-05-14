// End-to-end Heston pipeline demo:
//   1) Try Yahoo Finance for SPX option chain; fall back to synthetic SPX-like surface.
//   2) Build implied-vol surface (BS-invert mid prices).
//   3) Calibrate Heston (MathNet Nelder-Mead, vega-weighted, 5 starts).
//   4) Use the calibrated model to re-price options and round-trip prices -> implied vol;
//      compare against the input market surface.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HestonVolCalibrator.Calibration;
using HestonVolCalibrator.DataLoader;
using HestonVolCalibrator.Implementations;

public static class Program
{
    const double RiskFreeRate  = 0.045;   // continuously-compounded
    const double DividendYield = 0.013;

    public static async Task Main(string[] args)
    {
        Header("Heston end-to-end demo: SPX options -> calibration -> pricing");
        Console.WriteLine($"Risk-free rate: {RiskFreeRate * 100:F2}%   Dividend yield: {DividendYield * 100:F2}%");

        // 1) Load the option chain ----------------------------------------------------
        Header("Step 1: Load SPX option chain");

        double spot;
        List<OptionQuote> rawQuotes;
        bool usedFallback = false;

        try
        {
            Console.WriteLine("Attempting live Yahoo Finance fetch...");
            using var loader = new YahooOptionsLoader();
            (spot, rawQuotes) = await loader.LoadSpxAsync(maxExpiries: 6);
            Console.WriteLine($"Live fetch OK. spot={spot}, raw quotes={rawQuotes.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Live fetch failed: {ex.Message}");
            Console.WriteLine("Falling back to synthetic SPX-like surface.");
            (spot, rawQuotes) = BuildSyntheticSurface();
            usedFallback = true;
        }

        if (spot <= 0 || rawQuotes.Count == 0)
        {
            Console.WriteLine("No data available; aborting.");
            return;
        }

        // 2) Build IV surface ---------------------------------------------------------
        Header("Step 2: Build implied volatility surface");
        var (surface, volPoints) = SurfaceBuilder.Build(rawQuotes, spot, RiskFreeRate, DividendYield);
        Console.WriteLine($"Valid vol points: {volPoints.Count}  (source: {(usedFallback ? "synthetic" : "Yahoo")})");

        if (volPoints.Count == 0)
        {
            Console.WriteLine("No usable vol points; aborting.");
            return;
        }

        SurfaceBuilder.Print(volPoints, spot);

        string csvPath = Path.Combine(AppContext.BaseDirectory, "spx_vol_surface.csv");
        SurfaceBuilder.ExportCsv(volPoints, csvPath);
        Console.WriteLine($"Surface exported to: {csvPath}");

        // 3) Pick calibration grid ----------------------------------------------------
        Header("Step 3: Calibration grid");
        var samplePoints = new List<(double maturity, double strike)>();
        var maturities = volPoints.Select(p => p.Maturity).Distinct().OrderBy(t => t).ToList();
        foreach (var t in maturities)
        {
            var strikes = volPoints
                .Where(p => Math.Abs(p.Maturity - t) < 1e-6
                            && p.Strike >= spot * 0.85
                            && p.Strike <= spot * 1.15)
                .Select(p => p.Strike)
                .Distinct()
                .OrderBy(k => k)
                .ToList();
            int step = Math.Max(1, strikes.Count / 7);
            for (int i = 0; i < strikes.Count; i += step)
                samplePoints.Add((t, strikes[i]));
        }
        Console.WriteLine($"Selected {samplePoints.Count} (T, K) pairs across {maturities.Count} expiries.");

        // 4) Calibrate Heston ---------------------------------------------------------
        Header("Step 4: Heston calibration (MathNet Nelder-Mead, vega-weighted, 5 starts)");
        var market     = new SurfaceMarketData(surface);
        var calibrator = new HestonCalibrator(market);

        double atmVol = volPoints
            .Where(p => Math.Abs(p.Strike / spot - 1.0) < 0.05)
            .Select(p => p.ImpliedVol)
            .DefaultIfEmpty(0.18)
            .Average();
        double initVar = atmVol * atmVol;
        var initial = new HestonModelParams(
            kappa: 1.5, theta: initVar, sigma: 0.5, rho: -0.6, v0: initVar);

        Console.WriteLine($"Initial guess : {initial}");
        var sw = Stopwatch.StartNew();
        var calibrated = calibrator.Calibrate(
            spot, samplePoints, initial,
            rate: RiskFreeRate,
            dividendYield: DividendYield,
            maxIterations: 2000,
            numStarts: 5);
        sw.Stop();
        Console.WriteLine($"Calibrated    : {calibrated}");
        Console.WriteLine($"Elapsed       : {sw.Elapsed.TotalSeconds:F2}s");

        // Feller heuristic
        double feller = 2.0 * calibrated.Kappa * calibrated.Theta - calibrated.Sigma * calibrated.Sigma;
        Console.WriteLine($"2*kappa*theta - sigma^2 = {feller:F4}   ({(feller > 0 ? "Feller satisfied" : "Feller violated")})");

        // 5) Calibration fit quality --------------------------------------------------
        Header("Step 5: Calibration fit quality");
        Console.WriteLine($"  {"Maturity",8}  {"Strike",9}  {"Mkt %",8}  {"Model %",9}  {"Err (bp)",9}");
        Console.WriteLine("  " + new string('-', 53));
        double sse = 0; int cnt = 0;
        foreach (var (t, k) in samplePoints)
        {
            double mv = surface.GetVolByStrike(spot, k, t);
            double hv = HestonPricer.ImpliedVol(calibrated, spot, k, t, RiskFreeRate, DividendYield);
            double bp = (hv - mv) * 10000.0;
            sse += bp * bp; cnt++;
            Console.WriteLine($"  {t,8:F3}  {k,9:F1}  {mv * 100,8:F3}  {hv * 100,9:F3}  {bp,9:F1}");
        }
        double rmseBp = cnt > 0 ? Math.Sqrt(sse / cnt) : double.NaN;
        Console.WriteLine($"\n  RMSE = {rmseBp:F2} bp over {cnt} points");

        // 6) Practical use: price options with the calibrated model -------------------
        Header("Step 6: Use the calibrated model to price options");
        // Pick a small (T, K) demo grid covering near-ATM and OTM puts/calls.
        var demoMaturities = maturities
            .OrderBy(t => Math.Abs(t - 0.25))
            .Take(1)
            .Concat(maturities.OrderBy(t => Math.Abs(t - 1.0)).Take(1))
            .Distinct()
            .ToList();
        if (demoMaturities.Count < 2 && maturities.Count >= 2)
            demoMaturities = new List<double> { maturities.First(), maturities.Last() };
        double[] moneyness = { 0.90, 0.95, 1.00, 1.05, 1.10 };

        Console.WriteLine($"  {"T",6}  {"K",9}  {"Call",10}  {"Put",10}  {"P-C parity",11}  " +
                          $"{"Heston IV%",10}  {"Mkt IV%",9}  {"diff bp",8}");
        Console.WriteLine("  " + new string('-', 86));

        foreach (var t in demoMaturities)
        {
            foreach (var m in moneyness)
            {
                double k = Math.Round(spot * m / 5.0) * 5.0;
                double call = HestonPricer.CallPrice(calibrated, spot, k, t, RiskFreeRate, DividendYield);
                double put  = HestonPricer.PutPrice (calibrated, spot, k, t, RiskFreeRate, DividendYield);
                // Parity: C - P  ?= S e^{-qT} - K e^{-rT}
                double parityDiff = (call - put)
                                  - (spot * Math.Exp(-DividendYield * t) - k * Math.Exp(-RiskFreeRate * t));
                // Round-trip: model price -> BS implied vol.
                double hestonIv = HestonPricer.BsImpliedVol(call, spot, k, t, RiskFreeRate, DividendYield);
                double mktIv;
                try { mktIv = surface.GetVolByStrike(spot, k, t); }
                catch { mktIv = double.NaN; }
                double diffBp = double.IsNaN(mktIv) ? double.NaN : (hestonIv - mktIv) * 10000.0;

                Console.WriteLine(
                    $"  {t,6:F3}  {k,9:F1}  {call,10:F3}  {put,10:F3}  {parityDiff,11:E2}  " +
                    $"{hestonIv * 100,10:F3}  {(double.IsNaN(mktIv) ? "  n/a" : (mktIv * 100).ToString("F3")),9}  " +
                    $"{(double.IsNaN(diffBp) ? "  n/a" : diffBp.ToString("F1")),8}");
            }
        }

        Header("Done");
        Console.WriteLine($"Source: {(usedFallback ? "synthetic SPX-like surface" : "Yahoo Finance live data")}");
        Console.WriteLine($"Calibrated Heston params: {calibrated}");
        Console.WriteLine($"Surface fit RMSE: {rmseBp:F2} bp over {cnt} points  (elapsed {sw.Elapsed.TotalSeconds:F2}s)");
    }

    static void Header(string s)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine("  " + s);
        Console.WriteLine(new string('=', 64));
    }

    // ----------------------------------------------------------------------------
    // Synthetic SPX-like surface fallback (mirrors DataLoader/Program.cs).
    static (double spot, List<OptionQuote> quotes) BuildSyntheticSurface()
    {
        double s = 5300.0;
        var p = new HestonModelParams(kappa: 1.2, theta: 0.04, sigma: 0.35, rho: -0.75, v0: 0.035);
        const double rate = 0.045, divYield = 0.013;

        var list = new List<OptionQuote>();
        double[] ts = { 0.0833, 0.25, 0.5, 1.0, 1.5, 2.0 };
        double[] moneyness = { 0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.15, 1.20 };
        var rng = new Random(42);

        foreach (var t in ts)
            foreach (var m in moneyness)
            {
                double k = Math.Round(s * m / 5.0) * 5.0;
                double iv = HestonPricer.ImpliedVol(p, s, k, t, rate, divYield);
                if (iv < 0.01 || double.IsNaN(iv)) continue;
                double price = HestonPricer.CallPrice(p, s, k, t, rate, divYield);
                double spread = Math.Max(price * 0.01, 0.10);
                double noise  = 1.0 + (rng.NextDouble() - 0.5) * 0.002;
                list.Add(new OptionQuote(
                    Strike: k, Maturity: t,
                    Bid: price * noise - spread / 2,
                    Ask: price * noise + spread / 2,
                    LastPrice: price * noise,
                    OpenInterest: rng.Next(100, 5000),
                    Volume: rng.Next(10, 500),
                    IsCall: true));
            }
        return (s, list);
    }
}
