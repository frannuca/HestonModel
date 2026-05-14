using System;
using HestonVolCalibrator.Implementations;

namespace HestonVolCalibrator.DataLoader
{
    // Computes Black-Scholes implied volatility for an option quote.
    public static class ImpliedVolSolver
    {
        // Returns NaN if implied vol cannot be computed (illiquid, intrinsic, or bad input).
        public static double Solve(OptionQuote q, double spot, double rate, double dividendYield)
        {
            // Use mid-price; fall back to last price if spread is unusable
            double mid = 0.5 * (q.Bid + q.Ask);
            if (mid <= 0) mid = q.LastPrice;
            if (mid <= 0 || q.Maturity <= 0 || spot <= 0 || q.Strike <= 0) return double.NaN;

            // Convert put to equivalent call price via put-call parity so we always invert a call
            double df  = Math.Exp(-rate * q.Maturity);
            double dfq = Math.Exp(-dividendYield * q.Maturity);
            double callPrice;

            if (q.IsCall)
            {
                callPrice = mid;
            }
            else
            {
                // P = C - S*e^{-qT} + K*e^{-rT}  =>  C = P + S*dfq - K*df
                callPrice = mid + spot * dfq - q.Strike * df;
            }

            // Reject if the call price is below intrinsic or above spot
            double intrinsic = Math.Max(spot * dfq - q.Strike * df, 0.0);
            if (callPrice <= intrinsic + 1e-4 || callPrice >= spot * dfq) return double.NaN;

            // Filter by moneyness: only keep options between 70% and 130% of spot
            double moneyness = q.Strike / spot;
            if (moneyness < 0.70 || moneyness > 1.30) return double.NaN;

            double iv = HestonPricer.BsImpliedVol(callPrice, spot, q.Strike, q.Maturity, rate, dividendYield);

            // Reject implausible vols (likely illiquid or stale quotes)
            if (iv < 0.01 || iv > 2.0) return double.NaN;

            return iv;
        }
    }
}
