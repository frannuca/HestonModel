using System;
using System.Collections.Generic;
using HestonVolCalibrator.DataLoader;
using HestonVolCalibrator.Implementations;

namespace HestonVolCalibrator.Web;

// Mirrors the synthetic SPX-like surface in HestonVolCalibrator.DataLoader/Program.cs
// so the API can fall back when Yahoo blocks.
internal static class SyntheticSurface
{
    public static (double spot, List<OptionQuote> quotes) BuildSpx()
    {
        double s = 5300.0;
        var p = new HestonModelParams(kappa: 1.2, theta: 0.04, sigma: 0.35, rho: -0.75, v0: 0.035);
        double rate = 0.045, divYield = 0.013;

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

                double callPrice = HestonPricer.CallPrice(p, s, k, t, rate, divYield);
                double callSpread = Math.Max(callPrice * 0.01, 0.10);
                double callNoise = 1.0 + (rng.NextDouble() - 0.5) * 0.002;

                list.Add(new OptionQuote(
                    Strike: k, Maturity: t,
                    Bid: callPrice * callNoise - callSpread / 2,
                    Ask: callPrice * callNoise + callSpread / 2,
                    LastPrice: callPrice * callNoise,
                    OpenInterest: rng.Next(100, 5000),
                    Volume: rng.Next(10, 500),
                    IsCall: true));

                // Put via put-call parity: P = C - S*exp(-q*T) + K*exp(-r*T)
                double putPrice = callPrice - s * Math.Exp(-divYield * t) + k * Math.Exp(-rate * t);
                if (putPrice <= 0) continue;

                double putSpread = Math.Max(putPrice * 0.01, 0.10);
                double putNoise = 1.0 + (rng.NextDouble() - 0.5) * 0.002;

                list.Add(new OptionQuote(
                    Strike: k, Maturity: t,
                    Bid: putPrice * putNoise - putSpread / 2,
                    Ask: putPrice * putNoise + putSpread / 2,
                    LastPrice: putPrice * putNoise,
                    OpenInterest: rng.Next(100, 5000),
                    Volume: rng.Next(10, 500),
                    IsCall: false));
            }
        return (s, list);
    }
}
