using System.Diagnostics;
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

        public static async Task Main(string[] args)
        {
            // if (args is ["--diagnose", ..])
            //     await RunDiagnosticAsync(args);

            var valuationDate = new DateTime(2026, 11, 19);
            await Run(valuationDate);
        }

        private static async Task<int> RunDiagnosticAsync(string[] args)
        {
            if (args is not ["--diagnose", "stale-5y"] and not ["--diagnose", "stale-5y", "--stub"])
            {
                Console.Error.WriteLine("Usage: dotnet run -- --diagnose stale-5y [--stub]");
                return 2;
            }

            try
            {
                var root = ProjectPaths.RootDirectory
                    ?? throw new DirectoryNotFoundException("Could not find the CurveGuard project root.");
                using var fixture = File.OpenRead(Path.Combine(root, "examples", "stale-5y.json"));
                var saved = await JsonSerializer.DeserializeAsync<DiagnosticFixture>(fixture, JsonOptions)
                    ?? throw new JsonException("The diagnostic fixture must contain saved inputs.");
                var validation = await CurveInputValidation.RunInputValidation(saved.Inputs);
                var report = await ExplainDiagnosticAsync(saved.Inputs.ValuationDate, validation, useStub: args.Length == 3);
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                return report.DiagnosticError is null ? 0 : 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"CurveGuard: {error.Message}");
                return 1;
            }
        }

        private static async Task<DiagnosticReport> ExplainDiagnosticAsync(
            DateOnly valuationDate, ValidationResult validation, bool useStub)
        {
            if (useStub)
                return await CurveDiagnostic.AnalyzeWithStubAsync(valuationDate, validation);

            var configuredModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");
            var model = string.IsNullOrWhiteSpace(configuredModel) ? OpenAiCurveDiagnostic.DefaultModel : configuredModel.Trim();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var provider = new OpenAiCurveDiagnostic(client, Environment.GetEnvironmentVariable("OPENAI_API_KEY"), model);
            return await CurveDiagnostic.AnalyzeAsync(valuationDate, validation, "openai", model, provider.ExplainAsync);
        }

        private sealed record DiagnosticFixture(CurveInputs Inputs);

        private static async Task Run(DateTime valuationDate)
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

                ValidationResult results = await CurveInputValidation.RunInputValidation(inputs);

                if(results.Decision != "PASS")
                    throw new Exception();

                SOFRCurve curve;
                try
                {
                    curve = SOFRCurveCreation.Build(inputs);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or ApplicationException)
                {
                    results = new ValidationResult("QUARANTINE", "FAIL", "CURVE_BUILD_FAILED" + " " + error.Message, [], []);
                    throw new Exception();
                }

                results = await CurveRiskValidation.RunPricingValidation(inputs, curve);

                if(results.Decision != "PASS")
                    throw new Exception();

                var chartPath = CurveChart.Save(JsonSerializer.SerializeToElement(curve, JsonOptions));
                var reportPath = Path.ChangeExtension(chartPath, ".json");
                var report = new { Inputs = inputs, Curve = curve, Validation = results };

                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
                Console.WriteLine($"{results.Decision}: saved {reportPath}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"CurveGuard: {error.Message}");
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
