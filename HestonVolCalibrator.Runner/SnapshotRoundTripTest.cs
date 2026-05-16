using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HestonVolCalibrator.Calibration;
using HestonVolCalibrator.Implementations;

namespace HestonVolCalibrator.Runner
{
    // Round-trip test for the Snapshot persistence format. Builds a synthetic surface,
    // runs a quick calibration, serialises the resulting Snapshot to a temp file using the
    // same JSON options the Web layer uses, deserialises back, and asserts equality.
    //
    // Why a Runner-flag test instead of xUnit: the project intentionally has no test-runner
    // dependency, and the Snapshot schema is the only artefact that needs round-trip coverage
    // — a single-file inline test is easier to maintain than a separate xUnit project.
    internal static class SnapshotRoundTripTest
    {
        public static int Run()
        {
            Console.WriteLine("=== Snapshot round-trip test ===\n");

            // ── 1. Build a tiny synthetic surface ──
            double spot = 100.0;
            double rate = 0.045;
            double divYield = 0.013;
            var trueParams = new HestonModelParams(kappa: 1.5, theta: 0.05, sigma: 0.4, rho: -0.6, v0: 0.05);
            var expiries = new[] { 0.0833, 0.25, 0.5, 1.0 };
            var strikes = new[] { 80.0, 90.0, 95.0, 100.0, 105.0, 110.0, 120.0 };

            var surface = new GridVolatilitySurface();
            foreach (var t in expiries)
                foreach (var k in strikes)
                {
                    double iv = HestonPricer.ImpliedVol(trueParams, spot, k, t, rate, divYield);
                    if (!double.IsNaN(iv)) surface.AddPoint(t, k, iv);
                }

            // ── 2. Compose surface + call/put grids (some intentional NaN/null cells) ──
            int Ne = expiries.Length, Nk = strikes.Length;
            var iv2d = new double[Ne][];
            var callPx = new double?[Ne][];
            var putPx = new double?[Ne][];
            for (int i = 0; i < Ne; i++)
            {
                iv2d[i] = new double[Nk];
                callPx[i] = new double?[Nk];
                putPx[i] = new double?[Nk];
                for (int j = 0; j < Nk; j++)
                {
                    double t = expiries[i], k = strikes[j];
                    iv2d[i][j] = surface.HasMaturity(t) ? surface.GetVolByStrike(spot, k, t) : double.NaN;
                    double c = HestonPricer.CallPrice(trueParams, spot, k, t, rate, divYield);
                    double p = c - spot * Math.Exp(-divYield * t) + k * Math.Exp(-rate * t);
                    callPx[i][j] = c > 0 ? (double?)c : null;
                    putPx[i][j] = p > 0 ? (double?)p : null;
                    // Inject one NaN/null to verify the converter survives the round trip.
                    if (i == 0 && j == Nk - 1)
                    {
                        iv2d[i][j] = double.NaN;
                        callPx[i][j] = null;
                        putPx[i][j] = null;
                    }
                }
            }

            // ── 3. Quick calibration (short to keep the test fast) ──
            var calibrator = new HestonCalibrator(new SurfaceMarketData(surface));
            var req = new CalibrationRequest
            {
                Spot = spot,
                RiskFreeRate = rate,
                DividendYield = divYield,
                GlobalMethod = GlobalMethod.NelderMead,
                GradientMethod = GradientMethod.None,
                NelderMeadRestarts = 1,
                GlobalMaxIterations = 30,
                Seed = 42,
            };
            var calResult = calibrator.Calibrate(req, surface);
            Console.WriteLine($"  Calibrated kappa={calResult.HestonParams.Kappa:F3} " +
                              $"theta={calResult.HestonParams.Theta:F4} sigma={calResult.HestonParams.Sigma:F3} " +
                              $"rho={calResult.HestonParams.Rho:F3} v0={calResult.HestonParams.V0:F4} " +
                              $"rmse={calResult.FinalRmse:E3}");

            // ── 4. Build the Snapshot ──
            var snap = new Snapshot(
                Version: "1.0",
                CreatedAtUtc: DateTime.UtcNow,
                Surface: new SurfaceSnapshot(
                    Ticker: "TEST",
                    Source: "synthetic",
                    Spot: spot,
                    RiskFreeRate: rate,
                    DividendYield: divYield,
                    Expiries: expiries,
                    Strikes: strikes,
                    Iv: iv2d,
                    CallPrice: callPx,
                    PutPrice: putPx,
                    CleanStats: new SurfaceCleanStats(100, 92, 3, 2, 1, 2)),
                Calibration: new CalibrationSnapshot(
                    Params: calResult.HestonParams,
                    FinalRmse: calResult.FinalRmse,
                    Converged: calResult.Converged,
                    TotalIterations: calResult.TotalIterations,
                    ElapsedMs: calResult.ElapsedMs,
                    Expiries: calResult.Expiries,
                    Strikes: calResult.Strikes,
                    MarketIv: calResult.MarketIv,
                    HestonIv: calResult.HestonIv,
                    Stages: calResult.Stages,
                    History: calResult.History));

            // ── 5. Serialise → file → deserialise ──
            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new NaNAsNullDouble() }
            };
            var path = Path.Combine(Path.GetTempPath(), $"heston-snapshot-roundtrip-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(snap, opts));
                Console.WriteLine($"  Wrote {new FileInfo(path).Length} bytes to {path}");

                var loaded = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), opts)
                    ?? throw new Exception("Deserialisation returned null.");

                // ── 6. Assertions ──
                var fails = new List<string>();
                void check(bool cond, string msg) { if (!cond) fails.Add(msg); }

                check(loaded.Version == snap.Version, "Version mismatch");
                check(loaded.Surface.Ticker == snap.Surface.Ticker, "Ticker mismatch");
                check(loaded.Surface.Source == snap.Surface.Source, "Source mismatch");
                check(loaded.Surface.Spot == snap.Surface.Spot, "Spot mismatch");
                check(loaded.Surface.Expiries.SequenceEqual(snap.Surface.Expiries), "Expiries mismatch");
                check(loaded.Surface.Strikes.SequenceEqual(snap.Surface.Strikes), "Strikes mismatch");

                check(GridsEqual(loaded.Surface.Iv, snap.Surface.Iv), "Iv grid mismatch (NaN positions or values)");
                check(NullGridsEqual(loaded.Surface.CallPrice, snap.Surface.CallPrice), "CallPrice grid mismatch");
                check(NullGridsEqual(loaded.Surface.PutPrice, snap.Surface.PutPrice), "PutPrice grid mismatch");

                check(loaded.Surface.CleanStats?.Input == 100 && loaded.Surface.CleanStats?.Kept == 92,
                    "CleanStats mismatch");

                check(loaded.Calibration is not null, "Calibration missing");
                if (loaded.Calibration is not null)
                {
                    check(loaded.Calibration.Params == snap.Calibration!.Params, "Heston params mismatch");
                    check(loaded.Calibration.FinalRmse == snap.Calibration.FinalRmse, "FinalRmse mismatch");
                    check(loaded.Calibration.Converged == snap.Calibration.Converged, "Converged mismatch");
                    check(loaded.Calibration.History.Count == snap.Calibration.History.Count, "History length mismatch");
                    check(loaded.Calibration.Stages.Count == snap.Calibration.Stages.Count, "Stages length mismatch");
                    if (loaded.Calibration.Stages.Count == snap.Calibration.Stages.Count &&
                        snap.Calibration.Stages.Count > 0)
                    {
                        check(loaded.Calibration.Stages[0].Method == snap.Calibration.Stages[0].Method,
                            "Stage method mismatch");
                    }
                    check(GridsEqual(loaded.Calibration.MarketIv, snap.Calibration.MarketIv), "MarketIv mismatch");
                    check(GridsEqual(loaded.Calibration.HestonIv, snap.Calibration.HestonIv), "HestonIv mismatch");
                }

                if (fails.Count == 0)
                {
                    Console.WriteLine("\n  PASS: all assertions ok.");
                    return 0;
                }
                Console.WriteLine($"\n  FAIL: {fails.Count} assertion(s):");
                foreach (var f in fails) Console.WriteLine($"    - {f}");
                return 1;
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            }
        }

        // NaN-aware grid equality: NaN at the same position counts as equal.
        private static bool GridsEqual(double[][] a, double[][] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Length != b[i].Length) return false;
                for (int j = 0; j < a[i].Length; j++)
                {
                    bool nanA = double.IsNaN(a[i][j]);
                    bool nanB = double.IsNaN(b[i][j]);
                    if (nanA != nanB) return false;
                    if (!nanA && a[i][j] != b[i][j]) return false;
                }
            }
            return true;
        }

        private static bool NullGridsEqual(double?[][] a, double?[][] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Length != b[i].Length) return false;
                for (int j = 0; j < a[i].Length; j++)
                    if (a[i][j] != b[i][j]) return false;
            }
            return true;
        }

        // Mirror of HestonVolCalibrator.Web.NaNAsNullDoubleConverter so the test JSON encoding
        // matches what the Web layer writes (NaN/±Inf as JSON null, null reads back as NaN).
        private sealed class NaNAsNullDouble : JsonConverter<double>
        {
            public override double Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
                => reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();
            public override void Write(Utf8JsonWriter w, double v, JsonSerializerOptions o)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) w.WriteNullValue();
                else w.WriteNumberValue(v);
            }
        }
    }
}
