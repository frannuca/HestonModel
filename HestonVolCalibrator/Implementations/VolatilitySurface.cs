using System.Collections.Generic;
using System.Linq;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.Implementations
{
    // Grid-based volatility surface that stores vol at discrete strike/maturity points
    public class GridVolatilitySurface : IVolatilitySurface
    {
        private readonly Dictionary<(double maturity, double strike), double> _data = new();
        private readonly SortedSet<double> _maturities = new();
        private readonly SortedSet<double> _strikes = new();
        
        // Cache for interpolation to avoid repeated lookups
        private (double maturity, double strike, double vol)? _lastQuery;

        public IReadOnlyList<double> Expiries => _maturities.ToArray();
        public IReadOnlyList<double> Strikes => _strikes.ToArray();

        public bool HasPoint(double maturity, double strike) =>
            _data.ContainsKey((maturity, strike));

        public void AddPoint(double maturity, double strike, double vol)
        {
            _data[(maturity, strike)] = vol;
            _maturities.Add(maturity);
            _strikes.Add(strike);
            _lastQuery = null;  // Invalidate cache
        }

        public double GetVolByStrike(double spot, double strike, double maturity)
        {
            // Quick cache check
            if (_lastQuery?.maturity == maturity && _lastQuery?.strike == strike)
                return _lastQuery.Value.vol;

            // Try exact match first
            if (_data.TryGetValue((maturity, strike), out var vol))
            {
                _lastQuery = (maturity, strike, vol);
                return vol;
            }

            // Bilinear interpolation
            vol = InterpolateVol(strike, maturity);
            _lastQuery = (maturity, strike, vol);
            return vol;
        }

        public double GetVolByDelta(double spot, double delta, double maturity)
        {
            // Convert delta to strike using Black-Scholes approximation
            var strike = DeltaToStrike(spot, delta, maturity);
            return GetVolByStrike(spot, strike, maturity);
        }

        public bool HasMaturity(double maturity)
        {
            return _maturities.Contains(maturity);
        }

        private double InterpolateVol(double strike, double maturity)
        {
            // Find surrounding strikes and maturities
            var strikeFloor = FindFloor(_strikes, strike);
            var strikeCeil = FindCeiling(_strikes, strike);
            var maturityFloor = FindFloor(_maturities, maturity);
            var maturityCeil = FindCeiling(_maturities, maturity);

            // If exact match on one dimension, use 1D interpolation
            if (strikeFloor == strikeCeil && maturityFloor == maturityCeil)
                return _data[(maturityFloor, strikeFloor)];

            if (strikeFloor == strikeCeil)
            {
                // Interpolate in maturity only
                var matVol1 = _data[(maturityFloor, strikeFloor)];
                var matVol2 = _data[(maturityCeil, strikeFloor)];
                var w = (maturity - maturityFloor) / (maturityCeil - maturityFloor);
                return matVol1 * (1 - w) + matVol2 * w;
            }

            if (maturityFloor == maturityCeil)
            {
                // Interpolate in strike only
                var strVol1 = _data[(maturityFloor, strikeFloor)];
                var strVol2 = _data[(maturityFloor, strikeCeil)];
                var w = (strike - strikeFloor) / (strikeCeil - strikeFloor);
                return strVol1 * (1 - w) + strVol2 * w;
            }

            // Bilinear interpolation
            var v11 = _data[(maturityFloor, strikeFloor)];
            var v12 = _data[(maturityFloor, strikeCeil)];
            var v21 = _data[(maturityCeil, strikeFloor)];
            var v22 = _data[(maturityCeil, strikeCeil)];

            var wx = (strike - strikeFloor) / (strikeCeil - strikeFloor);
            var wy = (maturity - maturityFloor) / (maturityCeil - maturityFloor);

            var bilinVol1 = v11 * (1 - wx) + v12 * wx;
            var bilinVol2 = v21 * (1 - wx) + v22 * wx;

            return bilinVol1 * (1 - wy) + bilinVol2 * wy;
        }

        private double DeltaToStrike(double spot, double delta, double maturity)
        {
            if (delta <= 0 || delta >= 1)
                throw new System.ArgumentException("Delta must be in (0, 1)");

            // Get ATM vol for rough approximation
            var atmVol = GetVolByStrike(spot, spot, maturity);
            
            // Use Black-Scholes to convert delta to strike
            return BlackScholes.DeltaToStrike(spot, delta, atmVol, maturity);
        }

        private static double FindFloor(SortedSet<double> set, double value)
        {
            var floor = double.NegativeInfinity;
            foreach (var v in set)
            {
                if (v <= value)
                    floor = v;
                else
                    break;
            }
            return floor;
        }

        private static double FindCeiling(SortedSet<double> set, double value)
        {
            foreach (var v in set)
            {
                if (v >= value)
                    return v;
            }
            return double.PositiveInfinity;
        }
    }
}
