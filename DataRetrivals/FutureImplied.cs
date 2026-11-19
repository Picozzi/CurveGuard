using NodaTime;
using YahooQuotesApi;

public class FutureImpliedRate
{
    private static readonly char[] MonthCodes = { 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'Q', 'U', 'V', 'X', 'Z' };

    public sealed class ZqImpliedRate
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public decimal RatePercent { get; init; }
        public string? Ticker { get; init; }
        public DateOnly? CloseDate { get; init; }

        public ZqImpliedRate(int year, int month, decimal ratePercent,
            string? ticker = null, DateOnly? closeDate = null)
        {
            Year = year;
            Month = month;
            RatePercent = ratePercent;
            Ticker = ticker;
            CloseDate = closeDate;
        }
    }

    public async Task<List<ZqImpliedRate>> GetFuturesQuotesAsync(DateTime effectiveDate)
    {
        List<(DateTime Date, string Ticker)> tickers = new List<(DateTime Date, string Ticker)>();
        
        for (int i = 0; i < 14; i++)
        {
            var dt = effectiveDate.AddMonths(i);
            string ticker = $"ZQ{MonthCodes[dt.Month - 1]}{dt.Year % 100:D2}.CBT";
            tickers.Add((dt, ticker));
        }

        var valuationDate = LocalDate.FromDateTime(effectiveDate);
        var firstDate = valuationDate.PlusDays(-7);
        var chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];
        YahooQuotes yahooQuotes = new YahooQuotesBuilder()
            .WithHistoryStartDate(firstDate.AtStartOfDayInZone(chicago).ToInstant())
            .WithHistoryInterval("1d")
            .Build();

        var histories = await yahooQuotes.GetHistoryAsync(tickers.Select(t => t.Ticker));
        var quotes = new List<ZqImpliedRate>(tickers.Count);
        foreach (var ticker in tickers)
        {
            if (!histories.TryGetValue(ticker.Ticker, out var result) || !result.HasValue)
            {
                var reason = result.HasError ? result.Error.Message : "no history returned";
                Console.Error.WriteLine($"Yahoo Finance: {ticker.Ticker} history unavailable ({reason}).");
                continue;
            }

            var history = result.Value;
            var exchangeZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(history.ExchangeTimezoneName) ?? chicago;
            var close = history.Ticks
                .Select(tick => (Date: tick.Date.InZone(exchangeZone).Date, Price: tick.Close))
                .Where(tick => tick.Date >= firstDate && tick.Date <= valuationDate &&
                    double.IsFinite(tick.Price) && tick.Price > 0)
                .OrderBy(tick => tick.Date)
                .LastOrDefault();

            if (close.Price == 0)
            {
                Console.Error.WriteLine($"Yahoo Finance: {ticker.Ticker} has no daily close from {firstDate:yyyy-MM-dd} through {valuationDate:yyyy-MM-dd}.");
                continue;
            }

            var closeDate = new DateOnly(close.Date.Year, close.Date.Month, close.Date.Day);
            quotes.Add(new ZqImpliedRate(ticker.Date.Year, ticker.Date.Month, 100m - (decimal)close.Price,
                ticker.Ticker, closeDate));
        }

        return quotes;
    }
}
