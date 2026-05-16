using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HestonVolCalibrator.Calibration;
using HestonVolCalibrator.Implementations;
using HestonVolCalibrator.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

app.Run();

// ───────────────── helpers ─────────────────
// Defaults rate/dividend to the cached surface's values so the calibrator and the surface
// being fit assume the same forward curve. Mismatched rates lead to a systematic IV bias
// the optimiser cannot fully absorb when fitting one Heston to all expiries simultaneously.
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

