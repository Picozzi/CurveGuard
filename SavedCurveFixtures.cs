using System.Text.Json;

namespace CurveGuard;

internal sealed record SavedCurveReplay(CurveInputs Inputs,
    Dictionary<int, CurveInputs>? SavedBumps = null,
    Dictionary<int, double>? ReportedPvOverrides = null);

internal static class SavedCurveFixtures
{
    public const string Baseline = "sofr-forward-2026-11-02.json";
    public const string StaleFiveYear = "stale-5y.json";
    public const string MissingOneYear = "missing-1y.json";
    public const string DuplicateFiveYear = "duplicate-5y.json";
    public const string FutureFiveYear = "future-5y.json";
    public const string StaleSofrFixing = "stale-sofr-fixing.json";
    public const string SameDayEffrFixing = "same-day-effr-fixing.json";
    public const string InvalidEffrRate = "invalid-effr-rate.json";
    public const string ReversedEffrTargetRange = "reversed-effr-target-range.json";
    public const string StaleZqClose = "stale-zq-close.json";
    public const string FutureZqClose = "future-zq-close.json";
    public const string MissingZqCloseDate = "missing-zq-close-date.json";
    public const string OneBp = "5y-plus-1bp.json";
    public const string TenBp = "5y-plus-10bp.json";
    public const string DoubledPv = "5y-doubled-pv.json";

    public static IReadOnlyList<string> ReplayNames { get; } = new[]
    {
        StaleFiveYear, MissingOneYear, DuplicateFiveYear, FutureFiveYear, StaleSofrFixing,
        SameDayEffrFixing, InvalidEffrRate, ReversedEffrTargetRange, StaleZqClose,
        FutureZqClose, MissingZqCloseDate,
    }.Select(file => Path.GetFileNameWithoutExtension(file))
     .Prepend("clean")
     .Append("doubled-pv")
     .ToArray();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private static readonly DataRetrieval Reader = new(JsonOptions);

    public static async Task<SavedCurveReplay> LoadReplayAsync(string caseName)
    {
        switch (caseName)
        {
            case "clean":
                return new SavedCurveReplay(await ReadInputsAsync(Baseline), new Dictionary<int, CurveInputs>
                {
                    [1] = await ReadInputsAsync(OneBp),
                    [10] = await ReadInputsAsync(TenBp),
                });
            case "doubled-pv":
            {
                using var fixture = await ReadAsync<JsonDocument>(DoubledPv);
                var manifest = fixture.RootElement;
                var baseline = await ReadInputsAsync(manifest.GetProperty("baseline_file").GetString());
                var bumps = new Dictionary<int, CurveInputs>();
                var reportedPvs = new Dictionary<int, double>();
                foreach (var bump in manifest.GetProperty("bumps").EnumerateArray())
                {
                    var bumpBp = bump.GetProperty("bump_bp").GetInt32();
                    var inputsFile = bump.GetProperty("inputs_file").GetString();
                    bumps.Add(bumpBp, await ReadInputsAsync(inputsFile));
                    reportedPvs.Add(bumpBp, bump.GetProperty("reported_pv_usd").GetDouble());
                }

                return new SavedCurveReplay(baseline, bumps, reportedPvs);
            }
            default:
                if (ReplayNames.Contains(caseName))
                    return new SavedCurveReplay(await ReadInputsAsync($"{caseName}.json"));

                throw new ArgumentException($"Unknown replay '{caseName}'. Choose {string.Join(", ", ReplayNames)}, or all.", nameof(caseName));
        }
    }

    public static async Task<CurveInputs> ReadInputsAsync(string? name) => (await ReadAsync<SavedCurveReplay>(name)).Inputs;

    public static Task<T> ReadAsync<T>(string? name) where T : class
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            throw new ArgumentException("Expected a fixture filename from the examples directory.", nameof(name));

        var root = ProjectPaths.RootDirectory
            ?? throw new DirectoryNotFoundException("Could not find the project root containing CurveGuard.csproj.");
        return Reader.GetJsonAsync<T>(Path.Combine(root, "examples", name));
    }
}
