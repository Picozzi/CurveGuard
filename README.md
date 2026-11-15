# CurveGuard · Week 1

CurveGuard builds a USD SOFR overnight indexed swap (OIS) curve and evaluates it before it can pass the local gate. The first demo shows how a curve can reprice every calibration instrument while a stale market quote still requires quarantine.

## Build

Use the .NET 10 SDK and run these commands from the project directory. The project uses the official QuantLib 1.44 C# package from NuGet. [QuantLib for C#](https://www.quantlib.org/download.shtml)

```sh
dotnet build
mkdir -p reports
```

## Build a SOFR curve with a synthetic FOMC front end

```sh
dotnet run -- --fomc-calendar 2026-11-13
dotnet run -- --fomc examples/fomc-illustrative.json
```

The example uses eight official policy decision dates, the archived NY Fed fixing and CheckMySwap swaps, and **illustrative probabilities**. It uses the November 12, 2026 calendar revision, including the November 2027 decision and omitting the October 2027 strategy meeting. [2026 FOMC calendar](https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm) · [2027 calendar revision](https://www.federalreserve.gov/newsevents/pressreleases/monetary20261112a.htm)

To use market probabilities, copy the JSON file. Set `valuation_date` to the probability snapshot’s observation date and `probability_source` to its origin. Enter the current target range and the next eight policy decision dates. `--fomc-calendar` reads the currently published schedule. Save its output with each new probability snapshot so a later calendar revision cannot move the dates in a replay. Copy each meeting’s full set of target ranges and probabilities from the public FedWatch table or historical downloads. Enter `20` for a 20% probability. Set `front_end_date` after the last decision date. CME offers a paid API for automated probability retrieval. [FedWatch guide](https://www.cmegroup.com/tools-information/quikstrike/cme-fedwatch-tool-user-guide.html) · [FedWatch API](https://www.cmegroup.com/market-data/market-data-api/fedwatch-api.html)

The command fetches the previous US Government Bond business day’s SOFR fixing, assumed available by the valuation day’s close, and the valuation day’s indicative OIS snapshot. For each decision, it computes the probability weighted target midpoint. The modeled forward SOFR adds the assumed constant spread between the starting fixing and the current target midpoint. Rates step on the decision dates. QuantLib fits continuous discount factors at the segment boundaries and reprices the OIS quotes that mature after the handoff. The report lists any overlap it excludes, including the 1Y OIS in this example.

## Fetch an overnight SOFR fixing

```sh
dotnet run --no-build -- --sofr --url \
  'https://markets.newyorkfed.org/api/rates/secured/sofr/last/1.json'

dotnet run --no-build -- --sofr 2026-11-12 --url \
  'https://markets.newyorkfed.org/api/rates/secured/sofr/search.json?startDate=2026-11-12&endDate=2026-11-12'
```

Supply the full URL, including any date query parameters. The optional date after `--sofr` checks the effective date in the response. The JSON output includes the effective date, the published percentage, and the rate as a decimal for use in QuantLib. If the API has no entry for the requested date, the command reports that and exits with status `1`. The effective date identifies the underlying overnight transactions. The rate is typically published the following business day. [New York Fed SOFR API](https://markets.newyorkfed.org/static/docs/markets-api.html) · [Publication details](https://www.newyorkfed.org/markets/reference-rates/additional-information-about-reference-rates)

## Replay real market data

`DataRetrieval` takes a caller supplied `Uri` for every request. Use `GetJsonAsync<T>(url)` for a general JSON endpoint, `GetSofrFixingAsync(url)` for New York Fed formatted fixings, or `GetOisSwapRatesAsync(url, date)` for CheckMySwap formatted snapshots. The swap method returns every published tenor in the snapshot with the rate, source, and observation method.

```sh
ois_url='https://checkmyswap.com/api/curves/USD?date=2026-11-13'
dotnet run --no-build -- --ois 2026-11-13 --url "$ois_url"

dotnet run --no-build -- \
  --market-date 2026-11-13 --url "$ois_url" > reports/market-csharp-2026-11-13.json
```

The API supplies indicative USD SOFR OIS par rates derived from reported DTCC trades. CurveGuard requires the eight swap tenors from 1Y through 30Y used in this project. The first pillar comes from the 1Y swap, so the curve's short end is inferred by interpolation up to that pillar. The report records the API URL, snapshot hash, and observation method for each input, including any estimated points. [CheckMySwap archive](https://checkmyswap.com/rates?ccy=USD) · [Calculation methodology](https://checkmyswap.com/methodology)

This replay runs the bootstrap, discount factor, and repricing checks. The source provides dates without per-quote observation times and the replay has a single feed, so the freshness and reference checks are skipped. A successful bootstrap still produces a `QUARANTINE` decision and exit status `1`. The replay uses OIS inputs only. A futures based short end would require a separate source of dated futures prices.

## Run the synthetic scenarios

```sh
dotnet run --no-build -- \
  --candidate examples/candidate-clean.json \
  --reference examples/reference.json \
  --as-of 2025-01-15T16:00:00Z > reports/clean-csharp.json

dotnet run --no-build -- \
  --candidate examples/candidate-stale-5y.json \
  --reference examples/reference.json \
  --as-of 2025-01-15T16:00:00Z > reports/stale-csharp-5y.json
```

The CLI writes one JSON report to standard output. It exits `0` for `PASS`, `1` for `QUARANTINE`, and `2` for invalid arguments. Every required check has to pass for the local gate to return `PASS`.

| Scenario | Bootstrap and repricing | Quote freshness | Reference comparison | Gate |
|---|---|---|---|---|
| Clean | Pass | Pass | Pass | `PASS` |
| Stale 5Y | Pass | Fail | Fail, +9 bp at 5Y | `QUARANTINE` |

## Curve and quote checks

The local snapshot mode uses spot starting OIS par swap rates at 11 required tenors from 1M to 30Y. QuantLib's `Sofr`, `OISRateHelper`, and `PiecewiseLogLinearDiscount` build a single curve for SOFR projection and discounting. Both modes use two business days to spot and to payment, annual coupons, Actual/360 coupon accrual, the US Government Bond calendar, and log-linear interpolation of discount factors on an Actual/365 Fixed curve. The report records the configured conventions. [QuantLib OIS helper source](https://github.com/lballabio/QuantLib/blob/v1.44/ql/termstructures/yield/oisratehelper.cpp)

- **Input integrity** requires the expected curve ID, decimal rate units, one quote per required tenor, and unique quote IDs within each snapshot. Rates must be within ±25% as a demo guard against common unit errors. Enter 4.12% as `0.0412`.
- **Freshness** requires an explicit timezone offset and an observation time no more than 60 minutes before `--as-of` on both feeds. The New York date of `--as-of` is the valuation date and must be a business day in the curve calendar.
- **Cross-source deviation** requires distinct source labels and checks each candidate quote against the reference with a 3 bp absolute tolerance. In the stale example, the candidate 5Y quote is 4.21% and the reference is 4.12%. The source labels identify the two snapshots, and the caller supplies the reference feed.
- **Numerical validity** requires a successful bootstrap, finite positive discount factors at the pillars and monthly sample dates, and an absolute repricing residual no greater than 0.01 bp per instrument. Negative rates and discount factors above one are allowed.

The report includes the gate decision, blocking checks, input SHA-256 hashes, quote IDs, deviations, curve pillars, and per-tenor repricing residuals. The fixtures, convention choices, and thresholds are for synthetic replay. This first slice covers OIS-only calibration and local validation.
