using Xunit;

namespace CurveGuard.Tests;

public class CurveRiskTests
{
    [Fact]
    [Trait("Fixture", SavedCurveFixtures.OneBp)]
    public Task OneBpFiveYearBumpProducesExpectedPvChange()
        => AssertSavedBumpPvChangeAsync(SavedCurveFixtures.OneBp, 1, 444, 446);

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.TenBp)]
    public Task TenBpFiveYearBumpProducesExpectedPvChange()
        => AssertSavedBumpPvChangeAsync(SavedCurveFixtures.TenBp, 10, 4447, 4450);

    [Fact]
    public async Task LiveAndSavedScenariosProduceTheSamePricingChecks()
    {
        var replay = await SavedCurveFixtures.LoadReplayAsync("clean");
        var baseline = replay.Inputs;
        var curve = SOFRCurveCreation.Build(baseline);
        var saved = await CurveRiskValidation.RunPricingValidation(baseline, curve, replay.SavedBumps, null);
        var live = await CurveRiskValidation.RunPricingValidation(baseline, curve);

        Assert.NotNull(replay.SavedBumps);
        Assert.Equal("PASS", saved.Decision);
        Assert.Equal("PASS", live.Decision);
        Assert.Equal("PASS", live.BootstrapResult);
        Assert.Empty(live.message);
        Assert.Empty(live.validationRisks);
        Assert.Equal(saved.pricingRisks, live.pricingRisks);
        Assert.Equal(new int?[] { 1, 10 },
            live.pricingRisks.Where(check => check.Code == "pv_change_usd").Select(check => check.BumpBp));
        Assert.Equal(4.418m, Assert.Single(baseline.OisSnapshot.Quotes, quote => quote.Tenor == "5Y").Rate);
    }

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.DoubledPv)]
    public async Task DoubledPvFailsDollarChecksEvenWhenCurvesPass()
    {
        var fixture = await SavedCurveFixtures.ReadAsync<PricingFailureFixture>(SavedCurveFixtures.DoubledPv);
        var replay = await SavedCurveFixtures.LoadReplayAsync("doubled-pv");
        var baseline = replay.Inputs;

        var curve = SOFRCurveCreation.Build(baseline);
        var clean = await CurveRiskValidation.RunPricingValidation(baseline, curve, replay.SavedBumps, null);
        var report = await CurveRiskValidation.RunPricingValidation(baseline, curve, replay.SavedBumps, replay.ReportedPvOverrides);

        Assert.NotNull(replay.SavedBumps);
        Assert.Equal("PASS", clean.Decision);
        Assert.Equal("QUARANTINE", report.Decision);
        Assert.Equal("PASS", report.BootstrapResult);
        Assert.StartsWith("PRICING_VALIDATION_FAILED ", report.message);
        Assert.Empty(report.validationRisks);
        Assert.True(Assert.Single(report.pricingRisks, check => check.Code == "ten_bp_to_one_bp_ratio").Passed);
        var failures = report.pricingRisks.Where(check => !check.Passed)
            .Select(check => new PricingCheck(check.Code, check.BumpBp)).ToArray();
        Assert.Equal([new PricingCheck("pv_change_usd", 1), new PricingCheck("pv_change_usd", 10)], failures);
        Assert.Equal(fixture.ExpectedFailedChecks, failures);
    }

    [Fact]
    public async Task MissingFiveYearQuoteIsReportedAsAnInputRisk()
    {
        var baseline = await SavedCurveFixtures.ReadInputsAsync(SavedCurveFixtures.Baseline);
        var snapshot = baseline.OisSnapshot;
        var inputs = baseline with
        {
            OisSnapshot = new OISSOFR.OISSwapSnapshot(snapshot.ObservationDate, snapshot.Source, snapshot.Url,
                snapshot.ProviderCollectionTime, snapshot.Quotes.Where(quote => quote.Tenor != "5Y").ToArray()),
        };

        var result = await CurveRiskValidation.RunPricingValidation(inputs, SOFRCurveCreation.Build(inputs));

        Assert.Equal("QUARANTINE", result.Decision);
        Assert.Equal("PASS", result.BootstrapResult);
        Assert.StartsWith("MISSING_5Y_OIS_QUOTE ", result.message);
        Assert.Empty(result.pricingRisks);
        ValidationCheck inputRisk = Assert.Single(result.validationRisks);
        Assert.Equal("MISSING_5Y_OIS_QUOTE", inputRisk.Code);
    }

    private static async Task AssertSavedBumpPvChangeAsync(string fixture, int bumpBp, double minUsd, double maxUsd)
    {
        var baseline = await SavedCurveFixtures.ReadInputsAsync(SavedCurveFixtures.Baseline);
        var bumped = await SavedCurveFixtures.ReadInputsAsync(fixture);

        var report = await CurveRiskValidation.RunPricingValidation(baseline, SOFRCurveCreation.Build(baseline),
            new Dictionary<int, CurveInputs> { [bumpBp] = bumped }, null);

        Assert.Equal("PASS", report.Decision);
        var dollarCheck = Assert.Single(report.pricingRisks, check => check.Code == "pv_change_usd");
        Assert.Equal(bumpBp, dollarCheck.BumpBp);
        Assert.InRange(Assert.IsType<double>(dollarCheck.Actual), minUsd, maxUsd);
    }

    private sealed record PricingFailureFixture(IReadOnlyList<PricingCheck> ExpectedFailedChecks);

    private sealed record PricingCheck(string Name, int? BumpBp);
}
