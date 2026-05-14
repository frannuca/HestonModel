using System;
using System.Collections.Generic;
using System.Linq;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.DataLoader
{
    // Filters noisy / arbitrage-violating option quotes prior to surface building.
    // Side-effect free; returns kept quotes (input order preserved) and per-reason drop counts.
    public static class QuoteCleaner
    {
        public sealed record CleanOptions(
            double MaxRelSpread = 0.30,   // (ask - bid) / mid
            double MinBid       = 0.05,
            double MadKSigma    = 4.0,
            bool   EnforceConvexity = true,
            bool   EnforceBounds    = true);

        public sealed record CleanStats(
            int Input,
            int Kept,
            int DroppedBidAsk,
            int DroppedBounds,
            int DroppedConvexity,
            int DroppedIvOutlier);

        // Curve-aware overload: discount and dividend curves are queried per-quote at q.Maturity.
        public static (List<OptionQuote> kept, CleanStats stats) Clean(
            List<OptionQuote> quotes,
            double spot,
            IDiscountCurve discount,
            IYieldCurve dividend,
            CleanOptions opts)
        {
            opts ??= new CleanOptions();
            return CleanInternal(quotes, spot, discount, dividend, opts);
        }

        public static (List<OptionQuote> kept, CleanStats stats) Clean(
            List<OptionQuote> quotes,
            double spot,
            double rate,
            double divYield,
            CleanOptions opts)
        {
            opts ??= new CleanOptions();
            return CleanInternal(quotes, spot, new FlatDiscountCurve(rate), new FlatYieldCurve(divYield), opts);
        }

        private static (List<OptionQuote> kept, CleanStats stats) CleanInternal(
            List<OptionQuote> quotes,
            double spot,
            IDiscountCurve disc,
            IYieldCurve div,
            CleanOptions opts)
        {
            int input = quotes?.Count ?? 0;
            int droppedBidAsk = 0, droppedBounds = 0, droppedConvexity = 0, droppedIvOutlier = 0;

            if (quotes is null || input == 0)
                return (new List<OptionQuote>(), new CleanStats(0, 0, 0, 0, 0, 0));

            // Track surviving via parallel boolean array keyed to input index, to preserve order.
            var alive = new bool[input];
            for (int i = 0; i < input; i++) alive[i] = true;

            // --- Stage 1: bid/ask sanity ---
            for (int i = 0; i < input; i++)
            {
                var q = quotes[i];
                double mid = 0.5 * (q.Bid + q.Ask);
                bool bad =
                    !double.IsFinite(q.Bid) || !double.IsFinite(q.Ask) ||
                    q.Bid <= 0 || q.Ask <= 0 || q.Ask < q.Bid ||
                    q.Bid < opts.MinBid ||
                    mid <= 0 ||
                    (q.Ask - q.Bid) / mid > opts.MaxRelSpread;
                if (bad) { alive[i] = false; droppedBidAsk++; }
            }

            // --- Stage 2: intrinsic / upper-bound checks on mid price ---
            if (opts.EnforceBounds)
            {
                const double eps = 0.01;
                for (int i = 0; i < input; i++)
                {
                    if (!alive[i]) continue;
                    var q = quotes[i];
                    double t = Math.Max(q.Maturity, 0.0);
                    double df = disc.Df(t);
                    double qf = div.Df(t);
                    double mid = 0.5 * (q.Bid + q.Ask);
                    double lo, hi;
                    if (q.IsCall)
                    {
                        lo = Math.Max(spot * qf - q.Strike * df, 0.0);
                        hi = spot * qf;
                    }
                    else
                    {
                        lo = Math.Max(q.Strike * df - spot * qf, 0.0);
                        hi = q.Strike * df;
                    }
                    if (!double.IsFinite(mid) || mid < lo - eps || mid > hi + eps)
                    {
                        alive[i] = false;
                        droppedBounds++;
                    }
                }
            }

            // --- Stage 3: convexity in strike (per side, per maturity) ---
            if (opts.EnforceConvexity)
            {
                // Group surviving indices by (IsCall, rounded maturity), sorted by strike.
                var groups = Enumerable.Range(0, input)
                    .Where(i => alive[i])
                    .GroupBy(i => (quotes[i].IsCall, Math.Round(quotes[i].Maturity, 6)))
                    .Select(g => g.OrderBy(i => quotes[i].Strike).ToList())
                    .ToList();

                foreach (var grp in groups)
                {
                    if (grp.Count < 3) continue;
                    for (int sweep = 0; sweep < 5; sweep++)
                    {
                        var alive2 = grp.Where(i => alive[i]).ToList();
                        if (alive2.Count < 3) break;
                        bool removedAny = false;
                        for (int j = 1; j < alive2.Count - 1; j++)
                        {
                            int im = alive2[j - 1], ic = alive2[j], ip = alive2[j + 1];
                            double cm = 0.5 * (quotes[im].Bid + quotes[im].Ask);
                            double cc = 0.5 * (quotes[ic].Bid + quotes[ic].Ask);
                            double cp = 0.5 * (quotes[ip].Bid + quotes[ip].Ask);
                            double mx = Math.Max(cm, Math.Max(cc, cp));
                            double tol = 0.05 * Math.Max(mx, 1e-9);
                            double secondDiff = cp - 2 * cc + cm;
                            if (secondDiff < -tol)
                            {
                                alive[ic] = false;
                                droppedConvexity++;
                                removedAny = true;
                                j++; // skip neighbor on this pass to avoid cascade artifacts
                            }
                        }
                        if (!removedAny) break;
                    }
                }
            }

            // --- Stage 4: IV smile outlier rejection (per maturity, both sides pooled) ---
            // Group surviving indices by rounded maturity.
            var matGroups = Enumerable.Range(0, input)
                .Where(i => alive[i])
                .GroupBy(i => Math.Round(quotes[i].Maturity, 6))
                .ToList();

            foreach (var grp in matGroups)
            {
                double t = Math.Max(grp.Key, 1e-8);
                double dft = disc.Df(t);
                double qft = div.Df(t);
                double fwd = (dft > 0) ? spot * qft / dft : spot;

                // Compute IV for each surviving quote; collect (idx, x=ln(K/F), iv).
                var pts = new List<(int idx, double x, double iv)>();
                foreach (int i in grp)
                {
                    double iv = SafeSolveIv(quotes[i], spot, disc.ZeroRate(quotes[i].Maturity), div.ZeroRate(quotes[i].Maturity));
                    if (!double.IsFinite(iv) || iv <= 0)
                    {
                        alive[i] = false;
                        droppedIvOutlier++;
                        continue;
                    }
                    double x = Math.Log(Math.Max(quotes[i].Strike, 1e-12) / Math.Max(fwd, 1e-12));
                    pts.Add((i, x, iv));
                }

                if (pts.Count < 5) continue;

                // OLS quadratic: iv = a + b*x + c*x^2
                if (!TryFitQuadratic(pts, out double a, out double b, out double c)) continue;

                var residuals = pts.Select(p => p.iv - (a + b * p.x + c * p.x * p.x)).ToArray();
                double medR = Median(residuals);
                var absDev = residuals.Select(r => Math.Abs(r - medR)).ToArray();
                double mad = 1.4826 * Median(absDev);
                if (!(mad > 0) || !double.IsFinite(mad)) continue;

                double thresh = opts.MadKSigma * mad;
                for (int k = 0; k < pts.Count; k++)
                {
                    if (Math.Abs(residuals[k] - medR) > thresh)
                    {
                        alive[pts[k].idx] = false;
                        droppedIvOutlier++;
                    }
                }
            }

            var kept = new List<OptionQuote>(input);
            for (int i = 0; i < input; i++) if (alive[i]) kept.Add(quotes[i]);

            return (kept, new CleanStats(
                Input: input,
                Kept: kept.Count,
                DroppedBidAsk: droppedBidAsk,
                DroppedBounds: droppedBounds,
                DroppedConvexity: droppedConvexity,
                DroppedIvOutlier: droppedIvOutlier));
        }

        private static double SafeSolveIv(OptionQuote q, double spot, double rate, double divYield)
        {
            try { return ImpliedVolSolver.Solve(q, spot, rate, divYield); }
            catch { return double.NaN; }
        }

        private static double Median(IEnumerable<double> values)
        {
            var arr = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
            int n = arr.Length;
            if (n == 0) return double.NaN;
            return (n % 2 == 1) ? arr[n / 2] : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
        }

        // Solve 3x3 normal equations for y = a + b*x + c*x^2 by OLS.
        private static bool TryFitQuadratic(List<(int idx, double x, double iv)> pts,
            out double a, out double b, out double c)
        {
            a = b = c = 0.0;
            int n = pts.Count;
            if (n < 3) return false;

            double s0 = n, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
            double ty = 0, tyx = 0, tyx2 = 0;
            foreach (var p in pts)
            {
                double x = p.x, y = p.iv;
                double x2 = x * x;
                s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
                ty += y; tyx += y * x; tyx2 += y * x2;
            }

            // Solve [[s0,s1,s2],[s1,s2,s3],[s2,s3,s4]] * [a,b,c] = [ty,tyx,tyx2]
            double[,] M = { { s0, s1, s2 }, { s1, s2, s3 }, { s2, s3, s4 } };
            double[] r = { ty, tyx, tyx2 };
            if (!Solve3x3(M, r, out var sol)) return false;
            a = sol[0]; b = sol[1]; c = sol[2];
            return double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c);
        }

        private static bool Solve3x3(double[,] M, double[] r, out double[] x)
        {
            x = new double[3];
            // Cramer's rule with determinant guard.
            double det = Det3(M);
            if (Math.Abs(det) < 1e-18) return false;
            for (int col = 0; col < 3; col++)
            {
                var Mc = (double[,])M.Clone();
                Mc[0, col] = r[0]; Mc[1, col] = r[1]; Mc[2, col] = r[2];
                x[col] = Det3(Mc) / det;
            }
            return true;
        }

        private static double Det3(double[,] M) =>
            M[0, 0] * (M[1, 1] * M[2, 2] - M[1, 2] * M[2, 1])
          - M[0, 1] * (M[1, 0] * M[2, 2] - M[1, 2] * M[2, 0])
          + M[0, 2] * (M[1, 0] * M[2, 1] - M[1, 1] * M[2, 0]);
    }
}
