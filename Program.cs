using System.Globalization;
using System.Text.Json;

namespace CurveGuard;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--chart-from", var reportPath])
        {
            try
            {
                using var stream = File.OpenRead(reportPath);
                using var report = await JsonDocument.ParseAsync(stream);
                Console.WriteLine(CurveChart.Save(report.RootElement));
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine($"CurveGuard: {error.Message}");
                return 1;
            }
        }

        if (args is ["--sofr-curve", var date] && DateTime.TryParseExact(date, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var valuationDate))
            return await BuildSofrCurveAsync(valuationDate, writeJson: true);

        if (args.Length == 0)
            return await BuildSofrCurveAsync(new DateTime(2026, 11, 2), writeJson: false);

        Console.Error.WriteLine("Usage: dotnet run -- [--sofr-curve YYYY-MM-DD | --chart-from REPORT.json]");
        return 2;
    }

    private static async Task<int> BuildSofrCurveAsync(DateTime valuationDate, bool writeJson)
    {
        try
        {
            /*
            Note from MP:
            We use T−1 business day because the New York Fed publishes the fixing the next business morning, around 8 a.m. ET for SOFR and 9 a.m. ET for EFFR. Using a fixing dated T in a replay as of T would give the model information that only became available on T+1.
            
            For Example:
            For the November 13 valuation, CurveGuard uses the November 12 fixings to anchor overnight SOFR and calculate the SOFR–EFFR spread. The curve itself still starts on November 13, and the OIS and ZQ inputs are selected as of that valuation date.
            */

            //QuantLib requires a DateOnly for the valuation date.
            DateOnly valuationDateOnly = DateOnly.FromDateTime(valuationDate);

            var fixingDate = SOFRCurve.PreviousFixingDate(valuationDateOnly);
            
            var fixingTask = new OvernightSOFR().GetSOFRFixingAsync(fixingDate);
            var swapsTask = new OISSOFR().GetOisSwapRatesAsync(valuationDate);
            var effrTask = new EFFRCalculator().GetEffrFixingAsync(fixingDate);
            var futuresTask = new FutureImpliedRate().GetFuturesImpliedRates(valuationDate);
            await Task.WhenAll(fixingTask, swapsTask, effrTask, futuresTask);

            DateTime handoff = valuationDate.AddYears(1); //this is the split between the forward flat curve and the OIS curve. The FOMC rate predictions are used for the front end, and the OIS curve is used for the back end. The handoff is one year from the valuation date.
            
            var decisions = await new FOMCCalculator(valuationDate).LoadFrontEndAsync(handoff, effrTask.Result, futuresTask.Result);
            
            var curve = SOFRCurve.Build(valuationDateOnly, fixingTask.Result, swapsTask.Result, effrTask.Result, decisions);
            var report = JsonSerializer.SerializeToElement(curve, JsonOptions);
            var chart = CurveChart.Save(report);
            if (writeJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                Console.Error.WriteLine($"Chart saved to {chart}");
            }
            else
            {
                Console.WriteLine(chart);
            }

            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"CurveGuard: {error.Message}");
            return 1;
        }
    }
}
