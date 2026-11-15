using System.Globalization;
using System.Text.Json;
using QuantLib;

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
        DateTime valuationDate = new DateTime(2026, 11, 2);
        return await BuildSofrCurveAsync(valuationDate);
    }

    private static async Task<int> BuildSofrCurveAsync(DateTime valuationDate)
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
            
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"CurveGuard: {error.Message}");
            return 1;
        }
    }
}
