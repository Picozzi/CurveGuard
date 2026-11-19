using System.Text.Json;

namespace CurveGuard.Tests;

internal static class SavedFixtures
{
    public const string TraitName = "Fixture";
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

    public static string DirectoryPath { get; } = Path.Combine(ProjectPaths.RootDirectory
        ?? throw new DirectoryNotFoundException("Could not find the project root containing CurveGuard.csproj."), "examples");

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static async Task<CurveInputs> ReadInputsAsync(string name) => (await ReadAsync<SavedInputs>(name)).Inputs;

    public static async Task<T> ReadAsync<T>(string name) where T : class
    {
        using var file = File.OpenRead(Path.Combine(DirectoryPath, name));
        return await JsonSerializer.DeserializeAsync<T>(file, JsonOptions)
            ?? throw new JsonException($"Fixture '{name}' must contain a JSON object.");
    }

    private sealed record SavedInputs(CurveInputs Inputs);
}
