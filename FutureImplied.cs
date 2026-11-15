using System.Threading.Tasks;
using YahooQuotesApi;

public class FutureImpliedRate
{
    private readonly char[] MonthCodes = { 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'Q', 'U', 'V', 'X', 'Z' };
    public Dictionary<(int Year, int Month), decimal> futuresImpliedRates = new Dictionary<(int Year, int Month), decimal>();
    private DateTime effectiveDate;
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

        YahooQuotes yahooQuotes = new YahooQuotesBuilder().Build();
        Dictionary<string, Snapshot?> snapshots = await yahooQuotes.GetSnapshotAsync(tickers.Select(t => t.Ticker).ToList());
        
        foreach (var ticker in tickers)
        {
            if (snapshots.TryGetValue(ticker.Ticker, out Snapshot? snap) && snap != null)
            {
                decimal price = snap.RegularMarketPrice;

                if(price != 0)
                {
                    decimal impliedRate = 100.0m - price;
                    futuresImpliedRates[(ticker.Date.Year, ticker.Date.Month)] = impliedRate;
                }
            }
        }
    }
}
