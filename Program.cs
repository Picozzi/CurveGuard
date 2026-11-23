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
            string replayName = string.Empty;

            foreach(string arg in args)
            {
                var splitArgument = arg.Split('=');
                string category = splitArgument.First();
                string input = splitArgument.Last();

                if(category == "REPLAY")
                    replayName = input;
            }
            
            bool live = replayName == string.Empty ? true : false;


            if(!live)
            {
                List<string> cases = new List<string>();

                if (string.Equals(replayName, "ALL", StringComparison.OrdinalIgnoreCase))
                    cases = SavedCurveFixtures.ReplayNames.ToList();
                else if (replayName.Length > 0)
                    cases.Add(replayName);

                foreach (var replayCase in cases)
                {
                    try
                    {
                        await RunReplayAsync(replayCase);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine($"CurveGuard replay '{replayCase}': {error.Message}");
                    }
                }
            }
            else
            {
                try
                    {
                        await RunLiveAsync(new DateOnly(2026, 11, 19));
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine($"CurveGuard: {error.Message}");
                    }
            }
        }

        internal static async Task<JsonElement> RunLiveAsync(DateOnly valuationDate)
        {
            var inputs = await LoadLiveInputsAsync(valuationDate.ToDateTime(TimeOnly.MinValue));
            var run = await RunFromInputsAsync(inputs, null);
            return await SaveRunReportAsync(run.Inputs, run.Curve, run.Validation, null);
        }

        internal static async Task<JsonElement> RunReplayAsync(string name)
        {
            var replay = await SavedCurveFixtures.LoadReplayAsync(name);
            var run = await RunFromInputsAsync(replay.Inputs, replay);
            return await SaveRunReportAsync(run.Inputs, run.Curve, run.Validation, name);
        }

        private static async Task<DiagnosticReport> ExplainDiagnosticAsync(DateOnly valuationDate, ValidationResult validation)
        {
            if (ProjectPaths.RootDirectory is { } projectRoot)
                Env.NoClobber().Load(Path.Combine(projectRoot, ".env"));

            var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var provider = new OpenAiCurveDiagnostic(client, key, model);
            return await CurveDiagnostic.AnalyzeAsync(valuationDate, validation, "openai", model, provider.ExplainAsync);
        }

        private static async Task<CurveInputs> LoadLiveInputsAsync(DateTime valuationDate)
        {
            /*
            Note from MP:
            We use T−1 business day because the New York Fed publishes the fixing the next business morning, around 8 a.m. ET for SOFR and 9 a.m. ET for EFFR. Using a fixing dated T in a replay as of T would give the model information that only became available on T+1.

            For Example:
            For the November 13 valuation, CurveGuard uses the November 12 fixings to anchor overnight SOFR and calculate the SOFR–EFFR spread. The curve itself still starts on November 13, and the OIS and ZQ inputs are selected as of that valuation date.
            */
            var valuationDateOnly = DateOnly.FromDateTime(valuationDate);
            var fixingDate = SOFRCurveCreation.PreviousFixingDate(valuationDateOnly);
            var fixingTask = new OvernightSOFR().GetSOFRFixingAsync(fixingDate);
            var swapsTask = new OISSOFR().GetOisSwapRatesAsync(valuationDate);
            var effrTask = new EFFRCalculator().GetEffrFixingAsync(fixingDate);
            var futuresTask = new FutureImpliedRate().GetFuturesQuotesAsync(valuationDate);

            await Task.WhenAll(fixingTask, swapsTask, effrTask, futuresTask);
            return new CurveInputs(valuationDateOnly, fixingTask.Result, effrTask.Result, swapsTask.Result, [], futuresTask.Result);
        }

        private static async Task<(CurveInputs Inputs, SOFRCurve? Curve, ValidationResult Validation)> RunFromInputsAsync(CurveInputs inputs, SavedCurveReplay? replay)
        {
            var results = await CurveInputValidation.RunInputValidation(inputs);
            if (results.Decision != "PASS")
                return (inputs, null, results);

            if (inputs.FomcDecisions.Count() == 0)
            {
                var valuationDate = inputs.ValuationDate.ToDateTime(TimeOnly.MinValue);
                try
                {
                    var decisions = await new FOMCCalculator(valuationDate).LoadFOMCRateProbabilitiesAsync(
                        valuationDate.AddYears(1), inputs.EffrFixing, inputs.ZqImpliedRates);
                    inputs = inputs with { FomcDecisions = decisions };
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or ApplicationException or IOException or JsonException)
                {
                    results = new ValidationResult("QUARANTINE", "SKIPPED", $"FOMC_BUILD_FAILED {error.Message}", [], []);
                    return (inputs, null, results);
                }
            }

            SOFRCurve curve;
            try
            {
                curve = SOFRCurveCreation.Build(inputs);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or ApplicationException or IOException or JsonException)
            {
                results = new ValidationResult("QUARANTINE", "FAIL", $"CURVE_BUILD_FAILED {error.Message}", [], []);
                return (inputs, null, results);
            }

            try
            {
                results = replay is null
                    ? await CurveRiskValidation.RunPricingValidation(inputs, curve)
                    : await CurveRiskValidation.RunPricingValidation(inputs, curve, replay.SavedBumps
                        ?? throw new ArgumentException("Pricing this replay requires saved bump scenarios."), replay.ReportedPvOverrides);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or ApplicationException or FormatException)
            {
                results = new ValidationResult("QUARANTINE", "PASS", $"PRICING_VALIDATION_FAILED {error.Message}", [], []);
            }

            return (inputs, curve, results);
        }

        private static async Task<JsonElement> SaveRunReportAsync(CurveInputs inputs, SOFRCurve? curve, ValidationResult validation, string? replayCase)
        {
            bool liveRun = replayCase == null; 
            bool passed = validation.Decision == "PASS" && curve is not null;
            string reportDirectory = liveRun ? ProjectPaths.ReportDirectory : Path.Combine(ProjectPaths.ReportDirectory, "replays");
            Directory.CreateDirectory(reportDirectory);
           
            string prefix = passed ? "sofr-forward" : "sofr-quarantine";
            string reportName = liveRun ? $"{prefix}-{inputs.ValuationDate:yyyy-MM-dd}.json" : $"{replayCase}.json";
            
            var reportPath = Path.Combine(reportDirectory, reportName);
            var report = new { Inputs = inputs, Curve = curve, Validation = validation };

            var saved = await WriteReportAsync(reportPath, report);
            Console.WriteLine($"{validation.Decision}: saved {reportPath}");

            if (passed)
            {
                CurveChart.Save(JsonSerializer.SerializeToElement(curve, JsonOptions),
                    Path.ChangeExtension(reportPath, ".png"));
                return saved;
            }

            var diagnostic = await ExplainDiagnosticAsync(inputs.ValuationDate, validation);
            return await WriteReportAsync(reportPath, new
            {
                report.Inputs,
                report.Curve,
                report.Validation,
                diagnostic.Evidence,
                diagnostic.Model,
                diagnostic.Diagnostic,
                diagnostic.DiagnosticError,
            });
        }

        private static async Task<JsonElement> WriteReportAsync<T>(string reportPath, T report)
        {
            var temporaryPath = $"{reportPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var json = JsonSerializer.SerializeToElement(report, JsonOptions);
                await File.WriteAllTextAsync(temporaryPath, json.GetRawText() + Environment.NewLine);
                File.Move(temporaryPath, reportPath, overwrite: true);
                return json;
            }
            finally
            {
                File.Delete(temporaryPath);
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
