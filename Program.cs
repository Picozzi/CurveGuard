using System.Text.Json;

namespace CurveGuard
{
    public static class Program
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            RespectNullableAnnotations = true,
            WriteIndented = true,
        };

        public static async Task<int> Main(string[] args)
        {
            var valuationDate = new DateTime(2026, 11, 2);
            return await Run(valuationDate);
        }

        private static async Task<int> Run(DateTime valuationDate)
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

                var fixingDate = SOFRCurveCreation.PreviousFixingDate(valuationDateOnly);
                
                var fixingTask = new OvernightSOFR().GetSOFRFixingAsync(fixingDate);
                var swapsTask = new OISSOFR().GetOisSwapRatesAsync(valuationDate);
                var effrTask = new EFFRCalculator().GetEffrFixingAsync(fixingDate);
                var futuresTask = new FutureImpliedRate().GetFuturesQuotesAsync(valuationDate);
                await Task.WhenAll(fixingTask, swapsTask, effrTask, futuresTask);

                DateTime handoff = valuationDate.AddYears(1); //this is the split between the forward flat curve and the OIS curve. The FOMC rate predictions are used for the front end, and the OIS curve is used for the back end. The handoff is one year from the valuation date.
                
                var FOMCMeeting = new FOMCCalculator(valuationDate).LoadFOMCRateProbabilitiesAsync(handoff, effrTask.Result, futuresTask.Result);
                
                var inputs = new CurveInputs(valuationDateOnly, fixingTask.Result, effrTask.Result, swapsTask.Result, FOMCMeeting.Result, futuresTask.Result);
             
                var curve = SOFRCurveCreation.Build(inputs);

                return 1;
                // var reportDirectory = ProjectPaths.ReportDirectory;
                // Directory.CreateDirectory(reportDirectory);
                // var reportPath = Path.Combine(reportDirectory, $"sofr-quarantine-{valuationDateOnly:yyyy-MM-dd}.json");
                // if (run.Curve is { } curve)
                // {
                //     var chart = CurveChart.Save(JsonSerializer.SerializeToElement(curve, JsonOptions));
                //     reportPath = Path.ChangeExtension(chart, ".json");
                // }

                // await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(run, JsonOptions) + Environment.NewLine);
                // Console.WriteLine($"{run.Validation.Decision}: saved {reportPath}");
                // return run.Curve is null ? 1 : 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"CurveGuard: {error.Message}");
                return 1;
            }
        }
    }

    public sealed record CurveInputs
    (
        DateOnly ValuationDate,
        OvernightSOFR.SOFRFixing SofrFixing,
        EFFRCalculator.EFFRFixing EffrFixing,
        OISSOFR.OISSwapSnapshot OisSnapshot,
        List<FOMCCalculator.MeetingProbability> FomcDecisions,
        List<FutureImpliedRate.ZqImpliedRate> ZqImpliedRates
    );
}