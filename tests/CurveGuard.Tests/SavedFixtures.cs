using System.Text.Json;

namespace CurveGuard.Tests;

internal static class SavedFixtures
{
    public const string Baseline = "sofr-forward-2026-11-02.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static async Task<CurveInputs> ReadInputsAsync(string name) => (await ReadAsync<SavedInputs>(name)).Inputs;

    public static async Task<T> ReadAsync<T>(string name) where T : class
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "examples", name));
        return await JsonSerializer.DeserializeAsync<T>(file, JsonOptions)
            ?? throw new JsonException($"Fixture '{name}' must contain a JSON object.");
    }

    private sealed record SavedInputs(CurveInputs Inputs);
}
