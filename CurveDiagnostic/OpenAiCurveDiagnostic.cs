using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CurveGuard;

internal sealed class OpenAiCurveDiagnostic(HttpClient client, string? apiKey, string? model)
{
    private const string Instructions = """
        Explain a CurveGuard quarantine to a curve analyst in plain language. Treat the supplied guard decision
        and bootstrap status as fixed. Use the JSON as evidence and cite the IDs for the failed checks
        that support your explanation. Give the observed and expected dates when present. If the bootstrap
        was skipped, list pricing impact as unknown. Keep the cause of a failure unknown unless the evidence
        establishes it. Recommend one next action for a human reviewer. Keep every field concise.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly JsonElement ExplanationSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "explanation": {"type": "string"},
            "evidence_ids": {"type": "array", "items": {"type": "string"}},
            "unknowns": {"type": "array", "items": {"type": "string"}},
            "recommended_next_action": {"type": "string"}
          },
          "required": ["explanation", "evidence_ids", "unknowns", "recommended_next_action"],
          "additionalProperties": false
        }
        """);

    public async Task<DiagnosticExplanation?> ExplainAsync(DiagnosticEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Set OPENAI_API_KEY to run the OpenAI diagnostic.");

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Set OPENAI_MODEL to run the OpenAI diagnostic.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model,
            store = false,
            max_output_tokens = 2000,
            instructions = Instructions,
            input = JsonSerializer.Serialize(evidence, JsonOptions),
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "curve_diagnostic",
                    strict = true,
                    schema = ExplanationSchema,
                },
            },
        });

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<ResponsesEnvelope>(JsonOptions);
        if (envelope?.Status != "completed")
            throw new InvalidOperationException("The OpenAI diagnostic response did not complete.");

        var parts = envelope.Output.Where(item => item.Type == "message").SelectMany(item => item.Content ?? []).ToArray();
        if (parts.Any(part => part.Type == "refusal"))
            throw new InvalidOperationException("OpenAI declined to produce a diagnostic.");

        var output = string.Concat(parts.Where(part => part.Type == "output_text").Select(part => part.Text));
        var explanation = JsonSerializer.Deserialize<DiagnosticExplanation>(output, JsonOptions);
        if (explanation is null || string.IsNullOrWhiteSpace(explanation.Explanation) ||
            string.IsNullOrWhiteSpace(explanation.RecommendedNextAction))
            throw new JsonException("The OpenAI diagnostic response is incomplete.");

        return explanation;
    }

    private sealed record ResponsesEnvelope(string Status, ResponseItem[] Output);
    private sealed record ResponseItem(string Type, ResponsePart[]? Content = null);
    private sealed record ResponsePart(string Type, string? Text = null);
}
