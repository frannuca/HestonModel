using System;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.Implementations
{
    // Market data implementation that wraps a volatility surface
    public class SurfaceMarketData : IMarketData
    {
        private readonly IVolatilitySurface _surface;

        public SurfaceMarketData(IVolatilitySurface surface)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        public double GetPriceByStrike(double spot, double strike, double maturity)
        {
            var vol = _surface.GetVolByStrike(spot, strike, maturity);
            return BlackScholes.CallPrice(spot, strike, vol, maturity);
        }

        public double GetPriceByDelta(double spot, double delta, double maturity)
        {
            var vol = _surface.GetVolByDelta(spot, delta, maturity);
            var strike = BlackScholes.DeltaToStrike(spot, delta, vol, maturity);
            return BlackScholes.CallPrice(spot, strike, vol, maturity);
        }

        public double GetVolByStrike(double spot, double strike, double maturity)
        {
            return _surface.GetVolByStrike(spot, strike, maturity);
        }

        public double GetVolByDelta(double spot, double delta, double maturity)
        {
            return _surface.GetVolByDelta(spot, delta, maturity);
        }
    }
}
