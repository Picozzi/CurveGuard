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
        var curve = SOFRCurveCreation.Build(baseline);
        var saved = await CurveRiskValidation.RunPricingValidation(baseline, curve, savedBumps);
        var live = await CurveRiskValidation.RunPricingValidation(baseline, curve);

        Assert.Equal("PASS", saved.Decision);
        Assert.Equal("PASS", live.Decision);
        Assert.Empty(live.validationRisks);
        Assert.Equal(saved.pricingRisks, live.pricingRisks);
        Assert.Equal(new int?[] { 1, 10 },
            live.pricingRisks.Where(check => check.Code == "pv_change_usd").Select(check => check.BumpBp));
        Assert.Equal(4.418m, Assert.Single(baseline.OisSnapshot.Quotes, quote => quote.Tenor == "5Y").Rate);
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

        var curve = SOFRCurveCreation.Build(baseline);
        var clean = await CurveRiskValidation.RunPricingValidation(baseline, curve, bumps);
        var report = await CurveRiskValidation.RunPricingValidation(baseline, curve, bumps,
            fixture.Bumps.ToDictionary(bump => bump.BumpBp, bump => bump.ReportedPvUsd));

        Assert.Equal("PASS", clean.Decision);
        Assert.Equal("QUARANTINE", report.Decision);
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
        var baseline = await SavedFixtures.ReadInputsAsync(SavedFixtures.Baseline);
        var snapshot = baseline.OisSnapshot;
        var inputs = baseline with
        {
            OisSnapshot = new OISSOFR.OISSwapSnapshot(snapshot.ObservationDate, snapshot.Source, snapshot.Url,
                snapshot.ProviderCollectionTime, snapshot.Quotes.Where(quote => quote.Tenor != "5Y").ToArray()),
        };

        var result = await CurveRiskValidation.RunPricingValidation(inputs, SOFRCurveCreation.Build(inputs));

        Assert.Equal("QUARANTINE", result.Decision);
        Assert.Equal("PASS", result.Result);
        Assert.Empty(result.pricingRisks);
        ValidationCheck inputRisk = Assert.Single(result.validationRisks);
        Assert.Equal("MISSING_5Y_OIS_QUOTE", inputRisk.Code);
    }

    private static async Task AssertSavedBumpPvChangeAsync(string fixture, int bumpBp, double minUsd, double maxUsd)
    {
        var baseline = await SavedFixtures.ReadInputsAsync(SavedFixtures.Baseline);
        var bumped = await SavedFixtures.ReadInputsAsync(fixture);

        var report = await CurveRiskValidation.RunPricingValidation(baseline, SOFRCurveCreation.Build(baseline),
            new Dictionary<int, CurveInputs> { [bumpBp] = bumped });

        Assert.Equal("PASS", report.Decision);
        var dollarCheck = Assert.Single(report.pricingRisks, check => check.Code == "pv_change_usd");
        Assert.Equal(bumpBp, dollarCheck.BumpBp);
        Assert.InRange(Assert.IsType<double>(dollarCheck.Actual), minUsd, maxUsd);
    }

    private sealed record PricingFailureFixture(string BaselineFile, IReadOnlyList<PricingFailureBump> Bumps,
        IReadOnlyList<PricingCheck> ExpectedFailedChecks);

    private sealed record PricingFailureBump(int BumpBp, string InputsFile, double ReportedPvUsd);

    private sealed record PricingCheck(string Name, int? BumpBp);
}
