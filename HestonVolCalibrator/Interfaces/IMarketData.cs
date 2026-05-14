using System;

namespace HestonVolCalibrator.Interfaces
{
    public interface IMarketData
    {
        // Return option price (call) for given spot, strike and time to maturity
        double GetPriceByStrike(double spot, double strike, double maturity);

        // Return option price (call) for given spot, delta and time to maturity (delta in (0,1))
        double GetPriceByDelta(double spot, double delta, double maturity);

        // Helper to get implied vol (by strike)
        double GetVolByStrike(double spot, double strike, double maturity);

        double GetVolByDelta(double spot, double delta, double maturity);
    }
}

