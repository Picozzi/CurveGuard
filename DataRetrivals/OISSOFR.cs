using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard
{
    public class OISSOFR
    {
        public class OISSwapSnapshot
        {
            public DateTime ObservationDate { get; init; }
            public string Source { get; init; }
            public string Url { get; init; }
            public string? ProviderCollectionTime { get; init; }
            public IReadOnlyList<OisApiQuote> Quotes { get; init; }

            public OISSwapSnapshot(DateTime observationDate, string source, string url,
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
        private const string ArchiveEndpoint = "https://checkmyswap.com/api/curves/USD";
        private record OisResponse(string? Currency, DateOnly? Date, OisApiQuote?[]? Curve, string? FetchedAt);
        public record OisApiQuote(string? Tenor, decimal? Rate, DateTime? Date, string? Source, string? Method, int? Trades, int? CloseTrades);
        
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

        public async Task<OISSwapSnapshot> GetOisSwapRatesAsync(DateTime effectiveDate)
        {
            var expectedDate = DateOnly.FromDateTime(effectiveDate);
            var date = expectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = new Uri($"{ArchiveEndpoint}?date={date}");

            OisResponse payload;
            try
            {
                payload = await _dataRetrieval.GetJsonAsync<OisResponse>(url);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                throw new KeyNotFoundException($"No CheckMySwap USD OIS snapshot for {date}", error);
            }

            if (payload is not { Currency: "USD", Curve: { Length: > 0 } rates } || payload.Date != expectedDate)
                throw new JsonException($"expected a nonempty USD OIS curve for {date}");

            var quotes = new List<OisApiQuote>(rates.Length);
            var tenors = new HashSet<string>();
            foreach (var row in rates)
            {
                if (row is not { Tenor: { } tenor, Rate: not null, Date: { } quoteDate, Source: { } source, Method: { } method } ||
                    DateOnly.FromDateTime(quoteDate) != expectedDate || string.IsNullOrWhiteSpace(tenor) ||
                    string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(method) ||
                    row.Trades is < 0 || row.CloseTrades is < 0 || !tenors.Add(tenor))
                {
                    throw new JsonException($"invalid or duplicate OIS quote for {row?.Tenor ?? "unknown tenor"} on {date}");
                }

                quotes.Add(row);
            }

            return new OISSwapSnapshot(expectedDate.ToDateTime(TimeOnly.MinValue), "CheckMySwap", url.AbsoluteUri, payload.FetchedAt,
                quotes.AsReadOnly());
        }
    }
}
