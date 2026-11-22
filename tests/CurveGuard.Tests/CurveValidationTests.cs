using System.Text.Json;
using Xunit;

namespace CurveGuard.Tests;

public class CurveValidationTests
{
    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.Baseline)]
    public async Task CleanCurveBootstrapsAndRepricesSavedQuotes()
    {
        const double toleranceBp = 0.01;
        var baseline = await SavedFixtures.ReadAsync<BaselineFixture>(SavedFixtures.Baseline);

        var validation = await CurveInputValidation.RunInputValidation(baseline.Inputs);

        Assert.Equal("PASS", validation.Decision);
        Assert.Equal("UPCOMING", validation.BootstrapResult);
        Assert.Empty(validation.message);
        Assert.Empty(validation.pricingRisks);
        Assert.Empty(validation.validationRisks);

        var curve = JsonSerializer.SerializeToElement(SOFRCurveCreation.Build(baseline.Inputs), SavedFixtures.JsonOptions);
        Assert.Equal(baseline.Curve.GetProperty("reference_date").GetString(), curve.GetProperty("reference_date").GetString());
        var actual = curve.GetProperty("repricings").EnumerateArray().ToArray();
        var expected = baseline.Curve.GetProperty("repricing").EnumerateArray().Select(RepricingKey);
        Assert.Equal(expected, actual.Select(RepricingKey));
        Assert.All(actual, row => Assert.InRange(row.GetProperty("residual_bp").GetDouble(), -toleranceBp, toleranceBp));
    }

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.StaleFiveYear)]
    public Task StaleFiveYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.StaleFiveYear, "STALE_OIS_QUOTE");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.MissingOneYear)]
    public Task MissingOneYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.MissingOneYear, "MISSING_1Y_OIS_QUOTE");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.DuplicateFiveYear)]
    public Task DuplicateFiveYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.DuplicateFiveYear, "DUPLICATE_OIS_TENOR");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.FutureFiveYear)]
    public Task FutureFiveYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.FutureFiveYear, "FUTURE_OIS_QUOTE");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.StaleSofrFixing)]
    public Task StaleSofrFixingQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.StaleSofrFixing, "SOFR_FIXING_DATE_MISMATCH");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.SameDayEffrFixing)]
    public Task SameDayEffrFixingQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.SameDayEffrFixing, "EFFR_FIXING_DATE_MISMATCH");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.InvalidEffrRate)]
    public Task InvalidEffrRateQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.InvalidEffrRate, "INVALID_EFFR_RATE");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.ReversedEffrTargetRange)]
    public Task ReversedEffrTargetRangeQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.ReversedEffrTargetRange, "INVALID_EFFR_TARGET_RANGE");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.StaleZqClose)]
    public Task StaleZqCloseQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.StaleZqClose, "STALE_ZQ_CLOSE");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.FutureZqClose)]
    public Task FutureZqCloseQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.FutureZqClose, "FUTURE_ZQ_CLOSE");

    [Fact]
    [Trait(SavedFixtures.TraitName, SavedFixtures.MissingZqCloseDate)]
    public Task MissingZqCloseDateQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedFixtures.MissingZqCloseDate, "MISSING_ZQ_CLOSE_DATE");

    [Fact]
    public void PreviousFixingDateSkipsWeekend()
    {
        Assert.Equal(new DateOnly(2026, 10, 30), SOFRCurveCreation.PreviousFixingDate(new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void PreviousFixingDateSkipsHoliday()
    {
        Assert.Equal(new DateOnly(2026, 9, 4), SOFRCurveCreation.PreviousFixingDate(new DateOnly(2026, 9, 8)));
    }

    [Fact]
    public void PreviousFixingDateRejectsWeekendValuation()
    {
        Assert.Throws<ArgumentException>(() => SOFRCurveCreation.PreviousFixingDate(new DateOnly(2026, 11, 1)));
    }

    private static async Task AssertInvalidFixtureQuarantinesAsync(string fixture, string findingCode)
    {
        var inputs = await SavedFixtures.ReadInputsAsync(fixture);

        var validation = await CurveInputValidation.RunInputValidation(inputs);
        Assert.Equal("QUARANTINE", validation.Decision);
        Assert.Equal("SKIPPED", validation.BootstrapResult);
        Assert.StartsWith("INPUT_VALIDATION_FAILED ", validation.message);
        Assert.Empty(validation.pricingRisks);
        var inputRisk = Assert.Single(validation.validationRisks);
        Assert.Equal(findingCode, inputRisk.Code);
    }

    private static (string? Tenor, string? PillarDate, decimal QuotePercent) RepricingKey(JsonElement row)
        => (row.GetProperty("tenor").GetString(), row.GetProperty("pillar_date").GetString(), row.GetProperty("quote_percent").GetDecimal());

    private sealed record BaselineFixture(CurveInputs Inputs, JsonElement Curve);
}
