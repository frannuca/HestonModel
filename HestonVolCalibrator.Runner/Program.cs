using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.Calibration;

namespace HestonVolCalibrator.Runner
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--snapshot-test")
                return SnapshotRoundTripTest.Run();

            Console.WriteLine("=== Heston Smile Oscillation Diagnostic ===\n");

            double spot = 100.0;
            double rate = 0.04;

            // Synthetic SPX-like surface from "true" params
            var trueParams = new HestonModelParams(kappa: 1.5, theta: 0.06, sigma: 0.4, rho: -0.6, v0: 0.05);

            // SPX-like expiries (1w..2y)
            var expiries = new[] { 0.0192, 0.0833, 0.1667, 0.25, 0.5, 1.0, 2.0 };
            // SPX-like strikes (sparse, around spot)
            var strikes  = new[] { 70.0, 80.0, 90.0, 95.0, 100.0, 105.0, 110.0, 120.0, 130.0 };

            var surface = new GridVolatilitySurface();
            foreach (var t in expiries)
                foreach (var k in strikes)
                {
                    double iv = HestonPricer.ImpliedVol(trueParams, spot, k, t, rate);
                    surface.AddPoint(t, k, iv);
                }

            var market = new SurfaceMarketData(surface);
            var calibrator = new HestonCalibrator(market);

            // Real end-to-end: NM (3 restarts) + BFGS-B chained, default bounds
            var req = new CalibrationRequest
            {
                Spot = spot,
                RiskFreeRate = rate,
                DividendYield = 0.0,
                GlobalMethod = GlobalMethod.NelderMead,
                GradientMethod = GradientMethod.BfgsB,
                Chain = true,
                NelderMeadRestarts = 1,
                GlobalMaxIterations = 200,
                GradientMaxIterations = 50,
                Seed = 42,
            };

            Console.WriteLine($"True params : {trueParams}");
            var swCal = Stopwatch.StartNew();
            var result = calibrator.Calibrate(req, surface);
            swCal.Stop();

            var p = result.HestonParams;
            var cal = new HestonModelParams(p.Kappa, p.Theta, p.Sigma, p.Rho, p.V0);
            Console.WriteLine($"Calibrated  : Kappa={p.Kappa:F4}, Theta={p.Theta:F4}, Sigma={p.Sigma:F4}, Rho={p.Rho:F4}, V0={p.V0:F4}");
            Console.WriteLine($"finalRmse   : {result.FinalRmse:E4}");
            Console.WriteLine($"Calib time  : {swCal.Elapsed.TotalSeconds:F2}s\n");

            // Dense 80-strike smile per expiry using CALIBRATED params
            int Ndense = 80;
            double[] denseK = new double[Ndense];
            for (int i = 0; i < Ndense; i++)
                denseK[i] = 0.6 * spot + (1.4 * spot - 0.6 * spot) * i / (Ndense - 1);

            Console.WriteLine("Per-expiry dense-smile oscillation (calibrated params):");
            Console.WriteLine($"  {"T",8}  {"phiMax",10}  {"h",10}  {"maxD2_center",14}  {"maxSlope_center",16}");
            foreach (var T in expiries)
            {
                double phiMax = Math.Clamp(200.0 / Math.Sqrt(T * Math.Max(p.V0, 1e-4)), 100.0, 500.0);
                double h = phiMax / 1000.0;

                double[] iv = new double[Ndense];
                for (int i = 0; i < Ndense; i++)
                    iv[i] = HestonPricer.ImpliedVol(cal, spot, denseK[i], T, rate);

                double maxD2 = 0.0, maxSlope = 0.0;
                for (int i = 1; i < Ndense - 1; i++)
                {
                    if (denseK[i] < 0.85 * spot || denseK[i] > 1.15 * spot) continue;
                    double d2 = iv[i + 1] - 2.0 * iv[i] + iv[i - 1];
                    if (Math.Abs(d2) > maxD2) maxD2 = Math.Abs(d2);
                    double slope = (iv[i + 1] - iv[i]) / (denseK[i + 1] - denseK[i]);
                    if (Math.Abs(slope) > maxSlope) maxSlope = Math.Abs(slope);
                }
                Console.WriteLine($"  {T,8:F4}  {phiMax,10:F2}  {h,10:F4}  {maxD2,14:E4}  {maxSlope,16:E4}");
            }

            // Print 10 evenly-spaced IV values for shortest two expiries
            Console.WriteLine("\nDense IV samples (10 evenly-spaced strikes) for short expiries:");
            foreach (var T in expiries.Where(t => t <= 0.25))
            {
                Console.Write($"  T={T:F4}: ");
                for (int s = 0; s < 10; s++)
                {
                    int i = s * (Ndense - 1) / 9;
                    double iv = HestonPricer.ImpliedVol(cal, spot, denseK[i], T, rate);
                    Console.Write($"{iv * 100:F3} ");
                }
                Console.WriteLine();
            }

            // Timing: CallPrice ms at T=0.08 and T=2.0
            Console.WriteLine("\nTiming (1000 calls):");
            foreach (var T in new[] { 0.08, 2.0 })
            {
                var sw = Stopwatch.StartNew();
                int reps = 1000;
                double acc = 0.0;
                for (int i = 0; i < reps; i++)
                    acc += HestonPricer.CallPrice(cal, spot, 100.0, T, rate);
                sw.Stop();
                Console.WriteLine($"  T={T:F2}: {sw.Elapsed.TotalMilliseconds / reps:F3} ms/call  (acc={acc:F2})");
            }

            // Spot-check with larger params (slower-decaying integrand)
            Console.WriteLine("\nLarger-param spot-check (sigma=1.2, rho=-0.95, kappa=5, theta=0.06, v0=0.05):");
            var big = new HestonModelParams(kappa: 5.0, theta: 0.06, sigma: 1.2, rho: -0.95, v0: 0.05);
            foreach (var T in new[] { 0.0833, 0.25, 1.0 })
            {
                double[] iv = new double[Ndense];
                for (int i = 0; i < Ndense; i++)
                    iv[i] = HestonPricer.ImpliedVol(big, spot, denseK[i], T, rate);
                double maxD2 = 0.0;
                for (int i = 1; i < Ndense - 1; i++)
                {
                    if (denseK[i] < 0.85 * spot || denseK[i] > 1.15 * spot) continue;
                    double d2 = iv[i + 1] - 2.0 * iv[i] + iv[i - 1];
                    if (Math.Abs(d2) > maxD2) maxD2 = Math.Abs(d2);
                }
                Console.WriteLine($"  T={T:F4}: maxD2_center={maxD2:E4}");
            }

            Console.WriteLine("\nDone.");
            return 0;
        }
    }
}
