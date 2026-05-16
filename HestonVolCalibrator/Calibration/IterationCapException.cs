using System;

namespace HestonVolCalibrator.Calibration
{
    // Sentinel thrown by the gradient optimizers' inner F()/Grad() callbacks when the
    // user-specified iteration (or function-evaluation safety) cap is reached. The outer
    // try/catch in each optimizer recognises it and returns the best-so-far result.
    internal sealed class IterationCapException : Exception
    {
    }
}
