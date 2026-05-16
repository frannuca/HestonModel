using System;
using System.Collections.Generic;
using System.Linq;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.Implementations
{
    // Grid-based volatility surface storing IV at discrete (maturity, strike) points.
    //
    // Interpolation:
    //   - Strike axis: linear in IV (smile shape is already smooth in vol).
    //   - Maturity axis: linear in TOTAL VARIANCE w = sigma^2 * T. This avoids the
    //     calendar-arbitrage that linear-in-vol-vs-T can introduce when expiries are sparse.
    //   - Out-of-grid queries are clamped to the nearest grid boundary (flat extrapolation).
    //
    // Internally we keep the strike/maturity axes as sorted arrays so floor/ceil lookups are
    // O(log N) instead of the prior O(N) linear scan.
    public class GridVolatilitySurface : IVolatilitySurface
    {
        private readonly Dictionary<(double maturity, double strike), double> _data = new();
        private readonly SortedSet<double> _maturities = new();
        private readonly SortedSet<double> _strikes = new();

        // Sorted-array snapshots; rebuilt on AddPoint. Used for O(log N) bracketing.
        // Kept under the same lock that guards _data to ensure consistency under concurrent reads.
        private double[] _maturityArr = Array.Empty<double>();
        private double[] _strikeArr = Array.Empty<double>();
        private readonly object _gate = new();

        public IReadOnlyList<double> Expiries
        {
            get { lock (_gate) return (double[])_maturityArr.Clone(); }
        }
        public IReadOnlyList<double> Strikes
        {
            get { lock (_gate) return (double[])_strikeArr.Clone(); }
        }

        public bool HasPoint(double maturity, double strike)
        {
            lock (_gate) return _data.ContainsKey((maturity, strike));
        }

        public void AddPoint(double maturity, double strike, double vol)
        {
            lock (_gate)
            {
                _data[(maturity, strike)] = vol;
                if (_maturities.Add(maturity)) _maturityArr = _maturities.ToArray();
                if (_strikes.Add(strike)) _strikeArr = _strikes.ToArray();
            }
        }

        public double GetVolByStrike(double spot, double strike, double maturity)
        {
            lock (_gate)
            {
                if (_data.TryGetValue((maturity, strike), out var vol))
                    return vol;
                return InterpolateVolLocked(strike, maturity);
            }
        }

        public double GetVolByDelta(double spot, double delta, double maturity)
        {
            var strike = DeltaToStrike(spot, delta, maturity);
            return GetVolByStrike(spot, strike, maturity);
        }

        public bool HasMaturity(double maturity)
        {
            lock (_gate) return _maturities.Contains(maturity);
        }

        // Caller must hold _gate.
        private double InterpolateVolLocked(double strike, double maturity)
        {
            if (_maturityArr.Length == 0 || _strikeArr.Length == 0)
                throw new InvalidOperationException("Surface has no data.");

            // Clamp to grid (flat extrapolation at the boundary).
            double tQ = Math.Min(Math.Max(maturity, _maturityArr[0]), _maturityArr[^1]);
            double kQ = Math.Min(Math.Max(strike, _strikeArr[0]), _strikeArr[^1]);

            var (tLoIdx, tHiIdx, wT) = Bracket(_maturityArr, tQ);
            var (kLoIdx, kHiIdx, wK) = Bracket(_strikeArr, kQ);

            double tLo = _maturityArr[tLoIdx], tHi = _maturityArr[tHiIdx];
            double kLo = _strikeArr[kLoIdx],  kHi = _strikeArr[kHiIdx];

            // Strike-direction: linear in IV at each bracketing maturity.
            double v11 = _data[(tLo, kLo)];
            double v12 = _data[(tLo, kHi)];
            double v21 = _data[(tHi, kLo)];
            double v22 = _data[(tHi, kHi)];

            double sigLo = v11 * (1 - wK) + v12 * wK;   // IV at (tLo, kQ)
            double sigHi = v21 * (1 - wK) + v22 * wK;   // IV at (tHi, kQ)

            if (tLoIdx == tHiIdx) return sigLo;

            // Maturity-direction: linear in TOTAL VARIANCE w = sigma^2 * T.
            double wLo = sigLo * sigLo * tLo;
            double wHi = sigHi * sigHi * tHi;
            double wInterp = wLo * (1 - wT) + wHi * wT;
            if (wInterp <= 0 || tQ <= 0) return sigLo; // degenerate guard
            return Math.Sqrt(wInterp / tQ);
        }

        // Binary-search bracket. Returns (loIdx, hiIdx, w) such that
        //   value ≈ arr[loIdx] * (1-w) + arr[hiIdx] * w,    0 <= w <= 1.
        // Assumes value is already clamped into [arr[0], arr[^1]].
        private static (int lo, int hi, double w) Bracket(double[] arr, double value)
        {
            int lo = 0, hi = arr.Length - 1;
            if (lo == hi) return (lo, hi, 0.0);

            // Lower bound via binary search: largest index with arr[idx] <= value.
            int l = 0, r = arr.Length - 1;
            while (l < r)
            {
                int m = (l + r + 1) >> 1;
                if (arr[m] <= value) l = m; else r = m - 1;
            }
            int loIdx = l;
            int hiIdx = Math.Min(loIdx + 1, arr.Length - 1);
            if (loIdx == hiIdx || arr[hiIdx] == arr[loIdx]) return (loIdx, hiIdx, 0.0);
            double w = (value - arr[loIdx]) / (arr[hiIdx] - arr[loIdx]);
            return (loIdx, hiIdx, w);
        }

        private double DeltaToStrike(double spot, double delta, double maturity)
        {
            if (delta <= 0 || delta >= 1)
                throw new System.ArgumentException("Delta must be in (0, 1)");

            var atmVol = GetVolByStrike(spot, spot, maturity);
            return BlackScholes.DeltaToStrike(spot, delta, atmVol, maturity);
        }
    }
}
