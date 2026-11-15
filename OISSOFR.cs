using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard
{
    public class OISSOFR
    {
        public class OISSwapSnapshot
        {
            public DateOnly ObservationDate { get; init; }
            public string Source { get; init; }
            public string Url { get; init; }
            public string? ProviderCollectionTime { get; init; }
            public IReadOnlyList<OisApiQuote> Quotes { get; init; }

            public OISSwapSnapshot(DateOnly observationDate, string source, string url,
                string? providerCollectionTime, IReadOnlyList<OisApiQuote> quotes)
            {
                ObservationDate = observationDate;
                Source = source;
                Url = url;
                ProviderCollectionTime = providerCollectionTime;
                Quotes = quotes;
            }
        }
        
        private DataRetrieval _dataRetrieval;
        private static readonly Uri url = new("https://checkmyswap.com/api/curves/USD");     
        private record OisResponse(string? Currency, DateOnly? Date, OisApiQuote?[]? Curve, string? FetchedAt);
        public record OisApiQuote(string? Tenor, decimal? Rate, DateOnly? Date, string? Source, string? Method, int? Trades, int? CloseTrades);
        
        public OISSOFR()
        {
            HttpClient httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = 256 * 1024,
            };

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CurveGuard/1.0");

            JsonSerializerOptions OisJsonOptions = new(JsonSerializerDefaults.Web)
            {
                NumberHandling = JsonNumberHandling.Strict,
            };

            _dataRetrieval = new DataRetrieval(httpClient, OisJsonOptions);
        }

        public async Task<OISSwapSnapshot> GetOisSwapRatesAsync(DateOnly effectiveDate)
        {
            var date = effectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            
            var payload = await _dataRetrieval.GetJsonAsync<OisResponse>(url);
         
            if (payload is not { Currency: "USD", Curve: { Length: > 0 } rates } || payload.Date != effectiveDate)
                throw new JsonException($"expected a nonempty USD OIS curve for {date}");

            var quotes = new List<OisApiQuote>(rates.Length);
            var tenors = new HashSet<string>();
            foreach (var row in rates)
            {
                if (row is not { Tenor: { } tenor, Rate: { } rate, Source: { } source, Method: { } method } ||
                    row.Date != effectiveDate || string.IsNullOrWhiteSpace(tenor) ||
                    string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(method) ||
                    row.Trades is < 0 || row.CloseTrades is < 0 || !tenors.Add(tenor))
                {
                    throw new JsonException($"invalid or duplicate OIS quote for {row?.Tenor ?? "unknown tenor"} on {date}");
                }

                quotes.Add(new OisApiQuote(tenor, rate, effectiveDate, source, method, row.Trades, row.CloseTrades));
            }

            return new OISSwapSnapshot(effectiveDate, "CheckMySwap", url.AbsoluteUri, payload.FetchedAt,
                quotes.AsReadOnly());
        }
    }
}