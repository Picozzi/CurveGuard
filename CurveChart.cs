using System.Globalization;
using System.Text.Json;
using ScottPlot;

namespace CurveGuard;

internal static class CurveChart
{
    private static readonly Color FomcColor = Color.FromHex("#11796C");
    private static readonly Color OisColor = Color.FromHex("#234C87");
    private static readonly Color HandoffColor = Color.FromHex("#8C601F");

    public static string Save(JsonElement report)
    {
        var valuation = ReadDate(report, "reference_date");
        var forwards = report.GetProperty("monthly_and_pillar_forwards").EnumerateArray()
            .Select(point => new Point(ReadDate(point, "date"), point.GetProperty("instantaneous_forward_percent").GetDouble()))
            .OrderBy(point => point.Date).ToArray();

        if (forwards.Length < 2 || forwards.Any(point => !double.IsFinite(point.Rate)))
            throw new FormatException("The report must contain at least two finite instantaneous forward samples.");

        var steps = new List<Point>();
        DateOnly? handoff = null;
        if (report.TryGetProperty("fomc_front_end", out var frontEnd))
        {
            handoff = ReadDate(frontEnd, "handoff_date");
            foreach (var interval in frontEnd.GetProperty("segments").EnumerateArray())
            {
                var rate = interval.GetProperty("calibrated_forward_percent").GetDouble();
                // Two points at a shared boundary preserve the vertical step between flat intervals.
                steps.Add(new Point(ReadDate(interval, "start_date"), rate));
                steps.Add(new Point(ReadDate(interval, "end_date"), rate));
            }

            if (steps.Count == 0 || steps[0].Date != valuation || steps[^1].Date != handoff ||
                steps.Any(point => !double.IsFinite(point.Rate)))
                throw new FormatException("The report's FOMC segments must span valuation through the one-year handoff.");

            forwards = forwards.Where(point => point.Date > handoff.Value)
                .Prepend(steps[^1]).ToArray();
            if (forwards.Length < 2)
                throw new FormatException("The report must contain forward samples beyond the one-year handoff.");
        }

        var chart = new Multiplot();
        chart.AddPlots(2);
        chart.Layout = new ScottPlot.MultiplotLayouts.Grid(rows: 2, columns: 1);

        var full = chart.GetPlot(0);
        double MaturityAxis(DateOnly date) => (date.DayNumber - valuation.DayNumber) / 365.0;
        Draw(full, steps, forwards, handoff, MaturityAxis);
        full.Title($"USD SOFR forward curve  |  {valuation:yyyy-MM-dd}");
        full.XLabel("Years from valuation (ACT/365F)");
        var lastYear = MaturityAxis(forwards[^1].Date);
        full.Axes.SetLimitsX(-0.15, lastYear + 0.25);
        if (lastYear >= 5)
        {
            var ticks = new ScottPlot.TickGenerators.NumericManual();
            ticks.AddMajor(0, "0");
            for (var year = 5; year <= lastYear; year += 5)
                ticks.AddMajor(year, year.ToString(CultureInfo.InvariantCulture));
            full.Axes.Bottom.TickGenerator = ticks;
        }

        var near = chart.GetPlot(1);
        var through = forwards[^1].Date < valuation.AddMonths(18) ? forwards[^1].Date : valuation.AddMonths(18);
        var nearForwards = forwards.TakeWhile(point => point.Date <= through).ToList();
        if (nearForwards.Count < forwards.Length)
            nearForwards.Add(forwards[nearForwards.Count]);

        double CalendarAxis(DateOnly date) => date.ToDateTime(TimeOnly.MinValue).ToOADate();
        Draw(near, steps, nearForwards, handoff, CalendarAxis);
        near.Title(handoff.HasValue ? "Front end and one-year handoff" : "Front-end detail");
        var monthTicks = new ScottPlot.TickGenerators.NumericManual();
        for (var month = 0; valuation.AddMonths(month) <= through; month += 3)
        {
            var tick = valuation.AddMonths(month);
            monthTicks.AddMajor(CalendarAxis(tick), tick.ToString("MMM yyyy", CultureInfo.InvariantCulture));
        }
        near.Axes.Bottom.TickGenerator = monthTicks;
        near.Axes.SetLimitsX(CalendarAxis(valuation.AddDays(-5)), CalendarAxis(through.AddDays(12)));

        var output = Path.GetFullPath(Path.Combine("reports", $"sofr-forward-{valuation:yyyy-MM-dd}.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        chart.SavePng(output, 1440, 1060);
        return output;
    }

    private static void Draw(Plot plot, IReadOnlyList<Point> steps, IReadOnlyList<Point> forwards,
        DateOnly? handoff, Func<DateOnly, double> x)
    {
        plot.YLabel("Instantaneous forward (%)");
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => value.ToString("0.00", CultureInfo.InvariantCulture),
        };
        var tail = plot.Add.ScatterLine(forwards.Select(point => x(point.Date)).ToArray(),
            forwards.Select(point => point.Rate).ToArray(), OisColor);
        tail.LineWidth = 3;
        tail.LegendText = handoff.HasValue ? "OIS spline" : "SOFR forward curve";

        if (handoff is { } join)
        {
            var front = plot.Add.ScatterLine(steps.Select(point => x(point.Date)).ToArray(),
                steps.Select(point => point.Rate).ToArray(), FomcColor);
            front.LineWidth = 3;
            front.LegendText = "FOMC steps";

            var boundary = plot.Add.VerticalLine(x(join), 1.5f, HandoffColor, LinePattern.Dashed);
            boundary.LegendText = "1Y handoff";
        }

        plot.ShowLegend(Alignment.LowerRight);
    }

    private static DateOnly ReadDate(JsonElement element, string property) =>
        DateOnly.ParseExact(element.GetProperty(property).GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record Point(DateOnly Date, double Rate);
}
