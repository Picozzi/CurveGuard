using System.Diagnostics;
using System.Text.Json;
using DotNetEnv;

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
            if (ProjectPaths.RootDirectory is { } projectRoot)
                Env.NoClobber().Load(Path.Combine(projectRoot, ".env"));
            var valuationDate = new DateTime(2026, 11, 19);
            await Run(valuationDate);
        }

        private static async Task<DiagnosticReport> ExplainDiagnosticAsync(DateOnly valuationDate, ValidationResult validation)
        {
            var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var provider = new OpenAiCurveDiagnostic(client, key, model);
            return await CurveDiagnostic.AnalyzeAsync(valuationDate, validation, "openai", model, provider.ExplainAsync);
        }

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
             
                var handoff = valuationDate.AddYears(1);
                var decisions = await new FOMCCalculator(valuationDate).LoadFOMCRateProbabilitiesAsync(handoff, effrTask.Result, futuresTask.Result);

                var inputs = new CurveInputs(valuationDateOnly, fixingTask.Result, effrTask.Result, swapsTask.Result, decisions, futuresTask.Result);
                ValidationResult results = await CurveInputValidation.RunInputValidation(inputs);
                if (results.Decision != "PASS")
                {
                    await SaveRunReportAsync(inputs, null, results);
                    return;
                }

                SOFRCurve curve;
                try
                {
                    curve = SOFRCurveCreation.Build(inputs);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or ApplicationException or IOException or JsonException)
                {
                    results = new ValidationResult("QUARANTINE", "FAIL", $"CURVE_BUILD_FAILED {error.Message}", [], []);
                    await SaveRunReportAsync(inputs, null, results);
                    return;
                }

                try
                {
                    results = await CurveRiskValidation.RunPricingValidation(inputs, curve);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or ApplicationException or FormatException)
                {
                    results = new ValidationResult("QUARANTINE", "FAIL", $"PRICING_VALIDATION_FAILED {error.Message}", [], []);
                }

                await SaveRunReportAsync(inputs, curve, results);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"CurveGuard: {error.Message}");
            }
        }

        private static async Task SaveRunReportAsync(CurveInputs inputs, SOFRCurve? curve, ValidationResult validation)
        {
            string reportPath;
            string reportJson;
            if (validation.Decision == "PASS" && curve is not null)
            {
                var chartPath = CurveChart.Save(JsonSerializer.SerializeToElement(curve, JsonOptions));
                reportPath = Path.ChangeExtension(chartPath, ".json");
                reportJson = JsonSerializer.Serialize(new { Inputs = inputs, Curve = curve, Validation = validation }, JsonOptions);
            }
            else
            {
                var diagnostic = await ExplainDiagnosticAsync(inputs.ValuationDate, validation);
                var reportDirectory = ProjectPaths.ReportDirectory;
                Directory.CreateDirectory(reportDirectory);
                reportPath = Path.Combine(reportDirectory, $"sofr-quarantine-{inputs.ValuationDate:yyyy-MM-dd}.json");
                reportJson = JsonSerializer.Serialize(new
                {
                    Inputs = inputs,
                    Curve = curve,
                    Validation = validation,
                    diagnostic.Evidence,
                    diagnostic.Model,
                    diagnostic.Diagnostic,
                    diagnostic.DiagnosticError,
                }, JsonOptions);
            }

            await File.WriteAllTextAsync(reportPath, reportJson + Environment.NewLine);
            Console.WriteLine($"{validation.Decision}: saved {reportPath}");
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
