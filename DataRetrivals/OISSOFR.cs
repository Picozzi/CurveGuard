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
        public record OisApiQuote(string? Tenor = null, decimal? Rate = null, DateTime? Date = null,
            string? Source = null, string? Method = null, int? Trades = null, int? CloseTrades = null);
        
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

            if (payload is not { Currency: "USD", Date: { } observationDate, Curve: { } rates })
                throw new JsonException($"expected a dated USD OIS snapshot for {date}");

            var quotes = rates.Select(row => row ?? throw new JsonException($"null OIS quote for {date}")).ToArray();
            return new OISSwapSnapshot(observationDate.ToDateTime(TimeOnly.MinValue), "CheckMySwap", url.AbsoluteUri,
                payload.FetchedAt, Array.AsReadOnly(quotes));
        }
    }
}
