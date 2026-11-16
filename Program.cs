using System.Text.Json;

namespace CurveGuard;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--live"])
            return await BuildSofrCurveAsync(new DateTime(2026, 11, 2));

        return await TestAsync(args);
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
            var futuresTask = new FutureImpliedRate().GetFuturesQuotesAsync(valuationDate);
            await Task.WhenAll(fixingTask, swapsTask, effrTask, futuresTask);

            DateTime handoff = valuationDate.AddYears(1); //this is the split between the forward flat curve and the OIS curve. The FOMC rate predictions are used for the front end, and the OIS curve is used for the back end. The handoff is one year from the valuation date.
            
            var zqQuotes = futuresTask.Result;
            var inputs = new CurveInputs(valuationDateOnly, fixingTask.Result, effrTask.Result,
                swapsTask.Result, [], zqQuotes);
            var run = await CurveValidation.RunAsync(inputs, () =>
            {
                var zqRates = zqQuotes.ToDictionary(quote => (quote.Year, quote.Month), quote => quote.RatePercent);
                return new FOMCCalculator(valuationDate).LoadFrontEndAsync(handoff, effrTask.Result, zqRates);
            });
            var reportDirectory = ProjectPaths.ReportDirectory;
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, $"sofr-quarantine-{valuationDateOnly:yyyy-MM-dd}.json");
            if (run.Curve is { } curve)
            {
                var chart = CurveChart.Save(JsonSerializer.SerializeToElement(curve, JsonOptions));
                reportPath = Path.ChangeExtension(chart, ".json");
            }

            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(run, JsonOptions) + Environment.NewLine);
            Console.WriteLine($"{run.Validation.Decision}: saved {reportPath}");
            return run.Curve is null ? 1 : 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"CurveGuard: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> TestAsync(string[] args)
    {
        const string directory = "examples";
        const string baselineFile = "sofr-forward-2026-11-02.json";
        TestFixture[] fixtures =
        [
            new("clean", baselineFile),
            new("stale_5y", "stale-5y.json", "STALE_OIS_QUOTE"),
            new("missing_1y", "missing-1y.json", "MISSING_1Y_OIS_QUOTE"),
            new("duplicate_5y", "duplicate-5y.json", "DUPLICATE_OIS_TENOR"),
            new("future_5y", "future-5y.json", "FUTURE_OIS_QUOTE"),
            new("stale_sofr_fixing", "stale-sofr-fixing.json", "SOFR_FIXING_DATE_MISMATCH"),
            new("same_day_effr_fixing", "same-day-effr-fixing.json", "EFFR_FIXING_DATE_MISMATCH"),
            new("invalid_effr_rate", "invalid-effr-rate.json", "INVALID_EFFR_RATE"),
            new("reversed_effr_target_range", "reversed-effr-target-range.json", "INVALID_EFFR_TARGET_RANGE"),
            new("stale_zq_close", "stale-zq-close.json", "STALE_ZQ_CLOSE"),
            new("future_zq_close", "future-zq-close.json", "FUTURE_ZQ_CLOSE"),
            new("missing_zq_close_date", "missing-zq-close-date.json", "MISSING_ZQ_CLOSE_DATE"),
            new("5y_plus_1bp", "5y-plus-1bp.json", BumpBp: 1),
            new("5y_plus_10bp", "5y-plus-10bp.json", BumpBp: 10),
        ];

        var riskCommand = args is ["--risk", ..];
        if (riskCommand && args.Length > 2)
        {
            Console.Error.WriteLine("Usage: dotnet run -- --risk [saved-run.json]");
            return 2;
        }

        TestFixture[] selected;
        if (riskCommand)
        {
            selected = fixtures.Where(fixture => fixture.BumpBp.HasValue).ToArray();
        }
        else
        {
            for (var i = 0; i < fixtures.Length; i++)
                Console.Error.WriteLine($"{i + 1}. {fixtures[i].FileName}");

            Console.Error.WriteLine($"Enter a fixture number (1–{fixtures.Length}).");
            if (!int.TryParse(Console.ReadLine(), out var choice) || choice < 1 || choice > fixtures.Length)
            {
                Console.Error.WriteLine("Invalid fixture selection.");
                return 2;
            }

            selected = [fixtures[choice - 1]];
        }

        try
        {
            if (selected[0].BumpBp.HasValue)
            {
                var customBaseline = riskCommand && args.Length == 2 ? args[1] : null;
                var baseline = await ReadSavedInputsAsync(customBaseline ?? Path.Combine(directory, baselineFile));
                Dictionary<int, CurveInputs>? savedBumps = null;
                if (customBaseline is null)
                {
                    savedBumps = new Dictionary<int, CurveInputs>(selected.Length);
                    foreach (var fixture in selected)
                        savedBumps.Add(fixture.BumpBp!.Value, await ReadSavedInputsAsync(Path.Combine(directory, fixture.FileName)));
                }

                var report = await CurveRisk.RunAsync(baseline, savedBumps);
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                return report.Scenarios.All(scenario => scenario.Validation.Decision == "PASS") ? 0 : 1;
            }

            var guard = selected[0];
            var path = Path.Combine(directory, guard.FileName);
            var run = await CurveValidation.RunAsync(await ReadSavedInputsAsync(path));
            var expectedDecision = guard.ExpectedCode is null ? "PASS" : "QUARANTINE";
            var findings = run.Validation.Findings;
            var passed = run.Validation.Decision == expectedDecision &&
                (guard.ExpectedCode is null
                    ? findings.Count == 0
                    : findings.Count == 1 && findings[0].Code == guard.ExpectedCode);
            var result = new GuardTestResult(guard.Name, path, expectedDecision, guard.ExpectedCode, run.Validation, passed);
            Console.WriteLine(JsonSerializer.Serialize(new[] { result }, JsonOptions));
            return passed ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"CurveGuard: {error.Message}");
            return 2;
        }
    }

    private static async Task<CurveInputs> ReadSavedInputsAsync(string path)
    {
        var projectDirectory = ProjectPaths.RootDirectory
            ?? throw new DirectoryNotFoundException("Could not find the project root containing CurveGuard.csproj.");
        path = Path.GetFullPath(path, projectDirectory);

        using var file = File.OpenRead(path);
        var snapshot = await JsonSerializer.DeserializeAsync<SavedRun>(file, JsonOptions)
            ?? throw new JsonException($"The fixture '{path}' must contain inputs.");
        return snapshot.Inputs;
    }

    private sealed record SavedRun(CurveInputs Inputs);

    private sealed record TestFixture(string Name, string FileName, string? ExpectedCode = null, int? BumpBp = null);

    private sealed record GuardTestResult(string Scenario, string Fixture, string ExpectedDecision, string? ExpectedCode, ValidationResult Validation, bool Passed);
}
