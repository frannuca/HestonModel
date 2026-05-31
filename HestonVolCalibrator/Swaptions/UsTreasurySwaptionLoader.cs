using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HestonVolCalibrator.SABR;

namespace HestonVolCalibrator.Swaptions
{
    // Loads a swaption market snapshot using the St. Louis Fed FRED multi-series CSV API.
    // One request fetches all Treasury par-yield tenors; no API key required.
    //
    // URL: https://fred.stlouisfed.org/graph/fredgraph.csv?id=DGS1MO,...&cosd=...&coed=...
    //
    // When ForceSynthetic = true the network call is skipped and a hardcoded
    // typical 2025 UST curve is used to construct synthetic vols immediately.
    public sealed class UsTreasurySwaptionLoader : ISwaptionDataLoader, IDisposable
    {
        private const string FredBase = "https://fred.stlouisfed.org/graph/fredgraph.csv";

        // FRED series IDs in column order; must match YieldTenors below.
        private const string FredSeriesIds =
            "DGS1MO,DGS3MO,DGS6MO,DGS1,DGS2,DGS3,DGS5,DGS7,DGS10,DGS20,DGS30";

        private static readonly double[] YieldTenors =
        [
            1.0 / 12.0,
            3.0 / 12.0,
            6.0 / 12.0,
            1.0, 2.0, 3.0, 5.0, 7.0, 10.0, 20.0, 30.0,
        ];

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        public UsTreasurySwaptionLoader(HttpClient? httpClient = null)
        {
            if (httpClient is null)
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                _http.DefaultRequestHeaders.Add("User-Agent", "Wget/1.21");
                _ownsHttp = true;
            }
            else
            {
                _http = httpClient;
                _ownsHttp = false;
            }
        }

        public async Task<SwaptionMarketData> LoadAsync(
            SwaptionLoadRequest request, CancellationToken ct = default)
        {
            DateTime asOf = request.AsOf ?? DateTime.Today;
            ParRatePoint[]? parRates = null;
            string source;

            if (!request.ForceSynthetic)
            {
                parRates = await FetchFredYieldsAsync(asOf, ct);
                if (parRates is not { Length: > 0 })
                    throw new InvalidOperationException(
                        $"Could not retrieve Treasury yield data from FRED for {asOf:yyyy-MM-dd}. " +
                        "Check your internet connection or enable 'Use synthetic data'.");
                source = "FRED (St. Louis Fed)";
            }
            else
            {
                parRates = TypicalUstCurve();
                source = "Synthetic vol (hardcoded)";
            }

            DiscountPoint[] discountCurve = DiscountCurveBootstrap.Bootstrap(parRates, request.CouponFrequency);
            var volSurface = BuildVolSurface(request, discountCurve, parRates);

            return new SwaptionMarketData(asOf, source, parRates, discountCurve, volSurface);
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }

        // FRED series IDs as an array, parallel with YieldTenors.
        private static readonly string[] FredSeriesArray =
            FredSeriesIds.Split(',');

        // Last successful fetch — returned as fallback when a new fetch fails.
        private static ParRatePoint[]? _cachedRates;
        private static DateTime _cacheDate = DateTime.MinValue;

        // ── FRED parallel single-series fetch ────────────────────────────────────
        // Fires all 11 series in parallel with a short per-request timeout so the
        // whole call completes in ≤ 7 s even when FRED is throttling.
        // Any successful fetch is cached; a stale cache (≤ 7 days old) is used
        // as a fallback when fewer than 4 series come back.

        private async Task<ParRatePoint[]?> FetchFredYieldsAsync(DateTime asOf, CancellationToken ct)
        {
            string coed = asOf.ToString("yyyy-MM-dd");
            string cosd = asOf.AddDays(-14).ToString("yyyy-MM-dd");

            // Short-circuit timeout so we don't block the caller for 15 s per failed series.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));

            var tasks = FredSeriesArray
                .Select(id => FetchOneFredSeriesAsync(id, cosd, coed, cts.Token))
                .ToArray();

            double?[] results;
            try { results = await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { results = tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : null).ToArray(); }

            var points = new List<ParRatePoint>();
            for (int i = 0; i < results.Length; i++)
                if (results[i] is double rate)
                    points.Add(new ParRatePoint(YieldTenors[i], rate));

            if (points.Count >= 4)
            {
                _cachedRates = points.ToArray();
                _cacheDate   = asOf;
                return _cachedRates;
            }

            // Stale-cache fallback: reuse rates from a recent successful fetch.
            if (_cachedRates is { Length: >= 4 } && (asOf - _cacheDate).TotalDays <= 7)
                return _cachedRates;

            return null;
        }

        // Fetches one FRED series CSV and returns the most recent non-missing
        // value in [cosd, coed], or null on any error or non-2xx response.
        private async Task<double?> FetchOneFredSeriesAsync(
            string seriesId, string cosd, string coed, CancellationToken ct)
        {
            string url = $"{FredBase}?id={seriesId}&cosd={cosd}&coed={coed}";
            string csv;
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                csv = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch { return null; }

            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 1; i--)
            {
                var cols = lines[i].Split(',');
                if (cols.Length < 2) continue;
                string val = cols[1].Trim();
                if (!string.IsNullOrEmpty(val)
                    && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)
                    && rate > 0)
                {
                    return rate / 100.0;
                }
            }
            return null;
        }

        // Hardcoded fallback (approximate 2025 US levels).
        private static ParRatePoint[] TypicalUstCurve() =>
        [
            new(1.0 / 12.0, 0.0530),
            new(3.0 / 12.0, 0.0530),
            new(6.0 / 12.0, 0.0525),
            new(1.0,         0.0515),
            new(2.0,         0.0490),
            new(3.0,         0.0475),
            new(5.0,         0.0460),
            new(7.0,         0.0455),
            new(10.0,        0.0450),
            new(20.0,        0.0470),
            new(30.0,        0.0460),
        ];

        // ── Synthetic vol surface ──────────────────────────────────────────────────

        private static SwaptionVolPoint[] BuildVolSurface(
            SwaptionLoadRequest request,
            DiscountPoint[] curve,
            ParRatePoint[] parRates)
        {
            var result = new List<SwaptionVolPoint>();

            foreach (double optionExpiry in request.OptionExpiries)
            {
                foreach (double swapTenor in request.SwapTenors)
                {
                    double tEnd = optionExpiry + swapTenor;
                    if (tEnd > curve[^1].MaturityYears + 0.01) continue;

                    var (fwdSwapRate, annuity) = DiscountCurveBootstrap.ForwardSwapRate(
                        curve, optionExpiry, swapTenor, request.CouponFrequency);

                    if (fwdSwapRate <= 0 || annuity <= 0) continue;

                    double atmNormalVol = request.AtmNormalVolOverride
                        ?? EstimateAtmNormalVol(fwdSwapRate, optionExpiry, swapTenor);

                    SabrParams sabr = request.SyntheticSabrOverride
                        ?? BuildSyntheticSabr(atmNormalVol, fwdSwapRate, optionExpiry);

                    double[] strikes = request.StrikeOffsetsBps
                        .Select(bps => Math.Max(fwdSwapRate + bps / 10000.0, 1e-4))
                        .ToArray();

                    double[] vols = strikes.Select(k =>
                    {
                        double v = SabrPricer.NormalImpliedVol(sabr, fwdSwapRate, k, optionExpiry);
                        return double.IsNaN(v) || v <= 0 ? atmNormalVol : v;
                    }).ToArray();

                    result.Add(new SwaptionVolPoint(
                        optionExpiry, swapTenor,
                        fwdSwapRate, annuity,
                        strikes, vols,
                        VolConvention.Normal,
                        IsSynthetic: true,
                        SabrFit: sabr));
                }
            }

            return result.ToArray();
        }

        private static double EstimateAtmNormalVol(double fwdRate, double optionExpiry, double swapTenor)
        {
            const double baseBps = 100.0;
            double rateFactor = Math.Sqrt(Math.Max(fwdRate / 0.045, 0.2));
            double tenorFactor = 1.0 + 0.02 * swapTenor;
            double expiryFactor = Math.Sqrt(optionExpiry);
            return baseBps / 10000.0 * rateFactor * tenorFactor * expiryFactor;
        }

        private static SabrParams BuildSyntheticSabr(double atmNormalVol, double fwdRate, double optionExpiry)
        {
            const double rho = -0.30;
            const double nu  =  0.40;
            double correction = 1.0 + (2.0 - 3.0 * rho * rho) * nu * nu / 24.0 * optionExpiry;
            double alpha = atmNormalVol / Math.Max(correction, 0.5);
            return new SabrParams(alpha, 0.0, rho, nu);
        }
    }
}
