using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard
{
    public class OvernightSOFR
    {
        public class SOFRFixing
        {
            public DateOnly EffectiveDate { get; init; }
            public decimal RatePercent { get; init; }
            public decimal RateDecimal => RatePercent / 100m;

            public SOFRFixing(DateOnly effectiveDate, decimal ratePercent)
            {
                EffectiveDate = effectiveDate;
                RatePercent = ratePercent;
            }
        }
        
        private DataRetrieval _dataRetrieval;
        private const string SearchEndpoint = "https://markets.newyorkfed.org/api/rates/secured/sofr/search.json";
        private record SofrResponse([property: JsonPropertyName("refRates")] SofrApiRate?[]? Rates);
        private record SofrApiRate(DateOnly? EffectiveDate, string? Type, decimal? PercentRate);
        public OvernightSOFR()
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

        public async Task<SOFRFixing> GetSOFRFixingAsync(DateOnly effectiveDate)
        {
            var date = effectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = new Uri($"{SearchEndpoint}?startDate={date}&endDate={date}");
            var payload = await _dataRetrieval.GetJsonAsync<SofrResponse>(url);
            if (payload.Rates is not { } rates)
                throw new JsonException("New York Fed SOFR response is missing refRates");
            if (rates.Length == 0)
                throw new KeyNotFoundException($"No New York Fed SOFR fixing for {date}");

            if (rates is not [{ Type: "SOFR", EffectiveDate: { } dateOfFixing, PercentRate: { } rate }] ||
                dateOfFixing != effectiveDate)
            {
                throw new JsonException($"New York Fed response did not contain the SOFR fixing for {date}");
            }

            return new SOFRFixing(dateOfFixing, rate);
        }
    }
}
