using System;

namespace HestonVolCalibrator.Interfaces
{
    // Curve abstraction used by the Heston calibration pipeline.
    //
    // Sign / compounding conventions:
    //   df(t)     = exp(-r(t) * t)            (continuous compounding, ACT/365F implied)
    //   r(t)      = zero rate to time t       (annualized, continuous)
    //   fwd(0,t)  = spot * dfq(t) / df(t)     (where dfq is the dividend/repo discount curve)
    //
    // Time t is in years (double). All curves are calibrated as-of the same evaluation date.
    public interface IDiscountCurve
    {
        // Discount factor df(t) = exp(-z(t) * t). t in years, t >= 0.
        double Df(double t);

        // Zero rate z(t) such that df(t) = exp(-z(t) * t). Returns Spot rate r0 at t=0 by extrapolation.
        double ZeroRate(double t);

        // Instantaneous forward rate f(t) = -d ln df / dt.
        // Implementations may approximate via finite differences if no analytical form exists.
        double InstantaneousForward(double t);
    }

    // Dividend / repo / borrow curve. Same shape as IDiscountCurve but conceptually distinct,
    // to avoid mixing risk-free and dividend discounts at call sites.
    public interface IYieldCurve : IDiscountCurve
    {
        // Convenience: continuous dividend yield q(t) such that dfq(t) = exp(-q(t) * t).
        double DividendYield(double t);
    }

    // Forward curve for the underlying. Built from spot + discount + dividend curves
    // (or directly observed forwards on indices like SPX).
    public interface IForwardCurve
    {
        // Spot value at evaluation date.
        double Spot { get; }

        // F(0, t) = expected underlying value under the risk-neutral pricing measure.
        // Default construction: Spot * Dq(t) / D(t).
        double Forward(double t);

        // Log-moneyness x = ln(K / F(0,t)) — handy for smile fits.
        double LogMoneyness(double strike, double t);
    }
}
