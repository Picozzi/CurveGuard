using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard
{
    public class EFFRCalculator
    {
        public class EFFRFixing
        {
            public DateTime Date {get; set;}
            public double Rate { get; set; }
            public double TargetLower { get; set; }
            public double TargetUpper { get; set; }

            public EFFRFixing(DateTime date, double rate, double targetLower, double targetUpper)
            {
                this.Date = date;
                this.Rate = rate;
                this.TargetLower = targetLower;
                this.TargetUpper = targetUpper;
            }
        }
        
        private DataRetrieval _dataRetrieval;
        private const string EffrEndpoint = "https://markets.newyorkfed.org/api/rates/unsecured/effr";
        private record EffrResponse(EffrRate?[]? RefRates);
        private record EffrRate(DateOnly? EffectiveDate, string? Type, double? PercentRate, double? TargetRateFrom, double? TargetRateTo);
        
        public EFFRCalculator()
        {
            HttpClient httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = 256 * 1024,
            };

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CurveGuard/1.0");

            JsonSerializerOptions effrJsonOptions = new(JsonSerializerDefaults.Web)
            {
                NumberHandling = JsonNumberHandling.Strict,
            };

            _dataRetrieval = new DataRetrieval(httpClient, effrJsonOptions);
        }
        
        public Task<EFFRFixing> GetLatestEffrAsync() => FetchEffrAsync(new Uri($"{EffrEndpoint}/last/1.json"));

        public Task<EFFRFixing> GetEffrFixingAsync(DateTime effectiveDate)
        {
            var expectedDate = DateOnly.FromDateTime(effectiveDate);
            var date = expectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = new Uri($"{EffrEndpoint}/search.json?startDate={date}&endDate={date}");
            return FetchEffrAsync(url, expectedDate);
        }

        private async Task<EFFRFixing> FetchEffrAsync(Uri url, DateOnly? effectiveDate = null)
        {
            var response = await _dataRetrieval.GetJsonAsync<EffrResponse>(url);
            if (response.RefRates is { Length: 0 } && effectiveDate is { } requestedDate)
                throw new KeyNotFoundException($"No New York Fed EFFR fixing for {requestedDate:yyyy-MM-dd}");

            if (response.RefRates is not [{ Type: "EFFR", EffectiveDate: { } date, PercentRate: { } rate,
                    TargetRateFrom: { } lower, TargetRateTo: { } upper }] ||
                !double.IsFinite(rate) || !double.IsFinite(lower) || !double.IsFinite(upper) || lower > upper ||
                (effectiveDate is { } expectedDate && date != expectedDate))
            {
                throw new JsonException("New York Fed response did not contain one EFFR fixing with a valid target range.");
            }

            return new EFFRFixing(date.ToDateTime(TimeOnly.MinValue), rate, lower, upper);
        }
    }
}
