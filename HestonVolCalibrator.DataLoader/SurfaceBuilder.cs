using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HestonVolCalibrator.Implementations;

namespace HestonVolCalibrator.DataLoader
{
    public record VolPoint(double Maturity, double Strike, double ImpliedVol, bool IsCall);

    public static class SurfaceBuilder
    {
        // Build a GridVolatilitySurface from raw option quotes.
        // Returns (surface, valid vol points) for downstream use.
        public static (GridVolatilitySurface surface, List<VolPoint> points) Build(
            IEnumerable<OptionQuote> quotes,
            double spot,
            double rate,
            double dividendYield)
        {
            var surface = new GridVolatilitySurface();
            var points  = new List<VolPoint>();

            // Group by (maturity, strike); prefer calls near ATM, puts for OTM downside
            var byExpiry = quotes
                .GroupBy(q => Math.Round(q.Maturity, 4))
                .OrderBy(g => g.Key);

            foreach (var expGroup in byExpiry)
            {
                double t = expGroup.Key;

                // For each strike keep the more liquid leg (higher OI)
                var byStrike = expGroup
                    .GroupBy(q => q.Strike)
                    .Select(g => g.OrderByDescending(q => q.OpenInterest + q.Volume).First());

                foreach (var q in byStrike)
                {
                    double iv = ImpliedVolSolver.Solve(q, spot, rate, dividendYield);
                    if (double.IsNaN(iv)) continue;

                    surface.AddPoint(t, q.Strike, iv);
                    points.Add(new VolPoint(t, q.Strike, iv, q.IsCall));
                }
            }

            return (surface, points);
        }

        // Print a formatted vol surface table to the console.
        public static void Print(List<VolPoint> points, double spot)
        {
            var maturities = points.Select(p => p.Maturity).Distinct().OrderBy(t => t).ToList();
            var strikes = points.Select(p => p.Strike)
                                .Where(k => k >= spot * 0.80 && k <= spot * 1.20)
                                .Distinct().OrderBy(k => k).ToList();

            Console.WriteLine($"\n  Implied Vol Surface (spot={spot:F0}, % vols)");
            Console.Write($"  {"Strike",9}");
            foreach (var t in maturities) Console.Write($"  T={t:F3}y");
            Console.WriteLine();
            Console.WriteLine("  " + new string('-', 10 + maturities.Count * 10));

            foreach (var k in strikes)
            {
                Console.Write($"  {k,9:F1}");
                foreach (var t in maturities)
                {
                    var pt = points.FirstOrDefault(p => p.Strike == k
                        && Math.Abs(p.Maturity - t) < 0.001);
                    if (pt is null) Console.Write($"  {"--",7}  ");
                    else Console.Write($"  {pt.ImpliedVol * 100,7:F2}% ");
                }
                Console.WriteLine();
            }
        }

        // Export vol points to CSV for external analysis.
        public static void ExportCsv(List<VolPoint> points, string path)
        {
            using var w = new StreamWriter(path);
            w.WriteLine("Maturity,Strike,ImpliedVol,Type");
            foreach (var p in points.OrderBy(x => x.Maturity).ThenBy(x => x.Strike))
                w.WriteLine($"{p.Maturity:F6},{p.Strike:F4},{p.ImpliedVol:F6},{(p.IsCall ? "C" : "P")}");
            Console.WriteLine($"  Exported {points.Count} vol points to: {path}");
        }
    }
}
