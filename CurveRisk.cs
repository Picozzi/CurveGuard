namespace CurveGuard;

internal sealed record OisTrade(string Tenor, decimal FixedCouponPercent, double NotionalUsd)
{
    public string Side => "pay_fixed";
}

internal sealed record OisTradeValuation(string StartDate, string MaturityDate, double ModelParRatePercent, double PvUsd);

internal sealed record CurveBuildResult(object Report, OisTradeValuation? TradeValuation);

internal sealed record OisRiskScenario(int BumpBp, decimal MarketQuotePercent, double? ModelParRatePercent,
    double? PvUsd, double? PvChangeUsd, ValidationResult Validation);

internal sealed record OisRiskReport(DateOnly ValuationDate, OisTrade Trade, string? StartDate, string? MaturityDate,
    IReadOnlyList<OisRiskScenario> Scenarios);

internal static class CurveRisk
{
    public static async Task<OisRiskReport> RunAsync(CurveInputs inputs, IReadOnlyDictionary<int, CurveInputs>? savedBumps = null)
    {
        var fiveYear = FindFiveYearQuote(inputs.OisSnapshot);
        var baseRate = fiveYear.Rate!.Value;
        var trade = new OisTrade("5Y", baseRate, 1_000_000);
        var baseRun = await CurveValidation.RunAsync(inputs, trade: trade);
        var baseValuation = baseRun.TradeValuation;
        var scenarios = new List<OisRiskScenario>
        {
            new(0, baseRate, baseValuation?.ModelParRatePercent, baseValuation?.PvUsd,
                baseValuation is null ? null : 0.0, baseRun.Validation),
        };

        if (baseValuation is not null)
        {
            var bumps = savedBumps?.Keys.Order().ToArray() ?? [1, 10];
            foreach (var bumpBp in bumps)
            {
                // The market quotes are stored in percentage points. One basis point is 0.01 percentage points.
                var bumpedRate = baseRate + bumpBp / 100m;
                CurveInputs bumpedInputs;
                if (savedBumps is null)
                {
                    bumpedInputs = inputs with { OisSnapshot = BumpQuote(inputs.OisSnapshot, fiveYear, bumpedRate) };
                }
                else
                {
                    bumpedInputs = savedBumps[bumpBp];
                    if (bumpedInputs.ValuationDate != inputs.ValuationDate ||
                        FindFiveYearQuote(bumpedInputs.OisSnapshot).Rate != bumpedRate)
                        throw new ArgumentException($"The +{bumpBp} bp fixture must use the baseline valuation date and a 5Y rate of {bumpedRate}%.");
                }

                var bumpedRun = await CurveValidation.RunAsync(bumpedInputs, trade: trade);
                var bumpedValuation = bumpedRun.TradeValuation;
                scenarios.Add(new OisRiskScenario(bumpBp, bumpedRate, bumpedValuation?.ModelParRatePercent,
                    bumpedValuation?.PvUsd, bumpedValuation?.PvUsd - baseValuation.PvUsd, bumpedRun.Validation));
            }
        }

        return new OisRiskReport(inputs.ValuationDate, trade, baseValuation?.StartDate, baseValuation?.MaturityDate, scenarios);
    }

    private static OISSOFR.OisApiQuote FindFiveYearQuote(OISSOFR.OISSwapSnapshot snapshot)
    {
        if (snapshot.Quotes.Where(quote => quote?.Tenor == "5Y").Take(2).ToArray() is not [{ Rate: not null } fiveYear])
            throw new ArgumentException("The pricing run requires exactly one 5Y OIS quote with a rate.");

        return fiveYear;
    }

    private static OISSOFR.OISSwapSnapshot BumpQuote(OISSOFR.OISSwapSnapshot snapshot, OISSOFR.OisApiQuote target,
        decimal bumpedRate)
    {
        var quotes = snapshot.Quotes.Select(quote =>
            ReferenceEquals(quote, target) ? target with { Rate = bumpedRate } : quote).ToArray();
        return new OISSOFR.OISSwapSnapshot(snapshot.ObservationDate, snapshot.Source,
            snapshot.Url, snapshot.ProviderCollectionTime, quotes);
    }
}
