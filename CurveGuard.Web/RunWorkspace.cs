using System.Text.Json;

namespace CurveGuard.Web;

public sealed record ForwardPoint(DateOnly Date, double InstantaneousForwardPercent);
public sealed record ReportCurve(List<ForwardPoint> MonthlyAndPillarForwards);
public sealed record ReportValidation(string Decision, string? BootstrapResult, string? Bootstrap, string? Result,
    string? Message, List<ValidationCheck>? ValidationRisks, List<PricingRiskCheck>? PricingRisks);
public sealed record ReportDiagnostic(string Explanation, List<string> EvidenceIds, string RecommendedNextAction, List<string> Unknowns);
public sealed record RunReport(CurveInputs Inputs, ReportValidation Validation, ReportCurve? Curve, ReportDiagnostic? Diagnostic,
    string? DiagnosticError, string? Model)
{
    public string BootstrapStatus => Validation.BootstrapResult ?? Validation.Bootstrap ?? Validation.Result ?? "Unavailable";
}
public sealed record SavedReport(string Name, string Path, string Label);

public sealed class RunWorkspace : IDisposable
{
    private readonly SemaphoreSlim writer = new(1);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static string Archive => Path.Combine(ProjectPaths.RootDirectory!, "Evaluations", "replays-eval1");
    public IReadOnlyList<string> Fixtures => SavedCurveFixtures.ReplayNames;

    public List<SavedReport> LiveHistory()
    {
        if (!Directory.Exists(ProjectPaths.ReportDirectory)) return [];
        return Directory.EnumerateFiles(ProjectPaths.ReportDirectory, "sofr-*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => new SavedReport(Path.GetFileNameWithoutExtension(path), path, "Saved live run")).ToList();
    }

    public SavedReport? FindReplay(string name)
    {
        if (!Fixtures.Contains(name)) return null;
        var file = name + ".json";
        var recent = Path.Combine(ProjectPaths.ReportDirectory, "replays", file);
        if (File.Exists(recent)) return new(name, recent, "Saved replay");
        var archived = Path.Combine(Archive, file);
        return File.Exists(archived) ? new(name, archived, "Archived replay") : null;
    }

    public static async Task<RunReport> ReadAsync(SavedReport saved)
    {
        await using var file = new FileStream(saved.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<RunReport>(file, Options) ?? throw new JsonException("Empty run report.");
    }

    public async Task<RunReport> ExecuteAsync(DateOnly date, string? replay)
    {
        await writer.WaitAsync();
        try
        {
            var json = replay is null
                ? await global::CurveGuard.Program.RunLiveAsync(date)
                : await global::CurveGuard.Program.RunReplayAsync(replay);
            return json.Deserialize<RunReport>(Options) ?? throw new JsonException("Empty run report.");
        }
        finally { writer.Release(); }
    }

    public void Dispose() => writer.Dispose();
}
