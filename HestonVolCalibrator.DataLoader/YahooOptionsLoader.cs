using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace HestonVolCalibrator.DataLoader
{
    public record OptionQuote(
        double Strike,
        double Maturity,    // years to expiry from today
        double Bid,
        double Ask,
        double LastPrice,
        int OpenInterest,
        int Volume,
        bool IsCall);

    // Fetches SPX options chain from Yahoo Finance using the v7 API with crumb authentication.
    public class YahooOptionsLoader : IDisposable
    {
        private readonly HttpClient _http;
        private string? _crumb;

        public YahooOptionsLoader()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AllowAutoRedirect = true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }

        // Returns (spot, quotes) for SPX.
        public async Task<(double spot, List<OptionQuote> quotes)> LoadSpxAsync(int maxExpiries = 6)
        {
            await EnsureCrumbAsync();

            string symbol = "%5ESPX"; // ^SPX URL-encoded
            string baseUrl = $"https://query1.finance.yahoo.com/v7/finance/options/{symbol}";
            string crumbParam = _crumb is not null ? $"&crumb={Uri.EscapeDataString(_crumb)}" : "";

            JsonDocument? baseDoc = await FetchJsonAsync(baseUrl + "?" + crumbParam.TrimStart('&'));
            if (baseDoc is null)
                throw new Exception("Failed to fetch SPX options page after crumb authentication.");

            double spot = ParseSpot(baseDoc);
            long[] expiries = ParseExpiryDates(baseDoc);
            Console.WriteLine($"  SPX spot: {spot:F2}");
            Console.WriteLine($"  Available expiries: {expiries.Length}  (fetching first {Math.Min(maxExpiries, expiries.Length)})");

            var allQuotes = new List<OptionQuote>();
            DateTime today = DateTime.UtcNow.Date;

            int count = 0;
            foreach (long ts in expiries)
            {
                if (count++ >= maxExpiries) break;

                DateTime expDate = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.Date;
                double t = Math.Max((expDate - today).TotalDays / 365.25, 1.0 / 365.25);

                string url = $"{baseUrl}?date={ts}{crumbParam}";
                JsonDocument? doc = await FetchJsonAsync(url);
                if (doc is null) { Console.WriteLine($"  Skipping {expDate:yyyy-MM-dd}: fetch failed."); continue; }

                int n = ParseOptions(doc, t, allQuotes);
                Console.WriteLine($"  {expDate:yyyy-MM-dd}  T={t:F4}y  loaded {n} quotes");

                await Task.Delay(300);
            }

            return (spot, allQuotes);
        }

        private async Task EnsureCrumbAsync()
        {
            if (_crumb is not null) return;
            try
            {
                try
                {
                    using var fcReq = new HttpRequestMessage(HttpMethod.Get, "https://fc.yahoo.com/");
                    fcReq.Headers.Accept.Clear();
                    fcReq.Headers.Accept.ParseAdd("*/*");
                    using var fcResp = await _http.SendAsync(fcReq);
                }
                catch { }

                try
                {
                    using var homeReq = new HttpRequestMessage(HttpMethod.Get, "https://finance.yahoo.com/");
                    homeReq.Headers.Accept.Clear();
                    homeReq.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    using var homeResp = await _http.SendAsync(homeReq);
                }
                catch { }

                await Task.Delay(200);

                using var crumbReq = new HttpRequestMessage(HttpMethod.Get,
                    "https://query1.finance.yahoo.com/v1/test/getcrumb");
                crumbReq.Headers.Accept.Clear();
                crumbReq.Headers.Accept.ParseAdd("*/*");
                crumbReq.Headers.Referrer = new Uri("https://finance.yahoo.com/");
                using var crumbResp = await _http.SendAsync(crumbReq);
                crumbResp.EnsureSuccessStatusCode();
                string crumb = await crumbResp.Content.ReadAsStringAsync();

                if (!string.IsNullOrWhiteSpace(crumb) && crumb.Length < 50)
                {
                    _crumb = crumb.Trim();
                    Console.WriteLine($"  Obtained Yahoo Finance crumb: {_crumb}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Crumb fetch warning: {ex.Message} - proceeding without crumb.");
            }
            finally
            {
                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                if (!_http.DefaultRequestHeaders.Contains("Referer"))
                    _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://finance.yahoo.com/");
            }
        }

        private static double ParseSpot(JsonDocument doc)
        {
            try
            {
                return doc.RootElement
                    .GetProperty("optionChain").GetProperty("result")[0]
                    .GetProperty("quote").GetProperty("regularMarketPrice").GetDouble();
            }
            catch { return 0.0; }
        }

        private static long[] ParseExpiryDates(JsonDocument doc)
        {
            try
            {
                var arr = doc.RootElement
                    .GetProperty("optionChain").GetProperty("result")[0]
                    .GetProperty("expirationDates");
                var list = new List<long>();
                foreach (var el in arr.EnumerateArray())
                    list.Add(el.GetInt64());
                return list.ToArray();
            }
            catch { return Array.Empty<long>(); }
        }

        private static int ParseOptions(JsonDocument doc, double t, List<OptionQuote> output)
        {
            int n = 0;
            try
            {
                var optSection = doc.RootElement
                    .GetProperty("optionChain").GetProperty("result")[0]
                    .GetProperty("options")[0];

                foreach (bool isCall in new[] { true, false })
                {
                    string key = isCall ? "calls" : "puts";
                    if (!optSection.TryGetProperty(key, out var contracts)) continue;

                    foreach (var c in contracts.EnumerateArray())
                    {
                        if (!TryParseContract(c, t, isCall, out var q)) continue;
                        output.Add(q!);
                        n++;
                    }
                }
            }
            catch { /* malformed JSON – skip expiry */ }
            return n;
        }

        private static bool TryParseContract(JsonElement el, double t, bool isCall, out OptionQuote? q)
        {
            q = null;
            try
            {
                double strike = el.GetProperty("strike").GetDouble();
                double bid    = el.TryGetProperty("bid",        out var b)   ? b.GetDouble()   : 0.0;
                double ask    = el.TryGetProperty("ask",        out var a)   ? a.GetDouble()   : 0.0;
                double last   = el.TryGetProperty("lastPrice",  out var lp)  ? lp.GetDouble()  : 0.0;
                int    oi     = el.TryGetProperty("openInterest", out var oe) ? oe.GetInt32()   : 0;
                int    vol    = el.TryGetProperty("volume",     out var ve)  ? ve.GetInt32()   : 0;

                if (bid <= 0 || ask <= 0 || ask < bid) return false;

                q = new OptionQuote(strike, t, bid, ask, last, oi, vol, isCall);
                return true;
            }
            catch { return false; }
        }

        private async Task<JsonDocument?> FetchJsonAsync(string url)
        {
            try
            {
                string json = await _http.GetStringAsync(url);
                return JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  HTTP error: {ex.Message}");
                return null;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
