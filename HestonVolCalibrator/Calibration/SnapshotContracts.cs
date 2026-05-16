using System;
using System.Collections.Generic;

namespace HestonVolCalibrator.Calibration;

// Snapshot DTOs live in the core library so both the Web layer (filesystem persistence)
// and the Runner test (round-trip check) can share the schema without one depending on
// the other. JSON is the wire format; serialisation is driven by the Web layer's options
// (camelCase, NaN→null) but the records themselves are framework-agnostic.

public record Snapshot(
    string Version,
    DateTime CreatedAtUtc,
    SurfaceSnapshot Surface,
    CalibrationSnapshot? Calibration);

public record SurfaceSnapshot(
    string Ticker,
    string Source,
    double Spot,
    double RiskFreeRate,
    double DividendYield,
    double[] Expiries,
    double[] Strikes,
    double[][] Iv,
    double?[][] CallPrice,
    double?[][] PutPrice,
    SurfaceCleanStats? CleanStats);

public record SurfaceCleanStats(
    int Input,
    int Kept,
    int DroppedBidAsk,
    int DroppedBounds,
    int DroppedConvexity,
    int DroppedIvOutlier);

public record CalibrationSnapshot(
    HestonParams Params,
    double FinalRmse,
    bool Converged,
    int TotalIterations,
    double ElapsedMs,
    double[] Expiries,
    double[] Strikes,
    double[][] MarketIv,
    double[][] HestonIv,
    List<StageResult> Stages,
    List<ConvergencePoint> History);
