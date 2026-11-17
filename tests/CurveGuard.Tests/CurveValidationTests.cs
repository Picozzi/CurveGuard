using Xunit;

namespace CurveGuard.Tests;

public class CurveValidationTests
{
    [Theory]
    [InlineData("stale-5y.json", "STALE_OIS_QUOTE")]
    [InlineData("missing-1y.json", "MISSING_1Y_OIS_QUOTE")]
    [InlineData("duplicate-5y.json", "DUPLICATE_OIS_TENOR")]
    [InlineData("future-5y.json", "FUTURE_OIS_QUOTE")]
    [InlineData("stale-sofr-fixing.json", "SOFR_FIXING_DATE_MISMATCH")]
    [InlineData("same-day-effr-fixing.json", "EFFR_FIXING_DATE_MISMATCH")]
    [InlineData("invalid-effr-rate.json", "INVALID_EFFR_RATE")]
    [InlineData("reversed-effr-target-range.json", "INVALID_EFFR_TARGET_RANGE")]
    [InlineData("stale-zq-close.json", "STALE_ZQ_CLOSE")]
    [InlineData("future-zq-close.json", "FUTURE_ZQ_CLOSE")]
    [InlineData("missing-zq-close-date.json", "MISSING_ZQ_CLOSE_DATE")]
    public async Task LiveChecksQuarantineInvalidInputsBeforeBootstrapping(string fixture, string findingCode)
    {
        var inputs = await SavedFixtures.ReadInputsAsync(fixture);

        var run = await CurveRisk.RunLiveAsync(inputs, () =>
            throw new InvalidOperationException("FOMC data should only load after input validation passes."));

        Assert.Equal("QUARANTINE", run.Validation.Decision);
        Assert.Equal("SKIPPED", run.Validation.Bootstrap);
        Assert.Equal(findingCode, Assert.Single(run.Validation.Findings).Code);
        Assert.Null(run.Curve);
        Assert.Null(run.PricingRisk);
    }

    [Theory]
    [InlineData("2026-11-02", "2026-10-30")]
    [InlineData("2026-09-08", "2026-09-04")]
    public void PreviousFixingDateSkipsWeekendsAndHolidays(string valuationDate, string expectedFixingDate)
    {
        Assert.Equal(DateOnly.ParseExact(expectedFixingDate, "yyyy-MM-dd"),
            SOFRCurve.PreviousFixingDate(DateOnly.ParseExact(valuationDate, "yyyy-MM-dd")));
    }

    [Fact]
    public void PreviousFixingDateRejectsWeekendValuation()
    {
        Assert.Throws<ArgumentException>(() => SOFRCurve.PreviousFixingDate(new DateOnly(2026, 11, 1)));
    }
}
