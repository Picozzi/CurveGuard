using System.Text.Json;
using Xunit;

namespace CurveGuard.Tests;

public class CurveValidationTests
{
    [Fact]
    [Trait("Fixture", SavedCurveFixtures.Baseline)]
    public async Task CleanCurveBootstrapsAndRepricesSavedQuotes()
    {
        const double toleranceBp = 0.01;
        var baseline = await SavedCurveFixtures.ReadAsync<BaselineFixture>(SavedCurveFixtures.Baseline);

        var validation = await CurveInputValidation.RunInputValidation(baseline.Inputs);

        Assert.Equal("PASS", validation.Decision);
        Assert.Equal("UPCOMING", validation.BootstrapResult);
        Assert.Empty(validation.message);
        Assert.Empty(validation.pricingRisks);
        Assert.Empty(validation.validationRisks);

        var curve = JsonSerializer.SerializeToElement(SOFRCurveCreation.Build(baseline.Inputs), SavedCurveFixtures.JsonOptions);
        Assert.Equal(baseline.Curve.GetProperty("reference_date").GetString(), curve.GetProperty("reference_date").GetString());
        var actual = curve.GetProperty("repricings").EnumerateArray().ToArray();
        var expected = baseline.Curve.GetProperty("repricing").EnumerateArray().Select(RepricingKey);
        Assert.Equal(expected, actual.Select(RepricingKey));
        Assert.All(actual, row => Assert.InRange(row.GetProperty("residual_bp").GetDouble(), -toleranceBp, toleranceBp));
    }

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.StaleFiveYear)]
    public Task StaleFiveYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.StaleFiveYear, "STALE_OIS_QUOTE");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.MissingOneYear)]
    public Task MissingOneYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.MissingOneYear, "MISSING_1Y_OIS_QUOTE");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.DuplicateFiveYear)]
    public Task DuplicateFiveYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.DuplicateFiveYear, "DUPLICATE_OIS_TENOR");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.FutureFiveYear)]
    public Task FutureFiveYearOisQuoteQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.FutureFiveYear, "FUTURE_OIS_QUOTE");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.StaleSofrFixing)]
    public Task StaleSofrFixingQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.StaleSofrFixing, "SOFR_FIXING_DATE_MISMATCH");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.SameDayEffrFixing)]
    public Task SameDayEffrFixingQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.SameDayEffrFixing, "EFFR_FIXING_DATE_MISMATCH");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.InvalidEffrRate)]
    public Task InvalidEffrRateQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.InvalidEffrRate, "INVALID_EFFR_RATE");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.ReversedEffrTargetRange)]
    public Task ReversedEffrTargetRangeQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.ReversedEffrTargetRange, "INVALID_EFFR_TARGET_RANGE");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.StaleZqClose)]
    public Task StaleZqCloseQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.StaleZqClose, "STALE_ZQ_CLOSE");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.FutureZqClose)]
    public Task FutureZqCloseQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.FutureZqClose, "FUTURE_ZQ_CLOSE");

    [Fact]
    [Trait("Fixture", SavedCurveFixtures.MissingZqCloseDate)]
    public Task MissingZqCloseDateQuarantinesBeforeBootstrap()
        => AssertInvalidFixtureQuarantinesAsync(SavedCurveFixtures.MissingZqCloseDate, "MISSING_ZQ_CLOSE_DATE");

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
        var replay = await SavedCurveFixtures.LoadReplayAsync(Path.GetFileNameWithoutExtension(fixture));

        var validation = await CurveInputValidation.RunInputValidation(replay.Inputs);
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
