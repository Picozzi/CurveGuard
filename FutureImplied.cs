using NodaTime;
using YahooQuotesApi;

public class FutureImpliedRate
{
    private readonly char[] MonthCodes = { 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'Q', 'U', 'V', 'X', 'Z' };
    public Dictionary<(int Year, int Month), decimal> futuresImpliedRates = new Dictionary<(int Year, int Month), decimal>();
    private readonly DateTime effectiveDate;
    public FutureImpliedRate(DateTime effectiveDate)
    {
        this.effectiveDate = effectiveDate;
    }

    public async Task GetFuturesImpliedRates()
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
                .LastOrDefault(tick => tick.Date >= firstDate && tick.Date <= valuationDate &&
                    double.IsFinite(tick.Price) && tick.Price > 0);

            if (close.Price == 0)
            {
                Console.Error.WriteLine($"Yahoo Finance: {ticker.Ticker} has no daily close from {firstDate:yyyy-MM-dd} through {valuationDate:yyyy-MM-dd}.");
                continue;
            }

            if (close.Date != valuationDate)
                Console.Error.WriteLine($"Yahoo Finance: using {ticker.Ticker} close from {close.Date:yyyy-MM-dd} for {valuationDate:yyyy-MM-dd}.");

            futuresImpliedRates[(ticker.Date.Year, ticker.Date.Month)] = 100m - (decimal)close.Price;
        }

        if (futuresImpliedRates.Count == 0)
            throw new InvalidOperationException($"No Yahoo Finance ZQ daily closes available for {valuationDate:yyyy-MM-dd} or the prior seven days.");
    }
}
