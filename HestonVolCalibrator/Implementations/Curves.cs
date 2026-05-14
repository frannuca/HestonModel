using System;
using System.Collections.Generic;
using System.Linq;
using HestonVolCalibrator.Interfaces;

namespace HestonVolCalibrator.Implementations
{
    // Flat continuous rate. Used as a sane default and for unit-test fixtures.
    public sealed class FlatDiscountCurve : IDiscountCurve
    {
        public double Rate { get; }
        public FlatDiscountCurve(double rate) { Rate = rate; }
        public double Df(double t) => Math.Exp(-Rate * Math.Max(t, 0.0));
        public double ZeroRate(double t) => Rate;
        public double InstantaneousForward(double t) => Rate;
    }

    public sealed class FlatYieldCurve : IYieldCurve
    {
        public double Yield { get; }
        public FlatYieldCurve(double yield) { Yield = yield; }
        public double Df(double t) => Math.Exp(-Yield * Math.Max(t, 0.0));
        public double ZeroRate(double t) => Yield;
        public double InstantaneousForward(double t) => Yield;
        public double DividendYield(double t) => Yield;
    }

    // Piecewise-linear *zero rate* curve in time. Interpolates z(t) linearly between
    // pillar tenors; flat extrapolation outside the pillar range.
    //
    // Inputs: pillar times (years) and the matching zero rates.
    // Tenors must be strictly increasing and contain at least one point.
    public sealed class PiecewiseLinearZeroCurve : IDiscountCurve
    {
        private readonly double[] _t;
        private readonly double[] _z;

        public PiecewiseLinearZeroCurve(IReadOnlyList<double> tenors, IReadOnlyList<double> zeroRates)
        {
            if (tenors is null) throw new ArgumentNullException(nameof(tenors));
            if (zeroRates is null) throw new ArgumentNullException(nameof(zeroRates));
            if (tenors.Count == 0 || tenors.Count != zeroRates.Count)
                throw new ArgumentException("Tenor and rate arrays must be non-empty and equal length.");
            for (int i = 1; i < tenors.Count; i++)
                if (!(tenors[i] > tenors[i - 1]))
                    throw new ArgumentException("Tenors must be strictly increasing.");

            _t = tenors.ToArray();
            _z = zeroRates.ToArray();
        }

        public double ZeroRate(double t)
        {
            if (t <= _t[0]) return _z[0];
            if (t >= _t[^1]) return _z[^1];

            int hi = Array.BinarySearch(_t, t);
            if (hi >= 0) return _z[hi];
            hi = ~hi;
            int lo = hi - 1;
            double w = (t - _t[lo]) / (_t[hi] - _t[lo]);
            return _z[lo] * (1 - w) + _z[hi] * w;
        }

        public double Df(double t) => Math.Exp(-ZeroRate(t) * Math.Max(t, 0.0));

        public double InstantaneousForward(double t)
        {
            // f(t) = z(t) + t * dz/dt   (from df = exp(-z*t))
            if (_t.Length < 2) return _z[0];
            double t0 = Math.Max(t, 0.0);
            double h = Math.Max(1e-6, Math.Abs(t0) * 1e-4);
            double dz = (ZeroRate(t0 + h) - ZeroRate(t0 - h)) / (2 * h);
            return ZeroRate(t0) + t0 * dz;
        }
    }

    public sealed class PiecewiseLinearYieldCurve : IYieldCurve
    {
        private readonly PiecewiseLinearZeroCurve _inner;
        public PiecewiseLinearYieldCurve(IReadOnlyList<double> tenors, IReadOnlyList<double> yields)
        {
            _inner = new PiecewiseLinearZeroCurve(tenors, yields);
        }
        public double Df(double t) => _inner.Df(t);
        public double ZeroRate(double t) => _inner.ZeroRate(t);
        public double InstantaneousForward(double t) => _inner.InstantaneousForward(t);
        public double DividendYield(double t) => _inner.ZeroRate(t);
    }

    // Convenience helpers for snapping flat scalars into the curve abstractions.
    public static class CurveFactory
    {
        public static IDiscountCurve Flat(double rate) => new FlatDiscountCurve(rate);
        public static IYieldCurve FlatDiv(double q) => new FlatYieldCurve(q);
        public static IForwardCurve ForwardFromFlats(double spot, double rate, double q) =>
            new ForwardCurve(spot, new FlatDiscountCurve(rate), new FlatYieldCurve(q));
    }

    // Forward curve F(0,t) = S0 * Dq(t) / D(t).
    // If the consumer has an externally observed forward strip (e.g. SPX listed forwards),
    // construct via the (tenors, forwards) overload and the curves are inferred implicitly.
    public sealed class ForwardCurve : IForwardCurve
    {
        public double Spot { get; }
        private readonly IDiscountCurve _disc;
        private readonly IYieldCurve _div;

        public ForwardCurve(double spot, IDiscountCurve discount, IYieldCurve dividend)
        {
            if (!(spot > 0)) throw new ArgumentException("Spot must be positive.", nameof(spot));
            Spot = spot;
            _disc = discount ?? throw new ArgumentNullException(nameof(discount));
            _div = dividend ?? throw new ArgumentNullException(nameof(dividend));
        }

        public double Forward(double t)
        {
            double df = _disc.Df(t);
            if (!(df > 0)) return Spot;
            return Spot * _div.Df(t) / df;
        }

        public double LogMoneyness(double strike, double t)
        {
            double f = Forward(t);
            if (!(f > 0) || !(strike > 0)) return double.NaN;
            return Math.Log(strike / f);
        }
    }
}
