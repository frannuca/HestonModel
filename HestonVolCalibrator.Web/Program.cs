using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using HestonVolCalibrator.Calibration;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.IRProducts;
using HestonVolCalibrator.SABR;
using HestonVolCalibrator.Swaptions;
using HestonVolCalibrator.Web;


var builder = WebApplication.CreateBuilder(args);

// JSON: camelCase, ignore nulls, enums as strings.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.Converters.Add(new NaNAsNullDoubleConverter());
});

// Permissive dev CORS — any http://localhost:* origin.
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p =>
        p.SetIsOriginAllowed(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var u) &&
            u.Host is "localhost" or "127.0.0.1")
         .AllowAnyHeader()
         .AllowAnyMethod());
});

builder.Services.AddSingleton<SurfaceService>();
builder.Services.AddSingleton(sp => new SnapshotService(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<ISwaptionDataLoader>(_ => new UsTreasurySwaptionLoader());

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "calibrations.db");
builder.Services.AddSingleton<ICalibrationStore>(_ => new SqliteCalibrationStore(dbPath));

builder.WebHost.ConfigureKestrel(k =>
{
    // Default explicit binding so the CLI run path is predictable.
    k.ListenLocalhost(5000);
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();   // serves wwwroot/index.html at "/"
app.UseStaticFiles();

// JSON options used by manual writes (SSE final payload, etc.).
var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters =
    {
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        new NaNAsNullDoubleConverter()
    }
};

// ───────────────── /api/health ─────────────────
app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

// ───────────────── /api/surface ─────────────────
app.MapPost("/api/surface", async (SurfaceRequest req, SurfaceService svc) =>
{
    var cached = await svc.FetchAsync(
        req.Ticker, req.MaxExpiries, req.ForceSynthetic, req.Clean,
        cleanOptions: null,
        minMaturity: req.MinMaturity,
        maxMaturity: req.MaxMaturity,
        minMoneyness: req.MinMoneyness,
        maxMoneyness: req.MaxMoneyness);
    return Results.Ok(new SurfaceResponse(
        cached.Spot, cached.Ticker, cached.Expiries, cached.Strikes, cached.Iv,
        cached.CallPrice, cached.PutPrice, cached.Source,
        cached.RiskFreeRate, cached.DividendYield, cached.CleanStats));
});

// ───────────────── /api/calibrate ─────────────────
app.MapPost("/api/calibrate", async (CalibrateApiRequest req, SurfaceService svc, ILoggerFactory lf) =>
{
    var logger = lf.CreateLogger("Calibrate");
    try
    {
        var cached = await svc.GetOrFetchAsync(req.Ticker, maxExpiries: 6);
        var calibReq = BuildCalibrationRequest(req, cached);
        var calibrator = new HestonCalibrator(new SurfaceMarketData(cached.Surface));
        var result = calibrator.Calibrate(calibReq, cached.Surface);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Calibration failed for ticker {Ticker}", req.Ticker);
        return Results.Problem(
            title: "Calibration failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

// ───────────────── /api/calibrate/stream (SSE) ─────────────────
app.MapPost("/api/calibrate/stream", async (HttpContext ctx, CalibrateApiRequest req, SurfaceService svc, CancellationToken ct) =>
{
    var cached = await svc.GetOrFetchAsync(req.Ticker, maxExpiries: 6);
    var calibReq = BuildCalibrationRequest(req, cached);

    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    // Emit a "started" frame immediately so the client knows the stream is open
    // and gets surface stats up front. On large market grids the first iteration
    // can take many seconds; without this frame the UI appears frozen.
    var startedPayload = JsonSerializer.Serialize(new
    {
        ticker = req.Ticker,
        source = cached.Source,
        spot = cached.Spot,
        expiries = cached.Expiries.Length,
        strikes = cached.Strikes.Length,
        globalMethod = req.GlobalMethod.ToString(),
        gradientMethod = req.GradientMethod.ToString(),
    }, jsonOpts);
    await ctx.Response.WriteAsync($"event: started\ndata: {startedPayload}\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);

    var channel = Channel.CreateUnbounded<ConvergencePoint>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    var calibrator = new HestonCalibrator(new SurfaceMarketData(cached.Surface));

    var calibTask = Task.Run(() =>
    {
        try
        {
            var result = calibrator.Calibrate(calibReq, cached.Surface, p =>
            {
                channel.Writer.TryWrite(p);
            });
            channel.Writer.Complete();
            return result;
        }
        catch (Exception ex)
        {
            channel.Writer.Complete(ex);
            throw;
        }
    }, ct);

    try
    {
        await foreach (var pt in channel.Reader.ReadAllAsync(ct))
        {
            var payload = JsonSerializer.Serialize(pt, jsonOpts);
            await ctx.Response.WriteAsync($"event: progress\ndata: {payload}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }

        var final = await calibTask;
        var finalJson = JsonSerializer.Serialize(final, jsonOpts);
        await ctx.Response.WriteAsync($"event: done\ndata: {finalJson}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Client disconnected — nothing to emit.
    }
    catch (Exception ex)
    {
        var errPayload = JsonSerializer.Serialize(new { message = ex.Message, type = ex.GetType().Name }, jsonOpts);
        try
        {
            await ctx.Response.WriteAsync($"event: error\ndata: {errPayload}\n\n", CancellationToken.None);
            await ctx.Response.Body.FlushAsync(CancellationToken.None);
        }
        catch
        {
            // Response may already be closed; nothing more we can do.
        }
    }
});

// ───────────────── /api/snapshot/* ─────────────────
// Persist & restore the loaded market surface + last calibration so the user can
// re-open a fitted state without re-fetching/re-calibrating.
app.MapGet("/api/snapshot/list", (SnapshotService svc) => Results.Ok(svc.List()));

app.MapPost("/api/snapshot/save", async (SaveSnapshotRequest req, SnapshotService svc) =>
{
    if (req.Snapshot is null) return Results.BadRequest(new { message = "Snapshot payload is required." });
    var snap = req.Snapshot with { Version = SnapshotService.SnapshotVersion, CreatedAtUtc = DateTime.UtcNow };
    await svc.SaveAsync(req.Name, snap);
    return Results.Ok(new { name = SnapshotService.Sanitize(req.Name), createdAtUtc = snap.CreatedAtUtc });
});

app.MapGet("/api/snapshot/load/{name}", async (string name, SnapshotService svc) =>
{
    var snap = await svc.LoadAsync(name);
    return snap is null
        ? Results.NotFound(new { message = $"Snapshot '{name}' not found." })
        : Results.Ok(snap);
});

app.MapDelete("/api/snapshot/{name}", (string name, SnapshotService svc) =>
    svc.Delete(name) ? Results.Ok(new { deleted = true }) : Results.NotFound(new { message = "Not found." }));

// ───────────────── /api/heston-surface ─────────────────
app.MapPost("/api/heston-surface", (HestonSurfaceRequest req) =>
{
    var p = new HestonModelParams(req.Params.Kappa, req.Params.Theta, req.Params.Sigma, req.Params.Rho, req.Params.V0);
    var iv = new double[req.Maturities.Length][];
    for (int i = 0; i < req.Maturities.Length; i++)
    {
        iv[i] = new double[req.Strikes.Length];
        double t = req.Maturities[i];
        for (int j = 0; j < req.Strikes.Length; j++)
        {
            try { iv[i][j] = HestonPricer.ImpliedVol(p, req.Spot, req.Strikes[j], t, req.RiskFreeRate, req.DividendYield); }
            catch { iv[i][j] = double.NaN; }
        }
    }
    return Results.Ok(new HestonSurfaceResponse(iv));
});

// ───────────────── /api/heston-delta ─────────────────
// On-demand strike→delta transformation derived from the calibrated Heston model.
// Pipeline per (T, K): Heston price → BS-implied vol → BS-call-delta. Returns both arrays
// so the frontend smile plots get Heston IV (for the curve) and delta (for the x-axis) in
// one round-trip. Same request body as /api/heston-surface for easy substitution on the client.
app.MapPost("/api/heston-delta", (HestonSurfaceRequest req) =>
{
    var p = new HestonModelParams(req.Params.Kappa, req.Params.Theta, req.Params.Sigma, req.Params.Rho, req.Params.V0);
    int nT = req.Maturities.Length;
    int nK = req.Strikes.Length;
    var iv = new double[nT][];
    var delta = new double[nT][];
    Parallel.For(0, nT, i =>
    {
        iv[i] = new double[nK];
        delta[i] = new double[nK];
        double t = req.Maturities[i];
        for (int j = 0; j < nK; j++)
        {
            double k = req.Strikes[j];
            try
            {
                double vol = HestonPricer.ImpliedVol(p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                iv[i][j] = vol;
                // BlackScholes.CallDelta returns NaN if vol is NaN/non-positive — propagates cleanly.
                delta[i][j] = (vol > 0 && double.IsFinite(vol))
                    ?BlackScholes.CallDelta(req.Spot, k, vol, t, req.RiskFreeRate, req.DividendYield)
                    : double.NaN;
            }
            catch
            {
                iv[i][j] = double.NaN;
                delta[i][j] = double.NaN;
            }
        }
    });
   
    return Results.Ok(new HestonDeltaResponse(iv, delta));
});

// ───────────────── /api/heston-surface-with-greeks ─────────────────
// IV plus first-order Greeks (delta, gamma, vega, theta, rho) on the (maturity, strike) grid.
// `includeGreeks` is a query flag — when false the Greek arrays come back as null so the wire
// payload is the same size as /api/heston-surface (callers that don't need them skip a lot of
// pricer math). Each cell is independent: any failure NaNs the IV and nulls the Greeks for that
// cell without aborting the rest of the grid.
app.MapPost("/api/heston-surface-with-greeks", (HestonSurfaceRequest req, bool includeGreeks = true) =>
{
    var p = new HestonModelParams(req.Params.Kappa, req.Params.Theta, req.Params.Sigma, req.Params.Rho, req.Params.V0);
    int nT = req.Maturities.Length;
    int nK = req.Strikes.Length;

    var iv = new double[nT][];
    double?[][]? delta = null, gamma = null, vega = null, theta = null, rho = null;
    if (includeGreeks)
    {
        delta = new double?[nT][];
        gamma = new double?[nT][];
        vega  = new double?[nT][];
        theta = new double?[nT][];
        rho   = new double?[nT][];
    }

    for (int i = 0; i < nT; i++)
    {
        iv[i] = new double[nK];
        if (includeGreeks)
        {
            delta![i] = new double?[nK];
            gamma![i] = new double?[nK];
            vega![i]  = new double?[nK];
            theta![i] = new double?[nK];
            rho![i]   = new double?[nK];
        }
        double t = req.Maturities[i];
        for (int j = 0; j < nK; j++)
        {
            double k = req.Strikes[j];
            try
            {
                iv[i][j] = HestonPricer.ImpliedVol(p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                if (includeGreeks)
                {
                    delta![i][j] = HestonPricer.CallDelta(p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                    gamma![i][j] = HestonPricer.Gamma   (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                    vega![i][j]  = HestonPricer.Vega    (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                    theta![i][j] = HestonPricer.Theta   (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                    rho![i][j]   = HestonPricer.Rho     (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                }
            }
            catch
            {
                iv[i][j] = double.NaN;
                // Greek slots stay null (default for double?[]); no explicit assignment needed.
            }
        }
    }

    return Results.Ok(new HestonSurfaceResponseWithGreeks(iv, delta, gamma, vega, theta, rho));
});

// ───────────────── /api/heston-surface-with-greeks/stream (SSE) ─────────────────
// Same payload as the non-streaming variant, but emits one `event: progress` per cell so
// the UI can show a live "expiry i/N, strike j/M" progress bar. Each Greek requires several
// finite-difference pricer evaluations, so a 41×25 grid can take minutes — streaming makes
// the wait visible instead of a frozen request.
app.MapPost("/api/heston-surface-with-greeks/stream",
    async (HttpContext ctx, HestonSurfaceRequest req, CancellationToken ct) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    int nT = req.Maturities.Length;
    int nK = req.Strikes.Length;
    int total = nT * nK;

    // Emit a "started" frame so the bar shows up immediately, before any cell is computed.
    var startedJson = JsonSerializer.Serialize(new
    {
        total,
        expiries = nT,
        strikes = nK,
    }, jsonOpts);
    await ctx.Response.WriteAsync($"event: started\ndata: {startedJson}\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);

    var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    var p = new HestonModelParams(req.Params.Kappa, req.Params.Theta, req.Params.Sigma, req.Params.Rho, req.Params.V0);
    var iv    = new double[nT][];
    var delta = new double?[nT][];
    var gamma = new double?[nT][];
    var vega  = new double?[nT][];
    var theta = new double?[nT][];
    var rho   = new double?[nT][];
    for (int i = 0; i < nT; i++)
    {
        iv[i]    = new double[nK];
        delta[i] = new double?[nK];
        gamma[i] = new double?[nK];
        vega[i]  = new double?[nK];
        theta[i] = new double?[nK];
        rho[i]   = new double?[nK];
    }

    var task = Task.Run(() =>
    {
        try
        {
            int counter = 0;
            for (int i = 0; i < nT; i++)
            {
                double t = req.Maturities[i];
                for (int j = 0; j < nK; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    double k = req.Strikes[j];
                    double cellIv = double.NaN;
                    double? cellDelta = null, cellGamma = null, cellVega = null, cellTheta = null, cellRho = null;
                    try
                    {
                        cellIv    = HestonPricer.ImpliedVol(p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                        cellDelta = HestonPricer.CallDelta (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                        cellGamma = HestonPricer.Gamma    (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                        cellVega  = HestonPricer.Vega     (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                        cellTheta = HestonPricer.Theta    (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                        cellRho   = HestonPricer.Rho      (p, req.Spot, k, t, req.RiskFreeRate, req.DividendYield);
                    }
                    catch
                    {
                        // Leave cell values NaN/null; continue with the rest of the grid.
                    }
                    iv[i][j]    = cellIv;
                    delta[i][j] = cellDelta;
                    gamma[i][j] = cellGamma;
                    vega[i][j]  = cellVega;
                    theta[i][j] = cellTheta;
                    rho[i][j]   = cellRho;
                    counter++;

                    channel.Writer.TryWrite(new
                    {
                        iter = counter,
                        total,
                        expiryIdx = i,
                        strikeIdx = j,
                        expiry = t,
                        strike = k,
                        iv = cellIv,
                        delta = cellDelta,
                    });
                }
            }
            channel.Writer.Complete();
            return new HestonSurfaceResponseWithGreeks(iv, delta, gamma, vega, theta, rho);
        }
        catch (Exception ex)
        {
            channel.Writer.Complete(ex);
            throw;
        }
    }, ct);

    try
    {
        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
        {
            var payload = JsonSerializer.Serialize(frame, jsonOpts);
            await ctx.Response.WriteAsync($"event: progress\ndata: {payload}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }

        var final = await task;
        var finalJson = JsonSerializer.Serialize(final, jsonOpts);
        await ctx.Response.WriteAsync($"event: done\ndata: {finalJson}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    catch (Exception ex)
    {
        var errPayload = JsonSerializer.Serialize(new { message = ex.Message, type = ex.GetType().Name }, jsonOpts);
        try
        {
            await ctx.Response.WriteAsync($"event: error\ndata: {errPayload}\n\n", CancellationToken.None);
            await ctx.Response.Body.FlushAsync(CancellationToken.None);
        }
        catch { }
    }
});

// ───────────────── /api/sabr/calibrate ─────────────────
// Fit SABR (α, ρ, ν) — or (α, β, ρ, ν) when fixBeta=false — to a single vol smile slice.
app.MapPost("/api/sabr/calibrate", (SabrCalibrateRequest req) =>
{
    var input = new SabrCalibrator.CalibrateInput(
        req.Forward, req.Expiry,
        req.Strikes, req.MarketVols,
        req.Beta, req.FixBeta, req.Shift, req.Convention,
        req.MaxIterations, req.Restarts, req.Seed);
    var res = SabrCalibrator.Calibrate(input);

    SabrGreeksDto[]? greeks = null;
    if (req.Annuity is { } annuity)
    {
        greeks = req.Strikes.Select(k =>
        {
            var spec = new SwaptionSpec(req.Forward, k, req.Expiry, annuity, req.IsPayer);
            return new SabrGreeksDto(
                k,
                SwaptionPricer.Delta (spec, res.Params, req.Shift, req.Convention),
                SwaptionPricer.Gamma (spec, res.Params, req.Shift, req.Convention),
                SwaptionPricer.Vega  (spec, res.Params, req.Shift, req.Convention),
                SwaptionPricer.Vanna (spec, res.Params, req.Shift, req.Convention),
                SwaptionPricer.Volga (spec, res.Params, req.Shift, req.Convention));
        }).ToArray();
    }

    return Results.Ok(new SabrCalibrateResponse(
        ToDto(res.Params), res.Shift, res.FinalRmse, res.Converged, res.Iterations,
        res.ModelVols, res.MarketVols, greeks));
});

// ───────────────── /api/sabr/vol ─────────────────
// Evaluate SABR vol at a set of strikes given known parameters (no calibration).
app.MapPost("/api/sabr/vol", (SabrVolRequest req) =>
{
    var p = FromDto(req.Params);
    var vols = new double[req.Strikes.Length];
    for (int i = 0; i < req.Strikes.Length; i++)
    {
        double f = req.Forward + req.Shift;
        double k = req.Strikes[i] + req.Shift;
        vols[i] = req.Convention == VolConvention.Normal
            ? SabrPricer.NormalImpliedVol(p, f, k, req.Expiry)
            : SabrPricer.ImpliedVol(p, f, k, req.Expiry);
    }
    double atmVol = req.Convention == VolConvention.Normal
        ? SabrPricer.AtmNormalImpliedVol(p, req.Forward + req.Shift, req.Expiry)
        : SabrPricer.AtmImpliedVol(p, req.Forward + req.Shift, req.Expiry);
    return Results.Ok(new SabrVolResponse(vols, atmVol));
});

// ───────────────── /api/sabr/calibrate-surface ─────────────────
// Calibrate SABR independently to each slice in a vol surface (e.g. cap/floor surface,
// swaption cube row). Returns one SabrParams per slice — slice-by-slice calibration.
app.MapPost("/api/sabr/calibrate-surface", (SabrSurfaceCalibrateRequest req) =>
{
    var results = req.Slices.Select(s =>
    {
        var input = new SabrCalibrator.CalibrateInput(
            s.Forward, s.Expiry,
            s.Strikes, s.MarketVols,
            req.Beta, req.FixBeta, req.Shift, req.Convention,
            req.MaxIterations, req.Restarts, req.Seed);
        var r = SabrCalibrator.Calibrate(input);
        return new SabrSliceResult(
            s.Forward, s.Expiry,
            ToDto(r.Params), r.Shift, r.FinalRmse, r.Converged, r.Iterations,
            r.ModelVols, r.MarketVols);
    }).ToArray();
    return Results.Ok(new SabrSurfaceCalibrateResponse(results));
});

// ───────────────── /api/sabr/interpolate-smile ─────────────────
// Delegates entirely to SabrInterpolator in the model layer.
app.MapPost("/api/sabr/interpolate-smile", (SabrInterpolateSmileRequest req) =>
{
    try
    {
        var slices = req.Slices
            .Select(s => new SabrInterpolator.Slice(s.Expiry, s.Forward, FromDto(s.Params), s.Shift))
            .ToArray();

        var result = SabrInterpolator.InterpolateSmile(
            slices, req.TargetExpiry,
            req.StrikeMin, req.StrikeMax, req.NPoints,
            req.Convention);

        return Results.Ok(new SabrInterpolateSmileResponse(result.TargetForward, result.Strikes, result.Vols));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

// ───────────────── /api/sabr/greeks ─────────────────
// Compute per-strike Greeks on a variance-interpolated SABR smile.
// Runs total-variance interpolation first, then evaluates BachelierModel or Black76
// directly with the pre-computed vols (no SABR re-fitting for the target expiry).
app.MapPost("/api/sabr/greeks", (SabrGreeksRequest req) =>
{
    try
    {
        var slices = req.Slices
            .Select(s => new SabrInterpolator.Slice(s.Expiry, s.Forward, FromDto(s.Params), s.Shift))
            .ToArray();
        var result = SabrInterpolator.InterpolateSmile(
            slices, req.TargetExpiry,
            req.StrikeMin, req.StrikeMax, req.NPoints,
            req.Convention);

        double fwd = result.TargetForward;
        double T   = req.TargetExpiry;
        double df  = req.Annuity;
        bool payer = req.IsPayer;

        var greeks = result.Strikes.Zip(result.Vols, (k, vol) =>
        {
            double price, callDelta, gamma, vega, vanna, volga;
            if (req.Convention == VolConvention.Normal)
            {
                price     = payer ? BachelierModel.CallPrice(fwd, k, vol, T, df)
                                  : BachelierModel.PutPrice (fwd, k, vol, T, df);
                callDelta = BachelierModel.CallDelta(fwd, k, vol, T, df);
                gamma     = BachelierModel.Gamma    (fwd, k, vol, T, df);
                vega      = BachelierModel.Vega     (fwd, k, vol, T, df);
                vanna     = BachelierModel.Vanna    (fwd, k, vol, T, df);
                volga     = BachelierModel.Volga    (fwd, k, vol, T, df);
            }
            else
            {
                price     = payer ? Black76.CallPrice(fwd, k, vol, T, df)
                                  : Black76.PutPrice (fwd, k, vol, T, df);
                callDelta = Black76.CallDelta(fwd, k, vol, T, df);
                gamma     = Black76.Gamma    (fwd, k, vol, T, df);
                vega      = Black76.Vega     (fwd, k, vol, T, df);
                vanna     = Black76.Vanna    (fwd, k, vol, T, df);
                volga     = Black76.Volga    (fwd, k, vol, T, df);
            }
            // Receiver delta = call delta - annuity (call-put parity: dC/dF - dP/dF = df)
            double delta = payer ? callDelta : callDelta - df;
            return new SabrGreeksRowDto(k, vol, price, delta, gamma, vega, vanna, volga);
        }).ToArray();

        return Results.Ok(new SabrGreeksResponse(fwd, greeks));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

// ───────────────── /api/ir/caplet ─────────────────
// Price a caplet or floorlet under SABR-derived vol.
app.MapPost("/api/ir/caplet", (CapletPriceRequest req) =>
{
    var sabr = FromDto(req.SabrParams);
    var spec = new CapletSpec(
        req.ForwardRate, req.Strike, req.OptionExpiry,
        req.PeriodLength, req.DiscountFactor, req.IsCap);
    double vol = req.Convention == VolConvention.Normal
        ? SabrPricer.NormalImpliedVol(sabr, req.ForwardRate + req.Shift, req.Strike + req.Shift, req.OptionExpiry)
        : SabrPricer.ImpliedVol(sabr, req.ForwardRate + req.Shift, req.Strike + req.Shift, req.OptionExpiry);
    double price = CapletPricer.PriceWithVol(spec, vol, req.Convention);
    double vega  = CapletPricer.Vega(spec, sabr, req.Shift, req.Convention);
    double delta = CapletPricer.Delta(spec, sabr, req.Shift, req.Convention);
    return Results.Ok(new CapletPriceResponse(price, vol, delta, vega));
});

// ───────────────── /api/ir/swaption ─────────────────
// Price a European payer or receiver swaption under SABR-derived vol.
app.MapPost("/api/ir/swaption", (SwaptionPriceRequest req) =>
{
    var sabr = FromDto(req.SabrParams);
    var spec = new SwaptionSpec(
        req.ForwardSwapRate, req.Strike, req.OptionExpiry, req.Annuity, req.IsPayer);
    double vol = req.Convention == VolConvention.Normal
        ? SabrPricer.NormalImpliedVol(sabr, req.ForwardSwapRate + req.Shift, req.Strike + req.Shift, req.OptionExpiry)
        : SabrPricer.ImpliedVol(sabr, req.ForwardSwapRate + req.Shift, req.Strike + req.Shift, req.OptionExpiry);
    double price = SwaptionPricer.PriceWithVol(spec, vol, req.Convention);
    double vega  = SwaptionPricer.Vega(spec, sabr, req.Shift, req.Convention);
    double delta = SwaptionPricer.Delta(spec, sabr, req.Shift, req.Convention);
    return Results.Ok(new SwaptionPriceResponse(price, vol, delta, vega));
});

// ───────────────── /api/ir/futures-option ─────────────────
// Price an option on a short-rate futures contract (Eurodollar, SOFR 3M, ICE STIR, etc.).
// Rates and strikes are in decimal (e.g. 0.04 = 4%). Normal vol convention by default.
app.MapPost("/api/ir/futures-option", (IrFuturesPriceRequest req) =>
{
    var sabr = FromDto(req.SabrParams);
    var spec = new IrFuturesOptionSpec(
        req.FuturesRate, req.StrikeRate, req.OptionExpiry,
        req.PeriodFraction, req.NotionalPerContract, req.IsCap);
    double vol = req.Convention == VolConvention.Normal
        ? SabrPricer.NormalImpliedVol(sabr, req.FuturesRate + req.Shift, req.StrikeRate + req.Shift, req.OptionExpiry)
        : SabrPricer.ImpliedVol(sabr, req.FuturesRate + req.Shift, req.StrikeRate + req.Shift, req.OptionExpiry);
    double price = IrFuturesOptionPricer.PriceWithVol(spec, vol, req.Convention);
    double vega  = IrFuturesOptionPricer.Vega(spec, sabr, req.Shift, req.Convention);
    return Results.Ok(new IrFuturesPriceResponse(price, vol, vega));
});

// ───────────────── /api/swaption/surface ─────────────────
// Fetch the swaption vol surface from the US Treasury yield curve + synthetic SABR vol.
// Returns forward swap rates, annuities, and SABR-generated vol smiles per (expiry, tenor) cell.
app.MapPost("/api/swaption/surface", async (SwaptionSurfaceRequest req, ISwaptionDataLoader loader) =>
{
    DateTime? asOf = null;
    if (req.AsOf is { } dateStr && DateTime.TryParse(dateStr, out var d)) asOf = d;

    double? atmOverride = req.AtmNormalVolOverrideBps.HasValue
        ? req.AtmNormalVolOverrideBps.Value / 10000.0
        : null;

    var loadReq = new SwaptionLoadRequest
    {
        AsOf = asOf,
        OptionExpiries = req.OptionExpiries,
        SwapTenors = req.SwapTenors,
        StrikeOffsetsBps = req.StrikeOffsetsBps,
        VolConvention = VolConvention.Normal,
        AtmNormalVolOverride = atmOverride,
        CouponFrequency = req.CouponFrequency,
        ForceSynthetic = req.ForceSynthetic,
    };

    SwaptionMarketData data;
    try
    {
        data = await loader.LoadAsync(loadReq);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503,
            title: "Yield data unavailable");
    }

    var volPts = data.VolSurface.Select(pt => new SwaptionVolPointDto(
        pt.OptionExpiry, pt.SwapTenor,
        pt.ForwardSwapRate, pt.Annuity,
        pt.Strikes, pt.MarketVols,
        pt.Convention.ToString(),
        pt.IsSynthetic,
        pt.SabrFit is { } sf ? ToDto(sf) : null
    )).ToArray();

    return Results.Ok(new SwaptionSurfaceResponse(
        data.AsOf.ToString("yyyy-MM-dd"),
        data.Source,
        data.ParRates.Select(p => p.TenorYears).ToArray(),
        data.ParRates.Select(p => p.ParRate).ToArray(),
        volPts));
});

// ───────────────── /api/db — persistence layer ─────────────────────────────

// Swaption surface: save + list + load
app.MapPost("/api/db/swaption-surface/save", (SwaptionSurfaceResponse body, ICalibrationStore store) =>
{
    var id = store.SaveSwaptionSurface(body);
    return Results.Ok(new { id });
});

app.MapGet("/api/db/swaption-surfaces", (ICalibrationStore store) =>
    Results.Ok(store.ListSwaptionSurfaces()));

app.MapGet("/api/db/swaption-surface/{id:long}", (long id, ICalibrationStore store) =>
{
    try   { var (data, _) = store.LoadSwaptionSurface(id); return Results.Ok(data); }
    catch { return Results.NotFound(); }
});

// SABR calibrations: save + list
app.MapPost("/api/db/sabr-calibrations/{surfaceId:long}", (
    long surfaceId,
    SabrSurfaceCalibrationDbEntry[] body,
    ICalibrationStore store) =>
{
    store.SaveSabrCalibrations(surfaceId, body);
    return Results.Ok(new { saved = body.Length });
});

app.MapGet("/api/db/sabr-calibrations/{surfaceId:long}", (long surfaceId, ICalibrationStore store) =>
    Results.Ok(store.ListSabrCalibrations(surfaceId)));

// Heston surface: save + list + load
app.MapPost("/api/db/heston-surface/save", (SurfaceResponse body, ICalibrationStore store) =>
{
    var id = store.SaveHestonSurface(body);
    return Results.Ok(new { id });
});

app.MapGet("/api/db/heston-surfaces", (ICalibrationStore store) =>
    Results.Ok(store.ListHestonSurfaces()));

app.MapGet("/api/db/heston-surface/{id:long}", (long id, ICalibrationStore store) =>
{
    try   { var (data, _) = store.LoadHestonSurface(id); return Results.Ok(data); }
    catch { return Results.NotFound(); }
});

// Heston calibration: save + list
app.MapPost("/api/db/heston-calibration/{surfaceId:long}", (
    long surfaceId,
    HestonCalibrationDbEntry body,
    ICalibrationStore store) =>
{
    store.SaveHestonCalibration(surfaceId, body);
    return Results.Ok(new { saved = 1 });
});

app.MapGet("/api/db/heston-calibrations", (ICalibrationStore store) =>
    Results.Ok(store.ListHestonCalibrations()));

app.Run();

// ───────────────── helpers ─────────────────
// Defaults rate/dividend to the cached surface's values so the calibrator and the surface
// being fit assume the same forward curve. Mismatched rates lead to a systematic IV bias
// the optimiser cannot fully absorb when fitting one Heston to all expiries simultaneously.
static SabrParams FromDto(SabrParamsDto dto) =>
    new(dto.Alpha, dto.Beta, dto.Rho, dto.Nu);

static SabrParamsDto ToDto(SabrParams p) =>
    new(p.Alpha, p.Beta, p.Rho, p.Nu);

static CalibrationRequest BuildCalibrationRequest(CalibrateApiRequest a, CachedSurface cached) => new()
{
    GlobalMethod = a.GlobalMethod,
    GradientMethod = a.GradientMethod,
    Chain = a.Chain,
    NelderMeadRestarts = a.NelderMeadRestarts,
    GlobalMaxIterations = a.GlobalMaxIterations,
    GradientMaxIterations = a.GradientMaxIterations,
    Kappa = a.Kappa,
    Theta = a.Theta,
    Sigma = a.Sigma,
    Rho = a.Rho,
    V0 = a.V0,
    InitialGuess = a.InitialGuess,
    Spot = cached.Spot,
    RiskFreeRate = a.RiskFreeRate ?? cached.RiskFreeRate,
    DividendYield = a.DividendYield ?? cached.DividendYield,
    Seed = a.Seed
};

