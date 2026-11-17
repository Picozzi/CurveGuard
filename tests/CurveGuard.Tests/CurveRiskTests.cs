using Xunit;

namespace CurveGuard.Tests;

public class CurveRiskTests
{
    [Fact]
    public async Task LiveAndSavedScenariosProduceTheSamePricingChecks()
    {
        var baseline = await SavedFixtures.ReadInputsAsync(SavedFixtures.Baseline);
        var savedBumps = new Dictionary<int, CurveInputs>
        {
            [1] = await SavedFixtures.ReadInputsAsync("5y-plus-1bp.json"),
            [10] = await SavedFixtures.ReadInputsAsync("5y-plus-10bp.json"),
        };
        var savedRisk = await CurveRisk.RunAsync(baseline, savedBumps);

        var loaderCalls = 0;
        var live = await CurveRisk.RunLiveAsync(baseline with { FomcDecisions = [] }, () =>
        {
            loaderCalls++;
            return Task.FromResult(baseline.FomcDecisions.ToList());
        });

        Assert.Equal(1, loaderCalls);
        Assert.Equal("PASS", live.Validation.Decision);
        Assert.NotNull(live.Curve);
        var liveRisk = Assert.IsType<OisRiskReport>(live.PricingRisk);
        Assert.True(savedRisk.Passed);
        Assert.True(liveRisk.Passed);
        Assert.Equal(savedRisk.Checks, liveRisk.Checks);
        Assert.Equal([0, 1, 10], liveRisk.Scenarios.Select(scenario => scenario.BumpBp));
        Assert.Equal([4.418m, 4.428m, 4.518m], liveRisk.Scenarios.Select(scenario => scenario.MarketQuotePercent));
        Assert.Equal(4.418m, Assert.Single(live.Inputs.OisSnapshot.Quotes, quote => quote.Tenor == "5Y").Rate);
        Assert.InRange(liveRisk.Scenarios[1].PvChangeUsd.GetValueOrDefault(), 444, 446);
        Assert.InRange(liveRisk.Scenarios[2].PvChangeUsd.GetValueOrDefault(), 4447, 4450);
    }

    [Fact]
    public async Task DoubledPvFailsDollarChecksEvenWhenCurvesPass()
    {
        var fixture = await SavedFixtures.ReadAsync<PricingFailureFixture>("5y-doubled-pv.json");
        var baseline = await SavedFixtures.ReadInputsAsync(fixture.BaselineFile);
        var bumps = new Dictionary<int, CurveInputs>();
        foreach (var bump in fixture.Bumps)
            bumps.Add(bump.BumpBp, await SavedFixtures.ReadInputsAsync(bump.InputsFile));

        var report = await CurveRisk.RunAsync(baseline, bumps,
            fixture.Bumps.ToDictionary(bump => bump.BumpBp, bump => bump.ReportedPvUsd));

        Assert.False(report.Passed);
        Assert.All(report.Scenarios, scenario => Assert.Equal("PASS", scenario.Validation.Decision));
        var failures = report.Checks.Where(check => !check.Passed).Select(check => (check.Name, check.BumpBp)).ToArray();
        Assert.Equal(new (string, int?)[] { ("pv_change_usd", 1), ("pv_change_usd", 10) }, failures);
    }

    private sealed record PricingFailureFixture(string BaselineFile, IReadOnlyList<PricingFailureBump> Bumps);

    private sealed record PricingFailureBump(int BumpBp, string InputsFile, double ReportedPvUsd);
}
