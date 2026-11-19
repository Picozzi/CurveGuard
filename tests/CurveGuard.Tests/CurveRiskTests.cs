using Xunit;

namespace CurveGuard.Tests;

public class CurveRiskTests
{
    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.OneBp)]
    public Task OneBpFiveYearBumpProducesExpectedPvChange()
        => AssertSavedBumpPvChangeAsync(SavedFixtures.OneBp, 1, 444, 446);

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.TenBp)]
    public Task TenBpFiveYearBumpProducesExpectedPvChange()
        => AssertSavedBumpPvChangeAsync(SavedFixtures.TenBp, 10, 4447, 4450);

    [Fact]
    public async Task LiveAndSavedScenariosProduceTheSamePricingChecks()
    {
        var baseline = await SavedFixtures.ReadInputsAsync(SavedFixtures.Baseline);
        var savedBumps = new Dictionary<int, CurveInputs>
        {
            [1] = await SavedFixtures.ReadInputsAsync(SavedFixtures.OneBp),
            [10] = await SavedFixtures.ReadInputsAsync(SavedFixtures.TenBp),
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
        Assert.Equal(savedRisk.Scenarios.Select(scenario => scenario.PvChangeUsd),
            liveRisk.Scenarios.Select(scenario => scenario.PvChangeUsd));
    }

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.DoubledPv)]
    public async Task DoubledPvFailsDollarChecksEvenWhenCurvesPass()
    {
        var fixture = await SavedFixtures.ReadAsync<PricingFailureFixture>(SavedFixtures.DoubledPv);
        var baseline = await SavedFixtures.ReadInputsAsync(fixture.BaselineFile);
        var bumps = new Dictionary<int, CurveInputs>();
        foreach (var bump in fixture.Bumps)
            bumps.Add(bump.BumpBp, await SavedFixtures.ReadInputsAsync(bump.InputsFile));

        var report = await CurveRisk.RunAsync(baseline, bumps,
            fixture.Bumps.ToDictionary(bump => bump.BumpBp, bump => bump.ReportedPvUsd));

        Assert.False(report.Passed);
        Assert.All(report.Scenarios, scenario => Assert.Equal("PASS", scenario.Validation.Decision));
        Assert.True(Assert.Single(report.Checks, check => check.Name == "ten_bp_to_one_bp_ratio").Passed);
        var failures = report.Checks.Where(check => !check.Passed)
            .Select(check => new PricingCheck(check.Name, check.BumpBp)).ToArray();
        Assert.Equal([new PricingCheck("pv_change_usd", 1), new PricingCheck("pv_change_usd", 10)], failures);
        Assert.Equal(fixture.ExpectedFailedChecks, failures);
    }

    private static async Task AssertSavedBumpPvChangeAsync(string fixture, int bumpBp, double minUsd, double maxUsd)
    {
        var baseline = await SavedFixtures.ReadInputsAsync(SavedFixtures.Baseline);
        var bumped = await SavedFixtures.ReadInputsAsync(fixture);

        var report = await CurveRisk.RunAsync(baseline, new Dictionary<int, CurveInputs> { [bumpBp] = bumped });

        Assert.Equal([0, bumpBp], report.Scenarios.Select(scenario => scenario.BumpBp));
        var scenario = report.Scenarios[1];
        Assert.Equal("PASS", scenario.Validation.Decision);
        Assert.Equal(report.Trade.FixedCouponPercent + bumpBp / 100m, scenario.MarketQuotePercent);
        Assert.InRange(Assert.IsType<double>(scenario.PvChangeUsd), minUsd, maxUsd);
        Assert.True(report.Passed);
    }

    private sealed record PricingFailureFixture(string BaselineFile, IReadOnlyList<PricingFailureBump> Bumps,
        IReadOnlyList<PricingCheck> ExpectedFailedChecks);

    private sealed record PricingFailureBump(int BumpBp, string InputsFile, double ReportedPvUsd);

    private sealed record PricingCheck(string Name, int? BumpBp);
}
